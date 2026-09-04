using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
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
		private static DateTime _dropdownPlaylistsCacheTimestamp = DateTime.MinValue;

		// Pagination: playlists are shown 3 at a time with a Back/+More nav row.
		private const int PlaylistsPerPage = 3;
		private static int _dropdownPage = 0;

		private static readonly Dictionary<(string playlistId, string trackUri), bool> _localMembershipOverrides =
			new Dictionary<(string, string), bool>();

		private static readonly Dictionary<string, HashSet<string>> _playlistTrackUriCache =
			new Dictionary<string, HashSet<string>>();
		private static readonly Dictionary<string, DateTime> _playlistTrackUriCacheTimestamp =
			new Dictionary<string, DateTime>();
		private static readonly TimeSpan PlaylistTrackCacheTtl = TimeSpan.FromMinutes(5);

		private static readonly Dictionary<int, Pen> _penCache = new Dictionary<int, Pen>();
		private static readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
		// Keyed by (name, maxWidth) - maxWidth is the actual space the text will be drawn into,
		// NOT the panel width (the panel is far wider than the widget/dropdown row, so keying on
		// panel width meant long names were never actually truncated to fit their row).
		private static readonly Dictionary<(string name, int maxWidth), string> _truncateCache =
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

		private string TruncateCached(string name, Font font, int maxWidth)
		{
			var key = (name, maxWidth);
			if (_truncateCache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			var truncated = Truncate(name, font, maxWidth);

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

		private List<SimplePlaylist> CurrentPagePlaylists()
		{
			if (_dropdownPlaylists == null || _dropdownPlaylists.Count == 0)
			{
				return new List<SimplePlaylist>();
			}

			return _dropdownPlaylists
				.Skip(_dropdownPage * PlaylistsPerPage)
				.Take(PlaylistsPerPage)
				.ToList();
		}

		private int TotalDropdownPages =>
			_dropdownPlaylists == null || _dropdownPlaylists.Count == 0
				? 1
				: (int)Math.Ceiling(_dropdownPlaylists.Count / (double)PlaylistsPerPage);

		private bool DropdownNeedsPaging => (_dropdownPlaylists?.Count ?? 0) > PlaylistsPerPage;

		private Rectangle PlaylistDropdownBounds
		{
			get
			{
				int pageCount = CurrentPagePlaylists().Count;
				int rows = 1 + pageCount + (DropdownNeedsPaging ? 1 : 0);
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
					var nameRect = new Rectangle(widget.X + SpacingUnit, widget.Y, widget.Width - SpacingUnit * 2, widget.Height);
					var name = TruncateCached(_selectedPlaylist.Name, smallRegular, nameRect.Width);
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

				int availableTextWidth = Math.Max(0, bounds.Width - SpacingUnit * 3);
				foreach (var pl in CurrentPagePlaylists())
				{
					var rowRect = new Rectangle(bounds.X, rowY, bounds.Width, rowHeight);
					var label = TruncateCached(pl.Name, smallRegular, availableTextWidth);
					TextRenderer.DrawText(g, label, smallRegular, rowRect, fg,
						TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding | TextFormatFlags.EndEllipsis);
					rowY += rowHeight;
				}

				if (DropdownNeedsPaging)
				{
					DrawDropdownNavRow(g, new Rectangle(bounds.X, rowY, bounds.Width, rowHeight), fg);
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"DrawPlaylistDropdown failed: {ex.GetType().Name} - {ex.Message}");
			}
		}

		private void DrawDropdownNavRow(Graphics g, Rectangle navBounds, Color fg)
		{
			int half = navBounds.Width / 2;
			var backCell = new Rectangle(navBounds.X, navBounds.Y, half, navBounds.Height);
			var moreCell = new Rectangle(navBounds.X + half, navBounds.Y, navBounds.Width - half, navBounds.Height);

			bool canGoBack = _dropdownPage > 0;
			bool canGoMore = _dropdownPage < TotalDropdownPages - 1;

			g.DrawLine(GetPen(Color.FromArgb(40, fg)), navBounds.X, navBounds.Y, navBounds.Right, navBounds.Y);
			g.DrawLine(GetPen(Color.FromArgb(30, fg)), moreCell.X, moreCell.Y, moreCell.X, moreCell.Bottom);

			var backColor = canGoBack ? fg : Color.FromArgb(70, fg);
			var moreColor = canGoMore ? fg : Color.FromArgb(70, fg);

			TextRenderer.DrawText(g, "< Back", iconFont, backCell, backColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(g, "+ More", iconFont, moreCell, moreColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
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

						var pageItems = CurrentPagePlaylists();
						int navRowIndex = 1 + pageItems.Count;

						if (rowIndex == 0)
						{
							_playlistDropdownOpen = false;
							OpenCreatePlaylistPrompt();
						}
						else if (rowIndex - 1 < pageItems.Count)
						{
							_playlistDropdownOpen = false;
							SelectPlaylist(pageItems[rowIndex - 1]);
						}
						else if (DropdownNeedsPaging && rowIndex == navRowIndex)
						{
							// Clicking the nav row pages the list instead of closing the dropdown.
							int relativeX = clickPoint.X - dropdown.X;
							bool clickedMoreHalf = relativeX >= dropdown.Width / 2;

							if (clickedMoreHalf)
							{
								if (_dropdownPage < TotalDropdownPages - 1)
								{
									_dropdownPage++;
								}
							}
							else if (_dropdownPage > 0)
							{
								_dropdownPage--;
							}
						}
						else
						{
							_playlistDropdownOpen = false;
							_dropdownPlaylists = null;
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
						_dropdownPage = 0;
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
				bool cacheIsFresh = _dropdownPlaylistsCache != null
					&& DateTime.UtcNow - _dropdownPlaylistsCacheTimestamp < PlaylistTrackCacheTtl;

				if (cacheIsFresh)
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
				_dropdownPlaylistsCacheTimestamp = DateTime.UtcNow;
				_dropdownPlaylists = playlists;

				// This runs after an await with ConfigureAwait(false), so we're on a background
				// thread here - a direct panel.Invalidate() would be a cross-thread UI call.
				// RefreshPanelUi() marshals to the UI thread safely.
				RefreshPanelUi();
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

				// IsTrackInPlaylistAsync enforces its own timeout internally (see below), so this
				// always resolves within a bounded time even if the underlying Spotify call hangs.
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
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}
		}

		// Small POCOs for manually parsing the "Get Playlist Items" response, since this now goes
		// through a raw HTTP call instead of the SDK's PlaylistGetItemsRequest/GetItems (see
		// FetchAllPlaylistTrackUrisAsync below for why).
		private sealed class PlaylistItemsPage
		{
			[JsonProperty("items")]
			public List<PlaylistItemEntry> Items { get; set; }

			[JsonProperty("next")]
			public string Next { get; set; }
		}

		private sealed class PlaylistItemEntry
		{
			// "item" is the current field name; "track" is kept for older API responses/back-compat.
			[JsonProperty("item")]
			public PlaylistTrackRef Item { get; set; }

			[JsonProperty("track")]
			public PlaylistTrackRef Track { get; set; }
		}

		private sealed class PlaylistTrackRef
		{
			[JsonProperty("uri")]
			public string Uri { get; set; }

			[JsonProperty("id")]
			public string Id { get; set; }
		}

		private async Task<HashSet<string>> FetchAllPlaylistTrackUrisAsync(string playlistId)
		{
			var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			try
			{
				var token = await GetValidAccessTokenAsync().ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(token))
				{
					mbApiInterface.MB_Trace("FetchAllPlaylistTrackUrisAsync: no access token available");
					return uris;
				}

				const int limit = 100;
				int offset = 0;
				var fields = Uri.EscapeDataString("items(item(uri,id),track(uri,id)),next");

				while (true)
				{
					var url = $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit={limit}&offset={offset}&fields={fields}&additional_types=track";

					using (var request = new HttpRequestMessage(HttpMethod.Get, url))
					{
						request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

						using (var response = await _sharedHttpClient.SendAsync(request).ConfigureAwait(false))
						{
							var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

							if (!response.IsSuccessStatusCode)
							{
								mbApiInterface.MB_Trace($"FetchAllPlaylistTrackUrisAsync failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
								break;
							}

							var page = JsonConvert.DeserializeObject<PlaylistItemsPage>(body);
							if (page?.Items != null)
							{
								foreach (var entry in page.Items)
								{
									var trackRef = entry?.Item ?? entry?.Track;
									var trackUri = trackRef?.Uri ?? (trackRef?.Id != null ? "spotify:track:" + trackRef.Id : null);
									if (!string.IsNullOrWhiteSpace(trackUri))
									{
										uris.Add(trackUri);
									}
								}
							}

							if (string.IsNullOrEmpty(page?.Next))
							{
								break;
							}

							offset += limit;
						}
					}
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"FetchAllPlaylistTrackUrisAsync failed: {ex.GetType().Name} - {ex.Message}");
			}

			return uris;
		}

		// How long we'll wait for the playlist's tracks to be fetched before giving up. The
		// CancellationTokenSource this used to be wrapped in (see RefreshMembershipForCurrentTrackAsync
		// previously) was never actually passed into the call it was meant to time out, so a hung
		// request here would freeze playlist membership (and the Add/Remove button) forever - this
		// enforces the same 10s budget for real via Task.WhenAny instead.
		private static readonly TimeSpan PlaylistMembershipCheckTimeout = TimeSpan.FromSeconds(10);

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
				try
				{
					var token = await GetValidAccessTokenAsync().ConfigureAwait(false);
					if (string.IsNullOrWhiteSpace(token))
						return false;

					string trackId = trackUri.Replace("spotify:track:", "");

					var url = $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit=50&fields=items(track(id))";

					using (var request = new HttpRequestMessage(HttpMethod.Get, url))
					{
						request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

						using (var response = await _sharedHttpClient.SendAsync(request).ConfigureAwait(false))
						{
							var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

							if (!response.IsSuccessStatusCode)
							{
								mbApiInterface.MB_Trace($"Quick check failed: {body}");
								return false;
							}

							var page = JsonConvert.DeserializeObject<PlaylistItemsPage>(body);

							if (page?.Items != null)
							{
								foreach (var entry in page.Items)
								{
									var trackRef = entry?.Item ?? entry?.Track;
									if (trackRef?.Id == trackId)
										return true;
								}
							}
						}
					}

					// NOT FOUND in first page → assume false (fast)
					return false;
				}
				catch (Exception ex)
				{
					mbApiInterface.MB_Trace($"Quick check failed: {ex.Message}");
					return false;
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"IsTrackInPlaylistAsync failed: {ex.Message}");
				return false;
			}
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
				var accessToken = await GetValidAccessTokenAsync().ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(accessToken))
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
					request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
				_dropdownPlaylistsCacheTimestamp = DateTime.MinValue;
				_dropdownPage = 0;

				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace($"CreatePlaylistAsync failed: {ex.GetType().Name} - {ex.Message}");
				ShowError("Couldn't create the playlist:\n" + ex.Message, "Spotify Plugin Error");
			}
		}
	}
}