using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
	/// <summary>
	/// Lets the user paste in the Client ID from their own Spotify Developer app.
	/// Spotify's Development Mode apps are capped at a small allowlist of users,
	/// and extended access is no longer granted to individual/hobbyist developers -
	/// so instead of shipping one shared app, each user creates their own free app
	/// and this plugin authenticates against it. Because this uses PKCE (no client
	/// secret involved), a bare Client ID is safe to store in plain text.
	/// </summary>
	public class ClientIdSetupForm : Form
	{
		private readonly TextBox _clientIdBox;

		public string ClientId => _clientIdBox.Text.Trim();

		public ClientIdSetupForm(string existingClientId)
		{
			Text = "Set Up Your Spotify App";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			MaximizeBox = false;
			MinimizeBox = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size(420, 300);

			var instructions = new Label
			{
				Left = 12,
				Top = 12,
				Width = 396,
				Height = 78,
				Text = "Spotify limits how many people can use a single developer app " +
					   "without going through their review process. To use this plugin, " +
					   "create your own free Spotify app (about a minute), then paste its " +
					   "Client ID below."
			};

			var dashboardLink = new LinkLabel
			{
				Left = 12,
				Top = 92,
				Width = 396,
				Text = "Open Spotify Developer Dashboard"
			};
			dashboardLink.LinkClicked += (s, e) =>
			{
				try
				{
					Process.Start(new ProcessStartInfo("https://developer.spotify.com/dashboard")
					{
						UseShellExecute = true
					});
				}
				catch (Exception ex)
				{
					MessageBox.Show(this, "Couldn't open the browser automatically.\n" +
						"Please visit https://developer.spotify.com/dashboard manually.\n\n" + ex.Message,
						"Spotify Plugin");
				}
			};

			var redirectLabel = new Label
			{
				Left = 12,
				Top = 122,
				Width = 396,
				Height = 34,
				Text = "In your app's settings, set Redirect URI to exactly this " +
					   "(click to select, then copy):"
			};

			var redirectBox = new TextBox
			{
				Left = 12,
				Top = 156,
				Width = 396,
				ReadOnly = true,
				Text = "http://127.0.0.1:5000/callback"
			};
			redirectBox.Click += (s, e) => redirectBox.SelectAll();

			var clientIdLabel = new Label
			{
				Left = 12,
				Top = 190,
				Width = 396,
				Text = "Your Client ID:"
			};

			_clientIdBox = new TextBox
			{
				Left = 12,
				Top = 210,
				Width = 396,
				Text = existingClientId ?? string.Empty
			};

			var okButton = new Button
			{
				Text = "Save",
				Left = 252,
				Top = 250,
				Width = 75,
				DialogResult = DialogResult.OK
			};
			okButton.Click += (s, e) =>
			{
				if (string.IsNullOrWhiteSpace(_clientIdBox.Text))
				{
					MessageBox.Show(this, "Please enter a Client ID.", "Spotify Plugin");
					DialogResult = DialogResult.None;
				}
			};

			var cancelButton = new Button
			{
				Text = "Cancel",
				Left = 333,
				Top = 250,
				Width = 75,
				DialogResult = DialogResult.Cancel
			};

			Controls.Add(instructions);
			Controls.Add(dashboardLink);
			Controls.Add(redirectLabel);
			Controls.Add(redirectBox);
			Controls.Add(clientIdLabel);
			Controls.Add(_clientIdBox);
			Controls.Add(okButton);
			Controls.Add(cancelButton);

			AcceptButton = okButton;
			CancelButton = cancelButton;
		}
	}
}