using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VRMate.Tests
{
    /// <summary>
    /// スクリプトで受信の結果・例外・接続の成否を決められる IWebSocket。
    /// 受信は Push* で積んだ順に返し、積まれていなければ積まれるまで（または Abort / Dispose まで）待つ。
    /// </summary>
    internal sealed class FakeWebSocket : IWebSocket
    {
        public enum ConnectMode
        {
            Succeed,
            Fail,
            HangHonoringCancel,
            HangIgnoringCancel,
        }

        private enum StepKind
        {
            Frame,
            Close,
            Error,
        }

        private sealed class Step
        {
            public StepKind Kind;
            public byte[] Bytes;
            public WebSocketMessageType Type;
            public bool EndOfMessage;
            public Exception Error;
        }

        private readonly object _gate = new object();
        private readonly Queue<Step> _steps = new Queue<Step>();
        private readonly SemaphoreSlim _stepsAvailable = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _aborted = new CancellationTokenSource();
        private readonly List<string> _sentMessages = new List<string>();
        private readonly List<WebSocketMessageType> _sentTypes = new List<WebSocketMessageType>();
        private readonly MemoryStream _sendBuffer = new MemoryStream();
        private Step _current;
        private int _currentOffset;
        private int _state = (int)WebSocketState.None;
        private int _receiveInFlight;
        private int _sendInFlight;
        private int _concurrentReceives;
        private int _concurrentSends;
        private int _connectCalls;
        private int _closeOutputCalls;
        private int _abortCalls;
        private int _disposeCalls;
        private WebSocketCloseStatus? _closeOutputStatus;
        private Uri _connectUri;

        public FakeWebSocket(int index)
        {
            Index = index;
        }

        public int Index { get; private set; }

        public ConnectMode Connect = ConnectMode.Succeed;

        public Func<Exception> ConnectError = () => new WebSocketException("connection refused (fake)");

        /// <summary>true なら SendAsync は Abort / Dispose / 受信の失敗まで終わらない。</summary>
        public volatile bool BlockSends;

        public TimeSpan SendLatency = TimeSpan.Zero;

        public WebSocketState State
        {
            get { return (WebSocketState)Volatile.Read(ref _state); }
        }

        public Uri ConnectUri
        {
            get { lock (_gate) { return _connectUri; } }
        }

        public int ConnectCalls
        {
            get { return Volatile.Read(ref _connectCalls); }
        }

        public int CloseOutputCalls
        {
            get { return Volatile.Read(ref _closeOutputCalls); }
        }

        public WebSocketCloseStatus? CloseOutputStatus
        {
            get { lock (_gate) { return _closeOutputStatus; } }
        }

        public int AbortCalls
        {
            get { return Volatile.Read(ref _abortCalls); }
        }

        public int DisposeCalls
        {
            get { return Volatile.Read(ref _disposeCalls); }
        }

        public int ConcurrentReceives
        {
            get { return Volatile.Read(ref _concurrentReceives); }
        }

        public int ConcurrentSends
        {
            get { return Volatile.Read(ref _concurrentSends); }
        }

        public string[] SentMessages
        {
            get { lock (_gate) { return _sentMessages.ToArray(); } }
        }

        public WebSocketMessageType[] SentTypes
        {
            get { lock (_gate) { return _sentTypes.ToArray(); } }
        }

        // ---- スクリプト ----

        public void PushText(string text)
        {
            PushFrame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true);
        }

        public void PushFrame(byte[] bytes, WebSocketMessageType type, bool endOfMessage)
        {
            Enqueue(new Step { Kind = StepKind.Frame, Bytes = bytes, Type = type, EndOfMessage = endOfMessage });
        }

        public void PushClose()
        {
            Enqueue(new Step { Kind = StepKind.Close });
        }

        public void PushError(Exception error)
        {
            Enqueue(new Step { Kind = StepKind.Error, Error = error });
        }

        private void Enqueue(Step step)
        {
            lock (_gate)
            {
                _steps.Enqueue(step);
            }

            _stepsAvailable.Release();
        }

        // ---- IWebSocket ----

        public async Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            Interlocked.Increment(ref _connectCalls);
            lock (_gate)
            {
                _connectUri = uri;
            }

            SetState(WebSocketState.Connecting);
            switch (Connect)
            {
                case ConnectMode.Succeed:
                    await Task.Delay(1).ConfigureAwait(false);
                    SetState(WebSocketState.Open);
                    return;
                case ConnectMode.Fail:
                    await Task.Delay(1).ConfigureAwait(false);
                    SetState(WebSocketState.Closed);
                    throw ConnectError();
                case ConnectMode.HangHonoringCancel:
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _aborted.Token))
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            SetState(WebSocketState.Aborted);
                            throw;
                        }
                    }

                    return;
                default:
                    // キャンセルにも Abort にも応えない接続
                    await new TaskCompletionSource<bool>().Task.ConfigureAwait(false);
                    return;
            }
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _receiveInFlight) > 1)
            {
                Interlocked.Increment(ref _concurrentReceives);
            }

            try
            {
                var state = State;
                if (state != WebSocketState.Open && state != WebSocketState.CloseSent)
                {
                    throw new WebSocketException("receive in state " + state + " (fake)");
                }

                if (_current == null)
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _aborted.Token))
                    {
                        try
                        {
                            await _stepsAvailable.WaitAsync(linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            if (_aborted.IsCancellationRequested && !ct.IsCancellationRequested)
                            {
                                throw new WebSocketException("aborted (fake)");
                            }

                            throw;
                        }
                    }

                    lock (_gate)
                    {
                        _current = _steps.Dequeue();
                        _currentOffset = 0;
                    }
                }

                var step = _current;
                switch (step.Kind)
                {
                    case StepKind.Error:
                        _current = null;
                        SetState(WebSocketState.Aborted);
                        _aborted.Cancel();
                        throw step.Error;
                    case StepKind.Close:
                        _current = null;
                        SetState(State == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived);
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "bye (fake)");
                    default:
                        var count = Math.Min(buffer.Count, step.Bytes.Length - _currentOffset);
                        Array.Copy(step.Bytes, _currentOffset, buffer.Array, buffer.Offset, count);
                        _currentOffset += count;
                        var finished = _currentOffset >= step.Bytes.Length;
                        if (finished)
                        {
                            _current = null;
                        }

                        return new WebSocketReceiveResult(count, step.Type, finished && step.EndOfMessage);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _receiveInFlight);
            }
        }

        public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _sendInFlight) > 1)
            {
                Interlocked.Increment(ref _concurrentSends);
            }

            try
            {
                var state = State;
                if (state != WebSocketState.Open && state != WebSocketState.CloseReceived)
                {
                    throw new WebSocketException("send in state " + state + " (fake)");
                }

                var copy = buffer.Array.Skip(buffer.Offset).Take(buffer.Count).ToArray();

                if (BlockSends)
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _aborted.Token))
                    {
                        try
                        {
                            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new WebSocketException("send aborted (fake)");
                        }
                    }
                }

                if (SendLatency > TimeSpan.Zero)
                {
                    await Task.Delay(SendLatency).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(0).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _sendBuffer.Write(copy, 0, copy.Length);
                    if (endOfMessage)
                    {
                        _sentMessages.Add(Encoding.UTF8.GetString(_sendBuffer.ToArray()));
                        _sentTypes.Add(type);
                        _sendBuffer.SetLength(0);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _sendInFlight);
            }
        }

        public Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken ct)
        {
            Interlocked.Increment(ref _closeOutputCalls);
            lock (_gate)
            {
                _closeOutputStatus = status;
            }

            SetState(State == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent);
            return Task.FromResult(true);
        }

        public void Abort()
        {
            Interlocked.Increment(ref _abortCalls);
            if (State != WebSocketState.Closed)
            {
                SetState(WebSocketState.Aborted);
            }

            _aborted.Cancel();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (State != WebSocketState.Closed)
            {
                SetState(WebSocketState.Aborted);
            }

            _aborted.Cancel();
        }

        private void SetState(WebSocketState state)
        {
            Volatile.Write(ref _state, (int)state);
        }
    }

    /// <summary>接続ごとの FakeWebSocket を作り、作った順に記録する。</summary>
    internal sealed class FakeSocketFactory
    {
        private readonly object _gate = new object();
        private readonly List<FakeWebSocket> _created = new List<FakeWebSocket>();
        private readonly Action<FakeWebSocket> _configure;
        private readonly CallTrace _trace;

        public FakeSocketFactory(Action<FakeWebSocket> configure, CallTrace trace)
        {
            _configure = configure;
            _trace = trace;
        }

        public int Count
        {
            get { lock (_gate) { return _created.Count; } }
        }

        public FakeWebSocket[] Created
        {
            get { lock (_gate) { return _created.ToArray(); } }
        }

        public FakeWebSocket this[int index]
        {
            get { lock (_gate) { return _created[index]; } }
        }

        public IWebSocket Create()
        {
            FakeWebSocket socket;
            lock (_gate)
            {
                socket = new FakeWebSocket(_created.Count);
                if (_configure != null)
                {
                    _configure(socket);
                }

                _created.Add(socket);
            }

            _trace.Add("factory" + socket.Index);
            return socket;
        }
    }

    /// <summary>呼ばれた待ち時間を記録する delay。BlockFrom 以降の呼び出しは Release まで（またはキャンセルまで）終わらない。</summary>
    internal sealed class FakeDelay
    {
        private readonly object _gate = new object();
        private readonly List<TimeSpan> _calls = new List<TimeSpan>();
        private readonly CallTrace _trace;
        private int _blockFrom = int.MaxValue;
        private TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
        private int _cancelled;

        public FakeDelay(CallTrace trace)
        {
            _trace = trace;
        }

        public TimeSpan[] Calls
        {
            get { lock (_gate) { return _calls.ToArray(); } }
        }

        public int Count
        {
            get { lock (_gate) { return _calls.Count; } }
        }

        public int CancelledCount
        {
            get { return Volatile.Read(ref _cancelled); }
        }

        /// <summary>index 回目（0 始まり）以降の呼び出しを止める。</summary>
        public void BlockFrom(int index)
        {
            lock (_gate)
            {
                _blockFrom = index;
            }
        }

        public void ReleaseAll()
        {
            TaskCompletionSource<bool> release;
            lock (_gate)
            {
                _blockFrom = int.MaxValue;
                release = _release;
                _release = new TaskCompletionSource<bool>();
            }

            release.TrySetResult(true);
        }

        public Task Invoke(TimeSpan delay, CancellationToken ct)
        {
            int index;
            bool block;
            Task release;
            lock (_gate)
            {
                index = _calls.Count;
                _calls.Add(delay);
                block = index >= _blockFrom;
                release = _release.Task;
            }

            _trace.Add("delay");
            if (!block)
            {
                return Task.FromResult(true);
            }

            return WaitAsync(release, ct);
        }

        private async Task WaitAsync(Task release, CancellationToken ct)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                await Task.WhenAny(release, cancelled.Task).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw new OperationCanceledException(ct);
            }
        }
    }

    /// <summary>factory と delay の呼ばれた順を記録する。</summary>
    internal sealed class CallTrace
    {
        private readonly object _gate = new object();
        private readonly List<string> _entries = new List<string>();

        public void Add(string entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        public string[] Entries
        {
            get { lock (_gate) { return _entries.ToArray(); } }
        }
    }

    /// <summary>Post されたものを実行しない同期コンテキスト。テストのスレッドが Unity のメインスレッドのように塞がっている状態を作る。</summary>
    internal sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private int _posted;

        public int Posted
        {
            get { return Volatile.Read(ref _posted); }
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref _posted);
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref _posted);
        }
    }
}
