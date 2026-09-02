using SpotifyAPI.Web;
using System.Windows.Forms;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using System.Drawing;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static SpotifyClient _spotify;
        private static bool _codeExchanged = false;
        private static int _auth, _num, _trackMissing = 0;
        private static bool _trackLIB, _albumLIB, _artistLIB = false;
        private static string _title, _album, _artist, _trackID, _albumID, _artistID, _imageURL;
        private static string _clientID;
        private static long _searchGeneration = 0;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SearchResponse> _searchCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, SearchResponse>(StringComparer.OrdinalIgnoreCase);
        private const int MaxSearchCacheEntries = 200;

        private static string _cachedArtworkUrl;
        private static Bitmap _cachedArtwork;
        private static readonly object _artworkLock = new object();
        private static readonly HttpClient _artworkHttpClient = new HttpClient();

        // Lock for thread-safe access to static state
        private static readonly object _stateLock = new object();

        private const int AUTH_PORT = 5000;
        private const string AUTH_CALLBACK_PATH = "/callback";

        private void RefreshPanelUi()
        {
            try
            {
                if (panel == null)
                {
                    return;
                }

                if (panel.InvokeRequired)
                {
                    panel.BeginInvoke((MethodInvoker)(() =>
                    {
                        try
                        {
                            mbApiInterface.MB_RefreshPanels();
                            panel.Invalidate();
                        }
                        catch (Exception ex)
                        {
                            mbApiInterface.MB_Trace($"RefreshPanelUi invoke failed: {ex.GetType().Name} - {ex.Message}");
                        }
                    }));
                }
                else
                {
                    mbApiInterface.MB_RefreshPanels();
                    panel.Invalidate();
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"RefreshPanelUi failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public void SerializeConfig(PKCETokenResponse data, string path, RSACryptoServiceProvider rsaKey)
        {
            try
            {
                if (data == null) return;

                string json = JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
                using (StreamWriter file = new StreamWriter(path, false))
                {
                    file.Write(json);
                }
            }
            catch (Exception e)
            {
                mbApiInterface.MB_Trace($"SerializeConfig failed: {e.GetType().Name} - {e.Message}");
                MessageBox.Show("Error saving token file:\n" + e.Message, "Spotify Plugin Error");
            }
        }

        public PKCETokenResponse DeserializeConfig(string path, RSACryptoServiceProvider rsaKey)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<PKCETokenResponse>(json);
                }
            }
            catch (Exception e)
            {
                mbApiInterface.MB_Trace($"DeserializeConfig failed: {e.GetType().Name} - {e.Message}");
                MessageBox.Show("Error reading token file:\n" + e.Message, "Spotify Plugin Error");
            }
            return null;
        }

        private async Task SpotifyWebAuthAsync()
        {
            if (_authInProgress) return;

            if (string.IsNullOrWhiteSpace(_clientID))
            {
                mbApiInterface.MB_Trace("SpotifyWebAuthAsync: No Client ID configured");
                return;
            }

            _authInProgress = true;

            try
            {
                // Try to use saved token first
                if (File.Exists(_path))
                {
                    var token_response = DeserializeConfig(_path, _rsaKey);
                    if (token_response != null)
                    {
                        try
                        {
                            var authenticator = new PKCEAuthenticator(_clientID, token_response, _path);
                            var config = SpotifyClientConfig.CreateDefault()
                                .WithAuthenticator(authenticator);
                            _spotify = new SpotifyClient(config);

                            await _spotify.Search.Item(new SearchRequest(SearchRequest.Types.Track, "test")).ConfigureAwait(false);

                            lock (_stateLock)
                            {
                                _auth = 1;
                            }

                            await TrackSearch().ConfigureAwait(false);
                            RefreshPanelUi();
                            return;
                        }
                        catch (APIException apiEx)
                        {
                            mbApiInterface.MB_Trace($"Saved token validation failed: {apiEx.Response?.StatusCode} - {apiEx.Message}");
                            lock (_stateLock)
                            {
                                _trackMissing = 1;
                                _trackLIB = _albumLIB = _artistLIB = false;
                                _auth = 1;
                            }
                            RefreshPanelUi();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SpotifyWebAuthAsync token reload failed: {ex.GetType().Name} - {ex.Message}");
            }

            // Proceed with web auth
            bool browserOpened = false;
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes(120);
                var callbackUri = new Uri($"http://127.0.0.1:{AUTH_PORT}{AUTH_CALLBACK_PATH}");

                var loginRequest = new LoginRequest(callbackUri, _clientID, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] {
                        Scopes.UserLibraryModify, Scopes.UserFollowModify, Scopes.UserFollowRead, Scopes.UserLibraryRead,
                        Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic, Scopes.PlaylistReadPrivate
                    }
                };
                var uri = loginRequest.ToUri();

                var server = new EmbedIOAuthServer(callbackUri, AUTH_PORT);

                await server.Start().ConfigureAwait(false);
                browserOpened = true;

                server.PkceReceived += async (sender, response) =>
                {
                    if (_codeExchanged) return;
                    _codeExchanged = true;

                    await server.Stop().ConfigureAwait(false);
                    try
                    {
                        var initialResponse = await new OAuthClient().RequestToken(
                            new PKCETokenRequest(_clientID, response.Code, callbackUri, verifier)
                        ).ConfigureAwait(false);

                        var authenticator = new PKCEAuthenticator(_clientID, initialResponse, _path);
                        var config = SpotifyClientConfig.CreateDefault()
                            .WithAuthenticator(authenticator);
                        _spotify = new SpotifyClient(config);

                        var me = await _spotify.UserProfile.Current().ConfigureAwait(false);

                        SerializeConfig(initialResponse, _path, _rsaKey);

                        lock (_stateLock)
                        {
                            _auth = 1;
                            _searchTerm = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle)
                                        + " "
                                        + mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);
                        }

                        if (panel != null)
                        {
                            panel.BeginInvoke((Action)(async () =>
                            {
                                try
                                {
                                    _authInProgress = false;
                                    mbApiInterface.MB_RefreshPanels();
                                    await TrackSearch().ConfigureAwait(false);
                                    panel.Invalidate();
                                }
                                catch (Exception ex)
                                {
                                    mbApiInterface.MB_Trace($"Post-auth TrackSearch failed: {ex.GetType().Name} - {ex.Message}");
                                }
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        mbApiInterface.MB_Trace($"Token exchange failed: {ex.GetType().Name} - {ex.Message}");
                        MessageBox.Show("Authentication failed:\n" + ex.Message, "Spotify Plugin Error");
                        _authInProgress = false;
                        RefreshPanelUi();
                    }
                };

                System.Diagnostics.Process.Start(uri.ToString());
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SpotifyWebAuthAsync failed: {ex.GetType().Name} - {ex.Message}");
                _authInProgress = false;
                MessageBox.Show("Authentication failed:\n" + ex.Message, "Spotify Plugin Error");
                RefreshPanelUi();
            }
        }

        private async Task TrackSearch()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_searchTerm))
                {
                    return;
                }

                var currentGeneration = ++_searchGeneration;

                SearchResponse searchResponse;
                if (_searchCache.TryGetValue(_searchTerm, out var cached))
                {
                    searchResponse = cached;
                }
                else
                {
                    var request = new SearchRequest(SearchRequest.Types.Track, _searchTerm) { Limit = 1 };
                    searchResponse = await _spotify.Search.Item(request).ConfigureAwait(false);

                    if (_searchCache.Count > MaxSearchCacheEntries)
                    {
                        _searchCache.Clear();
                    }
                    _searchCache.TryAdd(_searchTerm, searchResponse);
                }

                if (currentGeneration != _searchGeneration)
                {
                    return; // A newer search started, ignore this result
                }

                if (searchResponse?.Tracks?.Items?.Count > 0)
                {
                    var track = searchResponse.Tracks.Items[0];

                    lock (_stateLock)
                    {
                        _trackID = track.Id;
                        _title = track.Name ?? "";
                        _artist = string.Join(", ", track.Artists?.Select(a => a.Name) ?? new List<string>());
                        _album = track.Album?.Name ?? "";
                        _imageURL = track.Album?.Images?.FirstOrDefault()?.Url;
                        _albumID = track.Album?.Id ?? "";
                        _artistID = track.Artists?.FirstOrDefault()?.Id ?? "";
                        _trackMissing = 0;
                        _num = 0;
                    }

                    await CheckLibraryAsync().ConfigureAwait(false);
                }
                else
                {
                    lock (_stateLock)
                    {
                        _trackMissing = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"TrackSearch failed: {ex.GetType().Name} - {ex.Message}");
                lock (_stateLock)
                {
                    _trackMissing = 1;
                }
            }
            finally
            {
                RefreshPanelUi();
            }
        }

        private async Task CheckLibraryAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_trackID))
                {
                    return;
                }

                var trackRequest = new LibraryCheckTracksRequest(new List<string> { _trackID });
                var trackResults = await _spotify.Library.CheckTracks(trackRequest).ConfigureAwait(false);

                var albumRequest = new LibraryCheckAlbumsRequest(new List<string> { _albumID });
                var albumResults = await _spotify.Library.CheckAlbums(albumRequest).ConfigureAwait(false);

                var artistRequest = new FollowCheckCurrentUserRequest(FollowCheckCurrentUserRequest.Type.Artist, new List<string> { _artistID });
                var artistResults = await _spotify.Follow.CheckCurrentUser(artistRequest).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _trackLIB = trackResults?.Count > 0 && trackResults[0];
                    _albumLIB = albumResults?.Count > 0 && albumResults[0];
                    _artistLIB = artistResults?.Count > 0 && artistResults[0];
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"CheckLibraryAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private Bitmap GetCachedArtwork(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            lock (_artworkLock)
            {
                if (url == _cachedArtworkUrl && _cachedArtwork != null)
                {
                    return _cachedArtwork;
                }
            }

            _ = FetchArtworkAsync(url);
            return null;
        }

        private async Task FetchArtworkAsync(string url)
        {
            try
            {
                using (var response = await _artworkHttpClient.GetAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var bitmap = new Bitmap(stream);
                            lock (_artworkLock)
                            {
                                _cachedArtwork?.Dispose();
                                _cachedArtwork = bitmap;
                                _cachedArtworkUrl = url;
                            }
                            RefreshPanelUi();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"FetchArtworkAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task SaveTrackAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_trackID))
                {
                    return;
                }

                var track = new LibrarySaveTracksRequest(new List<string> { _trackID });
                await _spotify.Library.SaveTracks(track).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _trackLIB = true;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"SaveTrackAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"SaveTrackAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to save track: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SaveTrackAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task SaveAlbumAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_albumID))
                {
                    return;
                }

                var album = new LibrarySaveAlbumsRequest(new List<string> { _albumID });
                await _spotify.Library.SaveAlbums(album).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _albumLIB = true;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"SaveAlbumAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"SaveAlbumAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to save album: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SaveAlbumAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task FollowArtistAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_artistID))
                {
                    return;
                }

                var artist = new FollowRequest(FollowRequest.Type.Artist, new List<string> { _artistID });
                await _spotify.Follow.Follow(artist).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _artistLIB = true;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"FollowArtistAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"FollowArtistAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to follow artist: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"FollowArtistAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task RemoveTrackAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_trackID))
                {
                    return;
                }

                var track = new LibraryRemoveTracksRequest(new List<string> { _trackID });
                await _spotify.Library.RemoveTracks(track).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _trackLIB = false;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"RemoveTrackAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"RemoveTrackAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to remove track: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"RemoveTrackAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task RemoveAlbumAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_albumID))
                {
                    return;
                }

                var album = new LibraryRemoveAlbumsRequest(new List<string> { _albumID });
                await _spotify.Library.RemoveAlbums(album).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _albumLIB = false;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"RemoveAlbumAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"RemoveAlbumAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to remove album: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"RemoveAlbumAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async Task UnfollowArtistAsync()
        {
            try
            {
                if (_spotify == null || string.IsNullOrWhiteSpace(_artistID))
                {
                    return;
                }

                var artist = new UnfollowRequest(UnfollowRequest.Type.Artist, new List<string> { _artistID });
                await _spotify.Follow.Unfollow(artist).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _artistLIB = false;
                }
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException ex)
            {
                mbApiInterface.MB_Trace($"UnfollowArtistAsync - Unauthorized: {ex.Message}");
                MessageBox.Show("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (APIException ex)
            {
                mbApiInterface.MB_Trace($"UnfollowArtistAsync - API Error: {ex.Response?.StatusCode} - {ex.Message}");
                MessageBox.Show("Failed to unfollow artist: " + ex.Message, "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"UnfollowArtistAsync failed: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }
}