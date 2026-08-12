using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpotifyAPI.Web.Http;
using URLs = SpotifyAPI.Web.SpotifyUrls;

namespace SpotifyAPI.Web
{
  public class FollowClient : APIClient, IFollowClient
  {
    public FollowClient(IAPIConnector apiConnector) : base(apiConnector) { }

    private static IDictionary<string, string> UrisQueryParam(IEnumerable<string> uris)
    {
      return new Dictionary<string, string> { { "uris", string.Join(",", uris) } };
    }

    public Task<List<bool>> CheckCurrentUser(FollowCheckCurrentUserRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var segment = request.TypeParam == FollowCheckCurrentUserRequest.Type.User ? "user" : "artist";
      var uris = request.Ids.Select(id => $"spotify:{segment}:{id}");
      return API.Get<List<bool>>(URLs.LibraryContains(), UrisQueryParam(uris));
    }

    // Not called anywhere in the plugin, and I don't have FollowCheckPlaylistRequest.cs to
    // confirm its post-migration shape - left untouched rather than guessing. Spotify's guide
    // lists GET /playlists/{id}/followers/contains as replaced by GET /me/library/contains too,
    // so this needs the same treatment before you rely on it.
    public Task<List<bool>> CheckPlaylist(string playlistId, FollowCheckPlaylistRequest request)
    {
      Ensure.ArgumentNotNullOrEmptyString(playlistId, nameof(playlistId));
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<List<bool>>(URLs.PlaylistFollowersContains(playlistId), request.BuildQueryParams());
    }

    public async Task<bool> Follow(FollowRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var segment = request.TypeParam == FollowRequest.Type.User ? "user" : "artist";
      var uris = request.Ids.Select(id => $"spotify:{segment}:{id}");
      var statusCode = await API.Put(URLs.Library(), UrisQueryParam(uris), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> FollowPlaylist(string playlistId)
    {
      Ensure.ArgumentNotNullOrEmptyString(playlistId, nameof(playlistId));
      var uris = new[] { $"spotify:playlist:{playlistId}" };
      var statusCode = await API.Put(URLs.Library(), UrisQueryParam(uris), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> FollowPlaylist(string playlistId, FollowPlaylistRequest request)
    {
      Ensure.ArgumentNotNullOrEmptyString(playlistId, nameof(playlistId));
      Ensure.ArgumentNotNull(request, nameof(request));
      // request's public/private flag has no equivalent param on the unified endpoint;
      // not used by the plugin, so flagging rather than guessing how it should map.
      var uris = new[] { $"spotify:playlist:{playlistId}" };
      var statusCode = await API.Put(URLs.Library(), UrisQueryParam(uris), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public Task<FollowedArtistsResponse> OfCurrentUser() => OfCurrentUser(new FollowOfCurrentUserRequest());

    public Task<FollowedArtistsResponse> OfCurrentUser(FollowOfCurrentUserRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      // GET /me/following (the list, not the check) wasn't in the Feb 2026 removal list - untouched
      return API.Get<FollowedArtistsResponse>(URLs.CurrentUserFollower(), request.BuildQueryParams());
    }

    public async Task<bool> Unfollow(UnfollowRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var segment = request.TypeParam == UnfollowRequest.Type.User ? "user" : "artist";
      var uris = request.Ids.Select(id => $"spotify:{segment}:{id}");
      var statusCode = await API.Delete(URLs.Library(), UrisQueryParam(uris), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> UnfollowPlaylist(string playlistId)
    {
      Ensure.ArgumentNotNullOrEmptyString(playlistId, nameof(playlistId));
      var uris = new[] { $"spotify:playlist:{playlistId}" };
      var statusCode = await API.Delete(URLs.Library(), UrisQueryParam(uris), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }
  }
}