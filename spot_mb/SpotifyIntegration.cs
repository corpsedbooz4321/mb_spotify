using SpotifyAPI.Web;
using System.Windows.Forms;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
        // Resized (to ARTWORK_SIZE) once when the artwork arrives, rather than being re-resized
        // from scratch on every single paint. Drawing always happens while holding _artworkLock
        // so a background fetch can never dispose this mid-draw.
        private static Bitmap _cachedArtworkResized;
        // Tracks a URL currently being downloaded so DrawPanel's frequent repaints don't kick
        // off a duplicate concurrent download for the same artwork.
        private static string _artworkFetchInProgressUrl;
        private static readonly object _artworkLock = new object();
        private static readonly HttpClient _sharedHttpClient = new HttpClient();

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

        // Several catch blocks show a MessageBox after an "await ...ConfigureAwait(false)" call,
        // which means that code is running on a background thread, not the UI thread. Showing a
        // MessageBox from a background thread can leave it unparented/mis-positioned relative to
        // the main window. This marshals it to the UI thread like RefreshPanelUi() does.
        private void ShowError(string message, string title)
        {
            try
            {
                if (panel != null && panel.InvokeRequired)
                {
                    panel.BeginInvoke((MethodInvoker)(() => MessageBox.Show(panel, message, title)));
                }
                else
                {
                    MessageBox.Show(panel, message, title);
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"ShowError failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        // As of Spotify's February 2026 Web API changes, the old per-type endpoints
        // (PUT/DELETE/GET /me/tracks[/contains], /me/albums[/contains], /me/following[/contains],
        // etc.) were removed in favor of one generic set that takes full Spotify URIs instead of
        // bare IDs: PUT /me/library, DELETE /me/library, GET /me/library/contains. See:
        // https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide
        // The installed SpotifyAPI.Web version's higher-level Library/Follow request classes were
        // seemingly only partially updated for this (Check Tracks specifically was throwing
        // "Invalid Spotify URI" for a bare ID), so all of these now go through raw HTTP calls we
        // fully control instead of relying on the SDK's request builders for this area.
        private sealed class RawApiException : Exception
        {
            public HttpStatusCode StatusCode { get; }
            public RawApiException(HttpStatusCode statusCode, string message) : base(message)
            {
                StatusCode = statusCode;
            }
        }

        // The SDK's own authenticator (PKCEAuthenticator) refreshes the access token
        // automatically when its methods are called, and writes the refreshed token back to
        // _path. Raw HTTP calls bypass that authenticator entirely, so we make one cheap SDK
        // call first to make sure the on-disk token is fresh, then read it directly.
        private async Task<string> GetValidAccessTokenAsync()
        {
            if (_spotify != null)
            {
                try
                {
                    await _spotify.UserProfile.Current().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    mbApiInterface.MB_Trace($"GetValidAccessTokenAsync: token refresh probe failed: {ex.GetType().Name} - {ex.Message}");
                }
            }

            var token = DeserializeConfig(_path, _rsaKey);
            return token?.AccessToken;
        }

        private async Task<List<bool>> CheckLibraryUrisAsync(IEnumerable<string> uris)
        {
            var uriList = (uris ?? Enumerable.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (uriList.Count == 0)
            {
                return new List<bool>();
            }

            var token = await GetValidAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new RawApiException(HttpStatusCode.Unauthorized, "No access token available");
            }

            var query = string.Join(",", uriList.Select(Uri.EscapeDataString));
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/me/library/contains?uris={query}"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using (var response = await _sharedHttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new RawApiException(response.StatusCode, $"{(int)response.StatusCode} {response.StatusCode} - {body}");
                    }

                    return JsonConvert.DeserializeObject<List<bool>>(body) ?? new List<bool>();
                }
            }
        }

        private async Task ModifyLibraryUrisAsync(HttpMethod method, IEnumerable<string> uris)
        {
            var uriList = (uris ?? Enumerable.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (uriList.Count == 0)
            {
                return;
            }

            var token = await GetValidAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new RawApiException(HttpStatusCode.Unauthorized, "No access token available");
            }

            var query = string.Join(",", uriList.Select(Uri.EscapeDataString));
            using (var request = new HttpRequestMessage(method, $"https://api.spotify.com/v1/me/library?uris={query}"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using (var response = await _sharedHttpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new RawApiException(response.StatusCode, $"{(int)response.StatusCode} {response.StatusCode} - {body}");
                    }
                }
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
                ShowError("Error saving token file:\n" + e.Message, "Spotify Plugin Error");
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
                ShowError("Error reading token file:\n" + e.Message, "Spotify Plugin Error");
            }
            return null;
        }

        // How long we'll wait for the user to complete the browser login before giving up.
        private const int AuthFlowTimeoutSeconds = 300;

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
                            _authInProgress = false;
                            RefreshPanelUi();
                            return;
                        }
                        catch (APIException apiEx)
                        {
                            // The saved token is invalid/expired and unusable - this is NOT a
                            // successful authentication, so _auth must stay 0 (previously this
                            // set _auth = 1, which put the panel in an "authenticated" state that
                            // showed a misleading "No Track Found!" message instead of prompting
                            // the user to re-authenticate).
                            mbApiInterface.MB_Trace($"Saved token validation failed: {apiEx.Response?.StatusCode} - {apiEx.Message}");
                            try
                            {
                                if (File.Exists(_path))
                                {
                                    File.Delete(_path);
                                }
                            }
                            catch (Exception deleteEx)
                            {
                                mbApiInterface.MB_Trace($"Failed to remove invalid token file: {deleteEx.GetType().Name} - {deleteEx.Message}");
                            }

                            lock (_stateLock)
                            {
                                _trackMissing = 1;
                                _trackLIB = _albumLIB = _artistLIB = false;
                                _auth = 0;
                            }
                            _spotify = null;
                            _codeExchanged = false;
                            _authInProgress = false;
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

                // Shared cleanup so a denied/errored/timed-out login can't leave the local
                // callback server running or _authInProgress stuck forever.
                async Task AbortAuthAsync(string reason)
                {
                    if (_codeExchanged) return;
                    _codeExchanged = true;

                    mbApiInterface.MB_Trace(reason);

                    try
                    {
                        await server.Stop().ConfigureAwait(false);
                    }
                    catch (Exception stopEx)
                    {
                        mbApiInterface.MB_Trace($"AbortAuthAsync: server.Stop failed: {stopEx.GetType().Name} - {stopEx.Message}");
                    }

                    _authInProgress = false;
                    RefreshPanelUi();
                }

                // Your EmbedIOAuthServer build doesn't expose an ErrorReceived event (only
                // PkceReceived), so a denied/cancelled login can't be detected the instant it
                // happens. The timeout below (AuthFlowTimeoutSeconds) is what recovers from that
                // case - it just takes until the timeout instead of firing immediately.

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
                        else
                        {
                            // No panel to marshal onto (e.g. plugin not fully initialised yet) -
                            // still need to clear the flag or every future auth attempt will
                            // silently no-op.
                            _authInProgress = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        mbApiInterface.MB_Trace($"Token exchange failed: {ex.GetType().Name} - {ex.Message}");
                        ShowError("Authentication failed:\n" + ex.Message, "Spotify Plugin Error");
                        _authInProgress = false;
                        RefreshPanelUi();
                    }
                };

                System.Diagnostics.Process.Start(uri.ToString());

                // Safety net: if the user closes the browser tab, or denies/cancels the login,
                // without it resulting in a call to PkceReceived, this timeout still recovers
                // the plugin instead of leaving it stuck waiting forever.
                _ = Task.Delay(TimeSpan.FromSeconds(AuthFlowTimeoutSeconds)).ContinueWith(async _ =>
                {
                    await AbortAuthAsync("SpotifyWebAuthAsync: timed out waiting for browser authorization").ConfigureAwait(false);
                });
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SpotifyWebAuthAsync failed: {ex.GetType().Name} - {ex.Message}");
                _authInProgress = false;
                ShowError("Authentication failed:\n" + ex.Message, "Spotify Plugin Error");
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
            if (_spotify == null || string.IsNullOrWhiteSpace(_trackID))
            {
                return;
            }

            // Each check runs and is applied independently, so an empty album/artist ID or a
            // failure on one doesn't discard the others' results.
            bool trackLib = await CheckSavedUriAsync("spotify:track:" + _trackID, "track").ConfigureAwait(false);
            bool albumLib = await CheckSavedUriAsync(
                string.IsNullOrWhiteSpace(_albumID) ? null : "spotify:album:" + _albumID, "album").ConfigureAwait(false);
            bool artistLib = await CheckSavedUriAsync(
                string.IsNullOrWhiteSpace(_artistID) ? null : "spotify:artist:" + _artistID, "artist").ConfigureAwait(false);

            lock (_stateLock)
            {
                _trackLIB = trackLib;
                _albumLIB = albumLib;
                _artistLIB = artistLib;
            }
        }

        private async Task<bool> CheckSavedUriAsync(string uri, string kind)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            try
            {
                var results = await CheckLibraryUrisAsync(new[] { uri }).ConfigureAwait(false);
                return results.Count > 0 && results[0];
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"CheckLibraryAsync ({kind}) failed: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        // Kicks off a background download if we don't already have (or aren't already fetching)
        // artwork for this URL. Safe to call on every paint - it's a no-op once cached/in-flight.
        private void EnsureArtworkLoading(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            lock (_artworkLock)
            {
                if (url == _cachedArtworkUrl && _cachedArtworkResized != null)
                {
                    return;
                }

                if (url == _artworkFetchInProgressUrl)
                {
                    // DrawPanel repaints far more often than a track changes, and previously each
                    // repaint fired its own FetchArtworkAsync while a request was still in
                    // flight, causing redundant concurrent downloads of the same image.
                    return;
                }

                _artworkFetchInProgressUrl = url;
            }

            _ = FetchArtworkAsync(url);
        }

        // Draws the cached artwork for `url`, if ready, directly onto `g`. The draw happens
        // while holding _artworkLock so a background fetch replacing/disposing the cached
        // bitmap can never race with an in-progress draw of the old one.
        private void DrawArtworkIfReady(Graphics g, string url)
        {
            lock (_artworkLock)
            {
                if (url != _cachedArtworkUrl || _cachedArtworkResized == null)
                {
                    return;
                }

                try
                {
                    g.DrawImage(_cachedArtworkResized, new Point(ARTWORK_X, ARTWORK_Y));
                }
                catch (Exception ex)
                {
                    mbApiInterface.MB_Trace($"DrawArtworkIfReady failed: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        private async Task FetchArtworkAsync(string url)
        {
            try
            {
                using (var response = await _sharedHttpClient.GetAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var downloaded = new Bitmap(stream))
                        {
                            // Resize once, here, instead of re-resizing from scratch on every
                            // single paint call in DrawPanel.
                            var resized = new Bitmap(downloaded, ARTWORK_SIZE, ARTWORK_SIZE);
                            lock (_artworkLock)
                            {
                                _cachedArtworkResized?.Dispose();
                                _cachedArtworkResized = resized;
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
            finally
            {
                lock (_artworkLock)
                {
                    if (_artworkFetchInProgressUrl == url)
                    {
                        _artworkFetchInProgressUrl = null;
                    }
                }
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

                await ModifyLibraryUrisAsync(HttpMethod.Put, new[] { "spotify:track:" + _trackID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _trackLIB = true;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"SaveTrackAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SaveTrackAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to save track: " + ex.Message, "Spotify Plugin Error");
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

                await ModifyLibraryUrisAsync(HttpMethod.Put, new[] { "spotify:album:" + _albumID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _albumLIB = true;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"SaveAlbumAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"SaveAlbumAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to save album: " + ex.Message, "Spotify Plugin Error");
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

                await ModifyLibraryUrisAsync(HttpMethod.Put, new[] { "spotify:artist:" + _artistID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _artistLIB = true;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"FollowArtistAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"FollowArtistAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to follow artist: " + ex.Message, "Spotify Plugin Error");
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

                await ModifyLibraryUrisAsync(HttpMethod.Delete, new[] { "spotify:track:" + _trackID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _trackLIB = false;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"RemoveTrackAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"RemoveTrackAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to remove track: " + ex.Message, "Spotify Plugin Error");
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

                await ModifyLibraryUrisAsync(HttpMethod.Delete, new[] { "spotify:album:" + _albumID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _albumLIB = false;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"RemoveAlbumAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"RemoveAlbumAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to remove album: " + ex.Message, "Spotify Plugin Error");
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

                await ModifyLibraryUrisAsync(HttpMethod.Delete, new[] { "spotify:artist:" + _artistID }).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _artistLIB = false;
                }
                RefreshPanelUi();
            }
            catch (RawApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                mbApiInterface.MB_Trace($"UnfollowArtistAsync - Unauthorized: {ex.Message}");
                ShowError("Authentication expired. Please re-authenticate.", "Spotify Plugin Error");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"UnfollowArtistAsync failed: {ex.GetType().Name} - {ex.Message}");
                ShowError("Failed to unfollow artist: " + ex.Message, "Spotify Plugin Error");
            }
        }
    }
}