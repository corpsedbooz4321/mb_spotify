using System;
using Newtonsoft.Json;

namespace SpotifyAPI.Web
{
  public class PlaylistTrack<T>
  {
    public DateTime? AddedAt { get; set; }
    public PublicUser AddedBy { get; set; } = default!;
    public bool IsLocal { get; set; }

    [JsonProperty("item")]
    [JsonConverter(typeof(PlayableItemConverter))]
    public T Track { get; set; } = default!;
  }
}

