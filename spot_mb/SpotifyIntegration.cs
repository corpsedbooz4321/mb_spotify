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
using System.Linq.Expressions; // Added for JSON serialization


namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static SpotifyClient _spotify;
        private static bool _codeExchanged = false;
        private static int _auth, _num, _trackMissing = 0;
        private static bool _trackLIB, _albumLIB, _artistLIB = false;
        private static string _title, _album, _artist, _trackID, _albumID, _artistID, _imageURL;
        // No longer hardcoded - each user sets their own Client ID via
        // ClientIdSetupForm (see PanelInterface.cs), since Spotify's Development
        // Mode allowlist can't scale to a shared app for many users.
        private static string _clientID;

        // Incremented every time a new track-change search is kicked off (see
        // ReceiveNotification in PanelInterface.cs). TrackSearch() captures the
        // value that was current when IT was called, and re-checks it after each
        // await. If a newer search has started in the meantime, this run is stale
        // and must NOT touch the shared UI fields or repaint - otherwise two
        // overlapping searches race and whichever happens to finish last wins,
        // even if it's the one that failed. This is what was causing the panel
        // to flash the correct track and then immediately show "No Track Found!".
        private static long _searchGeneration = 0;

        // Caches the search result for a given "title artist" query, so replaying a
        // track (or repeat mode) doesn't hit the network again for something already
        // looked up this session. Capped to avoid unbounded growth over a long session.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SearchResponse> _searchCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, SearchResponse>(StringComparer.OrdinalIgnoreCase);
        private const int MaxSearchCacheEntries = 200;

        // Holds the already-downloaded, already-resized artwork for the current track.
        // DrawPanel used to download and decode the image from scratch on every single
        // repaint (window move, focus change, etc.) - this caches it so a repaint is
        // just a cheap blit of an existing in-memory bitmap instead of a network call.
        private static string _cachedArtworkUrl;
        private static Bitmap _cachedArtwork;
        private static readonly object _artworkLock = new object();
        private static readonly HttpClient _artworkHttpClient = new HttpClient();

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
                        mbApiInterface.MB_RefreshPanels();
                        panel.Invalidate();
                    }));
                }
                else
                {
                    mbApiInterface.MB_RefreshPanels();
                    panel.Invalidate();
                }
            }
            catch (Exception)
            {
                // Ignore UI refresh failures so the plugin can continue.
            }
        }

        public void SerializeConfig(PKCETokenResponse data, string path, RSACryptoServiceProvider rsaKey)
        {
            try
            {
                if (data == null) return;

                // Serialize directly to JSON
                string json = JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
                using (StreamWriter file = new StreamWriter(path, false))
                {
                    file.Write(json);
                }
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("Error saving token file:\n" + e.Message, "Spotify Plugin Error");
            }
        }

        public PKCETokenResponse DeserializeConfig(string path, RSACryptoServiceProvider rsaKey)
        {
            try
            {
                if (File.Exists(path))
                {
                    // Read and deserialize JSON token
                    string json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<PKCETokenResponse>(json);
                }
            }
            catch (Exception e)
            {
                System.Windows.Forms.MessageBox.Show("Error reading token file:\n" + e.Message, "Spotify Plugin Error");
            }
            return null;
        }

        async void SpotifyWebAuth()
        {
            if (_authInProgress) return;

            if (string.IsNullOrWhiteSpace(_clientID))
            {
                // No Client ID configured yet - nothing to authenticate against.
                // The panel/menu should route the user to Configure() first;
                // this is just a safety net.
                return;
            }

            _authInProgress = true;

            try
            {
                if (File.Exists(_path))
                {
                    var token_response = DeserializeConfig(_path, _rsaKey);
                    if (token_response != null)
                    {
                        var authenticator = new PKCEAuthenticator(_clientID, token_response, _path);

                        var config = SpotifyClientConfig.CreateDefault()
                            .WithAuthenticator(authenticator);
                        _spotify = new SpotifyClient(config);

                        // Verify token validity
                        try
                        {
                            await _spotify.Search.Item(new SearchRequest(SearchRequest.Types.Track, "test"));
                            _auth = 1;
                            await TrackSearch();
                            return; // Successfully authenticated from saved token!
                        }
                        catch (APIException apiEx)
                        {
                            _trackMissing = 1;
                            _trackLIB = _albumLIB = _artistLIB = false;
                            _auth = 1;

                            var status = apiEx.Response?.StatusCode.ToString() ?? "unknown";
                            var body = apiEx.Response?.Body ?? "(no body)";
                            mbApiInterface.MB_Trace($"TrackSearch failed: APIException {status} - {body}");

                            RefreshPanelUi();
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to web auth if any startup checks fail
            }
            finally
            {
                if (_auth == 1)
                {
                    _authInProgress = false;
                    RefreshPanelUi();
                }
            }

            if (_auth == 1) return;
            //BrowserUtil OAuth flow
            bool browserOpened = false;
            try
            {

                var (verifier, challenge) = PKCEUtil.GenerateCodes(120);

                var loginRequest = new LoginRequest(
                    new Uri("http://127.0.0.1:5000/callback"), _clientID, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] {
                        Scopes.UserLibraryModify, Scopes.UserFollowModify, Scopes.UserFollowRead, Scopes.UserLibraryRead,
                        Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic, Scopes.PlaylistReadPrivate
                        }
                };
                var uri = loginRequest.ToUri();

                var server = new EmbedIOAuthServer(new Uri("http://127.0.0.1:5000/callback"), 5000);

                await server.Start(); // to start the server so it can listen to the callback.//

                server.PkceReceived += async (sender, response) =>
                {
                    if (_codeExchanged) return;
                    _codeExchanged = true;

                    await server.Stop();
                    try
                    {
                        var initialResponse = await new OAuthClient().RequestToken(
                            new PKCETokenRequest(_clientID, response.Code, server.BaseUri, verifier)
                        );

                        var authenticator = new PKCEAuthenticator(_clientID, initialResponse, _path);

                        var config = SpotifyClientConfig.CreateDefault()
                            .WithAuthenticator(authenticator);
                        _spotify = new SpotifyClient(config);

                        var me = await _spotify.UserProfile.Current();

                        // Save JSON token cleanly
                        SerializeConfig(initialResponse, _path, _rsaKey);
                        _auth = 1;

                        _searchTerm = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle)
                                    + " "
                                    + mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);

                        panel.BeginInvoke((Action)(async () =>
                        {
                            _authInProgress = false;
                            mbApiInterface.MB_RefreshPanels();
                            panel.Invalidate();

                            try
                            {
                                await TrackSearch();
                            }
                            catch (APIException ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
                                mbApiInterface.MB_Trace("TrackSearch (post-auth) failed: " + ex.GetType().Name + " - " + ex.Message);
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        _authInProgress = false;
                        System.Diagnostics.Debug.WriteLine($"OAuth callback failed: {ex.Message}");

                        //making the faiure being displayed and unlockk the paned instead of failing silently
                        panel.BeginInvoke((Action)(() =>
                        {
                            panel.Invalidate();
                            MessageBox.Show("Spotify authentication failed:\n" + ex.Message, "Spotify Plugin Error");
                        }));
                    }
                };

                try
                {
                    BrowserUtil.Open(uri);
                    browserOpened = true;
                }
                catch (Exception)
                {
                    Console.WriteLine("Unable to open URL, manually open: {0}", uri);
                }
            }
            catch (System.Net.WebException)
            {
                _auth = 0;
            }
            catch (Exception ex)
            {
                _auth = 0;
                _authInProgress = false;
                Console.WriteLine("Auth error: " + ex.Message);
                mbApiInterface.MB_RefreshPanels();
                panel.Invalidate();
                return;
            }
            if (!browserOpened)
            {
                //browserOpened never launched, nothing pending - CloseReason the flag
                _authInProgress = false;
                mbApiInterface.MB_RefreshPanels();
                panel.Invalidate();
            }
        }

        // True if `generation` is still the most recent TrackSearch() run. Used to
        // detect that THIS run has been superseded by a newer track-change search
        // that started while we were awaiting a network call - in which case we must
        // not write to the shared _title/_trackID/etc fields or repaint, since doing
        // so would either clobber a newer (already-correct) result, or send a
        // library-check request using an ID that no longer matches what's on screen.
        private static bool IsCurrentSearch(long generation)
        {
            return Interlocked.Read(ref _searchGeneration) == generation;
        }

        /// <summary>
        /// Downloads and pre-resizes artwork once per track, caching the result so
        /// DrawPanel (a Paint handler, which fires on every resize/focus-change/etc.)
        /// never has to hit the network or re-decode the image - it just blits
        /// whatever is currently cached.
        /// </summary>
        private async Task LoadArtworkAsync(string imageUrl, long generation)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return;
            }

            // Already cached for this exact URL - nothing to do.
            lock (_artworkLock)
            {
                if (_cachedArtworkUrl == imageUrl && _cachedArtwork != null)
                {
                    return;
                }
            }

            try
            {
                byte[] data = await _artworkHttpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);

                if (!IsCurrentSearch(generation))
                {
                    // A newer track superseded this one before the download finished -
                    // discard rather than caching artwork nobody asked for anymore.
                    return;
                }

                using (var rawImage = System.Drawing.Image.FromStream(new MemoryStream(data)))
                {
                    var resized = new Bitmap(rawImage, new Size(65, 65));

                    lock (_artworkLock)
                    {
                        _cachedArtwork?.Dispose();
                        _cachedArtwork = resized;
                        _cachedArtworkUrl = imageUrl;
                    }
                }

                if (IsCurrentSearch(generation))
                {
                    RefreshPanelUi();
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace("LoadArtworkAsync failed: " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        /// <summary>
        /// Thread-safe accessor for DrawPanel to read the current cached artwork
        /// without racing the background download/swap in LoadArtworkAsync.
        /// </summary>
        public static Bitmap GetCachedArtwork(string forImageUrl)
        {
            lock (_artworkLock)
            {
                return (_cachedArtworkUrl == forImageUrl) ? _cachedArtwork : null;
            }
        }

        public async Task<FullTrack> TrackSearch()
        {
            // Claim a generation for this run. Every call to TrackSearch() - from a
            // track change, from re-auth, from anywhere - gets its own increasing
            // number. Whichever run finishes with the HIGHEST number belongs to the
            // most recently requested track; any run that discovers a higher number
            // exists by the time one of its awaits completes knows it has been
            // superseded and bails out without touching shared state.
            long myGeneration = Interlocked.Increment(ref _searchGeneration);

            if (string.IsNullOrWhiteSpace(_searchTerm))
            {
                if (IsCurrentSearch(myGeneration))
                {
                    _trackMissing = 1;
                    _auth = 1;
                    RefreshPanelUi();
                }
                return null;
            }

            try
            {
                SearchResponse track;

                if (_searchCache.TryGetValue(_searchTerm, out var cachedTrack))
                {
                    track = cachedTrack;
                }
                else
                {
                    track = await _spotify.Search.Item(
                        new SearchRequest(SearchRequest.Types.Track, _searchTerm)
                    );

                    if (track?.Tracks?.Items != null && track.Tracks.Items.Count > 0)
                    {
                        _searchCache[_searchTerm] = track;

                        // Simple cap so a long listening session doesn't grow this
                        // unbounded. Not true LRU, just a coarse size limit.
                        if (_searchCache.Count > MaxSearchCacheEntries)
                        {
                            var oldestKey = _searchCache.Keys.FirstOrDefault();
                            if (oldestKey != null)
                            {
                                _searchCache.TryRemove(oldestKey, out _);
                            }
                        }
                    }
                }

                if (!IsCurrentSearch(myGeneration))
                {
                    // A newer track-change search started while this search was in
                    // flight. That newer run owns the shared UI state now - drop
                    // this stale result instead of racing it.
                    return null;
                }

                if (track?.Tracks?.Items == null || track.Tracks.Items.Count == 0)
                {
                    _title = _artist = _album = null;
                    _trackID = _albumID = _artistID = _imageURL = null;
                    _trackMissing = 1;
                    _trackLIB = _albumLIB = _artistLIB = false;
                    _auth = 1;
                    RefreshPanelUi();
                    return null;
                }

                // _num can be changed elsewhere (e.g. cycling through match results
                // for the current track) and is not necessarily valid for THIS track's
                // result set - a smaller/different result count would otherwise throw
                // an IndexOutOfRangeException here and get swallowed by the catch below,
                // silently showing "No Track Found!" even though the search succeeded.
                if (_num < 0 || _num >= track.Tracks.Items.Count)
                {
                    _num = 0;
                }

                var item = track.Tracks.Items[_num];

                _title = Truncate(
                    item.Name,
                    largeBold
                );

                _artist = Truncate(
                    string.Join(
                        ", ",
                     from artistItem in item.Artists
                     select artistItem.Name
                    ),
                    smallRegular
                );

                _album = Truncate(
                   item.Album.Name,
                 smallRegular
                );

                _trackID = item.Id;
                _albumID = item.Album.Id;
                _artistID = item.Artists.Count > 0 ? item.Artists[0].Id : null;
                _imageURL = (item.Album.Images != null && item.Album.Images.Count > 0)
                    ? item.Album.Images[0].Url
                    : null;

                // The search succeeded - commit and paint this now, BEFORE attempting
                // the library-check calls below. A failure in those calls (or this run
                // getting superseded mid-flight) must never undo a search that already
                // succeeded. This is the fix for the panel flashing the correct track
                // and then immediately reverting to "No Track Found!".
                _trackMissing = 0;
                _auth = 1;
                OnPlaylistWidgetTrackChanged();
                RefreshPanelUi();

                // Fire-and-forget: download and cache the artwork now, once, rather
                // than letting DrawPanel re-download it on every repaint. Not awaited
                // so the text/library-check flow below isn't held up by it; it repaints
                // the panel itself once the image is ready.
                _ = LoadArtworkAsync(_imageURL, myGeneration);

                // Library-check calls run in their OWN try/catch, decoupled from the
                // search above. They also re-validate that this run is still current -
                // both before firing the request (so a superseded run never sends a
                // check for the wrong track's IDs, which is what was producing the
                // stray 404s in the log) and before writing the result back (so a
                // slow response never overwrites a newer track's "Saved in Library"
                // state).


                try
                {
                    if (!IsCurrentSearch(myGeneration)) return null;

                    var tracks = new LibraryCheckTracksRequest(new List<string> { "spotify:track:" + _trackID });
                    var albums = new LibraryCheckAlbumsRequest(new List<string> { _albumID });
                    var artist = new FollowCheckCurrentUserRequest(
                        FollowCheckCurrentUserRequest.Type.Artist,
                        new List<string> { _artistID }
                    );

                    // Each check still fails independently (one 403 doesn't zero out
                    // the other two), but now they run CONCURRENTLY instead of one
                    // after another - three sequential round-trips became roughly one
                    // round-trip's worth of latency (the slowest of the three).
                    async Task<bool> SafeCheckTracks()
                    {
                        try { return (await _spotify.Library.CheckTracks(tracks))[0]; }
                        catch (Exception ex)
                        {
                            mbApiInterface.MB_Trace("TrackSearch (CheckTracks) failed: " + ex.GetType().Name + " - " + ex.Message);
                            return false;
                        }
                    }

                    async Task<bool> SafeCheckAlbums()
                    {
                        try { return (await _spotify.Library.CheckAlbums(albums))[0]; }
                        catch (Exception ex)
                        {
                            mbApiInterface.MB_Trace("TrackSearch (CheckAlbums) failed: " + ex.GetType().Name + " - " + ex.Message);
                            return false;
                        }
                    }

                    async Task<bool> SafeCheckArtist()
                    {
                        try { return (await _spotify.Follow.CheckCurrentUser(artist))[0]; }
                        catch (Exception ex)
                        {
                            mbApiInterface.MB_Trace("TrackSearch (CheckCurrentUser) failed: " + ex.GetType().Name + " - " + ex.Message);
                            return false;
                        }
                    }

                    var trackTask = SafeCheckTracks();
                    var albumTask = SafeCheckAlbums();
                    var artistTask = SafeCheckArtist();

                    await Task.WhenAll(trackTask, albumTask, artistTask);

                    bool trackSaved = trackTask.Result;
                    bool albumSaved = albumTask.Result;
                    bool artistFollowed = artistTask.Result;

                    if (!IsCurrentSearch(myGeneration)) return null;

                    _trackLIB = trackSaved;
                    _albumLIB = albumSaved;
                    _artistLIB = artistFollowed;
                    RefreshPanelUi();
                }
                catch (Exception ex)
                {
                    if (IsCurrentSearch(myGeneration))
                    {
                        mbApiInterface.MB_Trace("TrackSearch (library check) failed: " + ex.GetType().Name + " - " + ex.Message);
                        RefreshPanelUi();
                    }
                }

                return null;
            }
            catch (APIException apiEx)
            {
                if (IsCurrentSearch(myGeneration))
                {
                    _trackMissing = 1;
                    _trackLIB = _albumLIB = _artistLIB = false;
                    _auth = 1;

                    var status = apiEx.Response?.StatusCode.ToString() ?? "unknown";
                    var body = apiEx.Response?.Body ?? "(no body)";
                    mbApiInterface.MB_Trace($"TrackSearch (search) failed: APIException {status} - {body}");

                    RefreshPanelUi();
                }
                return null;
            }
            catch (Exception ex)
            {
                if (IsCurrentSearch(myGeneration))
                {
                    _trackMissing = 1;
                    _trackLIB = _albumLIB = _artistLIB = false;
                    _auth = 1;

                    // Log the real cause instead of silently swallowing it - this is what
                    // was making auth failures, rate limits, and indexing bugs all show up
                    // identically as a silent "No Track Found!" with no way to diagnose it.
                    mbApiInterface.MB_Trace("TrackSearch (search) failed: " + ex.GetType().Name + " - " + ex.Message);

                    RefreshPanelUi();
                }
                return null;
            }
        }

        public async void SaveTrack()
        {
            try
            {
                var track = new LibrarySaveTracksRequest(new List<string> { _trackID });
                await _spotify.Library.SaveTracks(track);
                _trackLIB = true;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public async void SaveAlbum()
        {
            try
            {
                var album = new LibrarySaveAlbumsRequest(new List<string> { _albumID });
                await _spotify.Library.SaveAlbums(album);
                _albumLIB = true;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public async void FollowArtist()
        {
            try
            {
                var artist = new FollowRequest(FollowRequest.Type.Artist, new List<string> { _artistID });
                await _spotify.Follow.Follow(artist);
                _artistLIB = true;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public async void RemoveTrack()
        {
            try
            {
                var track = new LibraryRemoveTracksRequest(new List<string> { _trackID });
                await _spotify.Library.RemoveTracks(track);
                _trackLIB = false;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public async void RemoveAlbum()
        {
            try
            {
                var album = new LibraryRemoveAlbumsRequest(new List<string> { _albumID });
                await _spotify.Library.RemoveAlbums(album);
                _albumLIB = false;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public async void UnfollowArtist()
        {
            try
            {
                var artist = new UnfollowRequest(UnfollowRequest.Type.Artist, new List<string> { _artistID });
                await _spotify.Follow.Unfollow(artist);
                _artistLIB = false;
                RefreshPanelUi();
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public Boolean CheckTrack(string id)
        {
            MessageBox.Show("CHECK TRACK: " + id);

            var tracks = new LibraryCheckTracksRequest(
                new List<String> { "spotify:track:" + id }
                );

            try
            {
                var result = _spotify.Library.CheckTracks(tracks).Result;

                MessageBox.Show(
                    "CHECK TRACK RESULT\n\n" +
                    "Count: " + result.Count + "\n" +
                    "Saved: " + result[0]
                );

                _trackLIB = result[0];
                return result[0];
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "CHECK TRACK ERROR\n\n" +
                    ex.GetType().FullName + "\n\n" +
                    ex.Message
                );

                throw;
            }
        }

        public Boolean CheckAlbum(string id)
        {
            var albums = new LibraryCheckAlbumsRequest(new List<String> { id });

            List<bool> albumsSaved = _spotify.Library.CheckAlbums(albums).Result;
            if (albumsSaved.ElementAt(0))
            {
                _albumLIB = true;
                return true;
            }
            else
            {
                _albumLIB = false;
                return false;
            }
        }

        public Boolean CheckArtist(string id)
        {
            var artist = new FollowCheckCurrentUserRequest(FollowCheckCurrentUserRequest.Type.Artist, new List<string> { id });

            List<bool> artistFollowed = _spotify.Follow.CheckCurrentUser(artist).Result;
            if (artistFollowed.ElementAt(0))
            {
                _artistLIB = true;
                return true;
            }
            else
            {
                _artistLIB = false;
                return false;
            }
        }
    }
}