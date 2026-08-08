using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using SpotifyAPI.Web.Http;

namespace SpotifyAPI.Web
{
  /// <summary>
  ///   This Authenticator requests new credentials token on demand and stores them into memory.
  ///   It is unable to query user specifc details.
  /// </summary>
  public class PKCEAuthenticator : IAuthenticator
  {
    /// <summary>
    ///   Initiate a new instance. The token will be refreshed once it expires.
    ///   The initialToken will be updated with the new values on refresh!
    /// </summary>
    public PKCEAuthenticator(string clientId, PKCETokenResponse initialToken, string path)
    {
      Ensure.ArgumentNotNull(clientId, nameof(clientId));
      Ensure.ArgumentNotNull(initialToken, nameof(initialToken));

      InitialToken = initialToken;
      ClientId = clientId;
      Path = path;
    }

    /// <summary>
    /// This event is called once a new refreshed token was aquired
    /// </summary>
    public event EventHandler<PKCETokenResponse>? TokenRefreshed;


    /// <summary>
    ///   The ClientID, defined in a spotify application in your Spotify Developer Dashboard
    /// </summary>
    public string ClientId { get; }

    public string Path { get; }

    /// <summary>
    ///   The inital token passed to the authenticator. Fields will be updated on refresh.
    /// </summary>
    /// <value></value>
    public PKCETokenResponse InitialToken { get; }

    public void SerializeConfig(PKCETokenResponse data)
    {

        using (StreamWriter file = new StreamWriter(Path, false))
        {
            XmlSerializer controlsDefaultsSerializer = new XmlSerializer(typeof(PKCETokenResponse));
            controlsDefaultsSerializer.Serialize(file, data);
            file.Close();
        }
    }

    public async Task Apply(IRequest request, IAPIConnector apiConnector)
    {
      Ensure.ArgumentNotNull(request, nameof(request));

      if (InitialToken.IsExpired)
      {
        var tokenRequest = new PKCETokenRefreshRequest(ClientId, InitialToken.RefreshToken);
        var refreshedToken = await OAuthClient.RequestToken(tokenRequest, apiConnector).ConfigureAwait(false);

        InitialToken.AccessToken = refreshedToken.AccessToken;
        InitialToken.CreatedAt = refreshedToken.CreatedAt;
        InitialToken.ExpiresIn = refreshedToken.ExpiresIn;
        InitialToken.Scope = refreshedToken.Scope;
        InitialToken.TokenType = refreshedToken.TokenType;
        InitialToken.RefreshToken = refreshedToken.RefreshToken;

        SerializeConfig(InitialToken);

        TokenRefreshed?.Invoke(this, InitialToken);
      }

      request.Headers["Authorization"] = $"{InitialToken.TokenType} {InitialToken.AccessToken}";
    }
  }
}
