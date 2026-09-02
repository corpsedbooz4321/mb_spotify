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
        private static bool _authInProgress = false;
        private Font largeBold, smallRegular, smallBold, iconFont;
        private RSACryptoServiceProvider _rsaKey;
        CspParameters _cspParams = new CspParameters();

        // Layout constants (replaces magic numbers)
        private const int PANEL_HEIGHT = 145;
        private const int TITLE_Y = 10;
        private const int ARTIST_Y = 30;
        private const int ALBUM_Y = 50;
        private const int ARTWORK_X = 10;
        private const int ARTWORK_Y = 80;
        private const int ACTION_TEXT_X = 80;
        private const int SAVE_TRACK_Y = 85;
        private const int SAVE_ALBUM_Y = 105;
        private const int FOLLOW_ARTIST_Y = 125;
        private const int TEXT_OFFSET_X = 5;
        private const int SETUP_TEXT_Y = 50;
        private const int NO_TRACK_Y = 70;

        private const int FONT_SIZE_LARGE = 12;
        private const int FONT_SIZE_MEDIUM = 9;
        private const int FONT_SIZE_SMALL = 8;
        private const int FONT_SIZE_ICON = 14;

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
            try
            {
                // Initialize fonts
                largeBold = new Font(panel.Font.FontFamily, FONT_SIZE_MEDIUM, FontStyle.Bold);
                smallRegular = new Font(panel.Font.FontFamily, FONT_SIZE_SMALL);
                smallBold = new Font(panel.Font.FontFamily, FONT_SIZE_SMALL, FontStyle.Bold);
                iconFont = new Font("Segoe UI Symbol", FONT_SIZE_ICON);

                panel.Paint += DrawPanel;
                panel.Click += PanelClick;

                this.panel = panel;
                panelHeight = PANEL_HEIGHT;

                return panelHeight;
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"OnDockablePanelCreated failed: {ex.GetType().Name} - {ex.Message}");
                return PANEL_HEIGHT;
            }
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

                return text + "...";
            }
        }

        private void DrawPanel(object sender, PaintEventArgs e)
        {
            try
            {
                var bg = panel.BackColor;
                var text1 = panel.ForeColor;
                var highlight = Color.FromArgb(2021216);
                e.Graphics.Clear(bg);
                panel.Cursor = Cursors.Hand;

                if (_runOnce)
                {
                    if (!string.IsNullOrWhiteSpace(_clientID) && !_authInProgress)
                    {
                        _ = SpotifyWebAuthAsync();
                    }
                    _runOnce = false;
                }

                if (_auth == 1 && _trackMissing != 1)
                {
                    DrawPlaylistWidget(e.Graphics);

                    TextRenderer.DrawText(e.Graphics, _title, largeBold, new Point(TEXT_OFFSET_X, TITLE_Y), text1);
                    TextRenderer.DrawText(e.Graphics, _artist, smallRegular, new Point(TEXT_OFFSET_X, ARTIST_Y), text1);
                    TextRenderer.DrawText(e.Graphics, _album, smallRegular, new Point(TEXT_OFFSET_X, ALBUM_Y), text1);

                    if (!string.IsNullOrWhiteSpace(_imageURL))
                    {
                        var cachedImage = GetCachedArtwork(_imageURL);
                        if (cachedImage != null)
                        {
                            try
                            {
                                e.Graphics.DrawImage(cachedImage, new Point(ARTWORK_X, ARTWORK_Y));
                            }
                            catch (Exception ex)
                            {
                                mbApiInterface.MB_Trace($"DrawPanel (artwork) failed: {ex.GetType().Name} - {ex.Message}");
                            }
                        }
                    }

                    if (_trackLIB)
                    {
                        TextRenderer.DrawText(e.Graphics, "✓ Saved Track", smallBold, new Point(ACTION_TEXT_X, SAVE_TRACK_Y), text1);
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, "♡ Save Track", smallRegular, new Point(ACTION_TEXT_X, SAVE_TRACK_Y), text1);
                    }

                    if (_albumLIB)
                    {
                        TextRenderer.DrawText(e.Graphics, "✓ Saved Album", smallBold, new Point(ACTION_TEXT_X, SAVE_ALBUM_Y), text1);
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, "♡ Save Album", smallRegular, new Point(ACTION_TEXT_X, SAVE_ALBUM_Y), text1);
                    }

                    if (_artistLIB)
                    {
                        TextRenderer.DrawText(e.Graphics, "✓ Following", smallBold, new Point(ACTION_TEXT_X, FOLLOW_ARTIST_Y), text1);
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, "+ Follow Artist", smallRegular, new Point(ACTION_TEXT_X, FOLLOW_ARTIST_Y), text1);
                    }
                }
                else if (_auth == 1 && _trackMissing == 1)
                {
                    TextRenderer.DrawText(e.Graphics, "No Track Found!", new Font(panel.Font.FontFamily, FONT_SIZE_LARGE), new Point(TEXT_OFFSET_X, NO_TRACK_Y), text1);
                }
                else if (_auth == 0)
                {
                    if (string.IsNullOrWhiteSpace(_clientID))
                    {
                        TextRenderer.DrawText(e.Graphics, "Please Click Here to \nSet Up Your Spotify App.", new Font(panel.Font.FontFamily, FONT_SIZE_LARGE), new Point(4, SETUP_TEXT_Y), text1);
                    }
                    else
                    {
                        TextRenderer.DrawText(e.Graphics, "Please Click Here to \nAuthenticate Spotify.", new Font(panel.Font.FontFamily, FONT_SIZE_LARGE), new Point(4, SETUP_TEXT_Y), text1);
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"DrawPanel failed: {ex.GetType().Name} - {ex.Message}");
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
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                _auth = 0;
                _codeExchanged = false;

                if (string.IsNullOrWhiteSpace(_clientID))
                {
                    Configure(IntPtr.Zero);
                }
                else if (!_authInProgress)
                {
                    _ = SpotifyWebAuthAsync();
                }

                mbApiInterface.MB_RefreshPanels();
                panel?.Invalidate();
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"reAuthSpotify failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public bool Configure(IntPtr panelHandle)
        {
            try
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
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Configure failed: {ex.GetType().Name} - {ex.Message}");
            }

            return true;
        }

        private void PanelClick(object sender, EventArgs e)
        {
            try
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
                        _ = SpotifyWebAuthAsync();
                    }
                    _trackMissing = 1;
                    panel.Invalidate();
                }
                else if (_auth == 1 && me.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Point clickPoint = panel.PointToClient(Cursor.Position);
                    if (HandlePlaylistWidgetClick(clickPoint))
                    {
                        panel.Invalidate();
                        return;
                    }

                    Point point = panel.PointToClient(Cursor.Position);

                    // Follow/Unfollow Artist
                    if (point.X > ACTION_TEXT_X && point.X < this.panel.Width && point.Y < 140 && point.Y > 130)
                    {
                        if (_artistLIB)
                        {
                            _ = UnfollowArtistAsync();
                            panel.Invalidate();
                        }
                        else
                        {
                            _ = FollowArtistAsync();
                            panel.Invalidate();
                        }
                    }
                    // Save/Remove Album
                    else if (point.X > ACTION_TEXT_X && point.X < this.panel.Width && point.Y < 120 && point.Y > 110)
                    {
                        if (_albumLIB)
                        {
                            _ = RemoveAlbumAsync();
                            panel.Invalidate();
                        }
                        else
                        {
                            _ = SaveAlbumAsync();
                            panel.Invalidate();
                        }
                    }
                    // Save/Remove Track
                    else if (point.X > ACTION_TEXT_X && point.X < this.panel.Width && point.Y < 100 && point.Y > 90)
                    {
                        if (_trackLIB)
                        {
                            _ = RemoveTrackAsync();
                            panel.Invalidate();
                        }
                        else
                        {
                            _ = SaveTrackAsync();
                            panel.Invalidate();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"PanelClick failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        public async void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (panel == null)
            {
                return;
            }

            switch (type)
            {
                case NotificationType.TrackChanged:
                    try
                    {
                        _num = 0;

                        string title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
                        string artist = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist);

                        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                        {
                            _trackMissing = 1;
                            RefreshPanelUi();
                            return;
                        }

                        string newSearchTerm = title + " " + artist;

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
                    }
                    catch (Exception ex)
                    {
                        mbApiInterface.MB_Trace($"ReceiveNotification.TrackChanged failed: {ex.GetType().Name} - {ex.Message}");
                    }
                    break;
            }
        }

        public void SaveSettings()
        {
        }

        public void Close(PluginCloseReason reason)
        {
            DisposeFonts();
            DisposeCachedArtwork();
        }

        public void Uninstall()
        {
            DisposeFonts();
            DisposeCachedArtwork();
        }

        private void DisposeFonts()
        {
            try
            {
                largeBold?.Dispose();
                smallRegular?.Dispose();
                smallBold?.Dispose();
                iconFont?.Dispose();
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"DisposeFonts failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private void DisposeCachedArtwork()
        {
            try
            {
                lock (_artworkLock)
                {
                    _cachedArtwork?.Dispose();
                    _cachedArtwork = null;
                    _cachedArtworkUrl = null;
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"DisposeCachedArtwork failed: {ex.GetType().Name} - {ex.Message}");
            }
        }
    }
}