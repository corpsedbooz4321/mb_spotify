using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
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
		/// <summary>
		/// Builds a rounded-rectangle path, used only by the +/- action buttons below.
		/// Kept local to this file rather than assuming a shared helper exists
		/// elsewhere in the plugin.
		/// </summary>
		private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
		{
			int diameter = radius * 2;
			var path = new GraphicsPath();
			var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

			path.AddArc(arc, 180, 90);
			arc.X = bounds.Right - diameter;
			path.AddArc(arc, 270, 90);
			arc.Y = bounds.Bottom - diameter;
			path.AddArc(arc, 0, 90);
			arc.X = bounds.Left;
			path.AddArc(arc, 90, 90);
			path.CloseFigure();

			return path;
		}

		private static bool _playlistDropdownOpen = false;
		private static SimplePlaylist _selectedPlaylist = null;
		private static bool _trackInSelectedPlaylist = false;
		private static bool _playlistMembershipKnown = false; // false while the live check is in flight
		private static List<SimplePlaylist> _dropdownPlaylists = null; // only populated while the dropdown is open
		private static bool _playlistActionInProgress = false; // guards +/- and Create against double-clicks

		// --- Paint-time caches -------------------------------------------------
		// DrawPlaylistWidget/DrawPlaylistDropdown/DrawRoundedIconButton used to
		// new-up a Pen/SolidBrush/GraphicsPath on every single paint. These are
		// cached instead and reused for the lifetime of the plugin. The set of
		// distinct colors in play here is tiny (a handful of fixed alphas over
		// whatever the current theme's fg/bg happen to be), so the cache stays
		// small even across theme changes - it's a deliberate trade of a few
		// never-disposed GDI handles for zero per-paint allocation.
		private static readonly Dictionary<int, Pen> _penCache = new Dictionary<int, Pen>();
		private static readonly Dictionary<int, Brush> _brushCache = new Dictionary<int, Brush>();
		private static GraphicsPath _iconButtonPath; // 18x18 rounded rect at the origin; positioned via TranslateTransform per button instead of rebuilt each time
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

		private static GraphicsPath GetIconButtonPath()
		{
			// Both callers (MinusButtonBounds/PlusButtonBounds) are fixed at 18x18,
			// so a single cached path built at the origin and translated into place
			// is safe. If either button size ever changes, this needs revisiting.
			if (_iconButtonPath == null)
			{
				_iconButtonPath = RoundedRect(new Rectangle(0, 0, 18, 18), 5);
			}
			return _iconButtonPath;
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

		// Corner widget geometry, computed fresh each paint against the current panel width.
		private Rectangle PlaylistWidgetBounds => new Rectangle(panel.Width - 96, 4, 92, 16);

		// The +/- buttons now live on their own row beneath the name, with a
		// small gap so they read as a distinct action row rather than crowding
		// the name.
		private Rectangle PlaylistActionRowBounds =>
			new Rectangle(PlaylistWidgetBounds.X, PlaylistWidgetBounds.Bottom + 4, PlaylistWidgetBounds.Width, 18);

		private Rectangle PlaylistDropdownBounds
		{
			get
			{
				int rows = 1 + (_dropdownPlaylists?.Count ?? 0); // "Create Playlist" + playlists
																 // When a playlist is selected the action row is visible beneath the
																 // name, so the dropdown needs to start below that instead of
																 // overlapping it.
				int top = _selectedPlaylist != null
					? PlaylistActionRowBounds.Bottom + 4
					: PlaylistWidgetBounds.Bottom + 4;
				return new Rectangle(panel.Width - 156, top, 152, rows * 18);
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
				var nameRect = new Rectangle(widget.X + 4, widget.Y, widget.Width - 8, widget.Height);
				TextRenderer.DrawText(g, name, smallRegular, nameRect, fg,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

				bool canRemove = _playlistMembershipKnown && _trackInSelectedPlaylist;
				bool canAdd = _playlistMembershipKnown && !_trackInSelectedPlaylist;

				DrawRoundedIconButton(g, MinusButtonBounds(widget), "−", canRemove, fg);
				DrawRoundedIconButton(g, PlusButtonBounds(widget), "+", canAdd, fg);
			}

			if (_playlistDropdownOpen)
			{
				DrawPlaylistDropdown(g);
			}
		}

		/// <summary>
		/// Draws one +/- action button: rounded badge background (filled + bordered
		/// when active, faint when greyed out) with the glyph centered on top, all in
		/// monochrome shades of the panel's own foreground color.
		/// </summary>
		private void DrawRoundedIconButton(Graphics g, Rectangle bounds, string glyph, bool active, Color fg)
		{
			int fillAlpha = active ? 26 : 10;
			int borderAlpha = active ? 70 : 25;
			var glyphColor = active ? fg : Color.FromArgb(90, fg);

			var fillBrush = GetBrush(Color.FromArgb(fillAlpha, fg));
			var borderPen = GetPen(Color.FromArgb(borderAlpha, fg));
			var path = GetIconButtonPath();

			var state = g.Save();
			g.TranslateTransform(bounds.X, bounds.Y);
			g.FillPath(fillBrush, path);
			g.DrawPath(borderPen, path);
			g.Restore(state);

			TextRenderer.DrawText(g, glyph, iconFont, bounds, glyphColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private Rectangle MinusButtonBounds(Rectangle widget) => new Rectangle(widget.Right - 44, PlaylistActionRowBounds.Y, 18, 18);
		private Rectangle PlusButtonBounds(Rectangle widget) => new Rectangle(widget.Right - 18, PlaylistActionRowBounds.Y, 18, 18);

		private void DrawPlaylistDropdown(Graphics g)
		{
			var fg = panel.ForeColor;
			var bounds = PlaylistDropdownBounds;

			// Plain rectangle, same reasoning as the widget above - rounding is
			// reserved for the +/- buttons specifically.
			g.FillRectangle(GetBrush(panel.BackColor), bounds);
			g.DrawRectangle(GetPen(Color.FromArgb(60, fg)), bounds);

			int rowY = bounds.Y;
			var createRect = new Rectangle(bounds.X, rowY, bounds.Width, 18);
			TextRenderer.DrawText(g, "Create Playlist", smallBold, createRect, fg,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
			rowY += 18;

			if (_dropdownPlaylists != null)
			{
				foreach (var pl in _dropdownPlaylists)
				{
					var rowRect = new Rectangle(bounds.X, rowY, bounds.Width, 18);
					var label = TruncateCached(pl.Name, smallRegular);
					TextRenderer.DrawText(g, label, smallRegular, rowRect, fg,
						TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
					rowY += 18;
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
					int rowIndex = relativeY / 18;

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
				var minusHit = MinusButtonBounds(widget);
				var plusHit = PlusButtonBounds(widget);

				if (minusHit.Contains(clickPoint))
				{
					if (_playlistMembershipKnown && _trackInSelectedPlaylist)
					{
						RemoveCurrentTrackFromSelectedPlaylist();
					}
					return true;
				}

				if (plusHit.Contains(clickPoint))
				{
					if (_playlistMembershipKnown && !_trackInSelectedPlaylist)
					{
						AddCurrentTrackToSelectedPlaylist();
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
						// entry.Track is typed as IPlayableItem (a marker interface -
						// it doesn't expose Uri directly). Since the request above asks
						// for AdditionalTypes.Track only, the concrete type here is
						// FullTrack - the same class already used in TrackSearch().
						if (entry?.Track is FullTrack fullTrack &&
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

		private async void RemoveCurrentTrackFromSelectedPlaylist()
		{
			if (_playlistActionInProgress || _selectedPlaylist == null || string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}
			_playlistActionInProgress = true;

			try
			{
				var request = new PlaylistRemoveItemsRequest
				{
					Tracks = new List<PlaylistRemoveItemsRequest.Item>
					{
						new PlaylistRemoveItemsRequest.Item { Uri = "spotify:track:" + _trackID }
					}
				};
				await _spotify.Playlists.RemoveItems(_selectedPlaylist.Id, request).ConfigureAwait(false);

				_trackInSelectedPlaylist = false;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
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