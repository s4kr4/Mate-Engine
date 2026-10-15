using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace VRMate
{
    /// <summary>The part of ClientWebSocket that LinkClient uses, so tests can script the peer.</summary>
    public interface IWebSocket : IDisposable
    {
        WebSocketState State { get; }

        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

        Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);

        Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);

        Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken);

        void Abort();
    }

    public sealed class ClientWebSocketAdapter : IWebSocket
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();

        public ClientWebSocketAdapter()
        {
            // The brain is on loopback; a system proxy must never see this traffic.
            _socket.Options.Proxy = null;
        }

        public WebSocketState State
        {
            get { return _socket.State; }
        }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return _socket.ConnectAsync(uri, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            return _socket.ReceiveAsync(buffer, cancellationToken);
        }

        public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            return _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        }

        public void Abort()
        {
            _socket.Abort();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}
