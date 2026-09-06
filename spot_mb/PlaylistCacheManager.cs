using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SpotifyAPI.Web;

namespace MusicBeePlugin
{
	public class PlaylistCacheManager
	{
		private readonly SpotifyClient _spotify;
		private readonly string _cacheFilePath;

		private PlaylistCache _cache;

		// Thread-safe membership cache
		private readonly ConcurrentDictionary<(string playlistId, string trackUri), (bool isMember, DateTime fetchedAt)> _membershipCache;

		private const int MembershipCacheTTLMinutes = 5;

		public PlaylistCacheManager(SpotifyClient spotify)
		{
			_spotify = spotify;
			_membershipCache = new ConcurrentDictionary<(string, string), (bool, DateTime)>();

			string cacheDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"MusicBee", "Spotify"
			);

			if (!Directory.Exists(cacheDir))
			{
				Directory.CreateDirectory(cacheDir);
			}

			_cacheFilePath = Path.Combine(cacheDir, "playlists_cache.json");
			LoadCacheFromDisk();
		}

		private readonly SemaphoreSlim _refreshlock = new SemaphoreSlim(1, 1);
		public async Task<List<SimplePlaylist>> GetPlaylistsAsync(int offset)
		{
			if (_cache == null || DateTime.UtcNow - _cache.LastFetched > TimeSpan.FromHours(1))
			{
				await _refreshlock.WaitAsync();
				try
				{
					if (_cache == null || DateTime.UtcNow - _cache.LastFetched > TimeSpan.FromHours(1))
					{
						await FetchPlaylistsFromAPIAsync();
					}
				}
				finally
				{
					_refreshlock.Release();
				}
			}

			if (_cache?.Playlists == null || _cache.Playlists.Count == 0)
			{
				return new List<SimplePlaylist>();
			}

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

		public int GetCachedTrackCount(string playlistId)
		{
			return _cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId)?.TotalTracks ?? 0;
		}

		public void InvalidatePlaylistListCache()
		{
			if (_cache != null)
			{
				_cache.LastFetched = DateTime.MinValue;
			}
		}

		public int GetTotalPlaylistsAvailable()
		{
			return _cache?.TotalAvailable ?? 0;
		}

		public async Task<bool?> IsTrackInPlaylistAsync(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);

			if (_membershipCache.TryGetValue(cacheKey, out var cached))
			{
				if (DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromMinutes(MembershipCacheTTLMinutes))
				{
					return cached.isMember;
				}
				_membershipCache.TryRemove(cacheKey, out _);
			}

			try
			{
				// Early-exit scan: returns as soon as the track is found
				var (isMember, totalTracks) = await FastCheckTrackInPlaylistAsync(playlistId, trackUri);

				UpdateCachedTrackCount(playlistId, totalTracks);

				_membershipCache[cacheKey] = (isMember, DateTime.UtcNow);
				return isMember;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Check membership failed: " + ex.Message);
				return null;
			}
		}

		private void UpdateCachedTrackCount(string playlistId, int actualCount)
		{
			var cached = _cache?.Playlists?.FirstOrDefault(p => p.Id == playlistId);
			if (cached != null && cached.TotalTracks != actualCount)
			{
				cached.TotalTracks = actualCount;
				SaveCacheToDisk();
			}
		}

		public bool? GetCachedMembership(string playlistId, string trackUri)
		{
			var cacheKey = (playlistId, trackUri);
			if (_membershipCache.TryGetValue(cacheKey, out var cached))
			{
				if (DateTime.UtcNow - cached.fetchedAt < TimeSpan.FromMinutes(MembershipCacheTTLMinutes))
				{
					return cached.isMember;
				}
				_membershipCache.TryRemove(cacheKey, out _);
			}
			return null;
		}

		public void NotifyTrackAdded(string playlistId, string trackUri)
		{
			_membershipCache[(playlistId, trackUri)] = (true, DateTime.UtcNow);
		}

		public void NotifyTrackRemoved(string playlistId, string trackUri)
		{
			_membershipCache[(playlistId, trackUri)] = (false, DateTime.UtcNow);
		}

		public async Task<bool?> RefreshTrackMembershipAsync(string playlistId, string trackUri)
		{
			_membershipCache.TryRemove((playlistId, trackUri), out _);
			return await IsTrackInPlaylistAsync(playlistId, trackUri);
		}

		public void ClearCache()
		{
			_membershipCache.Clear();
			_cache = new PlaylistCache();
			SaveCacheToDisk();
		}

		private void LoadCacheFromDisk()
		{
			try
			{
				if (File.Exists(_cacheFilePath))
				{
					string json = File.ReadAllText(_cacheFilePath);
					_cache = JsonConvert.DeserializeObject<PlaylistCache>(json) ?? new PlaylistCache();
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

		private async Task<(bool isMember, int totalTracks)> FastCheckTrackInPlaylistAsync(string playlistId, string targetUri)
		{
			var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track)
			{
				Limit = 100,
				Offset = 0
			};

			int totalTracks = 0;

			while (true)
			{
				Paging<PlaylistTrack<IPlayableItem>> page = await _spotify.Playlists.GetItems(playlistId, request);

				if (page?.Items == null)
				{
					break;
				}

				totalTracks = page.Total ?? totalTracks;

				// Corrected property reference: Item instead of Track
				foreach (var playlistTrack in page.Items)
				{
					if (playlistTrack.Item is FullTrack fullTrack &&
						string.Equals(fullTrack.Uri, targetUri, StringComparison.OrdinalIgnoreCase))
					{
						return (true, totalTracks);
					}
				}

				if (string.IsNullOrEmpty(page.Next))
				{
					break;
				}

				request.Offset = (request.Offset ?? 0) + (request.Limit ?? 100);
			}

			return (false, totalTracks);
		}
	}

	public class CachedPlaylist
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public int TotalTracks { get; set; }
		public DateTime CachedAt { get; set; }
	}

	public class PlaylistCache
	{
		public List<CachedPlaylist> Playlists { get; set; } = new List<CachedPlaylist>();
		public int TotalAvailable { get; set; }
		public DateTime LastFetched { get; set; }
	}
}