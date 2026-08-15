using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Net;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace MusicBeePlugin
{

    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private Control panel;
        public int panelHeight;
        private static string _searchTerm, _path;
        private static string _clientIdPath;
        private bool _runOnce = true;
        private static bool _authInProgress = false; // guards against triggering SpotifyWebAuth() twice
        Font largeBold, smallRegular, smallBold, iconFont;
        private RSACryptoServiceProvider _rsaKey;
        CspParameters _cspParams = new CspParameters();

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "mb_Spotify_Plugin";
            about.Description = "This plugin integrates Spotify with MusicBee.";
            about.Author = "zkhcohen";
            about.TargetApplication = "Spotify Plugin";
            about.Type = PluginType.PanelView;
            about.VersionMajor = 3;
            about.VersionMinor = 1;
            about.Revision = 0;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents);
            about.ConfigurationPanelHeight = 0;

            _path = mbApiInterface.Setting_GetPersistentStoragePath() + "token.xml";
            _clientIdPath = mbApiInterface.Setting_GetPersistentStoragePath() + "clientid.txt";
            _cspParams.KeyContainerName = "SPOTIFY_XML_ENC_RSA_KEY";
            _rsaKey = new RSACryptoServiceProvider(_cspParams);

            // Each user needs their own Spotify Client ID now (see ClientIdSetupForm) -
            // load whatever was saved from a previous run, if any.
            _clientID = LoadClientId();

            return about;
        }

        private string LoadClientId()
        {
            try
            {
                if (File.Exists(_clientIdPath))
                {
                    var saved = File.ReadAllText(_clientIdPath).Trim();
                    return string.IsNullOrWhiteSpace(saved) ? null : saved;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("Error reading saved Client ID:\n" + e.Message, "Spotify Plugin Error");
            }
            return null;
        }

        private void SaveClientId(string clientId)
        {
            try
            {
                File.WriteAllText(_clientIdPath, clientId.Trim());
            }
            catch (Exception e)
            {
                MessageBox.Show("Error saving Client ID:\n" + e.Message, "Spotify Plugin Error");
            }
        }

        public int OnDockablePanelCreated(Control panel)
        {

            float dpiScaling = 0;

            largeBold = new Font(panel.Font.FontFamily, 9, FontStyle.Bold);
            smallRegular = new Font(panel.Font.FontFamily, 8);
            smallBold = new Font(panel.Font.FontFamily, 8, FontStyle.Bold);
            iconFont = new Font("Segoe UI Symbol", 14);

            panel.Paint += DrawPanel;
            panel.Click += PanelClick;

            this.panel = panel;
            panelHeight = Convert.ToInt32(145 * dpiScaling);

            return panelHeight;

        }

        public string Truncate(string text, Font font)
        {

            if (TextRenderer.MeasureText(text + "...", font).Width < panel.Width)
            {
                return text;
            }
            else
            {
                int i = text.Length;
                while (TextRenderer.MeasureText(text + "...", font).Width > panel.Width)
                {
                    text = text.Substring(0, --i);
                    if (i == 0) break;
                }

                return text = text + "...";
            }

        }

        private void DrawPanel(object sender, PaintEventArgs e)
        {

            var bg = panel.BackColor;
            var text1 = panel.ForeColor;
            var text2 = text1;
            var highlight = Color.FromArgb(2021216);
            e.Graphics.Clear(bg);
            panel.Cursor = Cursors.Hand;

            if (_runOnce)
            {
                // Only auto-attempt login if a Client ID has already been configured -
                // otherwise wait for the user to click through the setup dialog first.
                if (!string.IsNullOrWhiteSpace(_clientID) && !_authInProgress)
                {
                    SpotifyWebAuth();
                }
                _runOnce = false;
            }

            if (_auth == 1 && _trackMissing != 1)
            {

                TextRenderer.DrawText(e.Graphics, _title, largeBold, new Point(5, 10), text1);
                TextRenderer.DrawText(e.Graphics, _artist, smallRegular, new Point(5, 30), text1);
                TextRenderer.DrawText(e.Graphics, _album, smallRegular, new Point(5, 50), text1);

                // Artwork is downloaded and resized ONCE per track (see LoadArtworkAsync
                // in SpotifyIntegration.cs, kicked off right after a search succeeds).
                // DrawPanel fires on every resize/focus-change/etc., so re-downloading
                // here on every repaint (the old behavior) was a real performance
                // problem - this is now just a cheap blit of an already-decoded bitmap.
                // If the download hasn't finished yet (or failed), we simply skip
                // drawing it this pass; the panel repaints again once it's ready.
                if (!string.IsNullOrWhiteSpace(_imageURL))
                {
                    var cachedImage = GetCachedArtwork(_imageURL);
                    if (cachedImage != null)
                    {
                        try
                        {
                            e.Graphics.DrawImage(cachedImage, new Point(10, 80));
                        }
                        catch (Exception ex)
                        {
                            mbApiInterface.MB_Trace("DrawPanel (artwork) failed: " + ex.GetType().Name + " - " + ex.Message);
                        }
                    }
                }

                if (_trackLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "✓ Saved Track", smallBold, new Point(80, 85), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "♡ Save Track", smallRegular, new Point(80, 85), text1);
                }

                if (_albumLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "✓ Saved Album", smallBold, new Point(80, 105), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "♡ Save Album", smallRegular, new Point(80, 105), text1);
                }

                if (_artistLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "✓ Following", smallBold, new Point(80, 125), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "+ Follow Artist", smallRegular, new Point(80, 125), text1);
                }


            }
            else if (_auth == 1 && _trackMissing == 1)
            {
                TextRenderer.DrawText(e.Graphics, "No Track Found!", new Font(panel.Font.FontFamily, 12), new Point(5, 70), text1);
            }
            else if (_auth == 0)
            {
                if (string.IsNullOrWhiteSpace(_clientID))
                {
                    TextRenderer.DrawText(e.Graphics, "Please Click Here to \nSet Up Your Spotify App.", new Font(panel.Font.FontFamily, 14), new Point(4, 50), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "Please Click Here to \nAuthenticate Spotify.", new Font(panel.Font.FontFamily, 14), new Point(4, 50), text1);
                }
            }

        }

        public List<ToolStripItem> GetMenuItems()
        {
            List<ToolStripItem> list = new List<ToolStripItem>();

            ToolStripMenuItem setup = new ToolStripMenuItem("Set Up Spotify App...");
            setup.Click += (s, e) => Configure(IntPtr.Zero);
            list.Add(setup);

            ToolStripMenuItem reAuth = new ToolStripMenuItem("Re-authenticate");
            reAuth.Click += reAuthSpotify;
            list.Add(reAuth);

            return list;
        }

        public void reAuthSpotify(object sender, EventArgs e)
        {
            File.Delete(_path);
            _auth = 0;
            _codeExchanged = false;

            if (string.IsNullOrWhiteSpace(_clientID))
            {
                Configure(IntPtr.Zero);
            }
            else if (!_authInProgress)
            {
                SpotifyWebAuth();
            }

            mbApiInterface.MB_RefreshPanels();
            panel.Invalidate();
        }

        /// <summary>
        /// Opens the Client ID setup dialog. Called from the plugin's Configure entry
        /// point (MusicBee's plugin settings) and from the panel when no Client ID has
        /// been set yet. Saving a new/changed Client ID clears any existing session,
        /// since a token issued for a different Spotify app can't be reused.
        /// </summary>
        public bool Configure(IntPtr panelHandle)
        {
            using (var form = new ClientIdSetupForm(_clientID))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    var newClientId = form.ClientId;

                    if (!string.IsNullOrWhiteSpace(newClientId) && newClientId != _clientID)
                    {
                        _clientID = newClientId;
                        SaveClientId(_clientID);

                        // Different app = different credentials required. Drop any
                        // saved token and force a fresh login against the new app.
                        if (File.Exists(_path))
                        {
                            File.Delete(_path);
                        }
                        _auth = 0;
                        _codeExchanged = false;
                        _spotify = null;
                        _trackMissing = 1;

                        mbApiInterface.MB_RefreshPanels();
                        panel?.Invalidate();
                    }
                }
            }

            return true;
        }

        private void PanelClick(object sender, EventArgs e)
        {


            MouseEventArgs me = (MouseEventArgs)e;
            if (_auth == 0 && me.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (string.IsNullOrWhiteSpace(_clientID))
                {
                    Configure(IntPtr.Zero);
                }
                else if (!_authInProgress)
                {
                    SpotifyWebAuth();
                }
                _trackMissing = 1;

                panel.Invalidate();

            }
            else if (_auth == 1 && me.Button == System.Windows.Forms.MouseButtons.Left)
            {

                Point point = panel.PointToClient(Cursor.Position);
                float currentPosX = point.X;
                float currentPosY = point.Y;


                if (point.X > 80 && point.X < this.panel.Width && point.Y < 140 && point.Y > 130)
                {

                    if (_artistLIB)
                    {
                        UnfollowArtist();
                        panel.Invalidate();
                    }
                    else
                    {
                        FollowArtist();
                        panel.Invalidate();
                    }

                }
                else if (point.X > 80 && point.X < this.panel.Width && point.Y < 120 && point.Y > 110)
                {

                    if (_albumLIB)
                    {
                        RemoveAlbum();
                        panel.Invalidate();
                    }
                    else
                    {
                        SaveAlbum();
                        panel.Invalidate();
                    }

                }
                else if (point.X > 80 && point.X < this.panel.Width && point.Y < 100 && point.Y > 90)
                {

                    if (_trackLIB)
                    {
                        RemoveTrack();
                        panel.Invalidate();
                    }
                    else
                    {
                        SaveTrack();
                        panel.Invalidate();
                    }

                }

            }

        }

        public async void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {

            switch (type)
            {

                case NotificationType.TrackChanged:

                    _num = 0;

                    // Read the tags for the NEW track first, and set _searchTerm
                    // BEFORE calling TrackSearch(). Previously TrackSearch() was
                    // called here using the stale _searchTerm from the prior track,
                    // then _searchTerm was updated and searched again - causing a
                    // flash of the wrong (or no) result before the correct one loaded.
                    string title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
                    string artist = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);

                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                    {
                        // No usable tag data for this track - show "not found" and stop,
                        // rather than searching with an empty/stale term.
                        _trackMissing = 1;
                        RefreshPanelUi();
                        return;
                    }

                    string newSearchTerm = title + " " + artist;

                    // MusicBee can fire TrackChanged more than once for what is, from
                    // the plugin's point of view, the same track (e.g. a metadata or
                    // playback-state event arriving right after the track-id event).
                    // If we already have a correctly displayed match for this exact
                    // title+artist, searching again just creates another concurrent
                    // TrackSearch() run for no reason - each one is safe on its own
                    // thanks to the generation guard, but skipping the redundant call
                    // avoids the extra API traffic entirely.
                    if (newSearchTerm == _searchTerm && _trackMissing == 0)
                    {
                        return;
                    }

                    _searchTerm = newSearchTerm;
                    _trackMissing = 0;

                    if (_auth == 1)
                    {
                        await TrackSearch();
                        mbApiInterface.MB_RefreshPanels();
                    }

                    panel.Invalidate();
                    break;
            }
        }

        public void SaveSettings()
        {
        }

        public void Close(PluginCloseReason reason)
        {
        }

        public void Uninstall()
        {
        }

    }

}