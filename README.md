# mb_Spotify-Plugin

A Spotify integration plugin for [MusicBee](https://www.getmusicbee.com/).

[⬇️ Skip to Installation & Setup](#installation--setup)

The plugin allows you to interact with your Spotify library directly from MusicBee, including searching for tracks, viewing Spotify track information and artwork, checking library status, and interacting with Spotify authentication.

> **Project status:** Actively maintained and under continued development.

## Screenshots

![MusicBee plugin panel](showcase/PluginPanel.png)

*Additional screenshots can be placed in the repository directory [showcase](showcase/).*

---

## Installation & Setup

### 1. Create a Spotify Developer App
Because of Spotify's Developer Mode restrictions, each user must have a premium account and configure their own application and provide a **Client ID** to the plugin.

1. Sign in to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
2. Click **Create an App**. Give it a name (e.g., `MusicBee Plugin`) and a brief description.
3. Add this link in the redirect Url - http://127.0.0.1:5000/callback.
4. Open your application's settings and copy the **Client ID**.
5. Configure the redirect URI required by mb_Spotify (if specified in the setup prompt) and save your changes.

> 🔴 **Security Warning:** The plugin only requires your **Client ID**. Never share your Spotify password or Client Secret with anyone.

### 2. Install the Plugin
1. Build the plugin or obtain the release files for `mb_Spotify`.
2. Copy the plugin files into MusicBee's Plugins directory.
   * *Example Windows path:* `C:\Users\<you>\AppData\Roaming\MusicBee\Plugins`
3. Open MusicBee and verify that the plugin appears in `Edit > Preferences > Plugins`.
4. Restart MusicBee.
5. When prompted, paste your **Client ID** to authorize the plugin with your Spotify account.

---

## Features

### Spotify Integration & Library Management
* **Authentication:** Secure OAuth 2.0 flow using PKCE, featuring persistent tokens and automatic background renewal.
* **Track Search:** Automatically searches Spotify for the track currently playing or selected in MusicBee, fetching relevant track information and artwork.
* **Library Status:** Independently checks if the current track or album is saved in your Spotify library, and whether you follow the artist.
* **Library Actions:** Add or remove supported tracks and albums directly from the MusicBee interface.

### Technical Highlights
* **Thread-Safe Reliability:** Token refresh operations are synchronized to prevent concurrent authentication overlaps and `invalid_grant` errors.
* **Search-Generation Control:** Prevents delayed asynchronous background searches from overwriting the UI when you skip rapidly through tracks.
* **Diagnostic Logging:** Records HTTP requests and Spotify API operations to help distinguish between API failures, token refresh issues, and UI state problems.

---

## Development

The project is written in **C#** and uses the Spotify Web API for integration. 

### Branches
* **`main`**: The current stable branch. Contains the latest fully tested working version.
* **`dev`**: The active development branch. New fixes, experiments, and future improvements are staged here.
* **`working-baseline`**: A preserved checkpoint representing an earlier known-good state of the plugin.

*(Note: The tag `spotify-stable-pre-framework` identifies the fully working state reached before future framework modernization.)*

---

## Project History

* **Original Development (up to v3.1):** Originally developed by Zachary Cohen (`zkhcohen`). This era established the base performance improvements, Spotify API 6.x.x integration, and fundamental PKCE authentication. The repository was archived in November 2022.
* **2026 Restoration:** Active development resumed to restore reliable Spotify authentication, fix UI freezes, introduce thread safety, and implement search-generation control. See the [`CHANGELOG`](CHANGELOG.md) for a detailed breakdown of ongoing improvements.

---

## Credits & License

* **Original Author:** Zachary Cohen (`zkhcohen`)
* **Current Maintainer:** Aditya Sharma (Resumed August 2026)

This project is licensed under the [MIT License](LICENSE). The original author's architecture, historical releases, and attribution are strictly preserved in the project history. Permission to continue development and distribute the project under the MIT License was granted by the original author.