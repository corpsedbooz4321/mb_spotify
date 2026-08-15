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
	/// - Clicking it opens a small dropdown, fetched LIVE from Spotify every time it
	///   opens (first 3 playlists returned by the account) - nothing is cached to disk.
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

		// Corner widget geometry, computed fresh each paint against the current panel width.
		private Rectangle PlaylistWidgetBounds => new Rectangle(panel.Width - 96, 4, 92, 16);
		private Rectangle PlaylistDropdownBounds
		{
			get
			{
				int rows = 1 + (_dropdownPlaylists?.Count ?? 0); // "Create Playlist" + playlists
				return new Rectangle(panel.Width - 156, 22, 152, rows * 18);
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
			using (var borderPen = new Pen(Color.FromArgb(50, fg)))
			{
				g.DrawRectangle(borderPen, widget);
			}

			if (_selectedPlaylist == null)
			{
				TextRenderer.DrawText(g, "Playlist", smallRegular, widget, fg,
					TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
			}
			else
			{
				var name = Truncate(_selectedPlaylist.Name, smallRegular);

				// Reserve the right-hand ~38px of the widget for the two icon buttons
				// so the name never overlaps them, regardless of how long it is.
				var nameRect = new Rectangle(widget.X + 4, widget.Y, widget.Width - 42, widget.Height);
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

			using (var path = RoundedRect(bounds, 5))
			using (var fillBrush = new SolidBrush(Color.FromArgb(fillAlpha, fg)))
			using (var borderPen = new Pen(Color.FromArgb(borderAlpha, fg)))
			{
				g.FillPath(fillBrush, path);
				g.DrawPath(borderPen, path);
			}

			TextRenderer.DrawText(g, glyph, iconFont, bounds, glyphColor,
				TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
		}

		private Rectangle MinusButtonBounds(Rectangle widget) => new Rectangle(widget.Right - 38, widget.Y - 1, 18, 18);
		private Rectangle PlusButtonBounds(Rectangle widget) => new Rectangle(widget.Right - 19, widget.Y - 1, 18, 18);

		private void DrawPlaylistDropdown(Graphics g)
		{
			var fg = panel.ForeColor;
			var bounds = PlaylistDropdownBounds;

			// Plain rectangle, same reasoning as the widget above - rounding is
			// reserved for the +/- buttons specifically.
			using (var bgBrush = new SolidBrush(panel.BackColor))
			using (var borderPen = new Pen(Color.FromArgb(60, fg)))
			{
				g.FillRectangle(bgBrush, bounds);
				g.DrawRectangle(borderPen, bounds);
			}

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
					var label = Truncate(pl.Name, smallRegular);
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

		private async void OpenPlaylistDropdown()
		{
			_playlistDropdownOpen = true;
			_dropdownPlaylists = null; // show just "Create Playlist" while the live fetch is in flight
			panel.Invalidate();

			try
			{
				// Live every time, per spec - nothing about the playlist list is cached.
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

				_dropdownPlaylists = first3;
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("OpenPlaylistDropdown (fetch playlists) failed: " + ex.GetType().Name + " - " + ex.Message);
				_dropdownPlaylists = new List<SimplePlaylist>();
			}

			RefreshPanelUi();
		}

		private async void SelectPlaylist(SimplePlaylist playlist)
		{
			_selectedPlaylist = playlist;
			_playlistMembershipKnown = false;
			_trackInSelectedPlaylist = false;
			RefreshPanelUi();

			if (string.IsNullOrWhiteSpace(_trackID))
			{
				return;
			}

			var myGeneration = playlist; // capture, in case the user picks a different playlist mid-check
			string trackUri = "spotify:track:" + _trackID;

			try
			{
				bool inPlaylist = await IsTrackInPlaylistAsync(playlist.Id, trackUri).ConfigureAwait(false);

				// If the user has since selected a different playlist (or a track change
				// happened) while this check was in flight, drop the stale result.
				if (!ReferenceEquals(_selectedPlaylist, myGeneration))
				{
					return;
				}

				_trackInSelectedPlaylist = inPlaylist;
				_playlistMembershipKnown = true;
				RefreshPanelUi();
			}
			catch (Exception ex)
			{
				mbApiInterface.MB_Trace("SelectPlaylist (membership check) failed: " + ex.GetType().Name + " - " + ex.Message);
				if (ReferenceEquals(_selectedPlaylist, myGeneration))
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
				Offset = 0
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