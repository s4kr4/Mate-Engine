using System;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 頭脳の出力を 1 行ずつ受け取り、brain.log に書くテキスト（末尾 "\n" 込み）を返すこと。
    /// バイト数は置換ありの UTF-8（不正なサロゲートは 3 バイト）で数える。これまでの合計 + この行 + LimitNote + "\n" が上限を超える行は書かず、
    /// 代わりに LimitNote + "\n" を 1 回だけ返し、以降はすべて null。返したテキストの合計は常に上限以下。null の行は状態を変えない。
    /// </summary>
    public class BrainLogTests
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private static readonly string Note = BrainLog.LimitNote + "\n";

        private static long NoteBytes
        {
            get { return Utf8.GetByteCount(Note); }
        }

        private static long Bytes(string text)
        {
            return Utf8.GetByteCount(text);
        }

        private static string Show(string text)
        {
            if (text == null)
            {
                return "null";
            }

            var sb = new StringBuilder();
            var shown = text.Length <= 80 ? text : text.Substring(0, 80);
            foreach (var c in shown)
            {
                if (c < 0x20 || c == 0x7F || char.IsSurrogate(c))
                {
                    sb.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (text.Length > 80)
            {
                sb.Append("...(").Append(text.Length).Append(" chars)");
            }

            return "\"" + sb + "\"";
        }

        // ---- 定数 ----

        [Test]
        public void MaxBytes_Is8MiB()
        {
            Assert.That(BrainLog.MaxBytes, Is.EqualTo(8L * 1024 * 1024));
        }

        [Test]
        public void LimitNote_IsOneAsciiLine()
        {
            Assert.That(BrainLog.LimitNote, Is.EqualTo("brain.log reached its size limit; later output is dropped"));
        }

        // ---- コンストラクタ ----

        [Test]
        public void Constructor_Throws_WhenMaxBytesCannotHoldTheNote()
        {
            foreach (var max in new[] { NoteBytes - 1, 1L, 0L, -1L, long.MinValue })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new BrainLog(max), "maxBytes = " + max);
            }
        }

        [Test]
        public void Constructor_Accepts_ExactlyTheNoteSize_AndThenOnlyTheNoteFits()
        {
            var log = new BrainLog(NoteBytes);

            Assert.That(log.WrittenBytes, Is.EqualTo(0L), "初めの WrittenBytes");
            Assert.That(log.Accept(""), Is.EqualTo(Note), "空行（1 バイト）でも入らないので注記を返す");
            Assert.That(log.WrittenBytes, Is.EqualTo(NoteBytes), "WrittenBytes");
            Assert.That(log.Accept(""), Is.Null, "注記のあと");
        }

        [Test]
        public void DefaultConstructor_StartsEmpty()
        {
            var log = new BrainLog();

            Assert.That(log.WrittenBytes, Is.EqualTo(0L));
        }

        // ---- 通常の行 ----

        [Test]
        public void Accept_ReturnsTheLineWithNewline_AndCountsItsBytes()
        {
            var log = new BrainLog();
            var lines = new[] { "hello", "", "ログ 😀", "\u001b[31mred\u001b[0m", "a\rb", "ends with CR\r", "  spaces  ", "tab\there", "\u0000" };
            var total = 0L;

            foreach (var line in lines)
            {
                var written = log.Accept(line);

                Assert.That(written, Is.EqualTo(line + "\n"), Show(line));
                total += Bytes(line + "\n");
                Assert.That(log.WrittenBytes, Is.EqualTo(total), Show(line) + " のあとの WrittenBytes");
            }
        }

        [Test]
        public void Accept_ReturnsAMultiMegabyteLine_AsIs()
        {
            var log = new BrainLog();
            var line = new string('x', 3 * 1024 * 1024 - 7) + "終😀";

            var written = log.Accept(line);

            Assert.That(written, Is.EqualTo(line + "\n"));
            Assert.That(log.WrittenBytes, Is.EqualTo(Bytes(line) + 1));
        }

        // ---- 上限の境界 ----

        [Test]
        public void Accept_WritesTheLine_WhenItFitsExactly_WithRoomLeftForTheNote()
        {
            var log = new BrainLog(NoteBytes + 6);

            Assert.That(log.Accept("abcde"), Is.EqualTo("abcde\n"), "6 バイトの行 + 注記 = 上限ちょうど");
            Assert.That(log.WrittenBytes, Is.EqualTo(6L));
            Assert.That(log.Accept(""), Is.EqualTo(Note), "次の 1 バイトは入らない");
            Assert.That(log.WrittenBytes, Is.EqualTo(NoteBytes + 6), "上限ちょうどまで書いた");
        }

        [Test]
        public void Accept_ReturnsTheNoteInsteadOfTheLine_WhenOneByteOver()
        {
            var log = new BrainLog(NoteBytes + 5);

            Assert.That(log.Accept("abcde"), Is.EqualTo(Note), "6 バイトの行 + 注記 = 上限 + 1");
            Assert.That(log.WrittenBytes, Is.EqualTo(NoteBytes));
        }

        [Test]
        public void Accept_ChecksTheLimit_AgainstTheRunningTotal()
        {
            var log = new BrainLog(NoteBytes + 10);

            Assert.That(log.Accept("abcd"), Is.EqualTo("abcd\n"), "1 行目（5 バイト）");
            Assert.That(log.Accept("efgh"), Is.EqualTo("efgh\n"), "2 行目（5 バイト、合計 10 で上限ちょうど）");
            Assert.That(log.Accept("i"), Is.EqualTo(Note), "3 行目（2 バイト）は入らない");
            Assert.That(log.WrittenBytes, Is.EqualTo(NoteBytes + 10));
        }

        // 属性の引数に置いた不正なサロゲートはメタデータを通るときに置き換わるため、コードで組み立てる
        private static readonly object[] LineByteCases =
        {
            new object[] { "あ", 3 },
            new object[] { "é", 2 },
            new object[] { "😀", 4 },
            new object[] { new string((char)0xD800, 1), 3 },
            new object[] { new string((char)0xDC00, 1), 3 },
            new object[] { new string(new[] { (char)0xDC00, (char)0xD800 }), 6 },
            new object[] { "a" + (char)0xD83D, 4 },
            new object[] { (char)0xD83D + "b", 4 },
            new object[] { new string((char)0, 1), 1 },
            new object[] { new string(new[] { (char)0x85, (char)0x2028 }), 5 },
        };

        [TestCaseSource(nameof(LineByteCases))]
        public void Accept_CountsUtf8Bytes_WithReplacementForLoneSurrogates(string line, int lineBytes)
        {
            // 行 + "\n" + 注記がちょうど収まる
            var fits = new BrainLog(NoteBytes + lineBytes + 1);
            var written = fits.Accept(line);
            Assert.That(written, Is.EqualTo(line + "\n"), Show(line) + " がちょうど収まるのに書かなかった、または中身が変わった");
            Assert.That(fits.WrittenBytes, Is.EqualTo((long)lineBytes + 1), Show(line) + " の WrittenBytes");

            // 1 バイト足りない
            var over = new BrainLog(NoteBytes + lineBytes);
            Assert.That(over.Accept(line), Is.EqualTo(Note), Show(line) + " が 1 バイト超えるのに書いた");
            Assert.That(over.WrittenBytes, Is.EqualTo(NoteBytes), Show(line) + " の WrittenBytes");
        }

        [Test]
        public void Accept_ReturnsTheNote_ForALineLongerThanMaxBytes()
        {
            var log = new BrainLog(NoteBytes + 10);

            Assert.That(log.Accept(new string('x', 1000)), Is.EqualTo(Note));
            Assert.That(log.WrittenBytes, Is.EqualTo(NoteBytes));
            Assert.That(log.Accept("a"), Is.Null, "短い行も注記のあとは書かない");
        }

        // ---- 注記のあと ----

        [Test]
        public void Accept_ReturnsTheNoteOnce_AndNullForEverythingAfter()
        {
            var log = new BrainLog(NoteBytes + 8);
            Assert.That(log.Accept("1234567"), Is.EqualTo("1234567\n"));
            Assert.That(log.Accept("x"), Is.EqualTo(Note), "上限に達した行");
            var after = log.WrittenBytes;

            var later = new[] { "", "x", "😀", "\uD800", new string('y', 100000), BrainLog.LimitNote, null };
            for (var round = 0; round < 20; round++)
            {
                foreach (var line in later)
                {
                    Assert.That(log.Accept(line), Is.Null, "注記のあとの " + Show(line) + "（" + round + " 周目）");
                }
            }

            Assert.That(log.WrittenBytes, Is.EqualTo(after), "注記のあとに WrittenBytes が変わった");
        }

        // ---- null の行 ----

        [Test]
        public void Accept_Null_ReturnsNull_AndChangesNothing()
        {
            var withNulls = new BrainLog(NoteBytes + 10);
            var plain = new BrainLog(NoteBytes + 10);
            var lines = new[] { "abcd", "efgh", "i", "j" };

            Assert.That(withNulls.Accept(null), Is.Null, "最初の null");
            Assert.That(withNulls.WrittenBytes, Is.EqualTo(0L), "最初の null のあと");

            foreach (var line in lines)
            {
                var expected = plain.Accept(line);
                var actual = withNulls.Accept(line);
                Assert.That(actual, Is.EqualTo(expected), Show(line) + " の結果が null を挟まないときと違う");
                Assert.That(withNulls.WrittenBytes, Is.EqualTo(plain.WrittenBytes), Show(line) + " のあとの WrittenBytes");

                var before = withNulls.WrittenBytes;
                Assert.That(withNulls.Accept(null), Is.Null, Show(line) + " のあとの null");
                Assert.That(withNulls.WrittenBytes, Is.EqualTo(before), Show(line) + " のあとの null で WrittenBytes が変わった");
            }

            Assert.That(plain.WrittenBytes, Is.EqualTo(NoteBytes + 10), "テスト側の前提: 注記まで進んでいる");
        }

        [Test]
        public void Accept_Null_DoesNotCountAsTheLimitBeingReached()
        {
            var log = new BrainLog(NoteBytes);

            for (var i = 0; i < 10; i++)
            {
                Assert.That(log.Accept(null), Is.Null);
            }

            Assert.That(log.WrittenBytes, Is.EqualTo(0L));
            Assert.That(log.Accept("x"), Is.EqualTo(Note), "null のあとでも、入らない行には注記を 1 回返す");
        }

        // ---- 既定の上限 ----

        [Test]
        public void DefaultLog_StopsAtAbout8MiB_WithTheNoteOnce()
        {
            var log = new BrainLog();
            var line = new string('x', 1023); // "\n" 込みで 1024 バイト
            var expectedLines = (BrainLog.MaxBytes - NoteBytes) / 1024;
            var written = 0L;
            var notes = 0;
            long accepted = 0;

            for (var i = 0; i < 9000; i++)
            {
                var result = log.Accept(line);
                if (result == null)
                {
                    Assert.That(notes, Is.EqualTo(1), i + " 行目: 注記の前に null を返した");
                    continue;
                }

                if (result == Note)
                {
                    notes++;
                    Assert.That(notes, Is.EqualTo(1), i + " 行目: 注記を 2 回返した");
                    Assert.That(accepted, Is.EqualTo(expectedLines), "注記までに書いた行数");
                }
                else
                {
                    Assert.That(notes, Is.EqualTo(0), i + " 行目: 注記のあとに行を返した");
                    Assert.That(result, Is.EqualTo(line + "\n"), i + " 行目の中身");
                    accepted++;
                }

                written += Bytes(result);
                Assert.That(written, Is.LessThanOrEqualTo(BrainLog.MaxBytes), i + " 行目: 合計が上限を超えた");
            }

            Assert.That(notes, Is.EqualTo(1), "注記の回数");
            Assert.That(log.WrittenBytes, Is.EqualTo(written), "WrittenBytes と返したテキストの合計");
            Assert.That(written, Is.EqualTo(expectedLines * 1024 + NoteBytes), "合計");
            Assert.That(written, Is.GreaterThan(BrainLog.MaxBytes - 1024 - NoteBytes), "上限のかなり手前で止まった");
        }

        // ---- 不変条件 ----

        [Test]
        public void Accept_KeepsTheInvariants_ForMixedLines()
        {
            var fragments = new[] { "a", "あ", "😀", "\uD800", "\uDFFF", "\u0000", "\u001b", "\r", " ", "", "長い行", "\x2028" };
            var random = new Random(20261026);

            foreach (var extra in new long[] { 0, 1, 2, 3, 7, 50, 333, 4096 })
            {
                var max = NoteBytes + extra;
                var log = new BrainLog(max);
                var total = 0L;
                var notes = 0;
                var limitReached = false;

                for (var i = 0; i < 400; i++)
                {
                    string line = null;
                    if (random.Next(10) != 0)
                    {
                        var sb = new StringBuilder();
                        var parts = random.Next(0, random.Next(10) == 0 ? 200 : 8);
                        for (var p = 0; p < parts; p++)
                        {
                            sb.Append(fragments[random.Next(fragments.Length)]);
                        }

                        line = sb.ToString();
                    }

                    var where = "maxBytes = 注記 + " + extra + " / " + i + " 回目 " + Show(line);
                    var result = log.Accept(line);

                    if (line == null || limitReached)
                    {
                        Assert.That(result, Is.Null, where);
                    }
                    else if (total + Bytes(line + "\n") + NoteBytes <= max)
                    {
                        Assert.That(result, Is.EqualTo(line + "\n"), where + ": 収まる行を順番どおりに返さなかった");
                    }
                    else
                    {
                        Assert.That(result, Is.EqualTo(Note), where + ": 収まらない行に注記を返さなかった");
                        limitReached = true;
                        notes++;
                    }

                    if (result != null)
                    {
                        total += Bytes(result);
                    }

                    Assert.That(total, Is.LessThanOrEqualTo(max), where + ": 合計が上限を超えた");
                    Assert.That(log.WrittenBytes, Is.EqualTo(total), where + ": WrittenBytes");
                    Assert.That(notes, Is.LessThanOrEqualTo(1), where);
                }
            }
        }
    }
}
