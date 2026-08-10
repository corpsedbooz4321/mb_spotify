using System.Collections.Generic;

namespace SpotifyAPI.Web
{
  public class LibraryCheckTracksRequest : RequestParams
  {
    /// <summary>
    ///
    /// </summary>
    /// <param name="uris">
    /// A comma-separated list of the Spotify uris for the tracks. Maximum: 50 uris.
    /// </param>
    public LibraryCheckTracksRequest(IList<string> uris)
    {
      Ensure.ArgumentNotNull(uris, nameof(uris));

      Uris = uris;
    }

    /// <summary>
    /// A comma-separated list of the Spotify uris for the tracks. Maximum: 50 uris.
    /// </summary>
    /// <value></value>
    [QueryParam("uris")]
    public IList<string> Uris { get; }
  }
}

