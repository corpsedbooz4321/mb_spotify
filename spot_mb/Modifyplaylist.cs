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
	/// <summary>
	/// Adds a "Playlist" corner widget to the existing Track Details panel. Purely
	/// additive - none of the existing Save Track / Save Album / Follow Artist logic
	/// in SpotifyIntegration.cs or PanelInterface.cs is touched.
	///
	/// Behavior (as spec'd):
	/// - Corner widget shows "Playlist" (nothing selected) or "[Name]  -  +" (selected).
	/// - Clicking it opens a small dropdown. The first open each session
	///   fetches live from Spotify; after that the list is cached in memory
	///   (never to disk) for the rest of the session, until you create a
	///   playlist through this widget (which invalidates it) or the app restarts.
	/// - Dropdown always lists "Create Playlist" first, then up to 3 existing playlists.
	/// - Selecting "Create Playlist" opens a small name-entry popup, creates the
	///   playlist on Spotify immediately, then the corner reverts to plain "Playlist"
	///   (no auto-select - a deliberate, separate second action).
	/// - Selecting an existing playlist closes the dropdown and does a LIVE, one-time
	///   membership check for the current track against that playlist, setting which
	///   of -/+ is greyed out.
	/// - Clicking +/- calls Spotify immediately - no batching/staging.
	/// - Clicking elsewhere while the dropdown is open just closes it, no state change.
	/// </summary>
	public partial class Plugin
	{
		private static bool _playlistDropdownOpen = false;
		private static SimplePlaylist _selectedPlaylist = null;
		private static bool _trackInSelectedPlaylist = false;
		private static bool _playlistMembershipKnown = false; // false while the live check is in flight
		private static List<SimplePlaylist> _dropdownPlaylists = null; // only populated while the dropdown is open
		private static bool _playlistActionInProgress = false; // guards +/- and Create against double-clicks

		// --- Paint-time caches -------------------------------------------------
		// DrawPlaylistWidget/DrawPlaylistDropdown used to new-up a Pen/SolidBrush
		// on every single paint. These are cached instead and reused for the
		// lifetime of the plugin. The set of distinct colors in play here is tiny
		// (a handful of fixed alphas over whatever the current theme's fg/bg
		// happen to be), so the cache stays small even across theme changes -
		// it's a deliberate trade of a few never-disposed GDI handles for zero
		// per-paint allocation.
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

		/// <summary>
		/// Thin memoization wrapper around the existing Truncate() helper. Keyed
		/// by (name, panel width) so a resize - which can change how much text
		/// fits - naturally invalidates stale entries instead of needing an
		/// explicit cache-clear hook.
		/// </summary>
		private string TruncateCached(string name, Font font)
		{
			var key = (name, panel.Width);
			if (_truncateCache.TryGetValue(key, out var cached))
			{
				return cached;
			}

			var truncated = Truncate(name, font);

			// Bounded in practice (selected playlist name + up to 3 dropdown
			// names per panel width), but guard against unbounded growth across
			// many resizes over a long session anyway.
			if (_truncateCache.Count > 64)
			{
				_truncateCache.Clear();
			}
			_truncateCache[key] = truncated;
			return truncated;
		}

		/// <summary>
		/// Layout below is derived from text actually measured at the panel's
		/// current font, instead of hardcoded pixel constants. TextRenderer.MeasureText
		/// already returns correct pixel sizes for whatever DPI/font-metrics are
		/// active in the current environment, so building geometry from these
		/// measurements - rather than fixed numbers picked to look right on one
		/// machine - is what keeps the corner widget from clipping or squishing
		/// when DPI scaling or font metrics differ between installs.
		/// </summary>
		private static Size MeasureLabel(string text, Font font) =>
			TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);

		// "Playlist" is the widest string shown in the collapsed state - everything
		// else (margins, row heights, widget width) scales off this single measurement.
		private Size PlaylistLabelSize => MeasureLabel("Playlist", smallRegular);

		// A spacing unit tied to the font's own line height rather than a fixed
		// pixel count, so gaps/margins grow and shrink with text size/DPI instead
		// of staying frozen at whatever looked right at one specific scale.
		private int SpacingUnit => Math.Max(2, PlaylistLabelSize.Height / 4);

		// "Create Playlist" (smallBold) is the tallest/widest string in the
		// dropdown, so dropdown row height and minimum width anchor off it.
		private int DropdownRowHeight => MeasureLabel("Create Playlist", smallBold).Height + SpacingUnit;

		// Corner widget geometry, computed fresh each paint against the current panel
		// width AND the current font metrics.
		private Rectangle PlaylistWidgetBounds
		{
			get
			{
				int height = PlaylistLabelSize.Height + SpacingUnit;
				// Double the "Playlist" label's own width - room for a truncated
				// playlist name, not just the collapsed-state word itself.
				int width = PlaylistLabelSize.Width * 2;
				int margin = SpacingUnit * 2; // inset from the panel's top/right edges
				return new Rectangle(panel.Width - width - margin, margin, width, height);
			}
		}

		// Add/Remove/Refresh live on their own row beneath the name, with a small
		// gap so they read as a distinct action row rather than crowding the name.
		private Rectangle PlaylistActionRowBounds =>
			new Rectangle(PlaylistWidgetBounds.X, PlaylistWidgetBounds.Bottom + SpacingUnit,
				PlaylistWidgetBounds.Width, PlaylistLabelSize.Height + SpacingUnit / 2);

		private Rectangle PlaylistDropdownBounds
		{
			get
			{
				int rows = 1 + (_dropdownPlaylists?.Count ?? 0); // "Create Playlist" + playlists
				int rowHeight = DropdownRowHeight;
				// At least as wide as "Create Playlist" itself (plus padding), and
				// never narrower than the corner widget it hangs off of.
				int width = Math.Max(
					MeasureLabel("Create Playlist", smallBold).Width + SpacingUnit * 4,
					PlaylistWidgetBounds.Width * 2);
				// When a playlist is selected the action row is visible beneath the
				// name, so the dropdown needs to start below that instead of
				// overlapping it.
				int top = _selectedPlaylist != null
					? PlaylistActionRowBounds.Bottom + SpacingUnit
					: PlaylistWidgetBounds.Bottom + SpacingUnit;
				return new Rectangle(panel.Width - width - SpacingUnit * 2, top, width, rows * rowHeight);
			}
		}

		/// <summary>
		/// Call from DrawPanel, only when a track is currently loaded (_auth == 1 &&
		/// _trackMissing != 1) - the widget needs a current track for the +/- actions
		/// to mean anything.
		/// </summary>
		private void DrawPlaylistWidget(Graphics g)
		{
			var fg = panel.ForeColor;
			var widget = PlaylistWidgetBounds;

			// Plain rectangle for the widget itself - rounding is reserved for the
			// +/- action buttons only, so it reads as "this shape means clickable
			// action" rather than being used decoratively everywhere.
			g.DrawRectangle(GetPen(Color.FromArgb(50, fg)), widget);

			if (_selectedPlaylist == null)
			{
				TextRenderer.DrawText(g, "Playlist", smallRegular, widget, fg,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			}
			else
			{
				var name = TruncateCached(_selectedPlaylist.Name, smallRegular);

				// Full width now - the +/- buttons live on their own row beneath,
				// so the name no longer needs to leave room for them here.
				var nameRect = new Rectangle(widget.X + SpacingUnit, widget.Y, widget.Width - SpacingUnit * 2, widget.Height);
				TextRenderer.DrawText(g, name, smallRegular, nameRect, fg,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

				// Single toggle button - Remove when the track is already in the
				// playlist, Add otherwise. Matches the Saved Track / Save Album
				// pattern right below it, rather than showing two labels where
				// only one is ever clickable.
				string actionLabel = _trackInSelectedPlaylist ? "Remove" : "Add";
				DrawActionButton(g, ActionButtonBounds(widget), actionLabel, _playlistMembershipKnown, fg);

				// Manual re-check of membership for the current track, in case
				// something changed the playlist from elsewhere (phone app, web
				// player) since the last automatic check.
				DrawActionButton(g, RefreshButtonBounds(widget), "\u21BB", _playlistMembershipKnown, fg);
			}

			if (_playlistDropdownOpen)
			{
				DrawPlaylistDropdown(g);
			}
		}

		/// <summary>
		/// Draws the single Add/Remove toggle: bordered box (plain rectangle,
		/// matching the name box's own border style above it) with the label
		/// centered inside, dimmed while membership is still resolving.
		/// </summary>
		private void DrawActionButton(Graphics g, Rectangle bounds, string text, bool clickable, Color fg)
		{
			int borderAlpha = clickable ? 60 : 25;
			var textColor = clickable ? fg : Color.FromArgb(90, fg);

			g.DrawRectangle(GetPen(Color.FromArgb(borderAlpha, fg)), bounds);
			TextRenderer.DrawText(g, text, smallRegular, bounds, textColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}

		/// <summary>
		/// Right-aligned under the name, sized to whichever label ("Remove" is
		/// wider than "Add") is currently showing, plus a little padding so the
		/// border doesn't hug the text.
		/// </summary>
		private Rectangle ActionButtonBounds(Rectangle widget)
		{
			string text = _trackInSelectedPlaylist ? "Remove" : "Add";
			var measured = MeasureLabel(text, smallRegular);
			int width = measured.Width + SpacingUnit * 2;
			int height = PlaylistActionRowBounds.Height + SpacingUnit;
			return new Rectangle(widget.Right - width, PlaylistActionRowBounds.Y - SpacingUnit / 2, width, height);
		}

		/// <summary>
		/// Small square button just left of the Add/Remove toggle, same height,
		/// with a SpacingUnit gap between the two.
		/// </summary>
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

		/// <summary>
		/// Call from PanelClick BEFORE the existing hit-testing, only when a track is
		/// currently loaded. Returns true if the click was handled by this widget (in
		/// which case the caller should stop processing the click further).
		/// </summary>
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

				// Clicked anywhere else while open - just close it, no state change.
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

		// In-memory only (never written to disk) session cache. Fetched once,
		// the first time the dropdown is opened, then reused for the rest of
		// the session - only CreatePlaylistAsync invalidates it. Trade-off:
		// playlists added/removed/renamed from elsewhere (phone app, web
		// player) during the session won't show up here until either you
		// create a playlist through this widget or MusicBee restarts.
		private static List<SimplePlaylist> _dropdownPlaylistsCache = null;

		private async void OpenPlaylistDropdown()
		{
			_playlistDropdownOpen = true;

			if (_dropdownPlaylistsCache != null)
			{
				// Already fetched this session - serve straight from cache, no round trip.
				_dropdownPlaylists = _dropdownPlaylistsCache;
				panel.Invalidate();
				RefreshPanelUi();
				return;
			}

			_dropdownPlaylists = null; // show just "Create Playlist" while this session's first fetch is in flight
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

				// Deliberately NOT caching a failed fetch - leave
				// _dropdownPlaylistsCache null so the next open retries instead
				// of being stuck empty for the rest of the session.
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

		/// <summary>
		/// Call this whenever the currently-loaded track changes (e.g. the next
		/// song starts playing) while a playlist is selected. Without this, the
		/// +/- buttons keep showing the *previous* track's membership state -
		/// e.g. "+" stays greyed out for the new song just because the old one
		/// was already in the playlist.
		///
		/// Hook this into wherever the plugin already reacts to a track change
		/// elsewhere (e.g. the existing TrackChanged handling that updates
		/// Saved Track / Save Album / Follow Artist) - this file has no
		/// visibility into that event on its own.
		/// </summary>
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

		/// <summary>
		/// Manual re-check, triggered by the refresh button. Same mechanics as
		/// OnPlaylistWidgetTrackChanged, just user-initiated instead of
		/// track-change-initiated - useful if the playlist was edited from
		/// somewhere else (phone app, web player) since the last automatic check.
		/// </summary>
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

		/// <summary>
		/// Runs the live membership check for the current track against the
		/// given playlist. Guarded by BOTH playlist identity and track ID at
		/// completion - a slow check landing after the user picked a different
		/// playlist, or after the track changed underneath it, is dropped
		/// rather than applied to the wrong context.
		/// </summary>
		private async Task RefreshMembershipForCurrentTrackAsync(SimplePlaylist playlist)
		{
			if (string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			var myPlaylist = playlist;
			var myTrackId = _trackID; // capture - a check for the old track finishing late must not overwrite a newer one's state
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
					// Leave _playlistMembershipKnown false - both +/- stay greyed rather
					// than guessing and risking a duplicate-add or a no-op remove.
					RefreshPanelUi();
				}
			}
		}

		/// <summary>
		/// Spotify has no "does this playlist contain track X" endpoint (unlike the
		/// library/follow "contains" checks) - the only way to know is to page through
		/// the playlist's own items and look for a matching URI.
		/// </summary>
		/// <summary>
		/// Pulls the played item off a playlist-page entry without hardcoding a
		/// property name. Different versions/branches of SpotifyAPI.Web's
		/// PlaylistTrack&lt;T&gt; have exposed this as either .Track or .Item, and
		/// the compiler has rejected BOTH names at different points on this
		/// project - which means whichever one is actually compiled in isn't
		/// reliably knowable from source alone. Reflection sidesteps the
		/// ambiguity entirely: it works whichever name the real, currently
		/// installed package uses, and needs no further guessing.
		/// </summary>
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
				// A FullTrack normally drags along album art URLs, every artist,
				// audio-feature refs, etc. All this check needs is the URI, so
				// ask Spotify to only send that plus the pagination cursor - for
				// a playlist near the 10k-track ceiling this meaningfully shrinks
				// what's fetched and parsed per page. Fields is a get-only
				// IList<string>, so it's populated via collection initializer
				// (which calls .Add on the existing list) rather than assigned.
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

		// Dedicated client for the raw fallback call below - kept separate from
		// _artworkHttpClient (SpotifyIntegration.cs) since that one is unauthenticated
		// and shared for image downloads; mixing an Authorization header onto it
		// would leak a bearer token onto unrelated requests.
		private static readonly HttpClient _rawApiHttpClient = new HttpClient();

		/// <summary>
		/// Spotify's Feb 2026 API migration removed DELETE /playlists/{id}/tracks
		/// entirely - its replacement, DELETE /playlists/{id}/items, also renamed
		/// the body key from "tracks" to "items". The installed SpotifyAPI.Web SDK's
		/// Playlists.RemoveItems() still targets the old route/key, so every call
		/// hits a dead endpoint and comes back "No uris provided". This bypasses the
		/// SDK for just this one call and hits the new endpoint directly, until the
		/// SDK ships a fix.
		///
		/// Reuses the same token file SpotifyWebAuth() already trusts rather than
		/// reaching into PKCEAuthenticator's internals for the current access token.
		/// A cheap authenticated call is made first so the authenticator refreshes
		/// (and re-persists to _path) if the token has expired, then the now-current
		/// token is read straight from that file.
		/// </summary>
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

		/// <summary>
		/// Small popup for entering a new playlist's name. Same pattern as
		/// ClientIdSetupForm - this custom-painted panel has no built-in way to take
		/// live keyboard text input, so a lightweight Form is used instead of trying
		/// to draw/edit text directly inside DrawPanel.
		/// </summary>
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

				// Deliberately NOT auto-selecting the new playlist - creating and
				// selecting are two separate actions per spec. Corner just goes back
				// to plain "Playlist"; the user opens the dropdown again to pick it.
				_selectedPlaylist = null;
				_playlistMembershipKnown = false;

				// Invalidate the cache so the newly created playlist shows up on
				// the next open instead of waiting out the TTL.
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