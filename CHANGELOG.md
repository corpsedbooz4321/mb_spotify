
# CHANGELOG
All notable changes to `mb_Spotify-Plugin` are documented here.

## 2026 — Aditya Sharma / Current Maintenance (Continued)

### August 18, 2026 — Bug Fixes & Refactoring

**Commits:** 
- `40a9ae2` - Fix NullReferenceException on slow MusicBee startup (closes #1)
- `b9dcbd2` - Removed junk created during Costura config and updated VS Code settings

* Fixed critical NullReferenceException that occurred when MusicBee started slowly
* Cleaned up build artifacts and configuration files created during Costura setup
* Updated VS Code workspace settings for improved development experience

---

### August 14, 2026 — UI Improvements & Performance Optimization

**Commits:** `635b8db`, `e272206`, `504ff71`, `fa581b6`, `8684fd3`

* Optimized artwork loading in `DrawPanel` for improved performance.
* Implemented artwork caching and search result optimization in `SpotifyIntegration`.
* Added new status messages with icons for tracks, albums, and artists in library rows.
* Implemented rounded rectangle drawing for improved UI appearance.
* Updated library row display with enhanced status messaging.

These changes improved the plugin's performance and user interface by implementing caching strategies and providing better visual feedback through icon-based status messages.

---

### August 11, 2026 — MIT Licensing

- Received permission from original author Zachary Cohen (`zkhcohen`) to continue development and distribute the project under the MIT License.
- Preserved the original author's attribution and project history.
- Added the MIT License to the maintained project.

## 2026 — Aditya Sharma / Current Maintenance

### August 11, 2026 — Stable Working State

**Commit:** `97c339b`

The plugin reached a fully working and significantly more robust state after extensive debugging of Spotify authentication, asynchronous requests, track searching, and panel updates.

* Improved error handling in `TrackSearch` and `DrawPanel`.
* Added search-generation control to prevent stale track-search results from overwriting newer results.
* Prevented stale asynchronous operations from incorrectly updating the MusicBee panel.
* Improved diagnostic logging for failed operations.
* Added safeguards against UI crashes caused by failed asynchronous operations.
* Stabilized track searching, authentication, artwork retrieval, track details, and library-status checks.

This commit represents the **last fully verified working state before further development**.

---

### August 11, 2026 — Thread-Safe Authentication & Search Handling

**Commit:** `39349d7`

* Refactored Spotify token-refresh logic in `PKCEAuthenticator`.
* Added thread-safe handling around token renewal.
* Prevented multiple simultaneous token-refresh operations.
* Improved `TrackSearch` handling to prevent stale data from being used.
* Improved error logging around asynchronous Spotify operations.

This addressed a major race condition where multiple simultaneous API requests could independently attempt to refresh an expired Spotify token, resulting in `invalid_grant` errors caused by reuse of the same authorization/refresh flow.

---

### August 11, 2026 — Track Search Safety Improvements

**Commits:** `ef362a8`, `5f39a0a`, `9377d12`

* Fixed index clamping in `TrackSearch` to prevent out-of-bounds errors.
* Improved track-result selection and validation.
* Implemented thread-safe token refresh in `AuthorizationCodeAuthenticator`.
* Added semaphore-based synchronization to prevent concurrent token-refresh requests.
* Updated documentation surrounding the authentication flow and race-condition fixes.

---

### August 11, 2026 — Authentication & UI Reliability

**Commits:** `a99ecad`, `17a8aba`, `2546632`, `166fde1`, `70d9394`

* Improved Spotify authentication flow.
* Prevented multiple simultaneous authentication attempts.
* Added user feedback for authentication requests and failures.
* Added immediate panel refresh after successful authentication.
* Improved UI responsiveness following authentication and track searches.
* Improved search-term formatting.
* Improved error handling for authentication failures.
* Updated project configuration for conditional plugin-file copying.

These changes resolved several issues where the plugin could remain visually stuck after authentication even though authentication itself had completed successfully.

---

### August 10, 2026 — API Diagnostics & Library Status

**Commits:** `7f33d74`, `95ecc87`, `57f231c`, `7407f32`

* Added detailed API request/response logging through `APIConnector`.
* Added Spotify library-status checks for:

  * Tracks
  * Albums
  * Followed artists
* Refactored library-check methods to use explicit boolean status values.
* Improved API request parameter naming.
* Added diagnostic logging for track-library checks.

The API logging became an important debugging tool for identifying real HTTP request concurrency and distinguishing Spotify API failures from UI/state-management problems.

---

### August 9, 2026 — Track Search & Authentication Debugging

**Commits:** `0a42299`, `7e53d3a`, `31fef45`, `47ad185`, `6b58ed5`

* Added validation for empty Spotify search terms.
* Improved retrieval and validation of track title and artist information.
* Fixed search-term handling for tracks containing spaces and special formatting.
* Added detailed debugging around Spotify authentication and track searching.
* Improved OAuth error handling and search-result logging.
* Removed temporary debug message boxes after diagnosing authentication and search behavior.
* Investigated asynchronous authentication and track-search behavior.

The debugging process revealed that MusicBee could trigger multiple asynchronous track-search operations close together, making shared search state vulnerable to being overwritten by newer track changes.

---

### August 8, 2026 — Project Restoration & Spotify Integration

**Commits:** `90b02d9`, `d5e9c75`, `fee3f39`, `c616897`

* Began active development on the archived MusicBee Spotify plugin.
* Added Spotify integration and supporting utility classes.
* Improved the Spotify authentication flow.
* Prevented multiple simultaneous authentication attempts.
* Changed token serialization from XML to JSON.
* Added development configuration for the C# tooling environment.
* Added and expanded project documentation.
* Documented Spotify authorization status and known issues.
* Updated `.gitignore` for local development and build files.

This marked the beginning of the project's modern continuation after the original repository had been archived.

---

### August 11, 2026 — Project Attribution & Maintenance Metadata

**Commit:** `e0c8559`

* Updated assembly metadata to identify the current maintainer.
* Preserved attribution to the original author.
* Updated project information to distinguish the original work from current maintenance.

The project continues to credit **Zachary Cohen (`zkhcohen`)** as the original author while identifying **Aditya Sharma** as the current maintainer/developer.

---

# Original Project History

The following entries preserve the historical changelog of the original `mb_Spotify-Plugin` project by **Zachary Cohen (`zkhcohen`)**.

## Beta 3.1

* Various bug fixes.
* Improved handling of network disconnections.
* Improvements to token refreshing.
* Additional general bug fixes.

## Release 3.0.5

* Upgraded Spotify API methods to the 6.x.x specification.
* Implemented PKCE authentication.
* Added token persistence.
* Added automatic token renewal.
* General performance improvements.

## Release 2.0.2

* Fixed error-log spam.

## Release 2.0

* Improved performance.
* Removed the previous 500-album restriction.
* Added automatic prompting to re-authenticate when the computer resumes from sleep.

---

# Original Author

**Zachary Cohen (`zkhcohen`)**

Original project: `mb_Spotify-Plugin`

The original repository was archived by its owner in November 2022.

The historical work and original release information remain attributed to the original author.

# Current Maintainer

**Aditya Sharma**

Active development resumed in August 2026, with the goal of maintaining, stabilizing, improving, and eventually modernizing the project while preserving the original project's history and attribution.
