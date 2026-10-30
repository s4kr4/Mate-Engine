using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// ReplyText は、最後に始め直してから同じ返事で受け取った text を、受け取った順にすべてつないだものを持つ。
    /// 吹き出しの表示は Markdown をタグに変換した文字列になるので、つなぐ元はこちらの文字列であり、中身を一切変えないことを確かめる。
    /// </summary>
    public class ReplyTextTests
    {
        private static readonly ReplyId First = new ReplyId(1, 1);
        private static readonly ReplyId NextTurn = new ReplyId(1, 2);
        private static readonly ReplyId Restarted = new ReplyId(2, 1);

        /// <summary>同じ返事で texts を順に足し、2 つ目以降が始め直しにならないことを確かめる。</summary>
        private static void AppendAll(ReplyText replyText, ReplyId reply, IEnumerable<string> texts)
        {
            var index = 0;
            foreach (var text in texts)
            {
                var restarted = replyText.Append(reply, text);
                if (index > 0)
                {
                    Assert.That(restarted, Is.False, (index + 1) + " 文目で始め直した: " + Escape(text));
                }

                index++;
            }
        }

        private static string Escape(string text)
        {
            return text == null ? "null" : "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        // ---- 初期状態 ----

        [Test]
        public void Text_IsEmpty_WhenNothingHasBeenAppended()
        {
            var replyText = new ReplyText();

            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
        }

        // ---- 最初の Append ----

        [Test]
        public void Append_ReturnsTrueAndKeepsText_WhenItIsTheFirstAppend()
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(First, "こんにちは。");

            Assert.That(restarted, Is.True, "最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("こんにちは。"));
        }

        [Test]
        public void Append_ReturnsTrue_WhenFirstAppendIsDefaultReplyId()
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(default(ReplyId), "a");

            Assert.That(restarted, Is.True, "既定値の ReplyId の最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("a"));
        }

        // ---- 同じ返事での連結 ----

        [Test]
        public void Append_ReturnsFalseAndConcatenates_WhenSameReplyContinues()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "一文目。");

            var restarted = replyText.Append(First, "二文目。");

            Assert.That(restarted, Is.False, "同じ返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("一文目。二文目。"));
        }

        [Test]
        public void Append_ConcatenatesInOrder_WhenSameReplyIsEqualButSeparatelyConstructed()
        {
            var replyText = new ReplyText();
            replyText.Append(new ReplyId(7, 3), "前");

            var restarted = replyText.Append(new ReplyId(7, 3), "後");

            Assert.That(restarted, Is.False, "値の等しい別インスタンスの ReplyId を別の返事として扱う");
            Assert.That(replyText.Text, Is.EqualTo("前後"));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(18)]
        [TestCase(20)]
        [TestCase(40)]
        public void Append_ConcatenatesAllSentencesInOrder_WhenReplyHasManySentences(int count)
        {
            var replyText = new ReplyText();
            var sentences = Enumerable.Range(1, count).Select(i => "文" + i + "。").ToList();

            AppendAll(replyText, First, sentences);

            Assert.That(replyText.Text, Is.EqualTo(string.Concat(sentences)));
        }

        [Test]
        public void Append_KeepsLaterSentences_WhenMarkdownSentenceIsFollowedByPlainSentences()
        {
            var replyText = new ReplyText();
            var sentences = new List<string> { "まず **大事な点** から。", "次に二つ目。", "最後に三つ目。" };
            sentences.AddRange(Enumerable.Range(4, 17).Select(i => "続き" + i + "。"));

            AppendAll(replyText, First, sentences);

            Assert.That(replyText.Text, Is.EqualTo(string.Concat(sentences)));
            Assert.That(replyText.Text.EndsWith("続き20。"), Is.True, "最後の文が残っていない: " + Escape(replyText.Text));
        }

        // ---- 中身を変えない ----

        [TestCase("**見出し**")]
        [TestCase("*斜体*")]
        [TestCase("~~取消~~")]
        [TestCase("- 項目")]
        [TestCase("# 見出し")]
        [TestCase("`code`")]
        [TestCase("<b>太字</b>")]
        [TestCase("<color=#fff>白</color>")]
        [TestCase("<size=20>")]
        [TestCase("</i>")]
        [TestCase("&lt;b&gt;")]
        [TestCase("\n")]
        [TestCase("\n\n")]
        [TestCase("\r\n")]
        [TestCase("一行目\r\n二行目\r\n")]
        [TestCase("\r")]
        [TestCase("  前後に空白  ")]
        [TestCase("\t")]
        [TestCase("\u3000全角空白")]
        [TestCase("ＡＢＣ１２３")]
        [TestCase("😀👍🏽")]
        [TestCase("\U0001F468\u200D\U0001F469\u200D\U0001F467")]
        [TestCase("\u200B\uFEFF")]
        [TestCase("e\u0301")]
        public void Append_KeepsTextUnchanged_WhenTextLooksLikeMarkupOrHasSpecialCharacters(string text)
        {
            var replyText = new ReplyText();

            replyText.Append(First, "前置き。");
            replyText.Append(First, text);
            replyText.Append(First, "後書き。");

            Assert.That(replyText.Text, Is.EqualTo("前置き。" + text + "後書き。"), "中身が変わった: " + Escape(replyText.Text));
            Assert.That(replyText.Text.Length, Is.EqualTo(4 + text.Length + 4), "UTF-16 の長さが変わった");
        }

        [TestCase("**見出し**")]
        [TestCase("<b>太字</b>")]
        [TestCase("\r\n")]
        [TestCase("😀")]
        public void Append_KeepsTextUnchanged_WhenItIsTheFirstAppend(string text)
        {
            var replyText = new ReplyText();

            replyText.Append(First, text);

            Assert.That(replyText.Text, Is.EqualTo(text), "最初の text の中身が変わった: " + Escape(replyText.Text));
        }

        [Test]
        public void Append_KeepsMarkdownSplitAcrossSentences_WhenMarkersArriveInDifferentAppends()
        {
            var replyText = new ReplyText();
            var sentences = new[] { "これは **太", "字** です。", "\n\n", "- 一つ目\r\n", "- 二つ目", "<b>", "閉じ</b>" };

            AppendAll(replyText, First, sentences);

            Assert.That(replyText.Text, Is.EqualTo(string.Concat(sentences)));
        }

        [Test]
        public void Append_KeepsSurrogatePair_WhenItIsSplitAcrossAppends()
        {
            var replyText = new ReplyText();
            var emoji = "😀";

            replyText.Append(First, emoji.Substring(0, 1));
            replyText.Append(First, emoji.Substring(1, 1));

            Assert.That(replyText.Text, Is.EqualTo(emoji), "分かれて届いたサロゲートペアがつながらない");
        }

        [Test]
        public void Append_KeepsLongText_WhenSentencesAreLong()
        {
            var replyText = new ReplyText();
            var sentences = Enumerable.Range(0, 20).Select(i => new string((char)('あ' + i), 500) + "**" + i + "**\n").ToList();

            AppendAll(replyText, First, sentences);

            Assert.That(replyText.Text, Is.EqualTo(string.Concat(sentences)));
        }

        // ---- 空文字・null ----

        [Test]
        public void Append_TreatsEmptyAsNothing_WhenSameReplyContinues()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "a");

            var restarted = replyText.Append(First, string.Empty);
            replyText.Append(First, "b");

            Assert.That(restarted, Is.False, "空文字の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("ab"));
        }

        [Test]
        public void Append_TreatsNullAsEmpty_WhenSameReplyContinues()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "a");

            var restarted = replyText.Append(First, null);
            replyText.Append(First, "b");

            Assert.That(restarted, Is.False, "null の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("ab"), "null が \"\" として扱われていない");
        }

        [Test]
        public void Append_ReturnsTrueAndTextIsEmpty_WhenFirstAppendIsNull()
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(First, null);

            Assert.That(restarted, Is.True, "null の最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
            Assert.That(replyText.Text, Is.Not.Null);
        }

        [Test]
        public void Append_ReturnsTrueAndTextIsEmpty_WhenFirstAppendIsEmpty()
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(First, string.Empty);

            Assert.That(restarted, Is.True, "空文字の最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Append_ContinuesStartedReply_WhenFirstAppendWasNull()
        {
            var replyText = new ReplyText();
            replyText.Append(First, null);

            var restarted = replyText.Append(First, "a");

            Assert.That(restarted, Is.False, "null で始めた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("a"));
        }

        [Test]
        public void Append_Restarts_WhenNullArrivesForAnotherReply()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事");

            var restarted = replyText.Append(NextTurn, null);
            var continued = replyText.Append(NextTurn, "次");

            Assert.That(restarted, Is.True, "別の返事の null で始め直したと答えない");
            Assert.That(continued, Is.False, "null で切り替えた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("次"), "前の返事の文字列が残っている");
        }

        [Test]
        public void Append_Restarts_WhenEmptyArrivesForAnotherReply()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事");

            var restarted = replyText.Append(NextTurn, string.Empty);

            Assert.That(restarted, Is.True, "別の返事の空文字で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo(string.Empty), "前の返事の文字列が残っている");
        }

        // ---- 返事の切り替え ----

        [TestCase(1L, 1L, 1L, 2L, TestName = "Append_Restarts_WhenOnlyTurnDiffers")]
        [TestCase(1L, 2L, 1L, 1L, TestName = "Append_Restarts_WhenTurnGoesBackward")]
        [TestCase(1L, 1L, 2L, 1L, TestName = "Append_Restarts_WhenOnlyConnectionDiffersAfterRestart")]
        [TestCase(1L, 5L, 2L, 5L, TestName = "Append_Restarts_WhenRestartedBrainReachesSameTurn")]
        [TestCase(1L, 2L, 2L, 1L, TestName = "Append_Restarts_WhenConnectionAndTurnAreSwapped")]
        [TestCase(0L, 0L, long.MinValue, long.MaxValue, TestName = "Append_Restarts_WhenBothDifferAtExtremes")]
        public void Append_RestartsWithOnlyNewText_WhenReplyChanges(long connectionA, long turnA, long connectionB, long turnB)
        {
            var replyText = new ReplyText();
            replyText.Append(new ReplyId(connectionA, turnA), "前の返事。");
            replyText.Append(new ReplyId(connectionA, turnA), "その続き。");

            var restarted = replyText.Append(new ReplyId(connectionB, turnB), "新しい返事。");

            Assert.That(restarted, Is.True, "別の返事で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("新しい返事。"), "前の返事の文字列が残っている");
        }

        [Test]
        public void Append_ContinuesNewReply_WhenReplyChangedAndThenContinues()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事。");
            replyText.Append(Restarted, "新しい返事。");

            var restarted = replyText.Append(Restarted, "その続き。");

            Assert.That(restarted, Is.False, "切り替えた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("新しい返事。その続き。"));
        }

        [Test]
        public void Append_RestartsAgain_WhenOlderReplyComesBack()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "A1。");
            replyText.Append(First, "A2。");
            replyText.Append(NextTurn, "B1。");

            var restarted = replyText.Append(First, "A3。");

            Assert.That(restarted, Is.True, "古い返事に戻ったときに始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("A3。"), "古い返事の文字列が引き継がれた、または途中の返事が残っている");
        }

        [Test]
        public void Append_RestartsEachTime_WhenRepliesAlternate()
        {
            var replyText = new ReplyText();
            var order = new[] { First, NextTurn, First, Restarted, NextTurn };

            for (var i = 0; i < order.Length; i++)
            {
                var text = "返事" + i;
                var restarted = replyText.Append(order[i], text);

                Assert.That(restarted, Is.True, (i + 1) + " 回目の切り替えで始め直したと答えない");
                Assert.That(replyText.Text, Is.EqualTo(text), (i + 1) + " 回目の切り替えで前の文字列が残っている");
            }
        }

        // ---- Clear ----

        [Test]
        public void Clear_MakesTextEmpty()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "a");
            replyText.Append(First, "b");

            replyText.Clear();

            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Append_ReturnsTrueWithOnlyNewText_WhenSameReplyArrivesAfterClear()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消える文。");

            replyText.Clear();
            var restarted = replyText.Append(First, "残る文。");

            Assert.That(restarted, Is.True, "Clear 後の同じ返事の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("残る文。"));
        }

        [Test]
        public void Append_ContinuesAfterClearRestart_WhenSameReplyContinues()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消える文。");
            replyText.Clear();
            replyText.Append(First, "残る文。");

            var restarted = replyText.Append(First, "続き。");

            Assert.That(restarted, Is.False, "Clear 後に始め直した返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("残る文。続き。"));
        }

        [Test]
        public void Append_ReturnsTrue_WhenNullArrivesAfterClear()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消える文。");
            replyText.Clear();

            var restarted = replyText.Append(First, null);

            Assert.That(restarted, Is.True, "Clear 後の null の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
        }

        [Test]
        public void Append_ReturnsTrue_WhenDefaultReplyIdArrivesAfterClear()
        {
            var replyText = new ReplyText();
            replyText.Append(default(ReplyId), "消える文。");
            replyText.Clear();

            var restarted = replyText.Append(default(ReplyId), "残る文。");

            Assert.That(restarted, Is.True, "Clear 後の既定値の ReplyId で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("残る文。"));
        }

        [Test]
        public void Clear_KeepsTextEmpty_WhenCalledBeforeAnyAppendOrTwice()
        {
            var replyText = new ReplyText();

            replyText.Clear();
            Assert.That(replyText.Text, Is.EqualTo(string.Empty), "最初の Clear で空にならない");

            replyText.Clear();
            Assert.That(replyText.Text, Is.EqualTo(string.Empty), "2 回目の Clear で空にならない");

            var restarted = replyText.Append(First, "a");
            Assert.That(restarted, Is.True, "Clear だけをした後の最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("a"));
        }

        // ---- 吹き出しの表示状態を受け取る Append ----
        // bubbleAlive は今の返事の吹き出しがまだ表示中か。false なら同じ返事でも始め直し、Text はその text だけになる。

        [TestCase(true)]
        [TestCase(false)]
        public void AppendWithBubbleState_ReturnsTrueAndKeepsText_WhenItIsTheFirstAppend(bool bubbleAlive)
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(First, "こんにちは。", bubbleAlive);

            Assert.That(restarted, Is.True, "最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("こんにちは。"));
        }

        [Test]
        public void AppendWithBubbleState_ReturnsFalseAndConcatenates_WhenSameReplyContinuesAndBubbleIsAlive()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "一文目。", true);

            var restarted = replyText.Append(First, "二文目。", true);

            Assert.That(restarted, Is.False, "表示中の吹き出しの返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("一文目。二文目。"));
        }

        [Test]
        public void AppendWithBubbleState_RestartsWithOnlyNewText_WhenReplyChangesAndBubbleIsAlive()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事。", true);
            replyText.Append(First, "その続き。", true);

            var restarted = replyText.Append(NextTurn, "新しい返事。", true);

            Assert.That(restarted, Is.True, "別の返事で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("新しい返事。"), "前の返事の文字列が残っている");
        }

        [Test]
        public void AppendWithBubbleState_RestartsWithOnlyNewText_WhenSameReplyContinuesButBubbleIsGone()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消えた吹き出しの文。", true);
            replyText.Append(First, "その続き。", true);

            var restarted = replyText.Append(First, "新しい吹き出しの文。", false);

            Assert.That(restarted, Is.True, "吹き出しが消えた同じ返事で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("新しい吹き出しの文。"), "消えた吹き出しの文字列を引き継いだ");
        }

        [Test]
        public void AppendWithBubbleState_ContinuesRestartedReply_WhenBubbleIsAliveAgainForSameReply()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消えた吹き出しの文。", true);
            replyText.Append(First, "新しい吹き出しの文。", false);

            var restarted = replyText.Append(First, "その続き。", true);

            Assert.That(restarted, Is.False, "始め直した吹き出しの続きで、もう一度始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("新しい吹き出しの文。その続き。"));
        }

        [Test]
        public void AppendWithBubbleState_RestartsWithOnlyNewText_WhenReplyChangesAndBubbleIsGone()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事。", true);

            var restarted = replyText.Append(NextTurn, "新しい返事。", false);
            var continued = replyText.Append(NextTurn, "その続き。", true);

            Assert.That(restarted, Is.True, "別の返事で吹き出しも消えているときに始め直したと答えない");
            Assert.That(continued, Is.False, "切り替えた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("新しい返事。その続き。"), "前の返事の文字列が残っている、または続きがつながらない");
        }

        [Test]
        public void AppendWithBubbleState_RestartsEachTime_WhenBubbleIsGoneEveryTime()
        {
            var replyText = new ReplyText();

            for (var i = 0; i < 5; i++)
            {
                var text = "文" + i + "。";
                var restarted = replyText.Append(First, text, false);

                Assert.That(restarted, Is.True, (i + 1) + " 回目の吹き出しが消えた Append で始め直したと答えない");
                Assert.That(replyText.Text, Is.EqualTo(text), (i + 1) + " 回目で前の文字列が残っている");
            }
        }

        [TestCase(20)]
        [TestCase(40)]
        public void AppendWithBubbleState_RestartsOnlyOnFirstSentence_WhenSameReplyContinuesAndBubbleIsAlive(int count)
        {
            var replyText = new ReplyText();
            var sentences = Enumerable.Range(1, count).Select(i => i == 1 ? "まず **大事な点** から。" : "続き" + i + "。").ToList();
            var restartCount = 0;

            foreach (var sentence in sentences)
            {
                if (replyText.Append(First, sentence, true))
                {
                    restartCount++;
                }
            }

            Assert.That(restartCount, Is.EqualTo(1), "1 つの返事で吹き出しを作り直した回数が 1 回ではない");
            Assert.That(replyText.Text, Is.EqualTo(string.Concat(sentences)));
        }

        [Test]
        public void AppendWithBubbleState_RestartsEachTime_WhenRepliesAlternateAndBubbleIsAlive()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "A1。", true);
            replyText.Append(First, "A2。", true);

            var toSecond = replyText.Append(NextTurn, "B1。", true);
            var secondText = replyText.Text;
            var backToFirst = replyText.Append(First, "A3。", true);

            Assert.That(toSecond, Is.True, "A から B に切り替えたときに始め直したと答えない");
            Assert.That(secondText, Is.EqualTo("B1。"), "A の文字列が B に残っている");
            Assert.That(backToFirst, Is.True, "B から A に戻ったときに始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("A3。"), "古い返事の文字列が引き継がれた、または途中の返事が残っている");
        }

        [Test]
        public void AppendWithBubbleState_ReturnsTrue_WhenSameReplyArrivesAfterClearAndBubbleIsAlive()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消える文。", true);
            replyText.Clear();

            var restarted = replyText.Append(First, "残る文。", true);

            Assert.That(restarted, Is.True, "Clear 後の同じ返事で始め直したと答えない");
            Assert.That(replyText.Text, Is.EqualTo("残る文。"));
        }

        [TestCase("**見出し**\r\n")]
        [TestCase("<b>太字</b>")]
        [TestCase("  前後に空白  ")]
        [TestCase("😀👍🏽")]
        [TestCase("​﻿")]
        public void AppendWithBubbleState_KeepsTextUnchanged_WhenItRestartsBecauseBubbleIsGone(string text)
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前置き。", true);

            replyText.Append(First, text, false);
            replyText.Append(First, "後書き。", true);

            Assert.That(replyText.Text, Is.EqualTo(text + "後書き。"), "中身が変わった: " + Escape(replyText.Text));
            Assert.That(replyText.Text.Length, Is.EqualTo(text.Length + 4), "UTF-16 の長さが変わった");
        }

        // ---- 吹き出しの表示状態を受け取る Append: 空文字・null ----

        [TestCase(null)]
        [TestCase("")]
        public void AppendWithBubbleState_TreatsNullOrEmptyAsNothing_WhenSameReplyContinuesAndBubbleIsAlive(string text)
        {
            var replyText = new ReplyText();
            replyText.Append(First, "a", true);

            var restarted = replyText.Append(First, text, true);
            replyText.Append(First, "b", true);

            Assert.That(restarted, Is.False, Escape(text) + " の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("ab"));
        }

        [TestCase(null, true)]
        [TestCase(null, false)]
        [TestCase("", true)]
        [TestCase("", false)]
        public void AppendWithBubbleState_ReturnsTrueAndTextIsEmpty_WhenFirstAppendIsNullOrEmpty(string text, bool bubbleAlive)
        {
            var replyText = new ReplyText();

            var restarted = replyText.Append(First, text, bubbleAlive);

            Assert.That(restarted, Is.True, Escape(text) + " の最初の Append で始め直したと答えない");
            Assert.That(replyText.Text, Is.Not.Null);
            Assert.That(replyText.Text, Is.EqualTo(string.Empty));
        }

        [TestCase(null)]
        [TestCase("")]
        public void AppendWithBubbleState_RestartsWithEmptyText_WhenNullOrEmptyArrivesAndBubbleIsGone(string text)
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消えた吹き出しの文。", true);

            var restarted = replyText.Append(First, text, false);
            var textAfterRestart = replyText.Text;
            var continued = replyText.Append(First, "次", true);

            Assert.That(restarted, Is.True, "吹き出しが消えた同じ返事の " + Escape(text) + " で始め直したと答えない");
            Assert.That(textAfterRestart, Is.Not.Null);
            Assert.That(textAfterRestart, Is.EqualTo(string.Empty), "消えた吹き出しの文字列が残っている");
            Assert.That(continued, Is.False, Escape(text) + " で始め直した返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("次"));
        }

        [TestCase(null, true)]
        [TestCase(null, false)]
        [TestCase("", true)]
        [TestCase("", false)]
        public void AppendWithBubbleState_RestartsWithEmptyText_WhenNullOrEmptyArrivesForAnotherReply(string text, bool bubbleAlive)
        {
            var replyText = new ReplyText();
            replyText.Append(First, "前の返事", true);

            var restarted = replyText.Append(NextTurn, text, bubbleAlive);
            var continued = replyText.Append(NextTurn, "次", true);

            Assert.That(restarted, Is.True, "別の返事の " + Escape(text) + " で始め直したと答えない");
            Assert.That(continued, Is.False, Escape(text) + " で切り替えた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("次"), "前の返事の文字列が残っている");
        }

        // ---- 吹き出しの表示状態を受け取る Append: 2 引数の Append との組み合わせ ----

        [Test]
        public void Append_ContinuesReply_WhenItFollowsRestartBecauseBubbleIsGone()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "消えた吹き出しの文。");
            replyText.Append(First, "新しい吹き出しの文。", false);

            var restarted = replyText.Append(First, "その続き。");

            Assert.That(restarted, Is.False, "吹き出しの表示状態で始め直した返事の続きを、2 引数の Append が別の返事として扱う");
            Assert.That(replyText.Text, Is.EqualTo("新しい吹き出しの文。その続き。"));
        }

        [Test]
        public void AppendWithBubbleState_ContinuesReply_WhenItFollowsTwoArgumentAppendAndBubbleIsAlive()
        {
            var replyText = new ReplyText();
            replyText.Append(First, "一文目。");

            var restarted = replyText.Append(First, "二文目。", true);

            Assert.That(restarted, Is.False, "2 引数の Append で始めた返事の続きで始め直したと答える");
            Assert.That(replyText.Text, Is.EqualTo("一文目。二文目。"));
        }

        // ---- 吹き出しの表示状態を受け取る Append: 不変条件 ----

        /// <summary>
        /// 各 Append の後で、Text が「最後に始め直してから同じ返事で受け取った text を順につないだもの」に等しいことを確かめる。
        /// 始め直すのは、返事が無いとき・別の返事のとき・吹き出しが消えているとき。期待値は各手順に直接書く。
        /// </summary>
        [Test]
        public void AppendWithBubbleState_TextIsTextsSinceLastRestartInOrder_ForMixedSequence()
        {
            var replyText = new ReplyText();
            var steps = new[]
            {
                new { Reply = First, Text = "A1 **太", Alive = true, Restarts = true, Expected = "A1 **太" },
                new { Reply = First, Text = "字**\r\n", Alive = true, Restarts = false, Expected = "A1 **太字**\r\n" },
                new { Reply = First, Text = (string)null, Alive = true, Restarts = false, Expected = "A1 **太字**\r\n" },
                new { Reply = First, Text = "A2", Alive = false, Restarts = true, Expected = "A2" },
                new { Reply = First, Text = " A3 ", Alive = true, Restarts = false, Expected = "A2 A3 " },
                new { Reply = NextTurn, Text = "B1", Alive = true, Restarts = true, Expected = "B1" },
                new { Reply = NextTurn, Text = "", Alive = true, Restarts = false, Expected = "B1" },
                new { Reply = Restarted, Text = "C1", Alive = false, Restarts = true, Expected = "C1" },
                new { Reply = Restarted, Text = "C2", Alive = false, Restarts = true, Expected = "C2" },
                new { Reply = Restarted, Text = "C3", Alive = true, Restarts = false, Expected = "C2C3" },
                new { Reply = First, Text = "A4", Alive = true, Restarts = true, Expected = "A4" },
                new { Reply = First, Text = "😀", Alive = true, Restarts = false, Expected = "A4😀" },
            };

            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];

                var restarted = replyText.Append(step.Reply, step.Text, step.Alive);

                Assert.That(restarted, Is.EqualTo(step.Restarts), (i + 1) + " 手目の始め直しの答えが違う");
                Assert.That(replyText.Text, Is.EqualTo(step.Expected), (i + 1) + " 手目の Text が違う: " + Escape(replyText.Text));
            }
        }
    }
}
