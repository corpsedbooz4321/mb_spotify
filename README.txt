DESCRIPTION:

This plugin integrates Spotify with your MusicBee library. You can add albums and tracks and follow artists directly from MusicBee, through the plugin interface.

DEVELOPERS:
- Previous developer: [Previous Developer Name]
- Current developer: [Your Name]

STATUS:
- Spotify authorization is working.
- Existing known issues remain unresolved and are retained below.


INSTALLATION:

Just extract the contents of the "plugins" folder to your MusicBee Plugins directory. 

Let me know if you encounter any bugs - "ZKHCOHEN" on the MusicBee Forums.


KNOWN ISSUES:

The Spotify API is (incorrectly) reporting the following error in the MusicBee ErrorLog.dat file:

System.AggregateException: A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. 
As a result, the unobserved exception was rethrown by the finalizer thread. ---> SpotifyAPI.Web.APIException: invalid_grant