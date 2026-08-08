mb_Spotify-Plugin for MusicBee

DESCRIPTION:
This plugin integrates Spotify with MusicBee, allowing you to browse and add Spotify albums, tracks, and follow artists directly from the MusicBee interface.

FEATURES:
- Spotify authorization support
- Add albums and tracks from Spotify into MusicBee
- Follow artists through the plugin
- Uses the SpotifyAPI.Web and SpotifyAPI.Web.Auth libraries

INSTALLATION:
1. Build the plugin with Visual Studio using the included solution file `MB-spoti\mb_Spotify-Plugin.sln`.
2. Copy the compiled plugin files from `MB-spoti\bin\Release` (or `bin\Debug`) into your MusicBee Plugins folder.
3. Restart MusicBee and enable the plugin from the MusicBee plugin manager.

REQUIREMENTS:
- MusicBee with plugin support enabled
- .NET Framework 4.6.2 or later
- Spotify account for authorization

BUILD NOTES:
- Project file: `MB-spoti\mb_Spotify-Plugin.csproj`
- Dependencies are referenced from `packages\Newtonsoft.Json.13.0.4`, `packages\Unosquare.Swan.Lite.3.1.0`, and `packages\System.ValueTuple.4.6.2`.
- Spotify API assemblies are built from `MB-spoti\SpotifyAPI.Web.Auth`.

KNOWN ISSUES:
- Some Spotify API authorization errors may appear in MusicBee's `ErrorLog.dat` when Spotify returns invalid grant information.
- If you see `System.AggregateException: A Task's exception(s) were not observed...` followed by `SpotifyAPI.Web.APIException: invalid_grant`, reauthorize Spotify and verify your client credentials.

CONTACT:
If you encounter bugs or need help, update the plugin documentation or reach out through the MusicBee forums.

DEVELOPERS:
- Previous developer: [Previous Developer Name]
- Current developer: [Your Name]