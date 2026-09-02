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
		private static bool _playlistMembershipKnown = false;
		private static List<SimplePlaylist> _dropdownPlaylists = null;
		private static bool _playlistActionInProgress = false;
		private static List<SimplePlaylist> _dropdownPlaylistsCache = null;

		private static readonly Dictionary<(string playlistId, string trackUri), bool> _localMembershipOverrides =
			new Dictionary<(string, string), bool>();

		private static readonly Dictionary<string, HashSet<string>> _playlistTrackUriCache =
			new Dictionary<string, HashSet<string>>();
		private static readonly Dictionary<string, DateTime> _playlistTrackUriCacheTimestamp =
			new Dictionary<string, DateTime>();
		private static readonly TimeSpan PlaylistTrackCacheTtl = TimeSpan.FromMinutes(5);

		private static readonly Dictionary<int, Pen> _penCache = new Dictionary<int, Pen>();
		private static readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
		private static readonly Dictionary<(string name, int panelWidth), string> _truncateCache =
			new Dictionary<(string, int), string>();

		private const int MaxGdiCacheEntries = 64;

		private static Pen GetPen(Color color)
		{
			int key = color.ToArgb();
			if (!_penCache.TryGetValue(key, out var pen))
			{
				if (_penCache.Count > MaxGdiCacheEntries)
				{
					foreach (var p in _penCache.Values) p.Dispose();
					_penCache.Clear();
				}

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
				if (_brushCache.Count > MaxGdiCacheEntries)
				{
					foreach (var b in _brushCache.Values) b.Dispose();
					_brushCache.Clear();
				}

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
			try
			{
				using (var deviceContext = panel.CreateGraphics())
				{
					return TextRenderer.MeasureText(deviceContext, text, font, Size.Empty, TextFormatFlags.NoPadding);
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"MeasureLabel failed: {ex.GetType().Name} - {ex.Message}");
				return Size.Empty;
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
				int width = PlaylistLabelSize.Width * 2;
				int margin = SpacingUnit * 2;
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
				int rows = 1 + (_dropdownPlaylists?.Count ?? 0);
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
			try
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
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"DrawPlaylistWidget failed: {ex.GetType().Name} - {ex.Message}");
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
			try
			{
				var fg = panel.ForeColor;
				var bounds = PlaylistDropdownBounds;
				int rowHeight = DropdownRowHeight;

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
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"DrawPlaylistDropdown failed: {ex.GetType().Name} - {ex.Message}");
			}
		}

		private bool HandlePlaylistWidgetClick(Point clickPoint)
		{
			try
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
				if (widget.Contains(clickPoint))
				{
					if (_selectedPlaylist == null)
					{
						_playlistDropdownOpen = true;
						_ = LoadDropdownPlaylistsAsync();
						panel.Invalidate();
						return true;
					}

					var actionBounds = ActionButtonBounds(widget);
					if (actionBounds.Contains(clickPoint))
					{
						if (_playlistMembershipKnown)
						{
							if (_trackInSelectedPlaylist)
							{
								_ = RemoveCurrentTrackFromSelectedPlaylistAsync();
							}
							else
							{
								_ = AddCurrentTrackToSelectedPlaylistAsync();
							}
						}
						panel.Invalidate();
						return true;
					}

					var refreshBounds = RefreshButtonBounds(widget);
					if (refreshBounds.Contains(clickPoint) && _selectedPlaylist != null)
					{
						_ = RefreshMembershipForCurrentTrackAsync();
						return true;
					}
				}

				return false;
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"HandlePlaylistWidgetClick failed: {ex.GetType().Name} - {ex.Message}");
				return false;
			}
		}

		private void SelectPlaylist(SimplePlaylist playlist)
		{
			try
			{
				_selectedPlaylist = playlist;
				_playlistMembershipKnown = false;
				_trackInSelectedPlaylist = false;
				_ = RefreshMembershipForCurrentTrackAsync();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"SelectPlaylist failed: {ex.GetType().Name} - {ex.Message}");
			}
		}

		private async Task LoadDropdownPlaylistsAsync()
		{
			try
			{
				if (_dropdownPlaylistsCache != null)
				{
					_dropdownPlaylists = _dropdownPlaylistsCache;
					panel.Invalidate();
					return;
				}

				if (_spotify == null)
				{
					return;
				}

				var playlists = new List<SimplePlaylist>();
				var request = new PlaylistCurrentUsersRequest { Limit = 50, Offset = 0 };

				while (true)
				{
					var page = await _spotify.Playlists.CurrentUsers(request).ConfigureAwait(false);
					if (page?.Items != null)
					{
						playlists.AddRange(page.Items);
					}

					if (string.IsNullOrEmpty(page?.Next))
					{
						break;
					}

					request.Offset = (request.Offset ?? 0) + (request.Limit ?? 50);
				}

				_dropdownPlaylistsCache = playlists;
				_dropdownPlaylists = playlists;
				panel?.Invalidate();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"LoadDropdownPlaylistsAsync failed: {ex.GetType().Name} - {ex.Message}");
			}
		}

		private async Task RefreshMembershipForCurrentTrackAsync()
		{
			try
			{
				if (_selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
				{
					return;
				}

				var myPlaylist = _selectedPlaylist;
				var myTrackId = _trackID;

				string trackUri = "spotify:track:" + _trackID;

				if (_localMembershipOverrides.TryGetValue((myPlaylist.Id, trackUri), out var overriddenMembership))
				{
					_trackInSelectedPlaylist = overriddenMembership;
					_playlistMembershipKnown = true;
					RefreshPanelUi();
					return;
				}

				_playlistMembershipKnown = false;
				RefreshPanelUi();

				bool inPlaylist = await IsTrackInPlaylistAsync(myPlaylist.Id, trackUri).ConfigureAwait(false);

				if (ReferenceEquals(_selectedPlaylist, myPlaylist) && _trackID == myTrackId)
				{
					_trackInSelectedPlaylist = inPlaylist;
					_playlistMembershipKnown = true;
					RefreshPanelUi();
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"RefreshMembershipForCurrentTrackAsync failed: {ex.GetType().Name} - {ex.Message}");
				if (ReferenceEquals(_selectedPlaylist, _selectedPlaylist) && _trackID == _trackID)
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
			try
			{
				if (_playlistTrackUriCache.TryGetValue(playlistId, out var cachedUris)
					&& _playlistTrackUriCacheTimestamp.TryGetValue(playlistId, out var cachedAt)
					&& DateTime.UtcNow - cachedAt < PlaylistTrackCacheTtl)
				{
					return cachedUris.Contains(trackUri);
				}

				var uris = await FetchAllPlaylistTrackUrisAsync(playlistId).ConfigureAwait(false);

				_playlistTrackUriCache[playlistId] = uris;
				_playlistTrackUriCacheTimestamp[playlistId] = DateTime.UtcNow;

				return uris.Contains(trackUri);
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"IsTrackInPlaylistAsync failed: {ex.GetType().Name} - {ex.Message}");
				return false;
			}
		}

		private async Task<HashSet<string>> FetchAllPlaylistTrackUrisAsync(string playlistId)
		{
			var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			try
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
							if (fullTrack?.Uri != null)
							{
								uris.Add(fullTrack.Uri);
							}
						}
					}

					if (string.IsNullOrEmpty(page?.Next))
					{
						break;
					}

					request.Offset = (request.Offset ?? 0) + (request.Limit ?? 100);
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"FetchAllPlaylistTrackUrisAsync failed: {ex.GetType().Name} - {ex.Message}");
			}

			return uris;
		}

		private async Task AddCurrentTrackToSelectedPlaylistAsync()
		{
			if (_playlistActionInProgress || _selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			_playlistActionInProgress = true;

			try
			{
				string trackUri = "spotify:track:" + _trackID;
				var request = new PlaylistAddItemsRequest(new List<string> { trackUri });
				await _spotify.Playlists.AddItems(_selectedPlaylist.Id, request).ConfigureAwait(false);

				_localMembershipOverrides[(_selectedPlaylist.Id, trackUri)] = true;

				if (_playlistTrackUriCache.TryGetValue(_selectedPlaylist.Id, out var cachedUris))
				{
					cachedUris.Add(trackUri);
				}

				_trackInSelectedPlaylist = true;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"AddCurrentTrackToSelectedPlaylistAsync failed: {ex.GetType().Name} - {ex.Message}");
			}
			finally
			{
				_playlistActionInProgress = false;
			}
		}

		private static readonly HttpClient _rawApiHttpClient = new HttpClient();

		private async Task<bool> RemoveTrackFromPlaylistViaRawApiAsync(string playlistId, string trackUri)
		{
			try
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
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"RemoveTrackFromPlaylistViaRawApiAsync failed: {ex.GetType().Name} - {ex.Message}");
				return false;
			}
		}

		private async Task RemoveCurrentTrackFromSelectedPlaylistAsync()
		{
			if (_playlistActionInProgress || _selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			_playlistActionInProgress = true;

			try
			{
				string trackUri = "spotify:track:" + _trackID;
				bool removed = await RemoveTrackFromPlaylistViaRawApiAsync(_selectedPlaylist.Id, trackUri).ConfigureAwait(false);

				if (removed)
				{
					_localMembershipOverrides[(_selectedPlaylist.Id, trackUri)] = false;

					if (_playlistTrackUriCache.TryGetValue(_selectedPlaylist.Id, out var cachedUris))
					{
						cachedUris.Remove(trackUri);
					}

					_trackInSelectedPlaylist = false;
					_playlistMembershipKnown = true;
					RefreshPanelUi();
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"RemoveCurrentTrackFromSelectedPlaylistAsync failed: {ex.GetType().Name} - {ex.Message}");
			}
			finally
			{
				_playlistActionInProgress = false;
			}
		}

		private void OpenCreatePlaylistPrompt()
		{
			try
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
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"OpenCreatePlaylistPrompt failed: {ex.GetType().Name} - {ex.Message}");
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
				mbApiInterface.MB_Trace($"CreatePlaylistAsync failed: {ex.GetType().Name} - {ex.Message}");
				MessageBox.Show("Couldn't create the playlist:\n" + ex.Message, "Spotify Plugin Error");
			}
		}
	}
}