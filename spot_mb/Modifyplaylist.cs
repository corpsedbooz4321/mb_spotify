using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using SpotifyAPI.Web;

namespace MusicBeePlugin
{
	public partial class Plugin
	{
		private static bool _playlistDropdownOpen = false;
		private static SimplePlaylist _selectedPlaylist = null;
		private static bool _trackInSelectedPlaylist = false;
		private static bool _playlistMembershipKnown = false; // false while the live check is in flight
		private static List<SimplePlaylist> _dropdownPlaylists = null; // only populated while the dropdown is open
		private static bool _playlistActionInProgress = false; // guards +/- and Create against double-clicks

		private static readonly Dictionary<int, Pen> _penCache = new Dictionary<int, Pen>();
		private static readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
		private static readonly Dictionary<(string name, int panelWidth), string> _truncateCache = new Dictionary<(string, int), string>();

		private static Pen GetPen(Color color)
		{
			int key = color.ToArgb();
			if (!_penCache.TryGetValue(key, out var pen))
			{
				pen = new Pen(color);
				_penCache[key] = pen;
			}
			return pen;
		}

		private static Brush GetBrush(Color color)
		{
			int key = color.ToArgb();
			if (!_brushCache.TryGetValue(key, out var brush))
			{
				brush = new SolidBrush(color);
				_brushCache[key] = brush;
			}
			return brush;
		}

		private string TruncateCached(string name, Font font)
		{
			var key = (name, panel.Width);
			if (_truncateCache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			var truncated = Truncate(name, font);

			if (_truncateCache.Count > 64)
			{
				_truncateCache.Clear();
			}
			_truncateCache[key] = truncated;
			return truncated;
		}

		private Size MeasureLabel(string text, Font font)
		{
			using (var deviceContext = panel.CreateGraphics())
			{
				return TextRenderer.MeasureText(deviceContext, text, font, Size.Empty, TextFormatFlags.NoPadding);
			}
		}

		private Size PlaylistLabelSize => MeasureLabel("Playlist", smallRegular);

		private int SpacingUnit => Math.Max(2, PlaylistLabelSize.Height / 4);

		private int DropdownRowHeight => MeasureLabel("Create Playlist", smallBold).Height + SpacingUnit;

		private Rectangle PlaylistWidgetBounds
		{
			get
			{
				int height = PlaylistLabelSize.Height + SpacingUnit;
				// playlist name, not just the collapsed-state word itself.
				int width = PlaylistLabelSize.Width * 2;
				int margin = SpacingUnit * 2; // inset from the panel's top/right edges
				return new Rectangle(panel.Width - width - margin, margin, width, height);
			}
		}

		private Rectangle PlaylistActionRowBounds =>
			new Rectangle(PlaylistWidgetBounds.X, PlaylistWidgetBounds.Bottom + SpacingUnit,
				PlaylistWidgetBounds.Width, PlaylistLabelSize.Height + SpacingUnit / 2);

		private Rectangle PlaylistDropdownBounds
		{
			get
			{
				int rows = 1 + (_dropdownPlaylists?.Count ?? 0); // "Create Playlist" + playlists
				int rowHeight = DropdownRowHeight;
				int width = Math.Max(
					MeasureLabel("Create Playlist", smallBold).Width + SpacingUnit * 4,
					PlaylistWidgetBounds.Width * 2);
				int top = _selectedPlaylist != null
					? PlaylistActionRowBounds.Bottom + SpacingUnit
					: PlaylistWidgetBounds.Bottom + SpacingUnit;
				return new Rectangle(panel.Width - width - SpacingUnit * 2, top, width, rows * rowHeight);
			}
		}

		private void DrawPlaylistWidget(Graphics g)
		{
			var fg = panel.ForeColor;
			var widget = PlaylistWidgetBounds;

			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), widget);

			if (_selectedPlaylist == null)
			{
				TextRenderer.DrawText(g, "Playlist", smallRegular, widget, fg,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			}
			else
			{
				var name = TruncateCached(_selectedPlaylist.Name, smallRegular);

				var nameRect = new Rectangle(widget.X + SpacingUnit, widget.Y, widget.Width - SpacingUnit * 2, widget.Height);
				TextRenderer.DrawText(g, name, smallRegular, nameRect, fg,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

				string actionLabel = _trackInSelectedPlaylist ? "Remove" : "Add";
				DrawActionButton(g, ActionButtonBounds(widget), actionLabel, _playlistMembershipKnown, fg);

				DrawActionButton(g, RefreshButtonBounds(widget), "\u21BB", _playlistMembershipKnown, fg);
			}

			if (_playlistDropdownOpen)
			{
				DrawPlaylistDropdown(g);
			}
		}

		private void DrawActionButton(Graphics g, Rectangle bounds, string text, bool clickable, Color fg)
		{
			int borderAlpha = clickable ? 60 : 25;
			var textColor = clickable ? fg : Color.FromArgb(90, fg);

			g.DrawRectangle(GetPen(Color.FromArgb(borderAlpha, fg)), bounds);
			TextRenderer.DrawText(g, text, smallRegular, bounds, textColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		private Rectangle ActionButtonBounds(Rectangle widget)
		{
			string text = _trackInSelectedPlaylist ? "Remove" : "Add";
			var measured = MeasureLabel(text, smallRegular);
			int width = measured.Width + SpacingUnit * 2;
			int height = PlaylistActionRowBounds.Height + SpacingUnit;
			return new Rectangle(widget.Right - width, PlaylistActionRowBounds.Y - SpacingUnit / 2, width, height);
		}

		private Rectangle RefreshButtonBounds(Rectangle widget)
		{
			var actionBounds = ActionButtonBounds(widget);
			int size = actionBounds.Height;
			return new Rectangle(actionBounds.X - SpacingUnit - size, actionBounds.Y, size, size);
		}

		private void DrawPlaylistDropdown(Graphics g)
		{
			var fg = panel.ForeColor;
			var bounds = PlaylistDropdownBounds;
			int rowHeight = DropdownRowHeight;

			// Plain rectangle, same reasoning as the widget above - rounding is
			// reserved for the +/- buttons specifically.
			g.FillRectangle(GetBrush(panel.BackColor), bounds);
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), bounds);

			int rowY = bounds.Y;
			var createRect = new Rectangle(bounds.X, rowY, bounds.Width, rowHeight);
			TextRenderer.DrawText(g, "Create Playlist", smallBold, createRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
			rowY += rowHeight;

			if (_dropdownPlaylists != null)
			{
				foreach (var pl in _dropdownPlaylists)
				{
					var rowRect = new Rectangle(bounds.X, rowY, bounds.Width, rowHeight);
					var label = TruncateCached(pl.Name, smallRegular);
					TextRenderer.DrawText(g, label, smallRegular, rowRect, fg,
						TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
					rowY += rowHeight;
				}
			}
		}

		private bool HandlePlaylistWidgetClick(Point clickPoint)
		{
			if (_playlistDropdownOpen)
			{
				var dropdown = PlaylistDropdownBounds;

				if (dropdown.Contains(clickPoint))
				{
					int relativeY = clickPoint.Y - dropdown.Y;
					int rowIndex = relativeY / DropdownRowHeight;

					_playlistDropdownOpen = false;

					if (rowIndex == 0)
					{
						OpenCreatePlaylistPrompt();
					}
					else if (_dropdownPlaylists != null && rowIndex - 1 < _dropdownPlaylists.Count)
					{
						SelectPlaylist(_dropdownPlaylists[rowIndex - 1]);
					}

					panel.Invalidate();
					return true;
				}

				_playlistDropdownOpen = false;
				_dropdownPlaylists = null;
				panel.Invalidate();
				return true;
			}

			var widget = PlaylistWidgetBounds;

			if (_selectedPlaylist != null)
			{
				var refreshHit = RefreshButtonBounds(widget);
				if (refreshHit.Contains(clickPoint))
				{
					RefreshCurrentPlaylistMembership();
					return true;
				}

				var actionHit = ActionButtonBounds(widget);

				if (actionHit.Contains(clickPoint))
				{
					if (_playlistMembershipKnown)
					{
						if (_trackInSelectedPlaylist)
						{
							RemoveCurrentTrackFromSelectedPlaylist();
						}
						else
						{
							AddCurrentTrackToSelectedPlaylist();
						}
					}
					return true;
				}
			}

			if (widget.Contains(clickPoint))
			{
				OpenPlaylistDropdown();
				return true;
			}

			return false;
		}

		private static List<SimplePlaylist> _dropdownPlaylistsCache = null;

		private async void OpenPlaylistDropdown()
		{
			_playlistDropdownOpen = true;

			if (_dropdownPlaylistsCache != null)
			{
				// Already fetched this session serve straight from cache no round work.
				_dropdownPlaylists = _dropdownPlaylistsCache;
				panel.Invalidate();
				RefreshPanelUi();
				return;
			}

			_dropdownPlaylists = null; // show just "Create Playlist" while this session's first fetch is in progresss
			panel.Invalidate();

			try
			{
				var page = await _spotify.Playlists.CurrentUsers().ConfigureAwait(false);
				var first3 = new List<SimplePlaylist>();

				if (page?.Items != null)
				{
					foreach (var pl in page.Items)
					{
						first3.Add(pl);
						if (first3.Count >= 3) break;
					}
				}

				_dropdownPlaylistsCache = first3;
				_dropdownPlaylists = first3;
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("OpenPlaylistDropdown (fetch playlists) failed: " + ex.GetType().Name + " - " + ex.Message);
				_dropdownPlaylists = new List<SimplePlaylist>();

			}

			RefreshPanelUi();
		}

		private void SelectPlaylist(SimplePlaylist playlist)
		{
			_selectedPlaylist = playlist;
			_playlistMembershipKnown = false;
			_trackInSelectedPlaylist = false;
			RefreshPanelUi();

			_ = RefreshMembershipForCurrentTrackAsync(playlist);
		}

		private void OnPlaylistWidgetTrackChanged()
		{
			if (_selectedPlaylist == null)
			{
				return;
			}

			_playlistMembershipKnown = false;
			_trackInSelectedPlaylist = false;
			RefreshPanelUi();

			_ = RefreshMembershipForCurrentTrackAsync(_selectedPlaylist);
		}

		private void RefreshCurrentPlaylistMembership()
		{
			if (_selectedPlaylist == null)
			{
				return;
			}

			_playlistMembershipKnown = false;
			RefreshPanelUi();

			_ = RefreshMembershipForCurrentTrackAsync(_selectedPlaylist);
		}

		private async Task RefreshMembershipForCurrentTrackAsync(SimplePlaylist playlist)
		{
			if (string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			var myPlaylist = playlist;
			var myTrackId = _trackID; // capture a check for the old track finishing late must not overwrite a newer one state
			string trackUri = "spotify:track:" + myTrackId;

			try
			{
				bool inPlaylist = await IsTrackInPlaylistAsync(playlist.Id, trackUri).ConfigureAwait(false);

				if (!ReferenceEquals(_selectedPlaylist, myPlaylist) || _trackID != myTrackId)
				{
					return;
				}

				_trackInSelectedPlaylist = inPlaylist;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("RefreshMembershipForCurrentTrackAsync failed: " + ex.GetType().Name + " - " + ex.Message);
				if (ReferenceEquals(_selectedPlaylist, myPlaylist) && _trackID == myTrackId)
				{
					RefreshPanelUi();
				}
			}
		}

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

		private async Task<bool> IsTrackInPlaylistAsync(string playlistId, string trackUri)
		{
			var request = new PlaylistGetItemsRequest(PlaylistGetItemsRequest.AdditionalTypes.Track)
			{
				Limit = 100,
				Offset = 0,
				Fields = { "items(track(uri))", "next" }
			};

			while (true)
			{
				var page = await _spotify.Playlists.GetItems(playlistId, request).ConfigureAwait(false);

				if (page?.Items != null)
				{
					foreach (var entry in page.Items)
					{
						var fullTrack = ExtractFullTrack(entry);
						if (fullTrack != null &&
							string.Equals(fullTrack.Uri, trackUri, StringComparison.OrdinalIgnoreCase))
						{
							return true;
						}
					}
				}

				if (string.IsNullOrEmpty(page?.Next))
				{
					return false;
				}

				request.Offset = (request.Offset ?? 0) + (request.Limit ?? 100);
			}
		}

		private async void AddCurrentTrackToSelectedPlaylist()
		{
			if (_playlistActionInProgress || _selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}
			_playlistActionInProgress = true;

			try
			{
				var request = new PlaylistAddItemsRequest(new List<string> { "spotify:track:" + _trackID });
				await _spotify.Playlists.AddItems(_selectedPlaylist.Id, request).ConfigureAwait(false);

				_trackInSelectedPlaylist = true;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("AddCurrentTrackToSelectedPlaylist failed: " + ex.GetType().Name + " - " + ex.Message);
			}
			finally
			{
				_playlistActionInProgress = false;
			}
		}

		private static readonly HttpClient _rawApiHttpClient = new HttpClient();

		private async Task<bool> RemoveTrackFromPlaylistViaRawApiAsync(string playlistId, string trackUri)
		{
			await _spotify.UserProfile.Current().ConfigureAwait(false);

			var token = DeserializeConfig(_path, _rsaKey);
			if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
			{
				mbApiInterface.MB_Trace("RemoveTrackFromPlaylistViaRawApiAsync: no access token available");
				return false;
			}

			var body = JsonConvert.SerializeObject(new
			{
				items = new[] { new { uri = trackUri } }
			});

			using (var request = new HttpRequestMessage(HttpMethod.Delete, $"https://api.spotify.com/v1/playlists/{playlistId}/items"))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");

				var response = await _rawApiHttpClient.SendAsync(request).ConfigureAwait(false);
				var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				if (!response.IsSuccessStatusCode)
				{
					mbApiInterface.MB_Trace($"RemoveTrackFromPlaylistViaRawApiAsync failed: {(int)response.StatusCode} {response.StatusCode} - {responseBody}");
					return false;
				}

				return true;
			}
		}

		private async void RemoveCurrentTrackFromSelectedPlaylist()
		{
			if (_playlistActionInProgress || _selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}
			_playlistActionInProgress = true;

			try
			{
				bool removed = await RemoveTrackFromPlaylistViaRawApiAsync(_selectedPlaylist.Id, "spotify:track:" + _trackID).ConfigureAwait(false);

				if (removed)
				{
					_trackInSelectedPlaylist = false;
					_playlistMembershipKnown = true;
					RefreshPanelUi();
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("RemoveCurrentTrackFromSelectedPlaylist failed: " + ex.GetType().Name + " - " + ex.Message);
			}
			finally
			{
				_playlistActionInProgress = false;
			}
		}

		private void OpenCreatePlaylistPrompt()
		{
			using (var form = new Form
			{
				Text = "Create Playlist",
				FormBorderStyle = FormBorderStyle.FixedDialog,
				MaximizeBox = false,
				MinimizeBox = false,
				StartPosition = FormStartPosition.CenterParent,
				ClientSize = new Size(300, 110)
			})
			{
				var label = new Label { Left = 12, Top = 12, Width = 276, Text = "Playlist name:" };
				var nameBox = new TextBox { Left = 12, Top = 34, Width = 276 };
				var okButton = new Button { Text = "Create", Left = 132, Top = 70, Width = 75, DialogResult = DialogResult.OK };
				var cancelButton = new Button { Text = "Cancel", Left = 213, Top = 70, Width = 75, DialogResult = DialogResult.Cancel };

				okButton.Click += (s, e) =>
				{
					if (string.IsNullOrWhiteSpace(nameBox.Text))
					{
						MessageBox.Show(form, "Please enter a name.", "Create Playlist");
						form.DialogResult = DialogResult.None;
					}
				};

				form.Controls.Add(label);
				form.Controls.Add(nameBox);
				form.Controls.Add(okButton);
				form.Controls.Add(cancelButton);
				form.AcceptButton = okButton;
				form.CancelButton = cancelButton;

				if (form.ShowDialog() == DialogResult.OK)
				{
					_ = CreatePlaylistAsync(nameBox.Text.Trim());
				}
			}
		}

		private async Task CreatePlaylistAsync(string name)
		{
			try
			{
				var request = new PlaylistCreateRequest(name);
				await _spotify.Playlists.Create(null, request).ConfigureAwait(false);

				_selectedPlaylist = null;
				_playlistMembershipKnown = false;

				_dropdownPlaylistsCache = null;

				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("CreatePlaylistAsync failed: " + ex.GetType().Name + " - " + ex.Message);
				MessageBox.Show("Couldn't create the playlist:\n" + ex.Message, "Spotify Plugin Error");
			}
		}
	}
}