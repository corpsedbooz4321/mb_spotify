using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
		private static bool _playlistSliderOpen = false;
		private static int _playlistOffset = 0;
		private static int _totalPlaylistsAvailable = 0;
		private static bool _loadingMorePlaylists = false;
		private static bool _refreshingSliderMemberships = false;
		private static List<SimplePlaylist> _visiblePlaylists;
		private PlaylistCacheManager _cacheManager;

		private static string BuildTrackUri(string trackId) => "spotify:track:" + trackId;

		private static SimplePlaylist _selectedPlaylist = null;
		private static bool _trackInSelectedPlaylist = false;
		private static bool _playlistMembershipKnown = false;
		private static bool _playlistActionInProgress = false;

		private static readonly Dictionary<(string playlistId, string trackUri), bool> _localMembershipOverrides =
			new Dictionary<(string, string), bool>();

		private static readonly Dictionary<string, HashSet<string>> _playlistTrackUriCache =
			new Dictionary<string, HashSet<string>>();
		private static readonly Dictionary<string, DateTime> _playlistTrackUriCacheTimestamp =
			new Dictionary<string, DateTime>();
		private static readonly TimeSpan PlaylistTrackCacheTtl = TimeSpan.FromMinutes(5);

		private static readonly Dictionary<int, Pen> _penCache = new Dictionary<int, Pen>();
		private static readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
		private static readonly Dictionary<(string name, int panelWidth), string> _truncateCache = new Dictionary<(string, int), string>();

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
			using (var deviceContext = panel.CreateGraphics())
			{
				return TextRenderer.MeasureText(deviceContext, text, font, Size.Empty, TextFormatFlags.NoPadding);
			}
		}

		private Size PlaylistLabelSize => MeasureLabel("Playlist", smallRegular);

		private int SpacingUnit => Math.Max(2, PlaylistLabelSize.Height / 4);

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

		private void DrawPlaylistWidget(Graphics g)
		{
			var fg = panel.ForeColor;
			var widget = PlaylistWidgetBounds;

			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), widget);

			if (_selectedPlaylist == null)
			{
				TextRenderer.DrawText(g, "Select Playlist", smallRegular, widget, fg,
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

		private Rectangle PlaylistSliderBounds
		{
			get
			{
				int margin = 10;
				return new Rectangle(margin, margin, panel.Width - margin * 2, panel.Height - margin * 2);
			}
		}

		private void DrawPlaylistSliderPanel(Graphics g)
		{
			var fg = panel.ForeColor;
			var bounds = PlaylistSliderBounds;

			// Draw background
			g.FillRectangle(GetBrush(panel.BackColor), bounds);
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), bounds);

			DrawSliderBackButton(g, bounds);
			DrawSliderCreateButton(g, bounds);
			DrawSliderRefreshButton(g, bounds);

			// 3 playlist card at a sinlgle click
			if (_visiblePlaylists != null && _visiblePlaylists.Count > 0)
			{
				int usableWidth = bounds.Width - 20;  // Left/right margins
				int cardWidth = usableWidth / 3;
				int x = bounds.X + 10;
				int y = bounds.Y + 40;

				foreach (var playlist in _visiblePlaylists)
				{
					Rectangle cardBounds = new Rectangle(x, y, cardWidth - 5, 85);
					DrawPlaylistCard(g, playlist, cardBounds);
					x += cardWidth;
				}
			}

			DrawPaginationButtons(g, bounds);
		}

		private void DrawPlaylistCard(Graphics g, SimplePlaylist playlist, Rectangle bounds)
		{
			var fg = panel.ForeColor;

			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), bounds);

			string name = Truncate(playlist.Name, smallRegular);
			var nameRect = new Rectangle(bounds.X + 5, bounds.Y + 5, bounds.Width - 10, 18);
			TextRenderer.DrawText(g, name, smallRegular, nameRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

			string count = $"{_cacheManager?.GetCachedTrackCount(playlist.Id) ?? 0} tracks";
			var countRect = new Rectangle(bounds.X + 5, bounds.Y + 24, bounds.Width - 10, 16);
			TextRenderer.DrawText(g, count, smallRegular, countRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

			bool? membership = _cacheManager?.GetCachedMembership(playlist.Id, BuildTrackUri(_trackID));

			string buttonText = "Loading...";
			if (membership.HasValue)
			{
				buttonText = membership.Value ? "Remove" : "Add";
			}

			Rectangle buttonBounds = new Rectangle(bounds.X + 5, bounds.Y + 45, bounds.Width - 10, 25);
			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), buttonBounds);
			TextRenderer.DrawText(g, buttonText, smallRegular, buttonBounds, fg,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private Rectangle SliderBackButtonBounds(Rectangle bounds) =>
			new Rectangle(bounds.X + 5, bounds.Y + 8, 35, 20);

		private Rectangle SliderRefreshButtonBounds(Rectangle bounds) =>
			new Rectangle(bounds.Right - 40, bounds.Y + 8, 35, 20);

		private Rectangle SliderCreateButtonBounds(Rectangle bounds)
		{
			var refresh = SliderRefreshButtonBounds(bounds);
			const int width = 60;
			return new Rectangle(refresh.X - SpacingUnit - width, refresh.Y, width, refresh.Height);
		}

		private void DrawSliderBackButton(Graphics g, Rectangle bounds)
		{
			var fg = panel.ForeColor;
			Rectangle backButton = SliderBackButtonBounds(bounds);
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), backButton);
			TextRenderer.DrawText(g, "← Back", smallRegular, backButton, fg,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private void DrawSliderCreateButton(Graphics g, Rectangle bounds)
		{
			var fg = panel.ForeColor;
			Rectangle createButton = SliderCreateButtonBounds(bounds);
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), createButton);
			TextRenderer.DrawText(g, "+ New", smallRegular, createButton, fg,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private void DrawSliderRefreshButton(Graphics g, Rectangle bounds)
		{
			var fg = panel.ForeColor;


			Rectangle refreshButton = SliderRefreshButtonBounds(bounds);


			var textColor = _refreshingSliderMemberships ? Color.FromArgb(90, fg) : fg;



			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), refreshButton);


			TextRenderer.DrawText(g, "↻", smallRegular, refreshButton, textColor,


				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private void DrawPaginationButtons(Graphics g, Rectangle bounds)
		{
			var fg = panel.ForeColor;
			int buttonY = bounds.Bottom - 35;

			if (_playlistOffset > 0)
			{
				Rectangle backPageButton = new Rectangle(bounds.X + 10, buttonY, 50, 25);


				g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), backPageButton);

				TextRenderer.DrawText(g, "[Back]", smallRegular, backPageButton, fg,

					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

			}

			if (_totalPlaylistsAvailable > 0)
			{
				int shownCount = _visiblePlaylists?.Count ?? 0;

				string rangeText = shownCount > 0

					? $"{_playlistOffset + 1}-{_playlistOffset + shownCount} of {_totalPlaylistsAvailable}"

					: $"0 of {_totalPlaylistsAvailable}";


				Rectangle rangeRect = new Rectangle(bounds.X + 70, buttonY, bounds.Width - 160, 25);

				TextRenderer.DrawText(g, rangeText, smallRegular, rangeRect, Color.FromArgb(160, fg),

					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

			}

			if (_totalPlaylistsAvailable > _playlistOffset + 3)
			{
				string moreText = _loadingMorePlaylists ? "Loading..." : "[More]";

				Rectangle moreButton = new Rectangle(bounds.Right - 80, buttonY, 70, 25);

				var textColor = _loadingMorePlaylists ? Color.FromArgb(90, fg) : fg;

				g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), moreButton);

				TextRenderer.DrawText(g, moreText, smallRegular, moreButton, textColor,

					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

			}

		}


		private bool HandlePlaylistSliderClick(Point clickPoint)
		{
			var sliderBounds = PlaylistSliderBounds;

			Rectangle backButton = SliderBackButtonBounds(sliderBounds);

			if (backButton.Contains(clickPoint))

			{
				_playlistSliderOpen = false;

				panel.Invalidate();

				return true;
			}

			Rectangle createButton = SliderCreateButtonBounds(sliderBounds);

			if (createButton.Contains(clickPoint))

			{

				OpenCreatePlaylistPrompt();

				return true;
			}

			Rectangle refreshButton = SliderRefreshButtonBounds(sliderBounds);

			if (refreshButton.Contains(clickPoint) && !_refreshingSliderMemberships)

			{

				RefreshAllMemberships();

				return true;

			}


			int buttonY = sliderBounds.Bottom - 35;

			if (_playlistOffset > 0)

			{

				Rectangle backPageButton = new Rectangle(sliderBounds.X + 10, buttonY, 50, 25);
				if (backPageButton.Contains(clickPoint))


				{
					_playlistOffset -= 3;
					if (_playlistOffset < 0) _playlistOffset = 0;
					_ = RefreshVisiblePlaylists();
					panel.Invalidate();
					return true;
				}
			}

			if (_totalPlaylistsAvailable > _playlistOffset + 3 && !_loadingMorePlaylists)
			{
				Rectangle moreButton = new Rectangle(sliderBounds.Right - 80, buttonY, 70, 25);
				if (moreButton.Contains(clickPoint))
				{
					LoadMorePlaylists();
					return true;
				}
			}

			if (_visiblePlaylists != null && _visiblePlaylists.Count > 0)
			{
				int usableWidth = sliderBounds.Width - 20;

				int cardWidth = usableWidth / 3;

				int x = sliderBounds.X + 10;

				int y = sliderBounds.Y + 40;


				for (int i = 0; i < _visiblePlaylists.Count; i++)
				{
					Rectangle cardBounds = new Rectangle(x, y, cardWidth - 5, 85);

					if (cardBounds.Contains(clickPoint))
					{
						// Check if click is on button area

						Rectangle buttonBounds = new Rectangle(cardBounds.X + 5, cardBounds.Y + 45,

							cardBounds.Width - 10, 25);

						if (buttonBounds.Contains(clickPoint))
						{
							HandleAddRemoveClick(_visiblePlaylists[i]);
							return true;
						}
						else
						{
							// Card clicked - select playlist and return to main

							SelectPlaylist(_visiblePlaylists[i]);

							_playlistSliderOpen = false;

							panel.Invalidate();

							return true;
						}
					}

					x += cardWidth;
				}
			}

			return false;
		}

		private async Task RefreshVisiblePlaylists()
		{
			try
			{
				_visiblePlaylists = (await _cacheManager.GetPlaylistsAsync(_playlistOffset)).ToList();

				_totalPlaylistsAvailable = _cacheManager.GetTotalPlaylistsAvailable();


				string trackUri = BuildTrackUri(_trackID);

				foreach (var pl in _visiblePlaylists)

				{

					_ = PrefetchMembershipAsync(pl.Id, trackUri);

				}

				RefreshPanelUi();

			}

			catch (Exception ex)

			{

				mbApiInterface.MB_Trace("RefreshVisiblePlaylists failed: " + ex.Message);

			}
		}

		private async void LoadMorePlaylists()
		{

			if (_loadingMorePlaylists) return;



			_loadingMorePlaylists = true;

			panel.Invalidate();

			try

			{

				_playlistOffset += 3;
				await RefreshVisiblePlaylists();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("LoadMorePlaylists failed: " + ex.Message);

				_playlistOffset -= 3;
			}

			finally

			{
				_loadingMorePlaylists = false;
			}
		}

		private async Task PrefetchMembershipAsync(string playlistId, string trackUri)
		{
			try
			{
				await _cacheManager.IsTrackInPlaylistAsync(playlistId, trackUri);
				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("PrefetchMembershipAsync failed: " + ex.Message);
			}
		}

		private async void HandleAddRemoveClick(SimplePlaylist playlist)
		{
			bool? isMember = await _cacheManager.IsTrackInPlaylistAsync(playlist.Id, BuildTrackUri(_trackID));

			if (isMember ?? false)
			{
				RemoveTrackFromPlaylist(playlist);
			}
			else
			{
				AddTrackToPlaylist(playlist);
			}
		}

		private async void RefreshAllMemberships()
		{
			if (_refreshingSliderMemberships || _visiblePlaylists == null || _visiblePlaylists.Count == 0)
			{
				return;
			}

			_refreshingSliderMemberships = true;

			panel.Invalidate();

			try
			{
				string trackUri = BuildTrackUri(_trackID);

				var tasks = _visiblePlaylists.Select(pl => _cacheManager.RefreshTrackMembershipAsync(pl.Id, trackUri));

				await Task.WhenAll(tasks).ConfigureAwait(false);

			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("RefreshAllMemberships failed: " + ex.GetType().Name + " - " + ex.Message);
			}
			finally
			{
				_refreshingSliderMemberships = false;
				RefreshPanelUi();
			}
		}

		private bool HandlePlaylistWidgetClick(Point clickPoint)
		{
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
							RemoveTrackFromPlaylist(_selectedPlaylist);
						}
						else
						{
							AddTrackToPlaylist(_selectedPlaylist);
						}
					}
					return true;
				}
			}

			if (widget.Contains(clickPoint))
			{
				_playlistSliderOpen = true;

				_playlistOffset = 0;

				_visiblePlaylists = null;

				_ = RefreshVisiblePlaylists();

				panel.Invalidate();

				return true;
			}

			return false;
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

			InvalidatePlaylistTrackCache(_selectedPlaylist.Id);

			_playlistMembershipKnown = false;
			RefreshPanelUi();

			_ = RefreshMembershipForCurrentTrackAsync(_selectedPlaylist);
		}

		private static void InvalidatePlaylistTrackCache(string playlistId)
		{
			_playlistTrackUriCache.Remove(playlistId);

			_playlistTrackUriCacheTimestamp.Remove(playlistId);
		}

		private async Task RefreshMembershipForCurrentTrackAsync(SimplePlaylist playlist)
		{
			if (string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			var myPlaylist = playlist;
			var myTrackId = _trackID; // capture a check for the old track finishing an late song are nnot override the current s state
			string trackUri = BuildTrackUri(myTrackId);

			bool hasOverride = _localMembershipOverrides.TryGetValue((playlist.Id, trackUri), out bool overriddenState);
			if (hasOverride)
			{
				_trackInSelectedPlaylist = overriddenState;
				_playlistMembershipKnown = true;
				RefreshPanelUi();

			}

			try
			{
				bool inPlaylist = await IsTrackInPlaylistAsync(playlist.Id, trackUri).ConfigureAwait(false);

				if (!ReferenceEquals(_selectedPlaylist, myPlaylist) || _trackID != myTrackId)
				{
					return;
				}

				if (hasOverride)
				{
					if (overriddenState == inPlaylist)
					{
						_localMembershipOverrides.Remove((playlist.Id, trackUri));

						_trackInSelectedPlaylist = inPlaylist;
					}
					else
					{
						_trackInSelectedPlaylist = overriddenState;
					}
				}
				else
				{
					_trackInSelectedPlaylist = inPlaylist;
				}

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

		private async Task<HashSet<string>> FetchAllPlaylistTrackUrisAsync(string playlistId)
		{
			var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

			return uris;
		}

		private async void AddTrackToPlaylist(SimplePlaylist playlist)
		{
			if (_playlistActionInProgress || playlist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}
			_playlistActionInProgress = true;

			try
			{
				string trackUri = BuildTrackUri(_trackID);
				var request = new PlaylistAddItemsRequest(new List<string> { trackUri });

				await _spotify.Playlists.AddItems(playlist.Id, request).ConfigureAwait(false);


				_cacheManager?.NotifyTrackAdded(playlist.Id, trackUri);

				_localMembershipOverrides[(playlist.Id, trackUri)] = true;

				if (_playlistTrackUriCache.TryGetValue(playlist.Id, out var cachedUris))
				{
					cachedUris.Add(trackUri);
				}

				if (_selectedPlaylist != null && _selectedPlaylist.Id == playlist.Id)
				{
					_trackInSelectedPlaylist = true;
					_playlistMembershipKnown = true;
				}

				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("AddTrackToPlaylist failed: " + ex.GetType().Name + " - " + ex.Message);
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

		private async void RemoveTrackFromPlaylist(SimplePlaylist playlist)
		{
			if (_playlistActionInProgress || playlist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}
			_playlistActionInProgress = true;

			try
			{
				string trackUri = BuildTrackUri(_trackID);
				bool removed = await RemoveTrackFromPlaylistViaRawApiAsync(playlist.Id, trackUri).ConfigureAwait(false);

				if (removed)
				{
					_cacheManager?.NotifyTrackRemoved(playlist.Id, trackUri);

					_localMembershipOverrides[(playlist.Id, trackUri)] = false;


					if (_playlistTrackUriCache.TryGetValue(playlist.Id, out var cachedUris))
					{
						cachedUris.Remove(trackUri);
					}

					if (_selectedPlaylist != null && _selectedPlaylist.Id == playlist.Id)
					{
						_trackInSelectedPlaylist = false;
						_playlistMembershipKnown = true;
					}

					RefreshPanelUi();
				}
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("RemoveTrackFromPlaylist failed: " + ex.GetType().Name + " - " + ex.Message);
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

				_cacheManager?.InvalidatePlaylistListCache();

				_playlistOffset = 0;

				if (_playlistSliderOpen)
				{
					_ = RefreshVisiblePlaylists();
				}

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