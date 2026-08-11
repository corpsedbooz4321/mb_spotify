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
        private bool _runOnce = true;
        private static bool _authInProgress = false; // guards against triggering SpotifyWebAuth() twice
        Font largeBold, smallRegular, smallBold;
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
            _cspParams.KeyContainerName = "SPOTIFY_XML_ENC_RSA_KEY";
            _rsaKey = new RSACryptoServiceProvider(_cspParams);

            return about;
        }

        public int OnDockablePanelCreated(Control panel)
        {

            float dpiScaling = 0;

            largeBold = new Font(panel.Font.FontFamily, 9, FontStyle.Bold);
            smallRegular = new Font(panel.Font.FontFamily, 8);
            smallBold = new Font(panel.Font.FontFamily, 8, FontStyle.Bold);

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
                if (!_authInProgress)
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

                WebClient webClient = new WebClient();
                byte[] data = webClient.DownloadData(_imageURL);
                System.Drawing.Image image = System.Drawing.Image.FromStream(new MemoryStream(data));
                image = new Bitmap(image, new Size(65, 65));
                e.Graphics.DrawImage(image, new Point(10, 80));
                webClient.Dispose();


                if (_trackLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "Track Saved in Library", smallBold, new Point(80, 85), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "Track Not in Library", smallRegular, new Point(80, 85), text1);
                }

                if (_albumLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "Album Saved in Library", smallBold, new Point(80, 105), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "Album Not in Library", smallRegular, new Point(80, 105), text1);
                }

                if (_artistLIB)
                {
                    TextRenderer.DrawText(e.Graphics, "Artist Already Followed", smallBold, new Point(80, 125), text1);
                }
                else
                {
                    TextRenderer.DrawText(e.Graphics, "Artist Not Followed", smallRegular, new Point(80, 125), text1);
                }


            }
            else if (_auth == 1 && _trackMissing == 1)
            {
                TextRenderer.DrawText(e.Graphics, "No Track Found!", new Font(panel.Font.FontFamily, 12), new Point(5, 70), text1);
            }
            else if (_auth == 0)
            {
                TextRenderer.DrawText(e.Graphics, "Please Click Here to \nAuthenticate Spotify.", new Font(panel.Font.FontFamily, 14), new Point(4, 50), text1);
            }

        }

        public List<ToolStripItem> GetMenuItems()
        {
            List<ToolStripItem> list = new List<ToolStripItem>();
            ToolStripMenuItem reAuth = new ToolStripMenuItem("Re-authenticate");

            reAuth.Click += reAuthSpotify;

            list.Add(reAuth);

            return list;
        }

        public void reAuthSpotify(object sender, EventArgs e)
        {
            File.Delete(_path);
            if (!_authInProgress)
            {
                SpotifyWebAuth();
            }

            mbApiInterface.MB_RefreshPanels();
            panel.Invalidate();
        }

        private void PanelClick(object sender, EventArgs e)
        {


            MouseEventArgs me = (MouseEventArgs)e;
            if (_auth == 0 && me.Button == System.Windows.Forms.MouseButtons.Left)
            {
                if (!_authInProgress)
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

                    _trackMissing = 0;
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

                    _searchTerm = title + " " + artist;

                    if (_auth == 1)
                    {
                        await TrackSearch();
                        mbApiInterface.MB_RefreshPanels();
                    }

                    panel.Invalidate();
                    break;
            }
        }

        public bool Configure(IntPtr panelHandle)
        {
            return true;
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