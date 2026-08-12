using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SpotifyAPI.Web.Http;

namespace SpotifyAPI.Web
{
  public class LibraryClient : APIClient, ILibraryClient
  {
    public LibraryClient(IAPIConnector apiConnector) : base(apiConnector) { }

    private static IDictionary<string, string> UrisQueryParam(IEnumerable<string> uris)
    {
      return new Dictionary<string, string> { { "uris", string.Join(",", uris) } };
    }

    private static IEnumerable<string> ToUris(IEnumerable<string> ids, string type)
    {
      return ids.Select(id => $"spotify:{type}:{id}");
    }

    public Task<List<bool>> CheckAlbums(LibraryCheckAlbumsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<List<bool>>(SpotifyUrls.LibraryContains(), UrisQueryParam(ToUris(request.Ids, "album")));
    }

    public Task<List<bool>> CheckShows(LibraryCheckShowsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<List<bool>>(SpotifyUrls.LibraryContains(), UrisQueryParam(ToUris(request.Ids, "show")));
    }

    public Task<List<bool>> CheckTracks(LibraryCheckTracksRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      // Stores full Spotify URIs already (see the SpotifyIntegration.cs fix below) - pass through as-is
      return API.Get<List<bool>>(SpotifyUrls.LibraryContains(), UrisQueryParam(request.Uris));
    }

    public Task<Paging<SavedAlbum>> GetAlbums() => API.Get<Paging<SavedAlbum>>(SpotifyUrls.LibraryAlbums());

    public Task<Paging<SavedAlbum>> GetAlbums(LibraryAlbumsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<Paging<SavedAlbum>>(SpotifyUrls.LibraryAlbums(), request.BuildQueryParams());
    }

    public Task<Paging<SavedShow>> GetShows() => API.Get<Paging<SavedShow>>(SpotifyUrls.LibraryShows());

    public Task<Paging<SavedShow>> GetShows(LibraryShowsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<Paging<SavedShow>>(SpotifyUrls.LibraryShows(), request.BuildQueryParams());
    }

    public Task<Paging<SavedTrack>> GetTracks() => API.Get<Paging<SavedTrack>>(SpotifyUrls.LibraryTracks());

    public Task<Paging<SavedTrack>> GetTracks(LibraryTracksRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      return API.Get<Paging<SavedTrack>>(SpotifyUrls.LibraryTracks(), request.BuildQueryParams());
    }

    public async Task<bool> RemoveAlbums(LibraryRemoveAlbumsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Delete(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "album")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> RemoveShows(LibraryRemoveShowsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Delete(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "show")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> RemoveTracks(LibraryRemoveTracksRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Delete(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "track")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> SaveAlbums(LibrarySaveAlbumsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Put(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "album")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> SaveShows(LibrarySaveShowsRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Put(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "show")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }

    public async Task<bool> SaveTracks(LibrarySaveTracksRequest request)
    {
      Ensure.ArgumentNotNull(request, nameof(request));
      var statusCode = await API.Put(SpotifyUrls.Library(), UrisQueryParam(ToUris(request.Ids, "track")), null).ConfigureAwait(false);
      return statusCode == HttpStatusCode.OK;
    }
  }
}