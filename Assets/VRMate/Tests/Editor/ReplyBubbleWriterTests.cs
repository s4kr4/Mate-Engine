using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// ReplyBubbleWriter は、返事（connection と turn の組）ごとに吹き出しを 1 つ作り、その吹き出しに
    /// 「最後に始め直してから同じ返事で受け取った text を、受け取った順にすべてつないだ文字列」を書く。
    /// 表示中の文字列は読み戻さず、中身を一切変えない。吹き出しが無い・表示されていない・別の返事なら、その text から始め直す。
    /// 偽の view で Add / Set / IsShown の呼び出しを記録して確かめる。
    /// </summary>
    public class ReplyBubbleWriterTests
    {
        private const long Connection = 1;
        private const long Turn = 1;

        /// <summary>view が作った吹き出し。どの Add で作られたかを番号で持つ。</summary>
        private sealed class FakeBubble
        {
            public FakeBubble(int number)
            {
                Number = number;
            }

            public int Number { get; }

            public override string ToString()
            {
                return "#" + Number;
            }
        }

        private enum CallKind
        {
            Add,
            Set,
            IsShown,
        }

        private sealed class Call
        {
            public Call(CallKind kind, FakeBubble bubble, string text)
            {
                Kind = kind;
                Bubble = bubble;
                Text = text;
            }

            public CallKind Kind { get; }

            /// <summary>Add では作って返した吹き出し（null を返したなら null）、Set と IsShown では渡された吹き出し。</summary>
            public FakeBubble Bubble { get; }

            /// <summary>Add と Set に渡された文字列。IsShown では null。</summary>
            public string Text { get; }

            public override string ToString()
            {
                return Kind + "(" + (Bubble == null ? "null" : Bubble.ToString()) + (Kind == CallKind.IsShown ? string.Empty : ", " + Escape(Text)) + ")";
            }
        }

        /// <summary>呼び出しを順に記録する view。刈り込まれた吹き出しは Hide で表し、Add が null を返す回は ReturnNullFromNextAdd で指定する。</summary>
        private sealed class FakeView : IReplyBubbleView<FakeBubble>
        {
            private readonly HashSet<FakeBubble> _hidden = new HashSet<FakeBubble>();
            private int _nullAdds;

            public List<Call> Calls { get; } = new List<Call>();

            public List<FakeBubble> Bubbles { get; } = new List<FakeBubble>();

            /// <summary>IsShown に null が渡された回数。</summary>
            public int NullIsShownQueries { get; private set; }

            /// <summary>Set に null の吹き出しが渡された回数。</summary>
            public int NullSetTargets { get; private set; }

            public void Hide(FakeBubble bubble)
            {
                _hidden.Add(bubble);
            }

            public void ReturnNullFromNextAdd()
            {
                _nullAdds++;
            }

            public FakeBubble Add(string text)
            {
                FakeBubble bubble = null;
                if (_nullAdds > 0)
                {
                    _nullAdds--;
                }
                else
                {
                    bubble = new FakeBubble(Bubbles.Count + 1);
                    Bubbles.Add(bubble);
                }

                Calls.Add(new Call(CallKind.Add, bubble, text));
                return bubble;
            }

            public void Set(FakeBubble bubble, string text)
            {
                if (bubble == null)
                {
                    NullSetTargets++;
                }

                Calls.Add(new Call(CallKind.Set, bubble, text));
            }

            public bool IsShown(FakeBubble bubble)
            {
                if (bubble == null)
                {
                    NullIsShownQueries++;
                }

                Calls.Add(new Call(CallKind.IsShown, bubble, null));
                return bubble != null && !_hidden.Contains(bubble);
            }

            /// <summary>Add と Set の呼び出しだけを順に返す。</summary>
            public List<Call> Writes()
            {
                return Calls.Where(call => call.Kind != CallKind.IsShown).ToList();
            }

            public string Describe()
            {
                return Calls.Count == 0 ? "（呼び出しなし）" : string.Join(" ", Calls);
            }
        }

        private static string Escape(string text)
        {
            if (text == null)
            {
                return "null";
            }

            return "\"" + text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static void AssertAdd(Call call, string text, string context)
        {
            Assert.That(call.Kind, Is.EqualTo(CallKind.Add), context + ": Add ではなく " + call + " が呼ばれた");
            Assert.That(call.Text, Is.EqualTo(text), context + ": Add に渡した文字列が " + Escape(text) + " ではない: " + Escape(call.Text));
        }

        private static void AssertSet(Call call, FakeBubble bubble, string text, string context)
        {
            Assert.That(call.Kind, Is.EqualTo(CallKind.Set), context + ": Set ではなく " + call + " が呼ばれた");
            Assert.That(call.Bubble, Is.SameAs(bubble), context + ": Set の相手が " + bubble + " ではない: " + call);
            Assert.That(call.Text, Is.EqualTo(text), context + ": Set に渡した文字列が " + Escape(text) + " ではない: " + Escape(call.Text));
        }

        /// <summary>同じ返事で texts を順に足し、Add は最初の 1 回だけ、その後の Set はその時点までの全文を Add が返した吹き出しに書くことを確かめる。</summary>
        private static void AssertOneBubbleHoldsWholeReply(IReadOnlyList<string> texts)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            foreach (var text in texts)
            {
                writer.Append(Connection, Turn, text);
            }

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(texts.Count), "Add と Set の回数が文の数と違う: " + view.Describe());
            AssertAdd(writes[0], texts[0], "1 文目");
            var bubble = writes[0].Bubble;
            var whole = texts[0];
            for (var i = 1; i < texts.Count; i++)
            {
                whole += texts[i];
                AssertSet(writes[i], bubble, whole, (i + 1) + " 文目");
            }

            Assert.That(view.Writes().Count(call => call.Kind == CallKind.Add), Is.EqualTo(1), "同じ返事で Add が 2 回以上呼ばれた: " + view.Describe());
        }

        // ---- コンストラクタ ----

        [Test]
        public void Constructor_ThrowsArgumentNullException_WhenViewIsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new ReplyBubbleWriter<FakeBubble>(null));
        }

        [Test]
        public void Constructor_DoesNotCallView()
        {
            var view = new FakeView();

            new ReplyBubbleWriter<FakeBubble>(view);

            Assert.That(view.Calls, Is.Empty, "作っただけで view を呼んだ: " + view.Describe());
        }

        // ---- 最初の文 ----

        [Test]
        public void Append_AddsBubbleWithText_WhenItIsTheFirstText()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, "こんにちは。");

            Assert.That(view.Writes().Count, Is.EqualTo(1), view.Describe());
            AssertAdd(view.Writes()[0], "こんにちは。", "最初の文");
        }

        [Test]
        public void Append_DoesNotAskIsShown_WhenThereIsNoBubbleYet()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, "こんにちは。");

            Assert.That(view.Calls.Count(call => call.Kind == CallKind.IsShown), Is.Zero, "吹き出しが無いのに IsShown を呼んだ: " + view.Describe());
        }

        [TestCase(0L, 0L)]
        [TestCase(long.MinValue, long.MinValue)]
        [TestCase(long.MaxValue, long.MaxValue)]
        [TestCase(long.MinValue, long.MaxValue)]
        public void Append_AddsBubbleThenSetsOnIt_ForAnyReply(long connection, long turn)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(connection, turn, "前半。");
            writer.Append(connection, turn, "後半。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[0], "前半。", "1 文目");
            AssertSet(writes[1], writes[0].Bubble, "前半。後半。", "2 文目");
        }

        // ---- 同じ返事の続き ----

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(10)]
        [TestCase(40)]
        public void Append_AddsOnceAndSetsWholeReplySoFar_ForEachTextOfSameReply(int count)
        {
            var texts = Enumerable.Range(1, count).Select(i => i + " 文目。").ToList();

            AssertOneBubbleHoldsWholeReply(texts);
        }

        [Test]
        public void Append_SetsWholeReplyWithoutReadingItBack_WhenTextsContainMarkdown()
        {
            var texts = new[] { "**太字**の", "説明です。\n", "- 項目1\n", "- 項目2\n", "# 見出し\n", "~~取り消し~~と*斜体*。" };

            AssertOneBubbleHoldsWholeReply(texts);
        }

        [Test]
        public void Append_AsksIsShownWithTheBubbleAddReturned()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, "前半。");
            writer.Append(Connection, Turn, "後半。");

            var asked = view.Calls.Where(call => call.Kind == CallKind.IsShown).Select(call => call.Bubble).ToList();
            Assert.That(asked, Is.Not.Empty, "同じ返事の続きで吹き出しが表示中かを IsShown で確かめていない: " + view.Describe());
            Assert.That(asked, Has.All.SameAs(view.Bubbles[0]), "IsShown に Add が返した吹き出しのほかを渡した: " + view.Describe());
        }

        // ---- 中身を変えない ----

        private static IEnumerable<TestCaseData> PreservedTexts()
        {
            yield return new TestCaseData("**").SetName("Append_KeepsTextUnchanged_WhenTextIsDoubleAsterisk");
            yield return new TestCaseData("*").SetName("Append_KeepsTextUnchanged_WhenTextIsAsterisk");
            yield return new TestCaseData("~~").SetName("Append_KeepsTextUnchanged_WhenTextIsDoubleTilde");
            yield return new TestCaseData("# ").SetName("Append_KeepsTextUnchanged_WhenTextIsHeadingMark");
            yield return new TestCaseData("- ").SetName("Append_KeepsTextUnchanged_WhenTextIsListMark");
            yield return new TestCaseData("\n").SetName("Append_KeepsTextUnchanged_WhenTextIsLineFeedOnly");
            yield return new TestCaseData("\r\n").SetName("Append_KeepsTextUnchanged_WhenTextIsCrLfOnly");
            yield return new TestCaseData("一行目\r\n二行目\r\n").SetName("Append_KeepsTextUnchanged_WhenTextHasCrLf");
            yield return new TestCaseData("<b>太字</b>").SetName("Append_KeepsTextUnchanged_WhenTextLooksLikeTag");
            yield return new TestCaseData("<color=#ff0000>赤</color>").SetName("Append_KeepsTextUnchanged_WhenTextLooksLikeColorTag");
            yield return new TestCaseData(char.ConvertFromUtf32(0x1F600) + "と" + char.ConvertFromUtf32(0x20BB7)).SetName("Append_KeepsTextUnchanged_WhenTextHasSurrogatePairs");
            yield return new TestCaseData("  前後に空白  ").SetName("Append_KeepsTextUnchanged_WhenTextHasSurroundingSpaces");
            yield return new TestCaseData("\t\u3000全角空白とタブ\u3000\t").SetName("Append_KeepsTextUnchanged_WhenTextHasTabsAndIdeographicSpaces");
            yield return new TestCaseData(" ").SetName("Append_KeepsTextUnchanged_WhenTextIsSingleSpace");
            yield return new TestCaseData("がとパ").SetName("Append_KeepsTextUnchanged_WhenTextHasCombiningMarks");
        }

        [TestCaseSource(nameof(PreservedTexts))]
        public void Append_KeepsTextUnchanged(string text)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, text);
            writer.Append(Connection, Turn, text);
            writer.Append(Connection, Turn, "続き");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[0], text, "1 文目");
            AssertSet(writes[1], writes[0].Bubble, text + text, "2 文目");
            AssertSet(writes[2], writes[0].Bubble, text + text + "続き", "3 文目");
        }

        [Test]
        public void Append_JoinsSurrogatePairSplitAcrossTexts()
        {
            var pair = char.ConvertFromUtf32(0x1F600);

            AssertOneBubbleHoldsWholeReply(new[] { "笑顔" + pair[0], pair[1] + "です。" });
        }

        [Test]
        public void Append_KeepsTextUnchanged_WhenItStartsOverWithSurroundingSpaces()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, 1, "前の返事。");
            writer.Append(Connection, 2, "  次の返事\r\n");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[1], "  次の返事\r\n", "別の返事の最初の文");
        }

        // ---- null と空文字 ----

        [TestCase(null)]
        [TestCase("")]
        public void Append_DoesNotCallView_WhenFirstTextIsNullOrEmpty(string text)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, text);

            Assert.That(view.Calls, Is.Empty, Escape(text) + " で view を呼んだ: " + view.Describe());
        }

        [TestCase(null)]
        [TestCase("")]
        public void Append_AddsFollowingText_AfterFirstTextWasNullOrEmpty(string text)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, text);
            writer.Append(Connection, Turn, "最初の文。");

            Assert.That(view.Writes().Count, Is.EqualTo(1), view.Describe());
            AssertAdd(view.Writes()[0], "最初の文。", "空の後の文");
        }

        [TestCase(null)]
        [TestCase("")]
        public void Append_DoesNotCallView_WhenTextInTheMiddleIsNullOrEmpty(string text)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, Turn, "前半。");
            var before = view.Calls.Count;

            writer.Append(Connection, Turn, text);

            Assert.That(view.Calls.Count, Is.EqualTo(before), Escape(text) + " で view を呼んだ: " + view.Describe());
        }

        [Test]
        public void Append_SetsWholeReplySkippingNullAndEmpty_WhenTheyArriveInTheMiddle()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, Turn, "前半。");
            writer.Append(Connection, Turn, null);
            writer.Append(Connection, Turn, string.Empty);
            writer.Append(Connection, Turn, "後半。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[0], "前半。", "1 文目");
            AssertSet(writes[1], writes[0].Bubble, "前半。後半。", "空の後の文");
        }

        [TestCase(null)]
        [TestCase("")]
        public void Append_KeepsCurrentReply_WhenNullOrEmptyArrivesForAnotherReply(string text)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, 1, "前半。");
            writer.Append(Connection, 2, text);
            writer.Append(Connection, 1, "後半。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertSet(writes[1], writes[0].Bubble, "前半。後半。", "別の返事の空の後の文");
        }

        // ---- 返事の切り替え ----

        [Test]
        public void Append_AddsNewBubbleWithOnlyThatText_WhenTurnChanges()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, 1, "一つ目の返事。");
            writer.Append(Connection, 2, "二つ目の返事。");
            writer.Append(Connection, 2, "続き。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[0], "一つ目の返事。", "turn 1");
            AssertAdd(writes[1], "二つ目の返事。", "turn 2 の最初の文");
            AssertSet(writes[2], writes[1].Bubble, "二つ目の返事。続き。", "turn 2 の続き");
        }

        [Test]
        public void Append_AddsNewBubbleWithOnlyThatText_WhenOnlyConnectionChanges()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(1, Turn, "再起動前の返事。");
            writer.Append(2, Turn, "再起動後の返事。");
            writer.Append(2, Turn, "続き。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[1], "再起動後の返事。", "別の connection の最初の文");
            AssertSet(writes[2], writes[1].Bubble, "再起動後の返事。続き。", "別の connection の続き");
        }

        [Test]
        public void Append_AddsNewBubble_WhenConnectionChangesFromZero()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(0, 0, "最初の接続。");
            writer.Append(5, 0, "次の接続。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[1], "次の接続。", "connection だけ違う最初の文");
        }

        [Test]
        public void Append_AddsNewBubbleWithOnlyThatText_WhenReplyGoesBackToEarlierOne()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(Connection, 1, "A の前半。");
            writer.Append(Connection, 2, "B。");
            writer.Append(Connection, 1, "A の後半。");
            writer.Append(Connection, 1, "A の最後。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(4), view.Describe());
            AssertAdd(writes[0], "A の前半。", "A");
            AssertAdd(writes[1], "B。", "B");
            AssertAdd(writes[2], "A の後半。", "B の後の A");
            AssertSet(writes[3], writes[2].Bubble, "A の後半。A の最後。", "戻った A の続き");
        }

        [TestCase(long.MinValue, long.MaxValue, long.MaxValue, long.MaxValue)]
        [TestCase(long.MaxValue, long.MinValue, long.MaxValue, long.MaxValue)]
        [TestCase(long.MaxValue, long.MaxValue, long.MaxValue, long.MinValue)]
        [TestCase(long.MaxValue, 0L, long.MinValue, 0L)]
        public void Append_AddsNewBubble_WhenReplyDiffersAtLongExtremes(long firstConnection, long firstTurn, long secondConnection, long secondTurn)
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(firstConnection, firstTurn, "前の返事。");
            writer.Append(secondConnection, secondTurn, "次の返事。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[1], "次の返事。", "別の返事の最初の文");
        }

        // ---- 刈り込まれた吹き出し ----

        [Test]
        public void Append_AddsNewBubbleWithOnlyThatText_WhenBubbleIsNoLongerShown()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, Turn, "前半。");
            view.Hide(view.Bubbles[0]);

            writer.Append(Connection, Turn, "後半。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(2), view.Describe());
            AssertAdd(writes[1], "後半。", "刈り込まれた後の文");
            Assert.That(writes[1].Bubble, Is.Not.SameAs(view.Bubbles[0]), view.Describe());
        }

        [Test]
        public void Append_SetsOnNewBubble_AfterBubbleWasNoLongerShown()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, Turn, "前半。");
            view.Hide(view.Bubbles[0]);

            writer.Append(Connection, Turn, "後半。");
            writer.Append(Connection, Turn, "最後。");
            writer.Append(Connection, Turn, "おまけ。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(4), view.Describe());
            AssertAdd(writes[1], "後半。", "刈り込まれた後の文");
            AssertSet(writes[2], writes[1].Bubble, "後半。最後。", "新しい吹き出しの続き");
            AssertSet(writes[3], writes[1].Bubble, "後半。最後。おまけ。", "新しい吹き出しの続き");
        }

        [Test]
        public void Append_AsksIsShownWithTheNewBubble_AfterBubbleWasNoLongerShown()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, Turn, "前半。");
            view.Hide(view.Bubbles[0]);
            writer.Append(Connection, Turn, "後半。");
            var askedBefore = view.Calls.Count;

            writer.Append(Connection, Turn, "最後。");

            var asked = view.Calls.Skip(askedBefore).Where(call => call.Kind == CallKind.IsShown).Select(call => call.Bubble).ToList();
            Assert.That(asked, Is.Not.Empty, "同じ返事の続きで吹き出しが表示中かを IsShown で確かめていない: " + view.Describe());
            Assert.That(asked, Has.All.SameAs(view.Bubbles[1]),"始め直した後の IsShown に新しい吹き出しのほかを渡した: " + view.Describe());
        }

        [Test]
        public void Append_AddsNewBubble_WhenBubbleOfAnotherReplyIsNoLongerShown()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, 1, "前の返事。");
            view.Hide(view.Bubbles[0]);

            writer.Append(Connection, 2, "次の返事。");
            writer.Append(Connection, 2, "続き。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[1], "次の返事。", "次の返事の最初の文");
            AssertSet(writes[2], writes[1].Bubble, "次の返事。続き。", "次の返事の続き");
        }

        // ---- Add が null を返す ----

        [Test]
        public void Append_AddsAgainWithOnlyThatText_WhenAddReturnedNull()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            view.ReturnNullFromNextAdd();

            writer.Append(Connection, Turn, "前半。");
            writer.Append(Connection, Turn, "後半。");
            writer.Append(Connection, Turn, "最後。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[0], "前半。", "null を返された文");
            AssertAdd(writes[1], "後半。", "null を返された次の文");
            AssertSet(writes[2], writes[1].Bubble, "後半。最後。", "作り直した吹き出しの続き");
        }

        [Test]
        public void Append_DoesNotWriteOnPreviousReplyBubble_WhenAddReturnedNullForNextReply()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, 1, "前の返事。");
            view.ReturnNullFromNextAdd();

            writer.Append(Connection, 2, "次の返事。");
            writer.Append(Connection, 2, "続き。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[1], "次の返事。", "次の返事の最初の文");
            AssertAdd(writes[2], "続き。", "Add が null を返した次の文");
            Assert.That(view.Calls.Where(call => call.Kind == CallKind.Set), Is.Empty, "前の返事の吹き出しに書いた: " + view.Describe());
        }

        [Test]
        public void Append_DoesNotWriteOnTrimmedBubble_WhenAddReturnedNullAfterIt()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            writer.Append(Connection, Turn, "前半。");
            view.Hide(view.Bubbles[0]);
            view.ReturnNullFromNextAdd();

            writer.Append(Connection, Turn, "後半。");
            writer.Append(Connection, Turn, "最後。");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(3), view.Describe());
            AssertAdd(writes[2], "最後。", "Add が null を返した次の文");
            Assert.That(view.Calls.Where(call => call.Kind == CallKind.Set), Is.Empty, "刈り込まれた吹き出しに書いた: " + view.Describe());
        }

        [Test]
        public void Append_NeverPassesNullBubbleToView_WhenAddReturnedNull()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);
            view.ReturnNullFromNextAdd();
            view.ReturnNullFromNextAdd();

            writer.Append(Connection, Turn, "1。");
            writer.Append(Connection, Turn, "2。");
            writer.Append(Connection, Turn, "3。");
            writer.Append(Connection, Turn, "4。");

            Assert.That(view.NullIsShownQueries, Is.Zero, "IsShown に null を渡した: " + view.Describe());
            Assert.That(view.NullSetTargets, Is.Zero, "Set に null の吹き出しを渡した: " + view.Describe());
            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(4), view.Describe());
            AssertAdd(writes[2], "3。", "null が続いた後の文");
            AssertSet(writes[3], writes[2].Bubble, "3。4。", "作れた吹き出しの続き");
        }

        // ---- 不変条件をまとめて ----

        [Test]
        public void Append_WritesConcatenationSinceLastRestart_AcrossMixedSequence()
        {
            var view = new FakeView();
            var writer = new ReplyBubbleWriter<FakeBubble>(view);

            writer.Append(1, 1, "**A1**");
            writer.Append(1, 1, " A2\r\n");
            writer.Append(1, 1, null);
            writer.Append(1, 2, "- B1\n");
            view.Hide(view.Bubbles[1]);
            writer.Append(1, 2, "B2 ");
            writer.Append(2, 2, "<i>C1</i>");
            writer.Append(2, 2, string.Empty);
            writer.Append(2, 2, "~~C2~~");

            var writes = view.Writes();
            Assert.That(writes.Count, Is.EqualTo(6), view.Describe());
            AssertAdd(writes[0], "**A1**", "A1");
            AssertSet(writes[1], writes[0].Bubble, "**A1** A2\r\n", "A2");
            AssertAdd(writes[2], "- B1\n", "B1");
            AssertAdd(writes[3], "B2 ", "刈り込まれた後の B2");
            AssertAdd(writes[4], "<i>C1</i>", "C1");
            AssertSet(writes[5], writes[4].Bubble, "<i>C1</i>~~C2~~", "C2");
        }
    }
}
