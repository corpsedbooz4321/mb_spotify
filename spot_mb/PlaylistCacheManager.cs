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

		/// Get playlists for a specific offset (pagination)
		/// begs from cache or API as needed
		public async Task<List<SimplePlaylist>> GetPlaylistsAsync(int offset)
		{
			// If cache is stale like older than 1 hour, refresh from api
			if (_cache == null || DateTime.UtcNow - _cache.LastFetched > TimeSpan.FromHours(1))
			{
				await FetchPlaylistsFromAPIAsync();
			}

			// Return 3 playlists from cache at one itme given given offset
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

		/// Track count for a playlist, straight from the cache. Kept as a plain int
		public int GetCachedTrackCount(string playlistId)
		{
			return _cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId)?.TotalTracks ?? 0;
		}

		/// Force the next call to GetPlaylistsAsync to re-beg from the API instead of
		public void InvalidatePlaylistListCache()
		{
			if (_cache != null)
			{
				_cache.LastFetched = DateTime.MinValue;
			}
		}

		/// Get total number of available playlists
		public int GetTotalPlaylistsAvailable()
		{
			return _cache?.TotalAvailable ?? 0;
		}

		/// Check if a track is in a playlist
		/// it uses saved cached entries first then it will start begging from api "falls back to api"
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

			// Cache miss or expired - beg from api
			// an IQ too high?🤡
			try
			{
				var allTracks = await FetchAllPlaylistTracksAsync(playlistId);
				bool isMember = allTracks.Contains(trackUri);

				UpdateCachedTrackCount(playlistId, allTracks.Count);

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

		/// Corrects a playlist's cached track count once we've actually walked its
		/// full track list (which happens as a side effect of any membership check).
		private void UpdateCachedTrackCount(string playlistId, int actualCount)
		{
			var cached = _cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId);
			if (cached != null && cached.TotalTracks != actualCount)
			{
				cached.TotalTracks = actualCount;
				SaveCacheToDisk();
			}
		}

		/// Get cached membership status without begging from API
		/// Returns null if not in cache
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

		/// User added a track to a playlist - update cache immediately
		public void NotifyTrackAdded(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			_membershipCache[cacheKey] = (true, DateTime.UtcNow);
		}

		/// User removed a track from a playlist update cache immediately
		public void NotifyTrackRemoved(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			_membershipCache[cacheKey] = (false, DateTime.UtcNow);
		}

		/// Force re-beg membership status from API
		public async Task<bool?> RefreshTrackMembershipAsync(string playlistId, string trackUri)
		{
			// Remove from cache to force API fetch
			var cacheKey = (playlistId, trackUri);
			_membershipCache.Remove(cacheKey);

			// Fetch from API
			return await IsTrackInPlaylistAsync(playlistId, trackUri);
		}

		/// Clear all caches
		public void ClearCache()
		{
			_membershipCache.Clear();
			_cache = new PlaylistCache();
			SaveCacheToDisk();
		}

		// Private Methods 
		/// Load playlist cache from AppData
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

		/// Save playlist cache to AppData
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

		/// Fetch playlists from Spotify API and cache them
		private async Task FetchPlaylistsFromAPIAsync()
		{
			try
			{
				const int pageSize = 50;
				var allPlaylists = new List<CachedPlaylist>();
				int offset = 0;
				int total = 0;

				while (true)
				{
					var request = new PlaylistCurrentUsersRequest
					{
						Limit = pageSize,
						Offset = offset
					};

					var result = await _spotify.Playlists.CurrentUsers(request);

					if (result?.Items == null || result.Items.Count == 0)
					{
						break;
					}

					allPlaylists.AddRange(result.Items.Select(p => new CachedPlaylist
					{
						Id = p.Id,
						Name = p.Name,
						TotalTracks = p.Tracks?.Total ?? 0,
						CachedAt = DateTime.UtcNow
					}));

					total = result.Total ?? allPlaylists.Count;
					offset += pageSize;

					if (offset >= total || result.Items.Count < pageSize || string.IsNullOrEmpty(result.Next))
					{
						break;
					}
				}

				_cache.Playlists = allPlaylists;
				_cache.TotalAvailable = total;
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

		/// Fetch all track URIs from a playlist
		private async Task<HashSet<string>> FetchAllPlaylistTracksAsync(string playlistId)
		{
			var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			// "Add" (Contains() on an empty set is always false), never "Remove".
			var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track)
			{
				Limit = 100,
				Offset = 0
			};

			while (true)
			{
				var page = await _spotify.Playlists.GetItems(playlistId, request);

				if (page?.Items != null)
				{
					int itemCount = page.Items.Count;
					int extractedCount = 0;

					foreach (var entry in page.Items)
					{
						var fullTrack = ExtractFullTrack(entry);
						if (fullTrack?.Uri != null)
						{
							uris.Add(fullTrack.Uri);
							extractedCount++;
						}
					}

					// Cheap tripwirefor  future regression like the fieldfilter bug
					// upthere shows up immediately in the log instead of silently gooning own its own
					// producing 0 tracks and always Add again.
					if (itemCount > 0 && extractedCount == 0)
					{
						System.Diagnostics.Debug.WriteLine(
							$"FetchAllPlaylistTracksAsync: playlist {playlistId} returned {itemCount} item(s) but extracted 0 track URIs - ExtractFullTrack may be failing to deserialize items.");
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

		/// Extract full track from playlist item
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

	/// Cached playlist metadata
	public class CachedPlaylist
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public int TotalTracks { get; set; }
		public DateTime CachedAt { get; set; }
	}

	/// Root cache structure saved to AppData
	public class PlaylistCache
	{
		public List<CachedPlaylist> Playlists { get; set; } = new List<CachedPlaylist>();
		public int TotalAvailable { get; set; }
		public DateTime LastFetched { get; set; }
	}
}