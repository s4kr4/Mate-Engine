using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VRMate
{
    public enum LinkEventKind
    {
        Connected,
        Message,
        Disconnected,
    }

    public struct LinkEvent
    {
        public LinkEvent(LinkEventKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public LinkEventKind Kind { get; }

        /// <summary>The message text for Message events; otherwise null.</summary>
        public string Text { get; }
    }

    public sealed class LinkOptions
    {
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

        public int MaxMessageBytes { get; set; } = MessageAssembler.DefaultMaxBytes;

        public static LinkOptions Default
        {
            get { return new LinkOptions(); }
        }
    }

    /// <summary>
    /// Keeps one WebSocket connection to the brain alive: connects, reads whole text messages, sends
    /// queued text, and reconnects after RetryDelay whenever the connection fails or ends.
    /// All socket work runs on the thread pool; the owner polls TryDequeue from its own thread.
    /// </summary>
    public sealed class LinkClient : IDisposable
    {
        private const int ReceiveBufferSize = 16 * 1024;
        private const int MaxQueuedSends = 1024;
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxTaskDelay = TimeSpan.FromMilliseconds(int.MaxValue);
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly Uri _uri;
        private readonly Func<IWebSocket> _socketFactory;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _connectTimeout;
        private readonly TimeSpan _retryDelay;
        private readonly int _maxMessageBytes;
        private readonly ConcurrentQueue<LinkEvent> _events = new ConcurrentQueue<LinkEvent>();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly object _gate = new object();
        private Connection _current;
        private bool _started;
        private bool _disposed;

        public LinkClient(Uri uri, Func<IWebSocket> socketFactory, LinkOptions options)
            : this(uri, socketFactory, options, null)
        {
        }

        public LinkClient(Uri uri, Func<IWebSocket> socketFactory, LinkOptions options, Func<TimeSpan, CancellationToken, Task> delay)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }

            if (socketFactory == null)
            {
                throw new ArgumentNullException(nameof(socketFactory));
            }

            options = options ?? LinkOptions.Default;
            _uri = uri;
            _socketFactory = socketFactory;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _connectTimeout = Clamp(options.ConnectTimeout, TimeSpan.FromMilliseconds(1));
            _retryDelay = Clamp(options.RetryDelay, TimeSpan.Zero);
            _maxMessageBytes = Math.Max(0, options.MaxMessageBytes);
        }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return !_disposed && _current != null && _current.Socket.State == WebSocketState.Open;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }

                _started = true;
            }

            // Task.Run keeps the loop off the caller's SynchronizationContext. Unity's main-thread
            // context only runs continuations between frames, and ConfigureAwait(false) below keeps
            // every later continuation off it as well.
            Task.Run(() => RunAsync(_lifetime.Token));
        }

        public bool TryDequeue(out LinkEvent linkEvent)
        {
            return _events.TryDequeue(out linkEvent);
        }

        /// <summary>Queues a text frame on the current connection. False when not connected or the queue is full.</summary>
        public bool TrySend(string text)
        {
            if (text == null)
            {
                return false;
            }

            lock (_gate)
            {
                var connection = _current;
                if (_disposed || connection == null || connection.Socket.State != WebSocketState.Open)
                {
                    return false;
                }

                // One queue and one send loop per connection keep the order and never overlap SendAsync.
                if (connection.Outgoing.Count >= MaxQueuedSends)
                {
                    return false;
                }

                connection.Outgoing.Enqueue(text);
                connection.Signal.Release();
                return true;
            }
        }

        public void Dispose()
        {
            Connection connection;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                connection = _current;
                _current = null;
                LinkEvent ignored;
                while (_events.TryDequeue(out ignored))
                {
                }
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (AggregateException)
            {
            }

            if (connection != null)
            {
                Task.Run(() => ShutdownAsync(connection));
            }
        }

        private async Task RunAsync(CancellationToken lifetime)
        {
            var first = true;
            while (!lifetime.IsCancellationRequested)
            {
                if (!first)
                {
                    try
                    {
                        await _delay(_retryDelay, lifetime).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Cancellation is checked below; any other delay failure just retries now.
                    }

                    if (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                }

                first = false;
                try
                {
                    await RunOnceAsync(lifetime).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Whatever went wrong with this connection, keep trying to reconnect.
                }
            }
        }

        private async Task RunOnceAsync(CancellationToken lifetime)
        {
            IWebSocket socket;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                socket = _socketFactory();
            }

            if (socket == null)
            {
                return;
            }

            try
            {
                if (await ConnectAsync(socket, lifetime).ConfigureAwait(false))
                {
                    await RunConnectionAsync(socket).ConfigureAwait(false);
                }
            }
            finally
            {
                AbortQuietly(socket);
                try
                {
                    socket.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private async Task<bool> ConnectAsync(IWebSocket socket, CancellationToken lifetime)
        {
            using (var connectCancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
            {
                Task connect;
                try
                {
                    connect = socket.ConnectAsync(_uri, connectCancel.Token);
                }
                catch (Exception)
                {
                    return false;
                }

                // A connect that ignores its token must still be abandoned on time, so the timeout
                // is a separate task rather than a cancellation of the connect itself.
                var timeout = Task.Delay(_connectTimeout, lifetime);
                var winner = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
                if (winner != connect)
                {
                    Observe(connect);
                    try
                    {
                        connectCancel.Cancel();
                    }
                    catch (AggregateException)
                    {
                    }

                    AbortQuietly(socket);
                    return false;
                }

                try
                {
                    await connect.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return false;
                }

                return socket.State == WebSocketState.Open;
            }
        }

        private async Task RunConnectionAsync(IWebSocket socket)
        {
            var connection = new Connection(socket);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _current = connection;
                _events.Enqueue(new LinkEvent(LinkEventKind.Connected, null));
            }

            var sendLoop = Task.Run(() => SendLoopAsync(connection));
            try
            {
                await ReceiveLoopAsync(connection).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Any receive failure (WebSocketException, IOException, ObjectDisposedException, ...)
                // ends this connection; the outer loop reconnects.
            }
            finally
            {
                lock (_gate)
                {
                    if (_current == connection)
                    {
                        _current = null;
                    }

                    if (!_disposed)
                    {
                        _events.Enqueue(new LinkEvent(LinkEventKind.Disconnected, null));
                    }
                }

                // Unsent messages belong to this connection and are dropped with it.
                connection.Stop();
            }

            await Task.WhenAny(sendLoop, Task.Delay(CloseTimeout)).ConfigureAwait(false);
        }

        private async Task ReceiveLoopAsync(Connection connection)
        {
            var socket = connection.Socket;
            var assembler = new MessageAssembler(_maxMessageBytes);
            var buffer = new byte[ReceiveBufferSize];
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), connection.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Mono's ClientWebSocket does not answer the peer's close frame by itself.
                    await CloseQuietlyAsync(socket, WebSocketCloseStatus.NormalClosure, "closing").ConfigureAwait(false);
                    return;
                }

                var assembled = assembler.Append(buffer, 0, result.Count, result.MessageType == WebSocketMessageType.Text, result.EndOfMessage);
                if (assembled.Status == AssembleStatus.Text)
                {
                    lock (_gate)
                    {
                        if (!_disposed)
                        {
                            _events.Enqueue(new LinkEvent(LinkEventKind.Message, assembled.Text));
                        }
                    }
                }
                else if (assembled.Status == AssembleStatus.TooLarge)
                {
                    await CloseQuietlyAsync(socket, WebSocketCloseStatus.MessageTooBig, "message too big").ConfigureAwait(false);
                    return;
                }
            }
        }

        private async Task SendLoopAsync(Connection connection)
        {
            var socket = connection.Socket;
            try
            {
                while (true)
                {
                    await connection.Signal.WaitAsync(connection.Token).ConfigureAwait(false);
                    string text;
                    lock (_gate)
                    {
                        text = connection.Outgoing.Dequeue();
                    }

                    var bytes = Utf8.GetBytes(text);
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, connection.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failed send leaves the connection unusable; aborting it ends the receive loop,
                // which reports Disconnected and reconnects.
                if (!connection.Token.IsCancellationRequested)
                {
                    AbortQuietly(socket);
                }
            }
        }

        private static async Task ShutdownAsync(Connection connection)
        {
            // Close first so the brain sees a clean close, then stop the loops (which aborts the socket).
            await CloseQuietlyAsync(connection.Socket, WebSocketCloseStatus.NormalClosure, "avatar closing").ConfigureAwait(false);
            connection.Stop();
        }

        private static async Task CloseQuietlyAsync(IWebSocket socket, WebSocketCloseStatus status, string description)
        {
            try
            {
                using (var cancel = new CancellationTokenSource())
                {
                    var close = socket.CloseOutputAsync(status, description, cancel.Token);
                    var winner = await Task.WhenAny(close, Task.Delay(CloseTimeout)).ConfigureAwait(false);
                    if (winner != close)
                    {
                        Observe(close);
                        cancel.Cancel();
                        return;
                    }

                    await close.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The socket may already be closed or aborted; nothing else to do.
            }
        }

        private static void AbortQuietly(IWebSocket socket)
        {
            try
            {
                socket.Abort();
            }
            catch (Exception)
            {
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(
                t => { var ignored = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static TimeSpan Clamp(TimeSpan value, TimeSpan min)
        {
            if (value < min)
            {
                return min;
            }

            return value > MaxTaskDelay ? MaxTaskDelay : value;
        }

        private sealed class Connection
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();

            public Connection(IWebSocket socket)
            {
                Socket = socket;
                Token = _stop.Token;
            }

            public IWebSocket Socket { get; }

            public CancellationToken Token { get; }

            /// <summary>Guarded by the client's lock.</summary>
            public Queue<string> Outgoing { get; } = new Queue<string>();

            public SemaphoreSlim Signal { get; } = new SemaphoreSlim(0);

            public void Stop()
            {
                try
                {
                    _stop.Cancel();
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
