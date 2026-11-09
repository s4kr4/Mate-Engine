using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 入力欄のプレースホルダーに出す文の決め方。
    /// つながっていないときは「頭脳につながっていません」。つながっているときは、最後に届いた listen の状態で決まり、
    /// recording なら「聞いています…」、recognizing なら「聞き取っています…」、idle か listen がまだ届いていないなら本来の文。
    /// 頭脳は終わるときに listen idle を送らないので、切断したら idle に戻り、つなぎ直しても新しい listen が来るまで本来の文のまま。
    /// 頭脳が状態を飛ばして送っても、最後に届いた状態に従う。
    /// </summary>
    public class InputPlaceholderTests
    {
        // 本来の文は ChatBot の設定から渡される。既定の "Message me" と区別できる値にする
        private const string ConnectedText = "aruka に話しかける";

        private const string NotConnectedText = "頭脳につながっていません";
        private const string RecordingText = "聞いています…";
        private const string RecognizingText = "聞き取っています…";

        private static readonly ListenState[] AllStates = { ListenState.Idle, ListenState.Recording, ListenState.Recognizing };

        private static string Expected(ListenState state)
        {
            switch (state)
            {
                case ListenState.Recording:
                    return RecordingText;
                case ListenState.Recognizing:
                    return RecognizingText;
                default:
                    return ConnectedText;
            }
        }

        private static InputPlaceholder Create()
        {
            return new InputPlaceholder(ConnectedText);
        }

        // ---- listen が届く前 ----

        [Test]
        public void Text_IsConnectedText_WhenConnectedBeforeAnyListen()
        {
            var placeholder = Create();

            Assert.That(placeholder.Text(true), Is.EqualTo(ConnectedText));
        }

        [Test]
        public void Text_IsNotConnectedText_WhenNotConnectedBeforeAnyListen()
        {
            var placeholder = Create();

            Assert.That(placeholder.Text(false), Is.EqualTo(NotConnectedText));
        }

        [Test]
        public void Text_IsTheGivenConnectedTextExactly_WhenConnected()
        {
            var texts = new[] { "Message me", "メッセージ 😀", " 前後に空白 " };

            foreach (var text in texts)
            {
                var placeholder = new InputPlaceholder(text);

                Assert.That(placeholder.Text(true), Is.EqualTo(text));
            }
        }

        // ---- listen の状態 ----

        [Test]
        public void Text_IsRecordingText_WhenListenRecordingArrives()
        {
            var placeholder = Create();

            placeholder.OnListen(ListenState.Recording);

            Assert.That(placeholder.Text(true), Is.EqualTo(RecordingText));
        }

        [Test]
        public void Text_IsRecognizingText_WhenListenRecognizingArrives()
        {
            var placeholder = Create();

            placeholder.OnListen(ListenState.Recognizing);

            Assert.That(placeholder.Text(true), Is.EqualTo(RecognizingText));
        }

        [Test]
        public void Text_FollowsEachStep_WhenListenGoesRecordingRecognizingIdle()
        {
            // 頭脳の順: recording → recognizing →（heard）→ idle →（say）
            var placeholder = Create();
            var seen = new List<string> { placeholder.Text(true) };

            placeholder.OnListen(ListenState.Recording);
            seen.Add(placeholder.Text(true));
            placeholder.OnListen(ListenState.Recognizing);
            seen.Add(placeholder.Text(true));
            placeholder.OnListen(ListenState.Idle);
            seen.Add(placeholder.Text(true));

            Assert.That(seen, Is.EqualTo(new[] { ConnectedText, RecordingText, RecognizingText, ConnectedText }));
        }

        [Test]
        public void Text_IsConnectedText_WhenListenIdleArrivesWithoutHeard()
        {
            // 聞き取れなかったときは heard が来ずに idle になる
            var placeholder = Create();
            placeholder.OnListen(ListenState.Recording);
            placeholder.OnListen(ListenState.Recognizing);

            placeholder.OnListen(ListenState.Idle);

            Assert.That(placeholder.Text(true), Is.EqualTo(ConnectedText));
        }

        [Test]
        public void Text_FollowsTheLastListen_ForEveryPairOfConsecutiveStates()
        {
            // 状態を飛ばす・同じ状態がもう一度来る（recording のまま次の recording など）を含む 9 通り
            foreach (var first in AllStates)
            {
                foreach (var second in AllStates)
                {
                    var placeholder = Create();
                    placeholder.OnListen(first);

                    placeholder.OnListen(second);

                    Assert.That(placeholder.Text(true), Is.EqualTo(Expected(second)), first + " → " + second);
                }
            }
        }

        [Test]
        public void Text_KeepsShowingTheState_WhenAskedEveryFrame()
        {
            var placeholder = Create();
            placeholder.OnListen(ListenState.Recording);

            var frames = Enumerable.Range(0, 5).Select(_ => placeholder.Text(true)).ToList();

            Assert.That(frames, Is.All.EqualTo(RecordingText));
        }

        // ---- 接続 ----

        [Test]
        public void Text_IsNotConnectedText_WhenNotConnectedWhateverTheLastListenWas()
        {
            foreach (var state in AllStates)
            {
                var placeholder = Create();
                placeholder.OnListen(state);

                Assert.That(placeholder.Text(false), Is.EqualTo(NotConnectedText), state.ToString());
            }
        }

        [Test]
        public void Text_IsNotConnectedText_WhenDisconnectedWhileRecording()
        {
            var placeholder = Create();
            placeholder.OnListen(ListenState.Recording);

            placeholder.OnDisconnected();

            Assert.That(placeholder.Text(false), Is.EqualTo(NotConnectedText));
        }

        [Test]
        public void Text_IsConnectedText_WhenReconnectedAfterDisconnectingWhileRecordingOrRecognizing()
        {
            // 頭脳は終わるときに idle を送らない。つなぎ直した相手（別の頭脳でもよい）から listen が来るまで本来の文
            foreach (var state in new[] { ListenState.Recording, ListenState.Recognizing })
            {
                var placeholder = Create();
                placeholder.OnListen(state);

                placeholder.OnDisconnected();

                Assert.That(placeholder.Text(true), Is.EqualTo(ConnectedText), state.ToString());
            }
        }

        [Test]
        public void Text_FollowsListenFromTheNewConnection_AfterReconnecting()
        {
            foreach (var state in AllStates)
            {
                var placeholder = Create();
                placeholder.OnListen(ListenState.Recording);
                placeholder.OnDisconnected();

                placeholder.OnListen(state);

                Assert.That(placeholder.Text(true), Is.EqualTo(Expected(state)), "recording → 切断 → " + state);
            }
        }

        [Test]
        public void Text_StaysConnectedText_WhenDisconnectedRepeatedlyWithoutListen()
        {
            var placeholder = Create();

            placeholder.OnDisconnected();
            placeholder.OnDisconnected();

            Assert.That(placeholder.Text(true), Is.EqualTo(ConnectedText));
            Assert.That(placeholder.Text(false), Is.EqualTo(NotConnectedText));
        }

        [Test]
        public void Text_ReturnsToConnectedText_WhenIdleArrivesAfterReconnectingAndRecordingAgain()
        {
            var placeholder = Create();
            placeholder.OnListen(ListenState.Recognizing);
            placeholder.OnDisconnected();
            placeholder.OnListen(ListenState.Recording);

            placeholder.OnListen(ListenState.Idle);

            Assert.That(placeholder.Text(true), Is.EqualTo(ConnectedText));
        }

        // ---- 本来の文が null ----

        [Test]
        public void Text_IsEmpty_WhenConnectedTextIsNullAndConnected()
        {
            // ChatBot の設定（Inspector の文字列）が null でも、入力欄には null ではなく空の文を渡す
            var placeholder = new InputPlaceholder(null);

            Assert.That(placeholder.Text(true), Is.Not.Null.And.EqualTo(string.Empty));
        }

        [Test]
        public void Text_IsNotConnectedText_WhenConnectedTextIsNullAndNotConnected()
        {
            var placeholder = new InputPlaceholder(null);

            Assert.That(placeholder.Text(false), Is.EqualTo(NotConnectedText));
        }

        [Test]
        public void Text_FollowsListen_WhenConnectedTextIsNull()
        {
            var placeholder = new InputPlaceholder(null);
            var seen = new List<string>();

            placeholder.OnListen(ListenState.Recording);
            seen.Add(placeholder.Text(true));
            placeholder.OnListen(ListenState.Recognizing);
            seen.Add(placeholder.Text(true));
            placeholder.OnListen(ListenState.Idle);
            seen.Add(placeholder.Text(true));

            Assert.That(seen, Is.EqualTo(new[] { RecordingText, RecognizingText, string.Empty }));
        }
    }
}
