using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

    /// <summary>
    ///   Ensures only one token refresh (and one token-file write) happens at a time.
    ///   Without this, multiple concurrent requests (e.g. search + library-check calls
    ///   firing together on a track change) can each see an expired token and race to
    ///   refresh it. PKCE refresh tokens are single-use/rotating, so the losing request(s)
    ///   reuse an already-invalidated refresh token and fail - and, worse, concurrent
    ///   writes to the token file on disk can leave it corrupted for future launches.
    /// </summary>
    private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

    public void SerializeConfig(PKCETokenResponse data)
    {
      string json = JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
      using (StreamWriter file = new StreamWriter(Path, false))
      {
        file.Write(json);
      }
    }

    public async Task Apply(IRequest request, IAPIConnector apiConnector)
    {
      Ensure.ArgumentNotNull(request, nameof(request));

      if (InitialToken.IsExpired)
      {
        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
          // Re-check after acquiring the lock: another caller may have already
          // refreshed the token (and rotated the refresh token) while we were
          // waiting. If so, skip refreshing again with the now-stale value.
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
        }
        finally
        {
          _refreshLock.Release();
        }
      }

      request.Headers["Authorization"] = $"{InitialToken.TokenType} {InitialToken.AccessToken}";
    }
  }
}