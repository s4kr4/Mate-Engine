using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 音声入力の知らせ（listen と heard）の読み方。
    /// listen の state は recording・recognizing・idle のどれかとちょうど一致する文字列のときだけ ListenMessage になる。
    /// それ以外の文字列（大文字違い・前後の空白・全角など）は知らない値として IgnoredMessage、state が無い・文字列でないときは InvalidMessage。
    /// heard の text は文字列ならどんな中身でもそのまま HeardMessage に入り、無い・文字列でないときは InvalidMessage。
    /// どちらも知らないフィールドは無視する。頭脳は ensure_ascii の JSON（非 ASCII を \uXXXX で書く）で送るので、その形でも確かめる。
    /// </summary>
    public class BrainProtocolListenTests
    {
        // 理由の文字列はログに毎回出るので、巨大な値でも長さが有界であること
        private const int MaxReasonLength = 160;

        private static readonly string[] States = { "recording", "recognizing", "idle" };

        private static readonly ListenState[] ExpectedStates = { ListenState.Recording, ListenState.Recognizing, ListenState.Idle };

        private static string ListenJson(string stateLiteral, string extra = "")
        {
            var parts = new List<string> { "\"type\":\"listen\"" };
            if (stateLiteral != null)
            {
                parts.Add("\"state\":" + stateLiteral);
            }

            if (!string.IsNullOrEmpty(extra))
            {
                parts.Add(extra);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static string HeardJson(string textLiteral, string extra = "")
        {
            var parts = new List<string> { "\"type\":\"heard\"" };
            if (textLiteral != null)
            {
                parts.Add("\"text\":" + textLiteral);
            }

            if (!string.IsNullOrEmpty(extra))
            {
                parts.Add(extra);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static ListenMessage ParseListen(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<ListenMessage>(), "listen として読めない: " + Visible(json) + " → " + Describe(message));
            return (ListenMessage)message;
        }

        private static HeardMessage ParseHeard(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<HeardMessage>(), "heard として読めない: " + Visible(json) + " → " + Describe(message));
            var heard = (HeardMessage)message;
            Assert.That(heard.Text, Is.Not.Null, "Text が null: " + Visible(json));
            return heard;
        }

        private static IgnoredMessage ParseIgnored(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<IgnoredMessage>(), "無視として扱われない: " + Visible(json) + " → " + Describe(message));
            var ignored = (IgnoredMessage)message;
            Assert.That(ignored.Reason, Is.Not.Null.And.Not.Empty, "Reason が空: " + Visible(json));
            return ignored;
        }

        private static InvalidMessage ParseInvalid(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<InvalidMessage>(), "不正として扱われない: " + Visible(json) + " → " + Describe(message));
            var invalid = (InvalidMessage)message;
            Assert.That(invalid.Reason, Is.Not.Null.And.Not.Empty, "Reason が空: " + Visible(json));
            Assert.That(invalid.Id.HasValue, Is.False, "listen・heard に say の id は無い: " + Visible(json));
            Assert.That(invalid.IsCue, Is.False, "listen・heard は表情・モーションのメッセージではない: " + Visible(json));
            return invalid;
        }

        private static string Describe(BrainMessage message)
        {
            var invalid = message as InvalidMessage;
            if (invalid != null)
            {
                return "InvalidMessage(" + invalid.Reason + ")";
            }

            var ignored = message as IgnoredMessage;
            if (ignored != null)
            {
                return "IgnoredMessage(" + ignored.Reason + ")";
            }

            var listen = message as ListenMessage;
            if (listen != null)
            {
                return "ListenMessage(" + listen.State + ")";
            }

            return message == null ? "null" : message.GetType().Name;
        }

        /// <summary>制御文字・行区切り・ゼロ幅などを \uXXXX で見える形にし、長いものは切る。</summary>
        private static string Visible(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7F || c == 0x0085 || (c >= 0x2028 && c <= 0x202E) || c == 0x200B || c == 0xFEFF || char.IsSurrogate(c))
                {
                    builder.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.Length > 200 ? builder.ToString(0, 200) + "...(" + value.Length + ")" : builder.ToString();
        }

        /// <summary>
        /// 頭脳（Python の json.dumps(ensure_ascii=True)）と同じ形の JSON 文字列リテラルにする。
        /// 空白から ~ までの外側（制御文字・DEL・非 ASCII）は UTF-16 の単位ごとに小文字の \uXXXX で書く。
        /// </summary>
        private static string AsciiQuote(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (c < 0x20 || c > 0x7E)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            var quoted = builder.Append('"').ToString();
            Assert.That(quoted.All(c => c >= 0x20 && c <= 0x7E), Is.True, "ensure_ascii の形になっていない: " + Visible(quoted));
            return quoted;
        }

        // 聞き取った文になりうるもの。制御文字や双方向制御文字をテスト名に出さないため、配列で持ってループで確かめる
        private static readonly string[] AnyTexts =
        {
            "",
            " ",
            "\n",
            "  \r\n\t ",
            ((char)0x3000).ToString(),
            "こんにちは",
            "アルカ、今日の天気は？",
            "改行\nを含む\r\n文",
            "😀👍🏽🇯🇵👨" + (char)0x200D + "👩" + (char)0x200D + "👧",
            "\"引用符\"と\\バックスラッシュと/スラッシュ",
            "a" + (char)0 + "b" + (char)0x1B + "c" + (char)0x7F + "d" + (char)0x1F,
            string.Concat((char)0x2028, (char)0x2029, (char)0x0085, (char)0x200B, (char)0xFEFF, (char)0x202E, (char)0x00A0),
            "{\"type\":\"say\",\"id\":1,\"turn\":1,\"text\":\"x\"}",
            "{\"type\":\"listen\",\"state\":\"recording\"}",
            "2026-11-07T12:00:00Z",
            "/Date(1760443200000)/",
            "null",
            "1.0",
            "\\u0041 はエスケープではなく文字",
        };

        // ---- listen: 3 つの状態 ----

        [Test]
        public void Parse_ReturnsListenWithState_WhenStateIsOneOfTheThreeNames()
        {
            for (var i = 0; i < States.Length; i++)
            {
                var listen = ParseListen(ListenJson(TestJson.Quote(States[i])));

                Assert.That(listen.State, Is.EqualTo(ExpectedStates[i]), States[i]);
            }
        }

        [Test]
        public void Parse_ReturnsListenWithState_WhenStateIsWrittenWithUnicodeEscapes()
        {
            // 同じ文字列の別の書き方（\u0072 は r、\u0069 は i）
            var cases = new[]
            {
                ("\"\\u0072ecording\"", ListenState.Recording),
                ("\"recognizin\\u0067\"", ListenState.Recognizing),
                ("\"\\u0069\\u0064\\u006c\\u0065\"", ListenState.Idle),
            };

            foreach (var (literal, expected) in cases)
            {
                var listen = ParseListen(ListenJson(literal));

                Assert.That(listen.State, Is.EqualTo(expected), literal);
            }
        }

        [Test]
        public void Parse_IgnoresUnknownFields_WhenListenHasExtraFields()
        {
            var inputs = new[]
            {
                ("{\"state\":\"recognizing\",\"type\":\"listen\"}", ListenState.Recognizing),
                (ListenJson("\"recording\"", "\"text\":\"聞いた文\",\"id\":3,\"turn\":4,\"nested\":{\"type\":\"heard\",\"text\":\"x\"}"), ListenState.Recording),
                (ListenJson("\"idle\"", "\"list\":[1,{\"state\":\"recording\"}],\"n\":NaN,\"flag\":true,\"nil\":null"), ListenState.Idle),
                ("  {\r\n \"type\" : \"listen\" ,\n\t\"state\" : \"recording\" }  \r\n", ListenState.Recording),
            };

            foreach (var (json, expected) in inputs)
            {
                var listen = ParseListen(json);

                Assert.That(listen.State, Is.EqualTo(expected), Visible(json));
            }
        }

        [Test]
        public void ListenMessage_KeepsState_WhenConstructed()
        {
            foreach (var state in ExpectedStates)
            {
                Assert.That(new ListenMessage(state).State, Is.EqualTo(state));
            }
        }

        // ---- listen: 知らない state は無視する ----

        // 3 つの名前と見た目や意味が近いが、ちょうど一致しない文字列
        private static readonly string[] UnknownStates =
        {
            "Recording",
            "RECORDING",
            "Recognizing",
            "Idle",
            "IDLE",
            "",
            " ",
            " recording",
            "recording ",
            "recording\n",
            "\tidle",
            "recognising",
            "listening",
            "record",
            "recordingx",
            "idl",
            "ｒｅｃｏｒｄｉｎｇ",
            "ｉｄｌｅ",
            "idle" + (char)0x200B,
            (char)0xFEFF + "idle",
            "idle" + (char)0,
            "rec" + (char)0x00AD + "ording",
            "recording,idle",
            "null",
            "2026-11-07T12:00:00Z",
        };

        [Test]
        public void Parse_ReturnsIgnored_WhenListenStateIsUnknownString()
        {
            foreach (var state in UnknownStates)
            {
                ParseIgnored(ListenJson(TestJson.Quote(state)));
                ParseIgnored(ListenJson(AsciiQuote(state)));
            }
        }

        // ---- listen: state の形の誤り ----

        [TestCase("null")]
        [TestCase("0")]
        [TestCase("1")]
        [TestCase("1.5")]
        [TestCase("true")]
        [TestCase("false")]
        [TestCase("NaN")]
        [TestCase("[]")]
        [TestCase("[\"recording\"]")]
        [TestCase("{}")]
        [TestCase("{\"state\":\"recording\"}")]
        public void Parse_ReturnsInvalid_WhenListenStateIsNotString(string literal)
        {
            ParseInvalid(ListenJson(literal));
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenListenStateIsMissing()
        {
            ParseInvalid(ListenJson(null));
            ParseInvalid(ListenJson(null, "\"State\":\"recording\""));
            ParseInvalid(ListenJson(null, "\"text\":\"recording\""));
        }

        [TestCase("\"state\":\"idle\"")]
        [TestCase("\"state\":\"recording\"")]
        [TestCase("\"type\":\"listen\"")]
        public void Parse_ReturnsInvalid_WhenListenHasDuplicateTopLevelKey(string duplicate)
        {
            ParseInvalid(ListenJson("\"recording\"", duplicate));
        }

        // ---- heard: 文をそのまま受け取る ----

        [Test]
        public void Parse_ReturnsHeardWithText_WhenTextIsString()
        {
            var heard = ParseHeard(HeardJson(TestJson.Quote("今日は何の日？")));

            Assert.That(heard.Text, Is.EqualTo("今日は何の日？"));
        }

        [Test]
        public void Parse_KeepsHeardTextExactly_WhenTextIsAnyStringWrittenAsTheBrainSendsIt()
        {
            foreach (var text in AnyTexts)
            {
                var heard = ParseHeard(HeardJson(AsciiQuote(text)));

                Assert.That(heard.Text, Is.EqualTo(text), "text: " + Visible(text));
            }
        }

        [Test]
        public void Parse_KeepsHeardTextExactly_WhenTextIsAnyStringWrittenAsRawUtf8()
        {
            foreach (var text in AnyTexts)
            {
                var heard = ParseHeard(HeardJson(TestJson.Quote(text)));

                Assert.That(heard.Text, Is.EqualTo(text), "text: " + Visible(text));
            }
        }

        [Test]
        public void Parse_KeepsHeardTextExactly_WhenTextUsesSurrogatePairEscapes()
        {
            // \uD83D\uDE00 は 😀、大文字と小文字の 16 進のどちらでも同じ
            var json = HeardJson("\"a\\uD83D\\uDE00b\\ud83c\\uddef\\ud83c\\uddf5c\"");

            var heard = ParseHeard(json);

            Assert.That(heard.Text, Is.EqualTo("a😀b🇯🇵c"));
        }

        [Test]
        public void Parse_ReturnsHeardKeepingSurroundingText_WhenTextHasUnpairedSurrogate()
        {
            // ensure_ascii は対になっていないサロゲートも \uXXXX で書く
            var inputs = new[]
            {
                HeardJson("\"前\\ud83d後\""),
                HeardJson("\"前\\ude00後\""),
                HeardJson("\"前\\ud83d\""),
            };

            foreach (var json in inputs)
            {
                var heard = ParseHeard(json);

                Assert.That(heard.Text.StartsWith("前", StringComparison.Ordinal), Is.True, Visible(json) + " → " + Visible(heard.Text));
                Assert.That(heard.Text.Length, Is.GreaterThanOrEqualTo(2), Visible(json) + " → " + Visible(heard.Text));
            }

            Assert.That(ParseHeard(inputs[0]).Text.EndsWith("後", StringComparison.Ordinal), Is.True);
            Assert.That(ParseHeard(inputs[1]).Text.EndsWith("後", StringComparison.Ordinal), Is.True);
        }

        [Test]
        public void Parse_KeepsHeardTextExactly_WhenTextIs8000CharactersOfJapanese()
        {
            var text = new string('漢', 8000);

            var heard = ParseHeard(HeardJson(AsciiQuote(text)));

            Assert.That(heard.Text, Is.EqualTo(text));
        }

        [Test]
        public void Parse_KeepsHeardTextExactly_WhenTextIs8000EmojiInAFrameLargerThanTheOutgoingLimit()
        {
            // 頭脳の 8,000 文字は Python の文字数。絵文字 1 つは ensure_ascii で 12 バイトになり、フレームは約 96 KB
            var text = string.Concat(Enumerable.Repeat("😀", 8000));
            var json = HeardJson(AsciiQuote(text));
            Assert.That(Encoding.UTF8.GetByteCount(json), Is.GreaterThan(BrainProtocol.MaxOutgoingBytes), "フレームが送る側の上限を超えていない");

            var heard = ParseHeard(json);

            Assert.That(heard.Text, Is.EqualTo(text));
        }

        [Test]
        public void Parse_IgnoresUnknownFields_WhenHeardHasExtraFields()
        {
            var inputs = new[]
            {
                "{\"text\":\"文\",\"type\":\"heard\"}",
                HeardJson("\"文\"", "\"state\":\"recording\",\"id\":9,\"turn\":2,\"nested\":{\"type\":\"listen\",\"state\":\"idle\"}"),
                HeardJson("\"文\"", "\"list\":[1,{\"text\":\"別\"}],\"n\":NaN,\"flag\":false,\"nil\":null"),
                "  {\r\n \"type\" : \"heard\" ,\n\t\"text\" : \"文\" }  \r\n",
            };

            foreach (var json in inputs)
            {
                var heard = ParseHeard(json);

                Assert.That(heard.Text, Is.EqualTo("文"), Visible(json));
            }
        }

        [Test]
        public void HeardMessage_KeepsText_WhenConstructed()
        {
            foreach (var text in AnyTexts)
            {
                Assert.That(new HeardMessage(text).Text, Is.EqualTo(text), Visible(text));
            }
        }

        // ---- heard: text の形の誤り ----

        [TestCase("null")]
        [TestCase("0")]
        [TestCase("1")]
        [TestCase("1.5")]
        [TestCase("true")]
        [TestCase("false")]
        [TestCase("NaN")]
        [TestCase("[]")]
        [TestCase("[\"文\"]")]
        [TestCase("{}")]
        [TestCase("{\"text\":\"文\"}")]
        public void Parse_ReturnsInvalid_WhenHeardTextIsNotString(string literal)
        {
            ParseInvalid(HeardJson(literal));
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenHeardTextIsMissing()
        {
            ParseInvalid(HeardJson(null));
            ParseInvalid(HeardJson(null, "\"Text\":\"文\""));
            ParseInvalid(HeardJson(null, "\"state\":\"idle\""));
        }

        [TestCase("\"text\":\"別の文\"")]
        [TestCase("\"text\":\"文\"")]
        [TestCase("\"type\":\"heard\"")]
        public void Parse_ReturnsInvalid_WhenHeardHasDuplicateTopLevelKey(string duplicate)
        {
            ParseInvalid(HeardJson("\"文\"", duplicate));
        }

        // ---- type はちょうど一致するときだけ ----

        [TestCase("{\"type\":\"Listen\",\"state\":\"recording\"}")]
        [TestCase("{\"type\":\"LISTEN\",\"state\":\"recording\"}")]
        [TestCase("{\"type\":\" listen\",\"state\":\"recording\"}")]
        [TestCase("{\"type\":\"listen \",\"state\":\"idle\"}")]
        [TestCase("{\"type\":\"listening\",\"state\":\"recording\"}")]
        [TestCase("{\"type\":\"Heard\",\"text\":\"文\"}")]
        [TestCase("{\"type\":\"HEARD\",\"text\":\"文\"}")]
        [TestCase("{\"type\":\"heard \",\"text\":\"文\"}")]
        [TestCase("{\"type\":\"hear\",\"text\":\"文\"}")]
        public void Parse_ReturnsIgnored_WhenTypeDiffersFromListenOrHeardOnlyInCaseSpacingOrSpelling(string json)
        {
            ParseIgnored(json);
        }

        // ---- 理由の文字列 ----

        [Test]
        public void Reason_IsBounded_WhenListenStateIsHuge()
        {
            var hugeString = ListenJson(TestJson.Quote(new string('r', 100000)));
            var hugeArray = ListenJson("[" + string.Join(",", Enumerable.Repeat("\"recording\"", 20000)) + "]");

            var ignored = ParseIgnored(hugeString);
            var invalid = ParseInvalid(hugeArray);

            Assert.That(ignored.Reason.Length, Is.LessThanOrEqualTo(MaxReasonLength), Visible(ignored.Reason));
            Assert.That(invalid.Reason.Length, Is.LessThanOrEqualTo(MaxReasonLength), Visible(invalid.Reason));
        }

        [Test]
        public void Reason_IsBounded_WhenHeardTextIsHugeNonString()
        {
            var hugeArray = HeardJson("[" + string.Join(",", Enumerable.Repeat("\"文\"", 20000)) + "]");
            var hugeObject = HeardJson("{\"text\":" + TestJson.Quote(new string('文', 100000)) + "}");

            foreach (var json in new[] { hugeArray, hugeObject })
            {
                var invalid = ParseInvalid(json);

                Assert.That(invalid.Reason.Length, Is.LessThanOrEqualTo(MaxReasonLength), Visible(invalid.Reason));
            }
        }

        // ---- どんな入力でも例外を出さない ----

        [Test]
        public void Parse_ReturnsInvalidWithoutThrowing_ForEveryProperPrefixOfListenAndHeard()
        {
            var messages = new[]
            {
                "{\"type\":\"listen\",\"state\":\"recognizing\"}",
                "{\"type\":\"heard\",\"text\":\"\\u3053\\u3093\\ud83d\\ude00\\n\"}",
            };

            foreach (var message in messages)
            {
                for (var length = 0; length < message.Length; length++)
                {
                    var prefix = message.Substring(0, length);
                    BrainMessage result = null;
                    Assert.DoesNotThrow(() => result = BrainProtocol.Parse(prefix), "prefix: " + prefix);
                    Assert.That(result, Is.InstanceOf<InvalidMessage>(), "prefix: " + prefix + " → " + Describe(result));
                }
            }
        }

        [Test]
        public void Parse_NeverThrowsOrReturnsNullAndKeepsListenAndHeardWellFormed_ForRandomMutations()
        {
            var bases = new[]
            {
                "{\"type\":\"listen\",\"state\":\"recording\"}",
                "{\"type\":\"listen\",\"state\":\"recognizing\"}",
                "{\"type\":\"listen\",\"state\":\"idle\"}",
                "{\"type\":\"heard\",\"text\":\"\\u4eca\\u65e5\\ud83d\\ude00\"}",
            };
            var alphabet = "{}[]\":,0123456789.-eE+tfnul aNIyRrgcdi\\/é😀\u0000";
            var random = new Random(20261107);

            for (var round = 0; round < 3000; round++)
            {
                var chars = new StringBuilder(bases[round % bases.Length]);
                var edits = 1 + random.Next(4);
                for (var e = 0; e < edits; e++)
                {
                    var position = random.Next(chars.Length + 1);
                    switch (random.Next(3))
                    {
                        case 0:
                            chars.Insert(position, alphabet[random.Next(alphabet.Length)]);
                            break;
                        case 1:
                            if (position < chars.Length)
                            {
                                chars.Remove(position, 1);
                            }

                            break;
                        default:
                            if (position < chars.Length)
                            {
                                chars[position] = alphabet[random.Next(alphabet.Length)];
                            }

                            break;
                    }
                }

                var json = chars.ToString();
                BrainMessage result = null;
                Assert.DoesNotThrow(() => result = BrainProtocol.Parse(json), "json: " + Visible(json));
                Assert.That(result, Is.Not.Null, "json: " + Visible(json));

                var listen = result as ListenMessage;
                if (listen != null)
                {
                    Assert.That(Enum.IsDefined(typeof(ListenState), listen.State), Is.True, Visible(json) + " → " + listen.State);
                }

                var heard = result as HeardMessage;
                if (heard != null)
                {
                    Assert.That(heard.Text, Is.Not.Null, Visible(json));
                }
            }
        }
    }
}
