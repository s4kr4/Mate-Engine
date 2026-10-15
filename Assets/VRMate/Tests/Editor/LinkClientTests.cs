using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class LinkClientTests
    {
        private static readonly Uri BrainUri = new Uri("ws://127.0.0.1:48021");
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1234);
        private const int WaitMilliseconds = 5000;

        private static LinkOptions Options(int maxMessageBytes = 1024 * 1024, int connectTimeoutMs = 3000)
        {
            return new LinkOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMs),
                RetryDelay = RetryDelay,
                MaxMessageBytes = maxMessageBytes,
            };
        }

        /// <summary>LinkClient と fake の組。Start は Post を実行しない同期コンテキストの上で呼ぶ（Unity のメインスレッドと同じ状況）。</summary>
        private sealed class Harness : IDisposable
        {
            public readonly CallTrace Trace = new CallTrace();
            public readonly FakeSocketFactory Factory;
            public readonly FakeDelay Delay;
            public readonly LinkClient Client;
            public readonly List<LinkEvent> Events = new List<LinkEvent>();

            public Harness(LinkOptions options, Action<FakeWebSocket> configure = null, bool start = true)
            {
                Factory = new FakeSocketFactory(configure, Trace);
                Delay = new FakeDelay(Trace);
                Client = new LinkClient(BrainUri, Factory.Create, options, Delay.Invoke);
                if (start)
                {
                    Start();
                }
            }

            public void Start()
            {
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                try
                {
                    Client.Start();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }

            public void Collect()
            {
                LinkEvent ev;
                while (Client.TryDequeue(out ev))
                {
                    Events.Add(ev);
                }
            }

            public string Describe()
            {
                Collect();
                return "events=[" + string.Join(", ", Events.Select(e => e.Kind + (e.Kind == LinkEventKind.Message ? "(" + Shorten(e.Text) + ")" : string.Empty)))
                    + "] trace=[" + string.Join(" ", Trace.Entries) + "]";
            }

            private static string Shorten(string text)
            {
                if (text == null)
                {
                    return "null";
                }

                return text.Length <= 40 ? text : text.Substring(0, 40) + "…(" + text.Length + ")";
            }

            /// <summary>条件が成り立つまでイベントを集めながら待つ。上限を超えたらテストを失敗させる。</summary>
            public void WaitUntil(Func<bool> condition, string because, int timeoutMs = WaitMilliseconds)
            {
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    Collect();
                    if (condition())
                    {
                        return;
                    }

                    if (watch.ElapsedMilliseconds > timeoutMs)
                    {
                        Assert.Fail("待ちの上限を超えた（" + timeoutMs + "ms）: " + because + " " + Describe());
                    }

                    Thread.Sleep(5);
                }
            }

            public void WaitForEventCount(int count, string because)
            {
                WaitUntil(() => Events.Count >= count, because);
            }

            /// <summary>しばらく待ってもイベントが増えないことを確かめる。</summary>
            public void AssertQuiet(int milliseconds, string because)
            {
                var before = Events.Count;
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < milliseconds)
                {
                    Collect();
                    Thread.Sleep(5);
                }

                Collect();
                Assert.That(Events.Count, Is.EqualTo(before), because + " " + Describe());
            }

            public FakeWebSocket WaitForSocket(int index)
            {
                WaitUntil(() => Factory.Count > index, "socket " + index + " が作られる");
                return Factory[index];
            }

            public FakeWebSocket WaitForConnection(int index)
            {
                var socket = WaitForSocket(index);
                WaitUntil(() => Events.Count(e => e.Kind == LinkEventKind.Connected) > ConnectedBefore(index), "接続 " + index + " の Connected");
                return socket;
            }

            private int ConnectedBefore(int index)
            {
                return Factory.Created.Take(index).Count(s => s.Connect == FakeWebSocket.ConnectMode.Succeed);
            }

            public LinkEventKind[] Kinds()
            {
                Collect();
                return Events.Select(e => e.Kind).ToArray();
            }

            public string[] Messages()
            {
                Collect();
                return Events.Where(e => e.Kind == LinkEventKind.Message).Select(e => e.Text).ToArray();
            }

            public void Dispose()
            {
                Client.Dispose();
                Delay.ReleaseAll();
            }
        }

        // ---- 既定値 ----

        [Test]
        public void Default_HasThreeSecondTimeoutOneSecondRetryAnd8MiBLimit()
        {
            var options = LinkOptions.Default;

            Assert.That(options.ConnectTimeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(options.RetryDelay, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(options.MaxMessageBytes, Is.EqualTo(8 * 1024 * 1024));
        }

        // ---- 接続 ----

        [Test]
        public void Start_ConnectsToGivenUriAndReportsConnected()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);

                Assert.That(socket.ConnectUri, Is.EqualTo(BrainUri));
                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected }));
                h.WaitUntil(() => h.Client.IsConnected, "IsConnected");
            }
        }

        [Test]
        public void IsConnectedAndTrySend_AreFalse_BeforeStart()
        {
            using (var h = new Harness(Options(), start: false))
            {
                Assert.That(h.Client.IsConnected, Is.False);
                Assert.That(h.Client.TrySend("{\"type\":\"input\",\"text\":\"x\"}"), Is.False);
                Assert.That(h.Factory.Count, Is.EqualTo(0));
                LinkEvent ev;
                Assert.That(h.Client.TryDequeue(out ev), Is.False);
            }
        }

        [Test]
        public void Start_RunsWithoutCallerContext_WhenCallerThreadNeverPumps()
        {
            // Harness は Post を実行しない同期コンテキストの上で Start を呼ぶ
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                socket.PushText("hello");

                h.WaitUntil(() => h.Messages().Length == 1, "メッセージ");
                Assert.That(h.Messages(), Is.EqualTo(new[] { "hello" }));
            }
        }

        [Test]
        public void Start_RetriesAfterDelay_WhenConnectFailsWithoutReportingEvents()
        {
            using (var h = new Harness(Options(), s => s.Connect = s.Index < 3 ? FakeWebSocket.ConnectMode.Fail : FakeWebSocket.ConnectMode.Succeed))
            {
                h.WaitForConnection(3);
                h.AssertQuiet(100, "接続のあとに余計なイベントが無い");

                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected }), h.Describe());
                Assert.That(h.Delay.Calls, Is.EqualTo(new[] { RetryDelay, RetryDelay, RetryDelay }), h.Describe());
                Assert.That(h.Factory.Count, Is.EqualTo(4));
                Assert.That(h.Factory.Created.All(s => s.ConnectCalls == 1), Is.True, "接続ごとに新しい socket を作る");
            }
        }

        [Test]
        public void Start_KeepsRetrying_WhenConnectFailsManyTimes()
        {
            using (var h = new Harness(Options(), s => s.Connect = s.Index < 60 ? FakeWebSocket.ConnectMode.Fail : FakeWebSocket.ConnectMode.Succeed))
            {
                h.WaitUntil(() => h.Kinds().Contains(LinkEventKind.Connected), "60 回失敗したあとの Connected", 15000);

                Assert.That(h.Factory.Count, Is.EqualTo(61));
                Assert.That(h.Delay.Count, Is.EqualTo(60));
                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected }), h.Describe());
            }
        }

        [Test]
        public void Start_IsNotConnected_WhileConnectKeepsFailing()
        {
            using (var h = new Harness(Options(), s => s.Connect = FakeWebSocket.ConnectMode.Fail))
            {
                h.Delay.BlockFrom(2);
                h.WaitUntil(() => h.Delay.Count >= 3, "3 回目の delay");

                Assert.That(h.Client.IsConnected, Is.False);
                Assert.That(h.Client.TrySend("{}"), Is.False);
                Assert.That(h.Kinds(), Is.Empty, h.Describe());
            }
        }

        [Test]
        public void Start_AbandonsConnect_WhenConnectExceedsTimeoutAndHonorsCancellation()
        {
            using (var h = new Harness(Options(connectTimeoutMs: 100), s => s.Connect = s.Index == 0 ? FakeWebSocket.ConnectMode.HangHonoringCancel : FakeWebSocket.ConnectMode.Succeed))
            {
                var watch = Stopwatch.StartNew();
                h.WaitForConnection(1);

                Assert.That(watch.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(80), "打ち切りまで待つ");
                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected }), h.Describe());
                Assert.That(h.Delay.Calls, Is.EqualTo(new[] { RetryDelay }), h.Describe());
            }
        }

        [Test]
        public void Start_AbandonsConnect_WhenConnectExceedsTimeoutAndIgnoresCancellation()
        {
            using (var h = new Harness(Options(connectTimeoutMs: 100), s => s.Connect = s.Index == 0 ? FakeWebSocket.ConnectMode.HangIgnoringCancel : FakeWebSocket.ConnectMode.Succeed))
            {
                h.WaitForConnection(1);

                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected }), h.Describe());
                Assert.That(h.Delay.Calls, Is.EqualTo(new[] { RetryDelay }), h.Describe());
            }
        }

        [Test]
        public void Start_CallsDelayBetweenEveryReconnect()
        {
            using (var h = new Harness(Options(), s => s.Connect = s.Index % 2 == 0 ? FakeWebSocket.ConnectMode.Fail : FakeWebSocket.ConnectMode.Succeed))
            {
                var first = h.WaitForConnection(1);
                first.PushError(new WebSocketException("peer died"));
                var second = h.WaitForConnection(3);
                second.PushClose();
                h.WaitForConnection(5);

                var trace = h.Trace.Entries;
                var factories = trace.Select((entry, index) => new { entry, index }).Where(x => x.entry.StartsWith("factory", StringComparison.Ordinal)).Select(x => x.index).ToArray();
                for (var i = 1; i < factories.Length; i++)
                {
                    var between = trace.Skip(factories[i - 1] + 1).Take(factories[i] - factories[i - 1] - 1).ToArray();
                    Assert.That(between, Is.EqualTo(new[] { "delay" }), "factory の間に delay が 1 回: " + string.Join(" ", trace));
                }

                Assert.That(h.Delay.Calls.All(d => d == RetryDelay), Is.True, h.Describe());
            }
        }

        // ---- 受信 ----

        [Test]
        public void Receive_ReportsEachTextMessageInOrder()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                socket.PushText("{\"type\":\"say\",\"id\":1}");
                socket.PushText("二つ目😀");
                socket.PushText(string.Empty);

                h.WaitUntil(() => h.Messages().Length == 3, "3 つのメッセージ");

                Assert.That(h.Messages(), Is.EqualTo(new[] { "{\"type\":\"say\",\"id\":1}", "二つ目😀", string.Empty }));
                Assert.That(socket.ConcurrentReceives, Is.EqualTo(0), "ReceiveAsync を重ねて呼ばない");
            }
        }

        [Test]
        public void Receive_JoinsFragments_WhenMessageSpansFramesAndSplitsCharacters()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                var bytes = Encoding.UTF8.GetBytes("あいう😀えお");
                socket.PushFrame(bytes.Take(1).ToArray(), WebSocketMessageType.Text, false);
                socket.PushFrame(bytes.Skip(1).Take(10).ToArray(), WebSocketMessageType.Text, false);
                socket.PushFrame(bytes.Skip(11).ToArray(), WebSocketMessageType.Text, true);

                h.WaitUntil(() => h.Messages().Length == 1, "1 つのメッセージ");

                Assert.That(h.Messages(), Is.EqualTo(new[] { "あいう😀えお" }));
            }
        }

        [Test]
        public void Receive_ReportsWholeMessage_WhenMessageIsLargerThanReceiveBuffer()
        {
            using (var h = new Harness(Options(maxMessageBytes: 4 * 1024 * 1024)))
            {
                var socket = h.WaitForConnection(0);
                var text = string.Concat(Enumerable.Range(0, 200000).Select(i => i % 7 == 0 ? "😀" : "あ"));
                socket.PushText(text);

                h.WaitUntil(() => h.Messages().Length == 1, "大きなメッセージ");

                Assert.That(h.Messages()[0], Is.EqualTo(text));
            }
        }

        [Test]
        public void Receive_DropsBinaryMessages_AndKeepsReadingText()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                socket.PushFrame(Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}"), WebSocketMessageType.Binary, false);
                socket.PushFrame(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true);
                socket.PushText("after");

                h.WaitUntil(() => h.Messages().Length >= 1, "テキスト");
                h.AssertQuiet(100, "余計なイベントが無い");

                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected, LinkEventKind.Message }), h.Describe());
                Assert.That(h.Messages(), Is.EqualTo(new[] { "after" }));
                Assert.That(h.Client.IsConnected, Is.True);
            }
        }

        [Test]
        public void Receive_DeliversMessage_WhenSizeIsExactlyMaxMessageBytes()
        {
            using (var h = new Harness(Options(maxMessageBytes: 1024)))
            {
                var socket = h.WaitForConnection(0);
                var text = new string('x', 1024);
                socket.PushText(text);

                h.WaitUntil(() => h.Messages().Length == 1, "ちょうど上限のメッセージ");

                Assert.That(h.Messages()[0], Is.EqualTo(text));
                Assert.That(h.Client.IsConnected, Is.True);
            }
        }

        [TestCase(false, TestName = "Receive_ClosesAndReconnects_WhenSingleFrameExceedsMaxMessageBytes")]
        [TestCase(true, TestName = "Receive_ClosesAndReconnects_WhenFragmentsTogetherExceedMaxMessageBytes")]
        public void Receive_ClosesAndReconnects_WhenMessageExceedsMaxMessageBytes(bool fragmented)
        {
            using (var h = new Harness(Options(maxMessageBytes: 1024)))
            {
                var socket = h.WaitForConnection(0);
                if (fragmented)
                {
                    socket.PushFrame(Encoding.ASCII.GetBytes(new string('a', 600)), WebSocketMessageType.Text, false);
                    socket.PushFrame(Encoding.ASCII.GetBytes(new string('b', 425)), WebSocketMessageType.Text, true);
                }
                else
                {
                    socket.PushText(new string('x', 1025));
                }

                h.WaitForConnection(1);

                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected, LinkEventKind.Disconnected, LinkEventKind.Connected }), h.Describe());
                Assert.That(socket.CloseOutputStatus == WebSocketCloseStatus.MessageTooBig || socket.AbortCalls > 0, Is.True,
                    "MessageTooBig で閉じるか Abort する: close=" + socket.CloseOutputStatus + " abort=" + socket.AbortCalls);
                Assert.That(h.Delay.Calls, Is.EqualTo(new[] { RetryDelay }), h.Describe());
            }
        }

        [Test]
        public void Receive_RespondsWithCloseOutputAndReconnects_WhenPeerSendsClose()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                socket.PushText("last");
                socket.PushClose();

                h.WaitForConnection(1);

                Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected, LinkEventKind.Message, LinkEventKind.Disconnected, LinkEventKind.Connected }), h.Describe());
                Assert.That(socket.CloseOutputCalls, Is.GreaterThanOrEqualTo(1), "close フレームに応える");
                Assert.That(h.Delay.Calls, Is.EqualTo(new[] { RetryDelay }), h.Describe());
            }
        }

        [Test]
        public void Receive_ReportsDisconnectedAndReconnects_WhenReceiveThrows()
        {
            var errors = new Func<Exception>[]
            {
                () => new WebSocketException("The remote party closed the WebSocket connection without completing the close handshake."),
                () => new System.IO.IOException("connection reset"),
                () => new ObjectDisposedException("socket"),
                () => new InvalidOperationException("unexpected"),
            };

            foreach (var error in errors)
            {
                using (var h = new Harness(Options()))
                {
                    var socket = h.WaitForConnection(0);
                    var exception = error();
                    socket.PushError(exception);

                    h.WaitForConnection(1);

                    Assert.That(h.Kinds(), Is.EqualTo(new[] { LinkEventKind.Connected, LinkEventKind.Disconnected, LinkEventKind.Connected }),
                        exception.GetType().Name + " " + h.Describe());
                }
            }
        }

        [Test]
        public void Events_KeepPerConnectionOrder_AcrossReconnects()
        {
            using (var h = new Harness(Options(), s => s.Connect = s.Index == 1 ? FakeWebSocket.ConnectMode.Fail : FakeWebSocket.ConnectMode.Succeed))
            {
                var first = h.WaitForConnection(0);
                first.PushText("a");
                first.PushText("b");
                first.PushError(new WebSocketException("gone"));
                first.PushText("stale");
                var second = h.WaitForConnection(2);
                second.PushText("c");
                second.PushClose();
                h.WaitForConnection(3);
                h.AssertQuiet(100, "余計なイベントが無い");

                var expected = new[]
                {
                    "Connected", "Message:a", "Message:b", "Disconnected",
                    "Connected", "Message:c", "Disconnected",
                    "Connected",
                };
                var actual = h.Events.Select(e => e.Kind == LinkEventKind.Message ? "Message:" + e.Text : e.Kind.ToString()).ToArray();
                Assert.That(actual, Is.EqualTo(expected), h.Describe());
            }
        }

        [Test]
        public void Receive_DoesNotCarryPartialMessage_IntoNextConnection()
        {
            using (var h = new Harness(Options()))
            {
                var first = h.WaitForConnection(0);
                first.PushFrame(Encoding.UTF8.GetBytes("hel"), WebSocketMessageType.Text, false);
                first.PushError(new WebSocketException("gone"));
                var second = h.WaitForConnection(1);
                second.PushText("world");

                h.WaitUntil(() => h.Messages().Length == 1, "2 つ目の接続のメッセージ");

                Assert.That(h.Messages(), Is.EqualTo(new[] { "world" }));
            }
        }

        [Test]
        public void IsConnected_BecomesFalse_AfterDisconnected()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                h.Delay.BlockFrom(0);
                socket.PushError(new WebSocketException("gone"));

                h.WaitUntil(() => h.Kinds().Contains(LinkEventKind.Disconnected), "Disconnected");
                h.WaitUntil(() => !h.Client.IsConnected, "IsConnected が false");

                Assert.That(h.Client.TrySend("{\"type\":\"played\",\"id\":1}"), Is.False);
            }
        }

        // ---- 送信 ----

        [Test]
        public void TrySend_SendsTextFrames_InCallOrder()
        {
            using (var h = new Harness(Options(), s => s.SendLatency = TimeSpan.FromMilliseconds(1)))
            {
                var socket = h.WaitForConnection(0);
                h.WaitUntil(() => h.Client.IsConnected, "IsConnected");
                var messages = Enumerable.Range(0, 100).Select(i => "{\"type\":\"played\",\"id\":" + i + "}").ToArray();

                foreach (var message in messages)
                {
                    Assert.That(h.Client.TrySend(message), Is.True);
                }

                h.WaitUntil(() => socket.SentMessages.Length == 100, "100 件の送信");

                Assert.That(socket.SentMessages, Is.EqualTo(messages));
                Assert.That(socket.SentTypes.All(t => t == WebSocketMessageType.Text), Is.True);
                Assert.That(socket.ConcurrentSends, Is.EqualTo(0), "SendAsync を重ねて呼ばない");
            }
        }

        [Test]
        public void TrySend_KeepsEachCallersOrderWithoutOverlap_WhenCalledFromManyThreads()
        {
            using (var h = new Harness(Options(), s => s.SendLatency = TimeSpan.FromMilliseconds(1)))
            {
                var socket = h.WaitForConnection(0);
                h.WaitUntil(() => h.Client.IsConnected, "IsConnected");
                var threads = Enumerable.Range(0, 4).Select(t => new Thread(() =>
                {
                    for (var i = 0; i < 25; i++)
                    {
                        h.Client.TrySend("t" + t + ":" + i);
                    }
                })).ToList();

                threads.ForEach(t => t.Start());
                threads.ForEach(t => t.Join());
                h.WaitUntil(() => socket.SentMessages.Length == 100, "100 件の送信");

                var sent = socket.SentMessages;
                for (var t = 0; t < 4; t++)
                {
                    var prefix = "t" + t + ":";
                    var own = sent.Where(m => m.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    Assert.That(own, Is.EqualTo(Enumerable.Range(0, 25).Select(i => prefix + i).ToArray()));
                }

                Assert.That(socket.ConcurrentSends, Is.EqualTo(0), "SendAsync を重ねて呼ばない");
            }
        }

        [Test]
        public void TrySend_KeepsTextExactly_WhenTextHasMultibyteAndEscapes()
        {
            using (var h = new Harness(Options()))
            {
                var socket = h.WaitForConnection(0);
                h.WaitUntil(() => h.Client.IsConnected, "IsConnected");
                var text = "{\"type\":\"input\",\"text\":\"改行\\nと😀と\\\"\"}";

                Assert.That(h.Client.TrySend(text), Is.True);
                h.WaitUntil(() => socket.SentMessages.Length == 1, "送信");

                Assert.That(socket.SentMessages[0], Is.EqualTo(text));
            }
        }

        [Test]
        public void TrySend_DoesNotCarryUnsentMessages_IntoNextConnection()
        {
            using (var h = new Harness(Options(), s => s.BlockSends = s.Index == 0))
            {
                var first = h.WaitForConnection(0);
                h.WaitUntil(() => h.Client.IsConnected, "IsConnected");
                Assert.That(h.Client.TrySend("old-1"), Is.True);
                Assert.That(h.Client.TrySend("old-2"), Is.True);
                Assert.That(h.Client.TrySend("old-3"), Is.True);

                first.PushError(new WebSocketException("gone"));
                var second = h.WaitForConnection(1);
                h.WaitUntil(() => h.Client.IsConnected, "2 つ目の接続の IsConnected");
                Thread.Sleep(200);

                Assert.That(second.SentMessages, Is.Empty, "前の接続の送信の列を持ち越さない");
                Assert.That(h.Client.TrySend("new"), Is.True);
                h.WaitUntil(() => second.SentMessages.Length == 1, "新しい送信");
                Thread.Sleep(50);
                Assert.That(second.SentMessages, Is.EqualTo(new[] { "new" }));
            }
        }

        // ---- 終了 ----

        [Test]
        public void Dispose_ClosesConnectedSocketAndStopsEvents()
        {
            var h = new Harness(Options());
            try
            {
                var socket = h.WaitForConnection(0);
                h.Collect();
                var eventsBefore = h.Events.Count;

                h.Client.Dispose();
                var watch = Stopwatch.StartNew();
                while (socket.CloseOutputCalls == 0 && socket.AbortCalls == 0 && socket.DisposeCalls == 0 && watch.ElapsedMilliseconds < WaitMilliseconds)
                {
                    Thread.Sleep(5);
                }

                Assert.That(socket.CloseOutputCalls, Is.GreaterThanOrEqualTo(1), "つながっていれば close を試みる");
                socket.PushText("after dispose");
                socket.PushError(new WebSocketException("gone"));
                h.AssertQuiet(300, "Dispose のあとはイベントを出さない");
                Assert.That(h.Events.Count, Is.EqualTo(eventsBefore));
                Assert.That(h.Factory.Count, Is.EqualTo(1), "Dispose のあとはつなぎ直さない");
                Assert.That(h.Client.IsConnected, Is.False);
                Assert.That(h.Client.TrySend("{}"), Is.False);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void Dispose_CanBeCalledRepeatedly_AndBeforeStart()
        {
            var notStarted = new Harness(Options(), start: false);
            Assert.DoesNotThrow(() => notStarted.Client.Dispose());
            Assert.DoesNotThrow(() => notStarted.Client.Dispose());

            using (var h = new Harness(Options()))
            {
                h.WaitForConnection(0);
                Assert.DoesNotThrow(() => h.Client.Dispose());
                Assert.DoesNotThrow(() => h.Client.Dispose());
            }
        }

        [Test]
        public void Dispose_StopsRetryLoop_WhileWaitingForDelay()
        {
            using (var h = new Harness(Options(), s => s.Connect = FakeWebSocket.ConnectMode.Fail))
            {
                h.Delay.BlockFrom(0);
                h.WaitUntil(() => h.Delay.Count == 1, "1 回目の delay");

                h.Client.Dispose();
                h.Delay.ReleaseAll();
                h.AssertQuiet(300, "イベントを出さない");

                Assert.That(h.Factory.Count, Is.EqualTo(1), "Dispose のあとはつなぎ直さない " + h.Describe());
                Assert.That(h.Kinds(), Is.Empty);
            }
        }

        [Test]
        public void Dispose_StopsLoop_WhileConnectIsHanging()
        {
            using (var h = new Harness(Options(connectTimeoutMs: 60000), s => s.Connect = FakeWebSocket.ConnectMode.HangHonoringCancel))
            {
                var socket = h.WaitForSocket(0);
                h.WaitUntil(() => socket.ConnectCalls == 1, "接続を試みている");

                h.Client.Dispose();
                h.Delay.ReleaseAll();
                h.AssertQuiet(300, "イベントを出さない");

                Assert.That(h.Factory.Count, Is.EqualTo(1), h.Describe());
                Assert.That(h.Kinds(), Is.Empty);
            }
        }
    }
}
