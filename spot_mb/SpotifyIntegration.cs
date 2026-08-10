using SpotifyAPI.Web;
using System.Windows.Forms;
using SpotifyAPI.Web.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Newtonsoft.Json;
using System.Linq.Expressions; // Added for JSON serialization


namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private static SpotifyClient _spotify;
        private static int _auth, _num, _trackMissing = 0;
        private static bool _trackLIB, _albumLIB, _artistLIB = false;
        private static string _title, _album, _artist, _trackID, _albumID, _artistID, _imageURL;
        private static string _clientID = "05356b07a417487d9d8c6d0587de87a7";

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
                            mbApiInterface.MB_RefreshPanels();
                            panel.Invalidate();
                            return; // Successfully authenticated from saved token!
                        }
                        catch (Exception)
                        {
                            // Token expired/invalid, will proceed to web auth below
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to web auth if any startup checks fail
            }

            // Trigger Browser OAuth Flow
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes(120);

                var loginRequest = new LoginRequest(
                    new Uri("http://127.0.0.1:5000/callback"), _clientID, LoginRequest.ResponseType.Code)
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] { Scopes.UserLibraryModify, Scopes.UserFollowModify, Scopes.UserFollowRead, Scopes.UserLibraryRead }
                };
                var uri = loginRequest.ToUri();

                var server = new EmbedIOAuthServer(new Uri("http://127.0.0.1:5000/callback"), 5000);

                server.PkceReceived += async (sender, response) =>
                {
                    await server.Stop();

                    var initialResponse = await new OAuthClient().RequestToken(
                        new PKCETokenRequest(_clientID, response.Code, server.BaseUri, verifier)
                    );

                    var authenticator = new PKCEAuthenticator(_clientID, initialResponse, _path);

                    var config = SpotifyClientConfig.CreateDefault()
                        .WithAuthenticator(authenticator);
                    _spotify = new SpotifyClient(config);

                    var me = await _spotify.UserProfile.Current();
                    MessageBox.Show("Logged is as : " + me.DisplayName);
                    

                    // Save JSON token cleanly
                    SerializeConfig(initialResponse, _path, _rsaKey);
                    _auth = 1;// Save JSON token cleanly

                    _searchTerm = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle)
                                + " + "
                                + mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);
                    try
                    {
                        await TrackSearch();
                    }
                    catch(Exception ex)
                    {
                        MessageBox.Show(
                            "DEBUG 9 ERROR:\n\n" +
                            ex.GetType().FullName +
                            "\n\n" +
                            ex.Message +
                            "\n\n" +
                            ex.StackTrace
                        );
                    }

                    if (panel.InvokeRequired)                      // <-- added: marshal to UI thread
                    {
                        panel.Invoke((MethodInvoker)delegate
                        {
                            mbApiInterface.MB_RefreshPanels();
                            panel.Invalidate();
                        });
                    }
                    else
                    {
                        mbApiInterface.MB_RefreshPanels();
                        panel.Invalidate();
                    }
                };

                await server.Start();

                try
                {
                    BrowserUtil.Open(uri);
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
                Console.WriteLine("Auth error: " + ex.Message);
            }
            finally
            {
                mbApiInterface.MB_RefreshPanels();
                panel.Invalidate();
            }
        }

        public async Task<FullTrack> TrackSearch()
        {
            if (string.IsNullOrWhiteSpace(_searchTerm))
            {
                return null;
            }
            try
            {
                var track = await _spotify.Search.Item(
                    new SearchRequest(SearchRequest.Types.Track, _searchTerm)
                );

                _title = Truncate(
                    track.Tracks.Items[_num].Name,
                    largeBold
                );

                _artist = Truncate(
                    string.Join(
                        ", ",
                     from item in track.Tracks.Items[_num].Artists
                        select item.Name
                    ),
                    smallRegular
                );

                _album = Truncate(
                   track.Tracks.Items[_num].Album.Name,
                 smallRegular
                );

                _trackID = track.Tracks.Items[_num].Id;
                _albumID = track.Tracks.Items[_num].Album.Id;
                _artistID = track.Tracks.Items[_num].Artists[0].Id;
                _imageURL = track.Tracks.Items[_num].Album.Images[0].Url;

                var tracks = new LibraryCheckTracksRequest(
                    new List<string> {_trackID}
                );

                var albums = new LibraryCheckAlbumsRequest(
                    new List<string> { _albumID }
                );
                
                var artist = new FollowCheckCurrentUserRequest(
                    FollowCheckCurrentUserRequest.Type.Artist,
                    new List<string> { _artistID }
                );

                MessageBox.Show("LIBRARY CHECK 1: Track");

                var tracksSaved = await _spotify.Library.CheckTracks(tracks);

                MessageBox.Show("LIBRARY CHECK 2: Track worked");

                var albumsSaved = await _spotify.Library.CheckAlbums(albums);

                MessageBox.Show("LIBRARY CHECK 3: Album worked");

                var artistFollowed = await _spotify.Follow.CheckCurrentUser(artist);

                MessageBox.Show("LIBRARY CHECK 4: Artist worked");

                _trackLIB = tracksSaved[0];
                _albumLIB = albumsSaved[0];
                _artistLIB = artistFollowed[0];
                

                _trackMissing = 0;

                return null;
            }

            catch (APIException ex)
            {
                MessageBox.Show(
                    "SPOTIFY API ERROR\n\n" +
                    "Type: " + ex.GetType().FullName + "\n\n" +
                    "Status: " + ex.Response?.StatusCode + "\n\n" +
                    "Response:\n" + ex.Response + "\n\n" +
                    "Message:\n" + ex.Message
                );

                throw;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "SEARCH ERROR\n\n" +
                    "Type: " + ex.GetType().FullName +
                    "\n\nMessage:\n" + ex.Message +
                    "\n\nStack:\n" + ex.StackTrace
                );

                throw;
            }
        }
            
        public void SaveTrack()
        {
            try
            {
                var track = new LibrarySaveTracksRequest(new List<string> { _trackID });
                _spotify.Library.SaveTracks(track);
                Console.WriteLine("Track Saved.");
            }
            catch (APIUnauthorizedException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " +_artistLIB);
            }
            catch (APIException e)
            {
                Console.WriteLine("Error Status: " + e.Response);
                Console.WriteLine("Error Msg: " + e.Message);
            }
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public void SaveAlbum()
        {
            try
            {
                var album = new LibrarySaveAlbumsRequest(new List<string> { _albumID });
                _spotify.Library.SaveAlbums(album);
                Console.WriteLine("Album Saved.");
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
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public void FollowArtist()
        {
            try
            {
                var artist = new FollowRequest(FollowRequest.Type.Artist, new List<string> { _artistID });
                _spotify.Follow.Follow(artist);
                Console.WriteLine("Artist Followed.");
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
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public void RemoveTrack()
        {
            try
            {
                var track = new LibraryRemoveTracksRequest(new List<string> { _trackID });
                _spotify.Library.RemoveTracks(track);
                Console.WriteLine("Track Unsaved.");
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
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public void RemoveAlbum()
        {
            try
            {
                var album = new LibraryRemoveAlbumsRequest(new List<string> { _albumID });
                _spotify.Library.RemoveAlbums(album);
                Console.WriteLine("Album Unsaved.");
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
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public void UnfollowArtist()
        {
            try
            {
                var artist = new UnfollowRequest(UnfollowRequest.Type.Artist, new List<string> { _artistID });
                _spotify.Follow.Unfollow(artist);
                Console.WriteLine("Artist Unfollowed.");
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
            catch (System.ArgumentOutOfRangeException e)
            {
                Console.WriteLine("Song not found!");
            }
        }

        public Boolean CheckTrack(string id)
        {
            MessageBox.Show("CHECK TRACK: " + id);

            var tracks = new LibraryCheckTracksRequest(
                new List<String> {"spotify:track:" + id }
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
