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
		// === NEW SLIDER PANEL VARIABLES ===
		private static bool _playlistSliderOpen = false;
		private static int _playlistOffset = 0;                    // 0, 3, 6, 9... for pagination
		private static int _totalPlaylistsAvailable = 0;
		private static bool _loadingMorePlaylists = false;
		private static bool _refreshingSliderMemberships = false;
		private static List<SimplePlaylist> _visiblePlaylists;     // Currently displayed 3 playlists
		private PlaylistCacheManager _cacheManager;

		// Every membership lookup/cache key needs the full "spotify:track:ID" form -
		// the cache manager's track-URI sets and the Spotify API both deal in URIs,
		// not bare IDs. Centralizing this avoids the bare-ID/URI mismatch bug where
		// membership checks silently always came back "not a member".
		private static string BuildTrackUri(string trackId) => "spotify:track:" + trackId;

		// === SINGLE-PLAYLIST QUICK-ACTION STATE (main panel) ===
		private static SimplePlaylist _selectedPlaylist = null;
		private static bool _trackInSelectedPlaylist = false;
		private static bool _playlistMembershipKnown = false; // false while the live check is in flight
		private static bool _playlistActionInProgress = false; // guards +/- and Create against double-clicks

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
				// playlist name, not just the collapsed-state word itself.
				int width = PlaylistLabelSize.Width * 2;
				int margin = SpacingUnit * 2; // inset from the panel's top/right edges
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
				// Draw "Select Playlist" button (clickable)
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

		// ========== SLIDER PANEL METHODS ==========

		private Rectangle PlaylistSliderBounds
		{
			get
			{
				// Full panel area minus some margins
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

			// Draw header: Back button | Create Playlist button | Refresh button
			DrawSliderBackButton(g, bounds);
			DrawSliderCreateButton(g, bounds);
			DrawSliderRefreshButton(g, bounds);

			// Draw playlist cards (3 at a time)
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

			// Draw pagination buttons
			DrawPaginationButtons(g, bounds);
		}

		private void DrawPlaylistCard(Graphics g, SimplePlaylist playlist, Rectangle bounds)
		{
			var fg = panel.ForeColor;

			// Draw card background
			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), bounds);

			// Draw playlist name (truncated)
			string name = Truncate(playlist.Name, smallRegular);
			var nameRect = new Rectangle(bounds.X + 5, bounds.Y + 5, bounds.Width - 10, 18);
			TextRenderer.DrawText(g, name, smallRegular, nameRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

			// Draw track count. Bug fix: this used to read playlist.Tracks?.Total, but
			// GetPlaylistsAsync never populated that property (and trying to populate it
			// via the SDK's own nested type broke the build - that type isn't stable
			// across SpotifyAPI.Web versions). The cache manager already has the real
			// count from when it fetched the playlist list, so read it from there.
			string count = $"{_cacheManager?.GetCachedTrackCount(playlist.Id) ?? 0} tracks";
			var countRect = new Rectangle(bounds.X + 5, bounds.Y + 24, bounds.Width - 10, 16);
			TextRenderer.DrawText(g, count, smallRegular, countRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

			// Draw Add/Remove button
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

		// Header button bounds are factored out and shared between drawing and click
		// handling (like ActionButtonBounds/RefreshButtonBounds above) so the two can
		// never drift apart the way the old inline rectangles could.
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
			// Bug fix: the refresh icon used to look identical whether or not a refresh
			// was in flight, and clicking it repeatedly fired overlapping refreshes.
			var textColor = _refreshingSliderMemberships ? Color.FromArgb(90, fg) : fg;
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), refreshButton);
			TextRenderer.DrawText(g, "↻", smallRegular, refreshButton, textColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private void DrawPaginationButtons(Graphics g, Rectangle bounds)
		{
			var fg = panel.ForeColor;
			int buttonY = bounds.Bottom - 35;

			// Draw "Back" button if not at offset 0
			if (_playlistOffset > 0)
			{
				Rectangle backPageButton = new Rectangle(bounds.X + 10, buttonY, 50, 25);
				g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), backPageButton);
				TextRenderer.DrawText(g, "[Back]", smallRegular, backPageButton, fg,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			}

			// Draw "Load More" button if more playlists exist
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

			// Check Back button (top-left)
			Rectangle backButton = SliderBackButtonBounds(sliderBounds);
			if (backButton.Contains(clickPoint))
			{
				_playlistSliderOpen = false;
				panel.Invalidate();
				return true;
			}

			// Check Create Playlist button (top, left of Refresh)
			Rectangle createButton = SliderCreateButtonBounds(sliderBounds);
			if (createButton.Contains(clickPoint))
			{
				OpenCreatePlaylistPrompt();
				return true;
			}

			// Check Refresh button (top-right)
			Rectangle refreshButton = SliderRefreshButtonBounds(sliderBounds);
			if (refreshButton.Contains(clickPoint) && !_refreshingSliderMemberships)
			{
				RefreshAllMemberships();
				return true;
			}

			// Check Back pagination button
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

			// Check More button
			if (_totalPlaylistsAvailable > _playlistOffset + 3 && !_loadingMorePlaylists)
			{
				Rectangle moreButton = new Rectangle(sliderBounds.Right - 80, buttonY, 70, 25);
				if (moreButton.Contains(clickPoint))
				{
					LoadMorePlaylists();
					return true;
				}
			}

			// Check playlist cards
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
							// Add/Remove button clicked
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

				// Prefetch membership status for each playlist (non-blocking)
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
			// Bug fix: this used to check membership against `playlist` but then act on
			// `_selectedPlaylist` via AddCurrentTrackToSelectedPlaylist/RemoveCurrentTrackFromSelectedPlaylist,
			// so clicking Add/Remove on a card did nothing (or acted on the wrong
			// playlist) unless it happened to match whatever was picked from the old
			// dropdown. AddTrackToPlaylist/RemoveTrackFromPlaylist now take the target
			// playlist explicitly.
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
			// Bug fix: this used to fire off refreshes without awaiting them and then
			// invalidate the panel immediately - the repaint happened before any of the
			// results came back, so the cards never visibly updated until some unrelated
			// repaint occurred later. It also passed the bare track ID instead of a URI,
			// so the refreshed result was always wrong anyway.
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
				// The dropdown is gone - the slider panel is now the only way to browse
				// and (re)select a playlist, whether or not one is already selected.
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

			// Force this playlist's track-URI set to refetch (in case tracks were
			// added/removed from elsewhere, e.g. Spotify's own app).
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
			var myTrackId = _trackID; // capture - a check for the old track finishing late must not overwrite a newer one's state
			string trackUri = BuildTrackUri(myTrackId);

			bool hasOverride = _localMembershipOverrides.TryGetValue((playlist.Id, trackUri), out bool overriddenState);
			if (hasOverride)
			{
				// Show the optimistic state immediately - don't wait on the network.
				_trackInSelectedPlaylist = overriddenState;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}

			try
			{
				bool inPlaylist = await IsTrackInPlaylistAsync(playlist.Id, trackUri).ConfigureAwait(false);

				if (!ReferenceEquals(_selectedPlaylist, myPlaylist) || _trackID != myTrackId)
				{
					// A newer track/playlist selection superseded this check.
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

		/// <summary>
		/// Adds the current track to the given playlist. Bug fix: this used to be
		/// AddCurrentTrackToSelectedPlaylist(), which ignored whatever playlist was
		/// actually clicked in the slider panel and always acted on _selectedPlaylist
		/// instead (silently doing nothing if nothing had ever been selected via the
		/// old dropdown). Taking the target playlist explicitly fixes that.
		/// </summary>
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

				// Notify cache manager
				_cacheManager?.NotifyTrackAdded(playlist.Id, trackUri);

				_localMembershipOverrides[(playlist.Id, trackUri)] = true;

				if (_playlistTrackUriCache.TryGetValue(playlist.Id, out var cachedUris))
				{
					cachedUris.Add(trackUri);
				}

				// Only the main-panel quick-action row cares about _selectedPlaylist's
				// state - update it only when the playlist just modified is the one
				// currently shown there.
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

		/// <summary>
		/// Removes the current track from the given playlist. See AddTrackToPlaylist
		/// for why this now takes the target playlist explicitly instead of always
		/// using _selectedPlaylist.
		/// </summary>
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
					// Notify cache manager
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

				// The old dropdown's cache invalidation is gone along with the dropdown -
				// invalidate the slider panel's playlist cache instead, and reset back to
				// the first page so the new playlist shows up on next fetch.
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