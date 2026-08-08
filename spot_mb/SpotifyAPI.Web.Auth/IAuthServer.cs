using System;
using System.Threading.Tasks;

namespace SpotifyAPI.Web.Auth
{
  public interface IAuthServer : IDisposable
  {
    event Func<object, AuthorizationCodeResponse, Task> AuthorizationCodeReceived;

    event Func<object, ImplictGrantResponse, Task> ImplictGrantReceived;

    event Func<object, PkceResponse, Task> PkceReceived;

    Task Start();
    Task Stop();

    Uri BaseUri { get; }
  }
}
