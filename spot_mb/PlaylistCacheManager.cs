using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpotifyAPI.Web;

namespace MusicBeePlugin
{
	public class PlaylistCacheManager
	{
		private SpotifyClient _spotify;
		private string _cacheDir;
		private string _cacheFilePath;

		private PlaylistCache _cache;
		private Dictionary<(string playlistId, string trackUri), (bool isMember, DateTime fetchedAt)> _membershipCache;

		private const int MembershipCacheTTLMinutes = 5;

		public PlaylistCacheManager(SpotifyClient spotify)
		{
			_spotify = spotify;
			_membershipCache = new Dictionary<(string, string), (bool, DateTime)>();

			// Set up AppData cache directory
			_cacheDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"MusicBee", "Spotify"
			);

			if (!Directory.Exists(_cacheDir))
			{
				Directory.CreateDirectory(_cacheDir);
			}

			_cacheFilePath = Path.Combine(_cacheDir, "playlists_cache.json");

			// Load cache from disk
			LoadCacheFromDisk();
		}

		/// <summary>
		/// Get playlists for a specific offset (pagination)
		/// Fetches from cache or API as needed
		/// </summary>
		public async Task<List<SimplePlaylist>> GetPlaylistsAsync(int offset)
		{
			// If cache is stale (older than 1 hour), refresh from API
			if (_cache == null || DateTime.UtcNow - _cache.LastFetched > TimeSpan.FromHours(1))
			{
				await FetchPlaylistsFromAPIAsync();
			}

			// Return 3 playlists from cache at the given offset
			if (_cache?.Playlists == null || _cache.Playlists.Count == 0)
			{
				return new List<SimplePlaylist>();
			}

			int endIndex = Math.Min(offset + 3, _cache.Playlists.Count);
			return _cache.Playlists
				.Skip(offset)
				.Take(3)
				.Select(cp => new SimplePlaylist
				{
					Id = cp.Id,
					Name = cp.Name
				})
				.ToList();
		}

		/// <summary>
		/// Get total number of available playlists
		/// </summary>
		public int GetTotalPlaylistsAvailable()
		{
			return _cache?.TotalAvailable ?? 0;
		}

		/// <summary>
		/// Check if a track is in a playlist
		/// Uses memory cache first, falls back to API
		/// </summary>
		public async Task<bool?> IsTrackInPlaylistAsync(string playlistId, string trackUri)
		{
			// Check memory cache first
			var cacheKey = (playlistId, trackUri);
			if (_membershipCache.TryGetValue(cacheKey, out var cached))
			{
				// If cache is still fresh (< 5 minutes), return it
				if (DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromMinutes(MembershipCacheTTLMinutes))
				{
					return cached.isMember;
				}

				// Cache expired, remove it
				_membershipCache.Remove(cacheKey);
			}

			// Cache miss or expired - fetch from API
			try
			{
				var allTracks = await FetchAllPlaylistTracksAsync(playlistId);
				bool isMember = allTracks.Contains(trackUri);

				// Cache the result
				_membershipCache[cacheKey] = (isMember, DateTime.UtcNow);

				return isMember;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Check membership failed: " + ex.Message);
				return null;
			}
		}

		/// <summary>
		/// Get cached membership status without fetching from API
		/// Returns null if not in cache
		/// </summary>
		public bool? GetCachedMembership(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			if (_membershipCache.TryGetValue(cacheKey, out var cached))
			{
				if (DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromMinutes(MembershipCacheTTLMinutes))
				{
					return cached.isMember;
				}
				_membershipCache.Remove(cacheKey);
			}
			return null;
		}

		/// <summary>
		/// User added a track to a playlist - update cache immediately
		/// </summary>
		public void NotifyTrackAdded(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			_membershipCache[cacheKey] = (true, DateTime.UtcNow);
		}

		/// <summary>
		/// User removed a track from a playlist - update cache immediately
		/// </summary>
		public void NotifyTrackRemoved(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			_membershipCache[cacheKey] = (false, DateTime.UtcNow);
		}

		/// <summary>
		/// Force refresh membership status from API
		/// </summary>
		public async Task<bool?> RefreshTrackMembershipAsync(string playlistId, string trackUri)
		{
			// Remove from cache to force API fetch
			var cacheKey = (playlistId, trackUri);
			_membershipCache.Remove(cacheKey);

			// Fetch from API
			return await IsTrackInPlaylistAsync(playlistId, trackUri);
		}

		/// <summary>
		/// Clear all caches
		/// </summary>
		public void ClearCache()
		{
			_membershipCache.Clear();
			_cache = new PlaylistCache();
			SaveCacheToDisk();
		}

		// ========== Private Methods ==========

		/// <summary>
		/// Load playlist cache from AppData
		/// </summary>
		private void LoadCacheFromDisk()
		{
			try
			{
				if (File.Exists(_cacheFilePath))
				{
					string json = File.ReadAllText(_cacheFilePath);
					_cache = JsonConvert.DeserializeObject<PlaylistCache>(json);
				}
				else
				{
					_cache = new PlaylistCache();
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Failed to load cache from disk: " + ex.Message);
				_cache = new PlaylistCache();
			}
		}

		/// <summary>
		/// Save playlist cache to AppData
		/// </summary>
		private void SaveCacheToDisk()
		{
			try
			{
				string json = JsonConvert.SerializeObject(_cache, Formatting.Indented);
				File.WriteAllText(_cacheFilePath, json);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Failed to save cache to disk: " + ex.Message);
			}
		}

		/// <summary>
		/// Fetch playlists from Spotify API and cache them
		/// </summary>
		private async Task FetchPlaylistsFromAPIAsync()
		{
			try
			{
				var request = new PlaylistCurrentUsersRequest
				{
					Limit = 50,
					Offset = 0
				};

				var result = await _spotify.Playlists.CurrentUsers(request);

				_cache.Playlists = result.Items.Select(p => new CachedPlaylist
				{
					Id = p.Id,
					Name = p.Name,
					TotalTracks = p.Tracks?.Total ?? 0,
					CachedAt = DateTime.UtcNow
				}).ToList();

				_cache.TotalAvailable = result.Total ?? 0;
				_cache.LastFetched = DateTime.UtcNow;

				SaveCacheToDisk();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Fetch playlists from API failed: " + ex.Message);
				if (_cache == null)
				{
					_cache = new PlaylistCache();
				}
			}
		}

		/// <summary>
		/// Fetch all track URIs from a playlist
		/// </summary>
		private async Task<HashSet<string>> FetchAllPlaylistTracksAsync(string playlistId)
		{
			var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track)
			{
				Limit = 100,
				Offset = 0,
				Fields = { "items(track(uri))", "next" }
			};

			while (true)
			{
				var page = await _spotify.Playlists.GetItems(playlistId, request);

				if (page?.Items != null)
				{
					foreach (var entry in page.Items)
					{
						var fullTrack = ExtractFullTrack(entry);
						if (fullTrack?.Uri != null)
						{
							uris.Add(fullTrack.Uri);
						}
					}
				}

				if (string.IsNullOrEmpty(page?.Next))
				{
					break;
				}

				request.Offset = (request.Offset ?? 0) + (request.Limit ?? 100);
			}

			return uris;
		}

		/// <summary>
		/// Extract full track from playlist item
		/// </summary>
		private static FullTrack ExtractFullTrack(object entry)
		{
			if (entry == null)
			{
				return null;
			}

			var type = entry.GetType();
			var prop = type.GetProperty("Track") ?? type.GetProperty("Item");
			return prop?.GetValue(entry) as FullTrack;
		}
	}

	/// <summary>
	/// Cached playlist metadata
	/// </summary>
	public class CachedPlaylist
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public int TotalTracks { get; set; }
		public DateTime CachedAt { get; set; }
	}

	/// <summary>
	/// Root cache structure saved to AppData
	/// </summary>
	public class PlaylistCache
	{
		public List<CachedPlaylist> Playlists { get; set; } = new List<CachedPlaylist>();
		public int TotalAvailable { get; set; }
		public DateTime LastFetched { get; set; }
	}
}