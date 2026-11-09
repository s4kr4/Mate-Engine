using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// VoiceInputDisplay は、Attach された source にだけ 3 つのイベント（Heard・ListenChanged・Disconnected）を 1 つずつ購読し、
    /// 受け取った heard 1 つにつき AddPlayerBubble をその文のまま 1 回呼び、
    /// Tick のたびに InputPlaceholder.Text(connected) が前回書いた文と違うときだけ SetPlaceholder を呼ぶ。
    /// Attach は毎フレーム呼ばれ、null なら今の購読を保ち、同じ source なら何もせず、別の source なら前の source から解除して listen の状態を idle に戻す。
    /// 偽の source で購読者の数を数えてイベントを発火し、偽の view で AddPlayerBubble・SetPlaceholder の呼び出しを順に記録して確かめる。
    /// </summary>
    public class VoiceInputDisplayTests
    {
        // 本来の文は ChatBot の設定から渡される。既定の "Message me" と区別できる値にする
        private const string ConnectedText = "aruka に話しかける";

        private const string NotConnectedText = "頭脳につながっていません";
        private const string RecordingText = "聞いています…";
        private const string RecognizingText = "聞き取っています…";

        private enum CallKind
        {
            AddPlayerBubble,
            SetPlaceholder,
        }

        private sealed class Call
        {
            public Call(CallKind kind, string text)
            {
                Kind = kind;
                Text = text;
            }

            public CallKind Kind { get; }

            public string Text { get; }

            public override string ToString()
            {
                return Kind + "(" + Escape(Text) + ")";
            }
        }

        /// <summary>AddPlayerBubble と SetPlaceholder の呼び出しを順に記録する view。</summary>
        private sealed class RecordingView : IVoiceInputView
        {
            public List<Call> Calls { get; } = new List<Call>();

            public void AddPlayerBubble(string text)
            {
                Calls.Add(new Call(CallKind.AddPlayerBubble, text));
            }

            public void SetPlaceholder(string text)
            {
                Calls.Add(new Call(CallKind.SetPlaceholder, text));
            }

            public List<string> Bubbles()
            {
                return Calls.Where(call => call.Kind == CallKind.AddPlayerBubble).Select(call => call.Text).ToList();
            }

            public List<string> Placeholders()
            {
                return Calls.Where(call => call.Kind == CallKind.SetPlaceholder).Select(call => call.Text).ToList();
            }

            /// <summary>Clear の後の呼び出しだけを見るために、ここまでの記録を捨てる。</summary>
            public void Clear()
            {
                Calls.Clear();
            }

            public string Describe()
            {
                return Calls.Count == 0 ? "（呼び出しなし）" : string.Join(" ", Calls);
            }
        }

        /// <summary>3 つのイベントの購読者の数を数え、イベントを発火できる source。</summary>
        private sealed class FakeSource : IVoiceInputSource
        {
            private readonly string _name;
            private Action<string> _heard;
            private Action<ListenState> _listenChanged;
            private Action _disconnected;

            public FakeSource(string name = "source")
            {
                _name = name;
            }

            public event Action<string> Heard
            {
                add { _heard += value; }
                remove { _heard -= value; }
            }

            public event Action<ListenState> ListenChanged
            {
                add { _listenChanged += value; }
                remove { _listenChanged -= value; }
            }

            public event Action Disconnected
            {
                add { _disconnected += value; }
                remove { _disconnected -= value; }
            }

            public int HeardSubscribers => Count(_heard);

            public int ListenSubscribers => Count(_listenChanged);

            public int DisconnectedSubscribers => Count(_disconnected);

            public void RaiseHeard(string text)
            {
                _heard?.Invoke(text);
            }

            public void RaiseListen(ListenState state)
            {
                _listenChanged?.Invoke(state);
            }

            public void RaiseDisconnected()
            {
                _disconnected?.Invoke();
            }

            public string Describe()
            {
                return _name + "（Heard " + HeardSubscribers + "・ListenChanged " + ListenSubscribers + "・Disconnected " + DisconnectedSubscribers + "）";
            }

            private static int Count(Delegate handler)
            {
                return handler == null ? 0 : handler.GetInvocationList().Length;
            }
        }

        private static string Escape(string text)
        {
            if (text == null)
            {
                return "null";
            }

            // 制御文字・サロゲートは \uXXXX で表す（失敗メッセージとテスト結果の XML に生のまま載せない）
            var shown = text.Length > 40 ? text.Substring(0, 40) : text;
            var escaped = new System.Text.StringBuilder();
            foreach (var c in shown)
            {
                if (c == '\\')
                {
                    escaped.Append("\\\\");
                }
                else if (char.IsControl(c) || char.IsSurrogate(c))
                {
                    escaped.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    escaped.Append(c);
                }
            }

            var rest = text.Length > 40 ? "…（" + text.Length + " 文字）" : string.Empty;
            return "\"" + escaped + "\"" + rest;
        }

        private static void AssertSubscribedOnce(FakeSource source)
        {
            Assert.That(source.HeardSubscribers, Is.EqualTo(1), "Heard の購読がちょうど 1 つではない: " + source.Describe());
            Assert.That(source.ListenSubscribers, Is.EqualTo(1), "ListenChanged の購読がちょうど 1 つではない: " + source.Describe());
            Assert.That(source.DisconnectedSubscribers, Is.EqualTo(1), "Disconnected の購読がちょうど 1 つではない: " + source.Describe());
        }

        private static void AssertNotSubscribed(FakeSource source)
        {
            Assert.That(source.HeardSubscribers, Is.Zero, "Heard の購読が残っている: " + source.Describe());
            Assert.That(source.ListenSubscribers, Is.Zero, "ListenChanged の購読が残っている: " + source.Describe());
            Assert.That(source.DisconnectedSubscribers, Is.Zero, "Disconnected の購読が残っている: " + source.Describe());
        }

        // ---- コンストラクタ ----

        /// <summary>view が null なら ArgumentNullException を出す。</summary>
        [TestCase(ConnectedText)]
        [TestCase(null)]
        public void Constructor_ThrowsArgumentNullException_WhenViewIsNull(string connectedText)
        {
            Assert.Throws<ArgumentNullException>(() => new VoiceInputDisplay(connectedText, null));
        }

        /// <summary>connectedText が null なら、接続中の idle の Tick は空文字を書く。</summary>
        [Test]
        public void Tick_WritesEmptyText_WhenConnectedTextIsNullAndIdle()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(null, view);

            display.Tick(true);

            Assert.That(view.Calls.Count, Is.EqualTo(1), view.Describe());
            Assert.That(view.Calls[0].Kind, Is.EqualTo(CallKind.SetPlaceholder), view.Describe());
            Assert.That(view.Calls[0].Text, Is.EqualTo(string.Empty), view.Describe());
        }

        /// <summary>作っただけでは view を呼ばない（SetPlaceholder は Tick からだけ呼ばれる）。</summary>
        [Test]
        public void Constructor_DoesNotCallView()
        {
            var view = new RecordingView();

            new VoiceInputDisplay(ConnectedText, view);

            Assert.That(view.Calls, Is.Empty, "作っただけで view を呼んだ: " + view.Describe());
        }

        // ---- heard を吹き出しに ----

        private static IEnumerable<TestCaseData> HeardTexts()
        {
            yield return new TestCaseData("こんにちは。").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIsSentence");
            yield return new TestCaseData(string.Empty).SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIsEmpty");
            yield return new TestCaseData(" ").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIsSingleSpace");
            yield return new TestCaseData("\t\u3000 \u00A0\u3000\t").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIsWhitespaceOnly");
            yield return new TestCaseData("\r\n").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIsCrLfOnly");
            yield return new TestCaseData("  前後に空白  ").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasSurroundingSpaces");
            yield return new TestCaseData(string.Concat(Enumerable.Repeat("長い文。", 2000))).SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextIs8000Characters");
            yield return new TestCaseData("前" + (char)0x00 + "中" + (char)0x07 + (char)0x1B + "[31m後" + (char)0x7F).SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasControlCharacters");
            yield return new TestCaseData("一行目\u2028二行目\u2029三行目\u0085四行目").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasUnicodeLineSeparators");
            yield return new TestCaseData("abc\u202Edef\u200B\uFEFF").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasBidiAndZeroWidthCharacters");
            yield return new TestCaseData(char.ConvertFromUtf32(0x1F600) + "と" + char.ConvertFromUtf32(0x1F468) + "\u200D" + char.ConvertFromUtf32(0x1F469) + "\u200D" + char.ConvertFromUtf32(0x1F467)).SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasEmoji");
            yield return new TestCaseData("切れた" + (char)0xD83D).SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextHasLoneSurrogate");
            yield return new TestCaseData("<b>太字</b>と**強調**").SetName("Heard_AddsPlayerBubbleWithTextAsIs_WhenTextLooksLikeMarkup");
        }

        /// <summary>Attach した source が heard を送ると、その文のまま AddPlayerBubble が 1 回呼ばれる（空・空白だけ・8,000 文字・制御文字・絵文字を含む）。</summary>
        [TestCaseSource(nameof(HeardTexts))]
        public void Heard_AddsPlayerBubbleWithTextAsIs(string text)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            source.RaiseHeard(text);

            Assert.That(view.Calls.Count, Is.EqualTo(1), "heard 1 つで view の呼び出しが 1 回ではない: " + view.Describe());
            Assert.That(view.Calls[0].Kind, Is.EqualTo(CallKind.AddPlayerBubble), view.Describe());
            Assert.That(view.Calls[0].Text, Is.EqualTo(text), "AddPlayerBubble に渡した文が heard の文と違う: " + view.Describe());
        }

        /// <summary>続けて届いた heard は、届いた順に 1 つずつ AddPlayerBubble に渡される（同じ文の繰り返し・空・空白だけ・長い文を含む）。</summary>
        [Test]
        public void Heard_AddsOnePlayerBubbleEach_InArrivalOrder()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            var texts = new[]
            {
                "一つ目。",
                "同じ文。",
                "同じ文。",
                "同じ文。",
                string.Empty,
                string.Empty,
                " ",
                string.Concat(Enumerable.Repeat("長い文。", 2000)),
                "最後。",
            };

            foreach (var text in texts)
            {
                source.RaiseHeard(text);
            }

            Assert.That(view.Bubbles(), Is.EqualTo(texts), view.Describe());
            Assert.That(view.Placeholders(), Is.Empty, "heard で SetPlaceholder を呼んだ: " + view.Describe());
        }

        /// <summary>heard は Tick の有無や接続の状態に関わらず、届いたときに AddPlayerBubble に渡される。</summary>
        [Test]
        public void Heard_AddsPlayerBubble_RegardlessOfTickAndConnection()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            source.RaiseHeard("Tick の前。");
            display.Tick(false);
            source.RaiseHeard("つながっていない Tick の後。");
            display.Tick(true);
            source.RaiseHeard("つながった Tick の後。");

            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "Tick の前。", "つながっていない Tick の後。", "つながった Tick の後。" }), view.Describe());
        }

        // ---- 同じ source への Attach ----

        /// <summary>同じ source に何度 Attach しても、3 つのイベントの購読はちょうど 1 つずつで、heard 1 つにつき AddPlayerBubble は 1 回だけ呼ばれる。</summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(60)]
        public void Attach_SubscribesOnce_WhenAttachedToSameSourceRepeatedly(int times)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();

            for (var i = 0; i < times; i++)
            {
                display.Attach(source);
            }

            source.RaiseHeard("一度だけ。");

            AssertSubscribedOnce(source);
            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "一度だけ。" }), view.Describe());
        }

        /// <summary>毎フレーム Attach と Tick を呼んでも、heard 1 つにつき AddPlayerBubble は 1 回で、listen の状態は保たれる。</summary>
        [Test]
        public void Attach_EveryFrameWithTick_KeepsOneSubscriptionAndListenState()
        {
            // ChatBot の Update と同じ呼び方
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();

            for (var frame = 0; frame < 10; frame++)
            {
                display.Attach(source);
                display.Tick(true);
            }

            source.RaiseListen(ListenState.Recording);
            source.RaiseHeard("聞き取った文。");
            for (var frame = 0; frame < 10; frame++)
            {
                display.Attach(source);
                display.Tick(true);
            }

            AssertSubscribedOnce(source);
            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "聞き取った文。" }), view.Describe());
            Assert.That(view.Placeholders(), Is.EqualTo(new[] { ConnectedText, RecordingText }), view.Describe());
        }

        // ---- Attach(null) ----

        /// <summary>まだ購読していないときの Attach(null) は、例外を出さず view も呼ばない。</summary>
        [Test]
        public void AttachNull_DoesNothing_BeforeAnySource()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);

            Assert.DoesNotThrow(() => display.Attach(null));
            Assert.DoesNotThrow(() => display.Attach(null));

            Assert.That(view.Calls, Is.Empty, view.Describe());
        }

        /// <summary>Attach(null) は今の購読を保ち、すでに購読している source の heard はそのまま届く。</summary>
        [Test]
        public void AttachNull_KeepsCurrentSubscription()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            Assert.DoesNotThrow(() => display.Attach(null));
            source.RaiseHeard("まだ届く。");

            AssertSubscribedOnce(source);
            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "まだ届く。" }), view.Describe());
        }

        /// <summary>Attach(null) は listen の状態を idle に戻さない。</summary>
        [Test]
        public void AttachNull_KeepsListenState()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);

            display.Attach(null);
            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { RecordingText }), view.Describe());
        }

        // ---- Detach ----

        /// <summary>Detach は今の source から 3 つのイベントの購読をすべて解除する。</summary>
        [Test]
        public void Detach_UnsubscribesAllThreeEvents()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            display.Detach();

            AssertNotSubscribed(source);
        }

        /// <summary>Detach の後は、その source の heard と listen は表示を変えない。</summary>
        [Test]
        public void Detach_IgnoresHeardAndListenFromThatSource()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            display.Tick(true);
            view.Clear();

            display.Detach();
            source.RaiseHeard("届かない。");
            source.RaiseListen(ListenState.Recording);
            source.RaiseDisconnected();
            display.Tick(true);

            Assert.That(view.Calls, Is.Empty, "Detach した source の知らせで表示が変わった: " + view.Describe());
        }

        /// <summary>Detach した source の heard・listen・切断は、その後に別の source を Attach しても表示を変えない。</summary>
        [Test]
        public void Detach_ThenAttachAnother_IgnoresDetachedSourceNotices()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var detached = new FakeSource("Detach した source");
            var current = new FakeSource("今の source");
            display.Attach(detached);
            display.Detach();
            display.Attach(current);
            current.RaiseListen(ListenState.Recording);
            display.Tick(true);
            view.Clear();

            detached.RaiseHeard("届かない。");
            detached.RaiseDisconnected();
            display.Tick(true);
            detached.RaiseListen(ListenState.Recognizing);
            display.Tick(true);

            AssertNotSubscribed(detached);
            Assert.That(view.Calls, Is.Empty, "Detach した source の知らせで表示が変わった: " + view.Describe());
        }

        /// <summary>購読前の Detach と、何度も呼ぶ Detach は例外を出さない。</summary>
        [Test]
        public void Detach_DoesNotThrow_BeforeAttachOrWhenRepeated()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();

            Assert.DoesNotThrow(() => display.Detach());
            display.Attach(source);
            Assert.DoesNotThrow(() => display.Detach());
            Assert.DoesNotThrow(() => display.Detach());
            Assert.DoesNotThrow(() => display.Detach());

            AssertNotSubscribed(source);
            Assert.That(view.Calls, Is.Empty, view.Describe());
        }

        /// <summary>Detach の後に同じ source へ Attach すると、また一度だけ購読し、heard 1 つにつき 1 回届く。</summary>
        [TestCase(1)]
        [TestCase(3)]
        public void Attach_AfterDetach_SubscribesOnceAgain(int cycles)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();

            for (var i = 0; i < cycles; i++)
            {
                display.Attach(source);
                display.Attach(source);
                display.Detach();
            }

            display.Attach(source);
            display.Attach(source);
            source.RaiseHeard("また届く。");

            AssertSubscribedOnce(source);
            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "また届く。" }), view.Describe());
        }

        // ---- 別の source への Attach ----

        /// <summary>別の source に Attach すると、前の source の購読をすべて解除し、新しい source に 1 つずつ購読する。</summary>
        [Test]
        public void Attach_ToAnotherSource_MovesSubscription()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var previous = new FakeSource("前の source");
            var next = new FakeSource("新しい source");
            display.Attach(previous);

            display.Attach(next);

            AssertNotSubscribed(previous);
            AssertSubscribedOnce(next);
        }

        /// <summary>別の source に Attach すると、前の source の heard・listen・切断は表示を変えず、新しい source の知らせだけが届く。</summary>
        [Test]
        public void Attach_ToAnotherSource_IgnoresPreviousSourceNotices()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var previous = new FakeSource("前の source");
            var next = new FakeSource("新しい source");
            display.Attach(previous);
            display.Attach(next);
            next.RaiseListen(ListenState.Recording);
            display.Tick(true);
            view.Clear();

            previous.RaiseHeard("前の source の文。");
            previous.RaiseDisconnected();
            display.Tick(true);
            previous.RaiseListen(ListenState.Recognizing);
            display.Tick(true);
            next.RaiseHeard("新しい source の文。");
            next.RaiseListen(ListenState.Recognizing);
            display.Tick(true);

            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "新しい source の文。" }), view.Describe());
            Assert.That(view.Placeholders(), Is.EqualTo(new[] { RecognizingText }), view.Describe());
        }

        /// <summary>別の source に Attach すると、listen の状態は idle に戻る。</summary>
        [TestCase(ListenState.Recording)]
        [TestCase(ListenState.Recognizing)]
        public void Attach_ToAnotherSource_ReturnsListenToIdle(ListenState state)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var previous = new FakeSource("前の source");
            var next = new FakeSource("新しい source");
            display.Attach(previous);
            previous.RaiseListen(state);
            display.Tick(true);

            display.Attach(next);
            display.Tick(true);

            Assert.That(view.Placeholders().Last(), Is.EqualTo(ConnectedText), view.Describe());
        }

        /// <summary>前の source に Attach し直すと、購読は前の source に 1 つずつ戻り、間の source からは外れる。</summary>
        [Test]
        public void Attach_BackToPreviousSource_MovesSubscriptionBack()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var first = new FakeSource("最初の source");
            var second = new FakeSource("次の source");

            display.Attach(first);
            display.Attach(second);
            display.Attach(first);
            first.RaiseHeard("最初の source の文。");
            second.RaiseHeard("次の source の文。");

            AssertSubscribedOnce(first);
            AssertNotSubscribed(second);
            Assert.That(view.Bubbles(), Is.EqualTo(new[] { "最初の source の文。" }), view.Describe());
        }

        // ---- listen とプレースホルダー ----

        /// <summary>つながっていて recording を受けると次の Tick で「聞いています…」、recognizing で「聞き取っています…」、idle で渡された文を書く。</summary>
        [Test]
        public void Tick_WritesListenText_WhenConnected()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            display.Tick(true);

            source.RaiseListen(ListenState.Recording);
            display.Tick(true);
            source.RaiseListen(ListenState.Recognizing);
            display.Tick(true);
            source.RaiseListen(ListenState.Idle);
            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { ConnectedText, RecordingText, RecognizingText, ConnectedText }), view.Describe());
        }

        /// <summary>頭脳が状態を飛ばしても、Tick は最後に受けた listen の状態の文を書く。</summary>
        [TestCase(ListenState.Recognizing, RecognizingText)]
        [TestCase(ListenState.Recording, RecordingText)]
        public void Tick_WritesLastListenText_WhenStatesAreSkipped(ListenState last, string expected)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            display.Tick(true);

            source.RaiseListen(ListenState.Idle);
            source.RaiseListen(last);
            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { ConnectedText, expected }), view.Describe());
        }

        /// <summary>接続中の idle では、渡された文をそのまま書く。</summary>
        [TestCase("Message me")]
        [TestCase(" 前後に空白 😀 ")]
        [TestCase("")]
        public void Tick_WritesConnectedTextAsIs_WhenConnectedAndIdle(string connectedText)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(connectedText, view);

            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { connectedText }), view.Describe());
        }

        /// <summary>listen を受けただけでは SetPlaceholder を呼ばず、次の Tick で書く。</summary>
        [Test]
        public void Listen_DoesNotWritePlaceholderUntilNextTick()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            display.Tick(true);
            view.Clear();

            source.RaiseListen(ListenState.Recording);
            var beforeTick = view.Describe();
            display.Tick(true);

            Assert.That(beforeTick, Is.EqualTo("（呼び出しなし）"), "listen を受けただけで view を呼んだ");
            Assert.That(view.Placeholders(), Is.EqualTo(new[] { RecordingText }), view.Describe());
        }

        // ---- つながっていない ----

        /// <summary>つながっていない Tick では、listen の状態に関わらず「頭脳につながっていません」を書く。</summary>
        [TestCase(ListenState.Idle)]
        [TestCase(ListenState.Recording)]
        [TestCase(ListenState.Recognizing)]
        public void Tick_WritesNotConnectedText_WhenNotConnected(ListenState state)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            source.RaiseListen(state);

            display.Tick(false);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { NotConnectedText }), view.Describe());
        }

        /// <summary>source を Attach する前でも、つながっていない Tick は「頭脳につながっていません」を書く。</summary>
        [Test]
        public void Tick_WritesNotConnectedText_BeforeAnySource()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);

            display.Tick(false);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { NotConnectedText }), view.Describe());
        }

        // ---- 切断 ----

        /// <summary>切断を受けると idle に戻り、つなぎ直した Tick では新しい listen が来るまで渡された文を書く。</summary>
        [TestCase(ListenState.Recording)]
        [TestCase(ListenState.Recognizing)]
        public void Disconnected_ReturnsToIdle_UntilNewListenAfterReconnect(ListenState state)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            source.RaiseListen(state);
            display.Tick(true);

            source.RaiseDisconnected();
            display.Tick(false);
            display.Tick(true);
            display.Tick(true);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);

            Assert.That(view.Placeholders().Skip(1), Is.EqualTo(new[] { NotConnectedText, ConnectedText, RecordingText }), view.Describe());
        }

        /// <summary>切断を受けた後、つながっていない Tick を挟まずにつながった Tick が来ても、渡された文を書く。</summary>
        [Test]
        public void Disconnected_ReturnsToIdle_WithoutNotConnectedTickBetween()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);

            source.RaiseDisconnected();
            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { RecordingText, ConnectedText }), view.Describe());
        }

        // ---- 文が変わったときだけ書く ----

        /// <summary>最初の Tick は必ず SetPlaceholder を呼ぶ（渡された文が空文字でも）。</summary>
        [TestCase(true, ConnectedText, ConnectedText)]
        [TestCase(false, ConnectedText, NotConnectedText)]
        [TestCase(true, "", "")]
        public void Tick_WritesOnFirstTick(bool connected, string connectedText, string expected)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(connectedText, view);

            display.Tick(connected);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { expected }), view.Describe());
        }

        /// <summary>前回書いた文と同じなら、Tick は SetPlaceholder を呼ばない。</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void Tick_DoesNotWriteAgain_WhenTextIsUnchanged(bool connected)
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);

            for (var i = 0; i < 30; i++)
            {
                display.Tick(connected);
            }

            Assert.That(view.Placeholders().Count, Is.EqualTo(1), view.Describe());
        }

        /// <summary>Tick は、接続と listen の任意の並びで、文が変わった Tick でだけ SetPlaceholder を呼ぶ。</summary>
        [Test]
        public void Tick_WritesOnlyWhenTextChanges()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            display.Tick(true);
            display.Tick(true);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);
            display.Tick(false);
            display.Tick(false);
            display.Tick(true);
            source.RaiseListen(ListenState.Recognizing);
            source.RaiseListen(ListenState.Recognizing);
            display.Tick(true);
            source.RaiseListen(ListenState.Idle);
            display.Tick(true);
            display.Tick(true);

            // つながっていない間も listen の状態は保たれ、つながると recording の文に戻る
            Assert.That(view.Placeholders(), Is.EqualTo(new[] { ConnectedText, RecordingText, NotConnectedText, RecordingText, RecognizingText, ConnectedText }), view.Describe());
        }

        /// <summary>Tick の間に listen が変わって元の状態に戻ったなら、文は前回と同じなので SetPlaceholder を呼ばない。</summary>
        [Test]
        public void Tick_DoesNotWrite_WhenListenChangesBackBeforeTick()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);
            display.Tick(true);

            source.RaiseListen(ListenState.Recording);
            source.RaiseListen(ListenState.Recognizing);
            source.RaiseListen(ListenState.Idle);
            display.Tick(true);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { ConnectedText }), view.Describe());
        }

        /// <summary>接続の状態が変わっても文が前回と同じなら、Tick は SetPlaceholder を呼ばない（比べるのは文そのもの）。</summary>
        [Test]
        public void Tick_DoesNotWrite_WhenConnectionChangesButTextIsSame()
        {
            // 渡された文が「頭脳につながっていません」と同じなら、つながっていてもいなくても同じ文になる
            var view = new RecordingView();
            var display = new VoiceInputDisplay(NotConnectedText, view);

            display.Tick(false);
            display.Tick(true);
            display.Tick(false);

            Assert.That(view.Placeholders(), Is.EqualTo(new[] { NotConnectedText }), view.Describe());
        }

        /// <summary>Tick は AddPlayerBubble を呼ばない。</summary>
        [Test]
        public void Tick_DoesNotAddPlayerBubble()
        {
            var view = new RecordingView();
            var display = new VoiceInputDisplay(ConnectedText, view);
            var source = new FakeSource();
            display.Attach(source);

            display.Tick(true);
            source.RaiseListen(ListenState.Recording);
            display.Tick(true);
            display.Tick(false);

            Assert.That(view.Bubbles(), Is.Empty, view.Describe());
        }
    }
}
