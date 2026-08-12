# mb_Spotify-Plugin

A Spotify integration plugin for [MusicBee](https://www.getmusicbee.com/).

The plugin allows you to interact with your Spotify library directly from MusicBee, including searching for tracks, viewing Spotify track information and artwork, checking library status, and interacting with Spotify authentication.

> **Project status:** Actively maintained and under continued development.

## Screenshots

![MusicBee plugin panel](showcase/PluginPanel.png)

Additional screenshots can be placed in the repository directory [showcase](showcase/). For example, future PNG files can be added there and referenced from this README.

---

## About

`mb_Spotify-Plugin` was originally created by **Zachary Cohen (`zkhcohen`)** and developed through the 3.x releases before the original repository was archived in November 2022.

Development resumed in August 2026 with the goal of maintaining and improving the plugin while preserving the original project's history and attribution.

The current development focuses on restoring reliability, improving asynchronous behavior, strengthening Spotify authentication, improving error handling, and eventually modernizing the underlying project.
---
## Installation

### Requirements

- [MusicBee](https://www.getmusicbee.com/)
- A Spotify account
- A Spotify Developer account
- A Spotify Developer App configured for mb_Spotify
- The **Client ID** from your Spotify Developer App
- An active Spotify Premium subscription if required by Spotify for the owner of a Developer Mode application

> <span style="color:green">Tip:</span> Create your Spotify Developer App and copy its **Client ID** before you install the plugin.

### Installing the plugin

1. Build the plugin or obtain the release files for `mb_Spotify`.
2. Copy the plugin files into MusicBee's Plugins directory.
   - Example Windows path: `C:\Users\<you>\AppData\Roaming\MusicBee\Plugins`
3. Open MusicBee and verify that the plugin appears in `Edit > Preferences > Plugins`.
4. Restart MusicBee after installation.

> <span style="color:red">Warning:</span> mb_Spotify only requires the Spotify **Client ID**. Do not share your Spotify password or Client Secret with anyone.

## Spotify Developer App Setup

mb_Spotify does **not** use a shared Spotify Developer App.

Each user must create and configure their own Spotify Developer App and provide its **Client ID** to mb_Spotify.

This is required because Spotify's current Developer Mode places restrictions on applications and authorized users.

### Important Information

Before using mb_Spotify with Spotify, you will need:

- A Spotify account.
- A Spotify Developer account.
- Your own Spotify Developer App.
- The **Client ID** from your Spotify Developer App.
- An active Spotify Premium subscription if required by Spotify for the owner of the Developer Mode application.

Your Spotify Developer App belongs to you. mb_Spotify does not require your Spotify password or Client Secret.

### Creating a Spotify Developer App

1. Sign in to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
2. Create a new application.
3. Give the application a name and description.
4. Open the application's settings.
5. Copy the **Client ID**.
6. Configure the redirect URI required by mb_Spotify.
7. Save the application settings.

### Configuring mb_Spotify

When mb_Spotify asks for a Spotify Client ID, paste the Client ID from your Spotify Developer App.

The Client ID identifies the Spotify Developer App that mb_Spotify will use when communicating with Spotify.

After the Client ID has been configured, continue with Spotify authorization and grant the permissions requested by the plugin.

##### NOW YOU HAVE YOU OWN MusicBee PLUGIN!!!

### Why does every user need their own application?

Spotify's current developer-access model places restrictions on Development Mode applications and their authorized users.

Using a separate application for each user means that mb_Spotify does not depend on a single shared Developer App.

Each user is responsible for their own:

- Spotify Developer App.
- Client ID.
- Spotify Developer account.
- Spotify API usage and restrictions.
- Spotify account requirements.

mb_Spotify only provides the MusicBee integration and communicates with Spotify using the application configured by the user.


---
## Current Features

### Spotify Integration

- Spotify authentication using PKCE.
- Persistent Spotify authentication tokens.
- Automatic token renewal.
- Spotify track searching.
- Spotify track information retrieval.
- Spotify artwork retrieval.

### Spotify Library

- Check whether a track is saved in the user's Spotify library.
- Check whether an album is saved in the user's Spotify library.
- Check whether an artist is followed.
- Add supported tracks and albums directly to the Spotify library.
- Remove supported tracks and albums directly from the Spotify library.
- Library operations are performed directly from the MusicBee interface.

### Reliability & Performance

- Thread-safe Spotify token refresh handling.
- Protection against simultaneous token-refresh operations.
- Search-generation control to prevent stale asynchronous search results.
- Independent handling of Spotify search results and library checks.
- Improved handling of Spotify API failures.
- Improved authentication error handling.
- Improved MusicBee panel refresh behavior.
- Improved recovery from asynchronous errors.
- Diagnostic logging for Spotify API operations.

### MusicBee Integration

- Spotify track information displayed directly in the MusicBee panel.
- Spotify artwork displayed in the panel.
- Library status displayed alongside Spotify track information.
- Spotify library actions available without leaving MusicBee.

---

## Authentication

mb_Spotify uses Spotify's OAuth 2.0 authorization flow with **PKCE**.

Each user connects mb_Spotify to their own Spotify Developer App. The application Client ID is configured during the initial setup, after which Spotify authorization is performed through the normal OAuth flow.

Authentication state is persisted so the plugin does not need to request authorization every time MusicBee starts.
The authentication system also handles token renewal and prevents multiple refresh operations from occurring simultaneously.

### Authentication reliability

During the 2026 maintenance work, several concurrency problems were identified and corrected.

Multiple Spotify API requests can occur almost simultaneously when MusicBee changes tracks. Previously, several requests could detect an expired token at the same time and attempt to refresh it concurrently.

This could result in Spotify returning:

```text
invalid_grant
```

Token refresh is now synchronized so that only one refresh operation occurs at a time.

### Permissions

mb_Spotify requests only the Spotify permissions required for its supported features.

These permissions allow the plugin to:

- Read the user's Spotify library status.
- Read followed-artist information.
- Modify supported library content.
- Search and retrieve Spotify track information.

Spotify authorization is handled by Spotify's OAuth system. mb_Spotify does not require or store the user's Spotify password.

This also sets us up nicely for the later **Spotify Developer App Setup tutorial**, because users will understand *why* they're providing a Client ID before we explain exactly how to create one.

## Track Search

mb_Spotify searches Spotify for the track currently selected in MusicBee and retrieves the corresponding Spotify track information.

The search process is independent from Spotify library-status checks. A track can be found successfully even if it is not saved in the user's Spotify library.

### Search reliability

MusicBee can generate multiple track-change notifications close together. Because Spotify requests are asynchronous, an older search can otherwise finish after a newer search and overwrite the current panel state.

The current implementation uses **search-generation control** to ensure that only the latest search is allowed to update the MusicBee panel.

Search input is also validated before requests are sent to Spotify, preventing empty or incomplete search requests.

### Library checks

After a Spotify track is successfully identified, the plugin can independently check:

- Whether the track is saved in the user's Spotify library.
- Whether the album is saved in the user's Spotify library.
- Whether the artist is followed.

A failure in one of these library checks does not cause a successful Spotify track search to be treated as a failed search.

For example, a track may be successfully found on Spotify while the library state is:

```text
Track:  Not in Library
Album:  Saved in Library
Artist: Not Followed
```

Or, if no track is matched:

```text
No Track Found!
```

---

## Diagnostic Logging

mb_Spotify includes diagnostic logging for Spotify API operations and important plugin state changes.

The logging system was expanded during the 2026 restoration to make it easier to distinguish between Spotify API problems and issues occurring inside the MusicBee integration.

Diagnostic information can help identify:

- Authentication failures.
- Token-refresh failures.
- Spotify API errors.
- HTTP request and response failures.
- Concurrent API operations.
- Stale or overlapping track searches.
- Track-search failures.
- Library-status check failures.
- Library modification failures.
- MusicBee panel and UI state problems.

This logging was particularly useful during the 2026 Spotify API compatibility work and continues to assist with troubleshooting and future development.

> <span style="color:red">**Note:** Diagnostic logs may contain Spotify API request information. When sharing logs publicly, remove any sensitive information or authentication data before posting them.</span>

### Security

> <span style="color:red">**Warning:** Never share your Spotify password or Client Secret with anyone.</span>

mb_Spotify only requires the Client ID for configuring the application. Spotify handles the actual user authorization through its OAuth authentication system.

For screenshots and a complete step-by-step walkthrough, see the setup tutorial in the project documentation.

## Development

The project is written in **C#** and uses the Spotify Web API through the included Spotify API components.

The main components include:

```text
spot_mb/
├── MusicBeeInterface.cs
├── PanelInterface.cs
├── SpotifyIntegration.cs
├── mb_Spotify-Plugin.cs
│
├── Authenticators/
│   ├── AuthorizationCodeAuthenticator.cs
│   └── PKCEAuthenticator.cs
│
└── SpotifyAPI.Web/
    └── Http/
        └── APIConnector.cs
```

The project also contains supporting models, Spotify API utilities, authentication components, and MusicBee integration code.

---

## Development Branches

The repository currently uses separate branches for stable and active development.

### `main`

The current stable branch.

It contains the latest fully tested working version.

### `dev`

The active development branch.

New fixes, experiments, and future improvements should be developed here before being merged into `main`.

### `working-baseline`

A preserved checkpoint representing the earlier known-good state of the plugin before the later authentication, concurrency, and UI fixes.

---

## Stable Checkpoint

The repository contains a tagged stable checkpoint:

```text
spotify-stable-pre-framework
```

This tag identifies the fully working state reached before future framework modernization.

It is intentionally preserved as a recovery point while further development continues.

---

## Project History

### Original Development

The plugin was originally developed by **Zachary Cohen (`zkhcohen`)**.

The original project progressed through several releases:

### Release 2.0

* Improved performance.
* Removed the previous 500-album restriction.
* Added automatic re-authentication prompts after the computer resumed from sleep.

### Release 2.0.2

* Fixed error-log spam.

### Release 3.0.5

* Upgraded Spotify API methods to the 6.x.x specification.
* Implemented PKCE authentication.
* Added token persistence.
* Added automatic token renewal.
* Added general speed improvements.

### Beta 3.1

* Fixed issues related to network disconnections.
* Improved refreshing behavior.
* Included various additional bug fixes.

The original repository was archived by its owner in November 2022.

---

## 2026 Development

Active development resumed in August 2026.

The initial restoration focused heavily on understanding the existing architecture and stabilizing the plugin before making larger structural changes.

Major work included:

* Restoring reliable Spotify authentication.
* Fixing authentication UI freezes.
* Improving authentication error handling.
* Adding API diagnostics.
* Fixing empty search terms.
* Improving track-search handling.
* Adding Spotify library-status checks.
* Fixing concurrent token-refresh operations.
* Adding semaphore-based synchronization.
* Preventing overlapping searches from producing stale UI data.
* Improving panel refresh behavior.
* Separating successful Spotify searches from library-check failures.
* Improving asynchronous error handling.
* Preserving the original project attribution and history.

See [`CHANGELOG`](CHANGELOG.md) for the detailed development history.

---

## Known Issues

The project is actively maintained and may still contain areas requiring further testing.

Spotify API behavior, authentication requirements, MusicBee notifications, and asynchronous request timing can all affect plugin behavior.

Additional testing is especially important when modifying:

* Authentication
* Token renewal
* Spotify API requests
* Track-change handling
* Library modifications
* MusicBee UI updates

---

## Roadmap

Future development may include:

* Further Spotify API compatibility improvements.
* Improved library modification support.
* More robust asynchronous request management.
* Additional error recovery.
* Improved logging and diagnostics.
* Project/framework modernization.
* Additional MusicBee integration improvements.
* Continued testing against current Spotify API behavior.

Framework modernization will be performed separately from the current stable implementation so that the existing working state remains recoverable.

---

## Credits

### Original Author

**Zachary Cohen (`zkhcohen`)**

The original `mb_Spotify-Plugin` project, architecture, and historical releases were created by Zachary Cohen.

### Current Maintainer

**Aditya Sharma**

Active maintenance and development resumed in August 2026.

The current development builds upon the original project while preserving the original author's work and attribution.

---

## License

This project is licensed under the [MIT License](LICENSE).

The original project was created by **Zachary Cohen (`zkhcohen`)**.  
Development and maintenance resumed in 2026 under **Aditya Sharma**.
The original author's work and attribution are preserved in the project history.
Permission to continue development and distribute the project under the MIT License was granted by the original author.

Refer to the repository's license files and original project documentation for licensing information.

**Original project:** Zachary Cohen / `zkhcohen`
**Current maintainer:** Aditya Sharma
From August 8, 2026
**Project:** `mb_Spotify-Plugin`
