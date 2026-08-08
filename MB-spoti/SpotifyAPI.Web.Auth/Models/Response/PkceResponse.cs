namespace SpotifyAPI.Web.Auth
{
    public class PkceResponse
    {
        public PkceResponse(string code)
        {
            Ensure.ArgumentNotNullOrEmptyString(code, nameof(code));

            Code = code;
        }

        public string Code { get; set; } = default!;
        public string? State { get; set; } = default!;
    }
}
