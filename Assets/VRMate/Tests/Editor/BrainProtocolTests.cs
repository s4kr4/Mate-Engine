using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class BrainProtocolTests
    {
        private static string SayJson(string id = "1", string turn = "2", string text = "\"hi\"", string extra = "")
        {
            var parts = new List<string> { "\"type\":\"say\"" };
            if (id != null)
            {
                parts.Add("\"id\":" + id);
            }

            if (turn != null)
            {
                parts.Add("\"turn\":" + turn);
            }

            if (text != null)
            {
                parts.Add("\"text\":" + text);
            }

            if (!string.IsNullOrEmpty(extra))
            {
                parts.Add(extra);
            }

            return "{" + string.Join(",", parts) + "}";
        }

        private static SayMessage ParseSay(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<SayMessage>(), "say として読めない: " + json + " → " + Describe(message));
            return (SayMessage)message;
        }

        private static InvalidMessage ParseInvalid(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<InvalidMessage>(), "不正として扱われない: " + json + " → " + Describe(message));
            var invalid = (InvalidMessage)message;
            Assert.That(invalid.Reason, Is.Not.Null.And.Not.Empty, "Reason が空");
            return invalid;
        }

        private static string Describe(BrainMessage message)
        {
            return message == null ? "null" : message.GetType().Name;
        }

        // ---- Parse: say の正常系 ----

        [Test]
        public void Parse_ReturnsSayWithAllFields_WhenSayHasAudioAndVisemes()
        {
            // Arrange
            var json = "{\"type\":\"say\",\"id\":7,\"turn\":3,\"text\":\"こんにちは\",\"audio\":\"UklGRg==\","
                + "\"visemes\":[{\"t\":0,\"v\":\"aa\"},{\"t\":0.12,\"v\":\"oh\"},{\"t\":0.5,\"v\":\"sil\"}]}";

            // Act
            var say = ParseSay(json);

            // Assert
            Assert.That(say.Id, Is.EqualTo(7L));
            Assert.That(say.Turn, Is.EqualTo(3L));
            Assert.That(say.Text, Is.EqualTo("こんにちは"));
            Assert.That(say.HasAudio, Is.True);
            Assert.That(say.AudioBase64, Is.EqualTo("UklGRg=="));
            Assert.That(say.Visemes, Is.Not.Null);
            Assert.That(say.Visemes.Count, Is.EqualTo(3));
            Assert.That(say.Visemes[0].Time, Is.EqualTo(0.0));
            Assert.That(say.Visemes[0].Shape, Is.EqualTo(VisemeShape.Aa));
            Assert.That(say.Visemes[1].Time, Is.EqualTo(0.12));
            Assert.That(say.Visemes[1].Shape, Is.EqualTo(VisemeShape.Oh));
            Assert.That(say.Visemes[2].Time, Is.EqualTo(0.5));
            Assert.That(say.Visemes[2].Shape, Is.EqualTo(VisemeShape.Sil));
        }

        [Test]
        public void Parse_ReturnsSayWithoutAudioAndVisemes_WhenOptionalFieldsAreMissing()
        {
            var say = ParseSay(SayJson());

            Assert.That(say.Id, Is.EqualTo(1L));
            Assert.That(say.Turn, Is.EqualTo(2L));
            Assert.That(say.Text, Is.EqualTo("hi"));
            Assert.That(say.HasAudio, Is.False);
            Assert.That(say.AudioBase64, Is.Null);
            Assert.That(say.Visemes, Is.Null);
        }

        [Test]
        public void Parse_ReturnsSayWithoutAudio_WhenAudioAndVisemesAreNull()
        {
            var say = ParseSay(SayJson(extra: "\"audio\":null,\"visemes\":null"));

            Assert.That(say.HasAudio, Is.False);
            Assert.That(say.AudioBase64, Is.Null);
            Assert.That(say.Visemes, Is.Null);
        }

        [TestCase("0", 0L)]
        [TestCase("-1", -1L)]
        [TestCase("9223372036854775807", long.MaxValue)]
        [TestCase("-9223372036854775808", long.MinValue)]
        public void Parse_AcceptsIdAndTurn_WhenValueIsIntegerWithinLongRange(string literal, long expected)
        {
            var say = ParseSay(SayJson(id: literal, turn: literal));

            Assert.That(say.Id, Is.EqualTo(expected));
            Assert.That(say.Turn, Is.EqualTo(expected));
        }

        [Test]
        public void Parse_IgnoresUnknownFields_WhenSayHasExtraFields()
        {
            var json = "{\"expression\":\"joy\",\"type\":\"say\",\"nested\":{\"type\":\"cancel\",\"id\":\"x\"},"
                + "\"id\":5,\"list\":[1,2,{\"a\":null}],\"turn\":6,\"text\":\"ok\",\"motion\":true,\"n\":NaN}";

            var say = ParseSay(json);

            Assert.That(say.Id, Is.EqualTo(5L));
            Assert.That(say.Turn, Is.EqualTo(6L));
            Assert.That(say.Text, Is.EqualTo("ok"));
        }

        // 制御文字や双方向制御文字をテスト名に出さないため、配列で持ってループで確かめる
        private static readonly string[] AnyTexts =
        {
            "",
            " ",
            "😀👍🏽🇯🇵👨" + (char)0x200D + "👩" + (char)0x200D + "👧",
            "改行\nを含む\r\nテキスト\tと\"引用符\"と\\",
            "1.0",
            "null",
            "{\"type\":\"cancel\"}",
            string.Concat((char)0x2028, (char)0x2029, (char)0x200B, (char)0xFEFF, (char)0x202E, (char)0x3000, (char)0x0085),
            "<script>alert(1)</script>",
            "\\u0041 はエスケープではなく文字",
            "a" + (char)0 + "b" + (char)0x1B + "c" + (char)0x7F,
        };

        private static string Visible(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7F || (c >= 0x2028 && c <= 0x202E) || c == 0x0085 || c == 0x200B || c == 0xFEFF)
                {
                    builder.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        [Test]
        public void Parse_KeepsTextExactly_WhenTextIsAnyString()
        {
            foreach (var text in AnyTexts)
            {
                var say = ParseSay(SayJson(text: TestJson.Quote(text)));

                Assert.That(say.Text, Is.EqualTo(text), "text: " + Visible(text));
            }
        }

        [TestCase("2026-10-14T12:00:00Z")]
        [TestCase("2026-10-14T12:00:00+09:00")]
        [TestCase("2026-10-14T12:00:00.1234567-05:00")]
        [TestCase("2026-10-14")]
        [TestCase("/Date(1760443200000)/")]
        [TestCase("/Date(1760443200000+0900)/")]
        public void Parse_KeepsTextExactly_WhenTextLooksLikeDateTime(string text)
        {
            var say = ParseSay(SayJson(text: TestJson.Quote(text)));

            Assert.That(say.Text, Is.EqualTo(text));
        }

        [Test]
        public void Parse_KeepsAudioStringExactly_WhenAudioLooksLikeDateTime()
        {
            var say = ParseSay(SayJson(extra: "\"audio\":\"2026-10-14T12:00:00Z\""));

            Assert.That(say.HasAudio, Is.True);
            Assert.That(say.AudioBase64, Is.EqualTo("2026-10-14T12:00:00Z"));
        }

        [Test]
        public void Parse_KeepsTextExactly_WhenTextUsesUnicodeEscapes()
        {
            // サロゲートペアのエスケープは 😀、NUL と U+001F は制御文字
            var json = SayJson(text: "\"a\\u00e9\\uD83D\\uDE00\\u0000\\u001F\\/z\"");

            var say = ParseSay(json);

            Assert.That(say.Text, Is.EqualTo("aé😀" + (char)0 + (char)0x1F + "/z"));
        }

        [Test]
        public void Parse_KeepsLongTextAndAudio_WhenStringsAreLarge()
        {
            var text = new string('あ', 200000);
            var audio = Convert.ToBase64String(Enumerable.Range(0, 1000000).Select(i => (byte)(i * 31)).ToArray());

            var say = ParseSay(SayJson(text: TestJson.Quote(text), extra: "\"audio\":" + TestJson.Quote(audio)));

            Assert.That(say.Text, Is.EqualTo(text));
            Assert.That(say.AudioBase64, Is.EqualTo(audio));
        }

        // ---- Parse: audio ----

        [TestCase("\"\"", "")]
        [TestCase("\"not base64 !!\"", "not base64 !!")]
        public void Parse_KeepsAudioString_WhenAudioIsString(string literal, string expected)
        {
            var say = ParseSay(SayJson(extra: "\"audio\":" + literal));

            Assert.That(say.HasAudio, Is.True);
            Assert.That(say.AudioBase64, Is.EqualTo(expected));
        }

        [TestCase("123")]
        [TestCase("1.5")]
        [TestCase("true")]
        [TestCase("false")]
        [TestCase("{}")]
        [TestCase("[\"UklGRg==\"]")]
        [TestCase("NaN")]
        public void Parse_MarksAudioAsUnplayable_WhenAudioIsNotString(string literal)
        {
            var say = ParseSay(SayJson(extra: "\"audio\":" + literal));

            Assert.That(say.HasAudio, Is.True);
            Assert.That(say.AudioBase64, Is.Null);
        }

        // ---- Parse: visemes ----

        [TestCase("aa", VisemeShape.Aa)]
        [TestCase("ih", VisemeShape.Ih)]
        [TestCase("ou", VisemeShape.Ou)]
        [TestCase("ee", VisemeShape.Ee)]
        [TestCase("oh", VisemeShape.Oh)]
        [TestCase("sil", VisemeShape.Sil)]
        [TestCase("xx", VisemeShape.Sil)]
        [TestCase("AA", VisemeShape.Sil)]
        [TestCase("Oh", VisemeShape.Sil)]
        [TestCase("", VisemeShape.Sil)]
        [TestCase(" aa", VisemeShape.Sil)]
        [TestCase("aa ", VisemeShape.Sil)]
        [TestCase("ａａ", VisemeShape.Sil)]
        [TestCase("a", VisemeShape.Sil)]
        [TestCase("N", VisemeShape.Sil)]
        public void Parse_MapsVisemeName_WhenVIsString(string name, VisemeShape expected)
        {
            var say = ParseSay(SayJson(extra: "\"visemes\":[{\"t\":0,\"v\":" + TestJson.Quote(name) + "}]"));

            Assert.That(say.Visemes, Is.Not.Null);
            Assert.That(say.Visemes.Count, Is.EqualTo(1));
            Assert.That(say.Visemes[0].Shape, Is.EqualTo(expected));
        }

        [Test]
        public void Parse_AcceptsVisemes_WhenTimesAreIntegersExponentsAndFirstIsNotZero()
        {
            var say = ParseSay(SayJson(extra:
                "\"visemes\":[{\"t\":1e-3,\"v\":\"ih\",\"extra\":1},{\"t\":1,\"v\":\"ou\"},{\"t\":2.5E0,\"v\":\"ee\"}]"));

            Assert.That(say.Visemes, Is.Not.Null);
            Assert.That(say.Visemes.Select(k => k.Time).ToArray(), Is.EqualTo(new[] { 0.001, 1.0, 2.5 }));
            Assert.That(say.Visemes.Select(k => k.Shape).ToArray(),
                Is.EqualTo(new[] { VisemeShape.Ih, VisemeShape.Ou, VisemeShape.Ee }));
        }

        [Test]
        public void Parse_KeepsVisemeOrderAndCount_WhenVisemesAreLong()
        {
            var names = new[] { "aa", "ih", "ou", "ee", "oh", "sil" };
            var shapes = new[] { VisemeShape.Aa, VisemeShape.Ih, VisemeShape.Ou, VisemeShape.Ee, VisemeShape.Oh, VisemeShape.Sil };
            var items = Enumerable.Range(0, 2000).Select(i => "{\"t\":" + (i * 0.01).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"v\":\"" + names[i % 6] + "\"}");

            var say = ParseSay(SayJson(extra: "\"visemes\":[" + string.Join(",", items) + "]"));

            Assert.That(say.Visemes, Is.Not.Null);
            Assert.That(say.Visemes.Count, Is.EqualTo(2000));
            for (var i = 0; i < 2000; i++)
            {
                Assert.That(say.Visemes[i].Shape, Is.EqualTo(shapes[i % 6]), "index " + i);
                Assert.That(say.Visemes[i].Time, Is.EqualTo(i * 0.01).Within(1e-12), "index " + i);
            }
        }

        [TestCase("[]", TestName = "Parse_DropsVisemesButKeepsSay_WhenVisemesIsEmptyArray")]
        [TestCase("{}", TestName = "Parse_DropsVisemesButKeepsSay_WhenVisemesIsObject")]
        [TestCase("\"aa\"", TestName = "Parse_DropsVisemesButKeepsSay_WhenVisemesIsString")]
        [TestCase("0", TestName = "Parse_DropsVisemesButKeepsSay_WhenVisemesIsNumber")]
        [TestCase("true", TestName = "Parse_DropsVisemesButKeepsSay_WhenVisemesIsBool")]
        [TestCase("[0]", TestName = "Parse_DropsVisemesButKeepsSay_WhenElementIsNumber")]
        [TestCase("[null]", TestName = "Parse_DropsVisemesButKeepsSay_WhenElementIsNull")]
        [TestCase("[[0,\"aa\"]]", TestName = "Parse_DropsVisemesButKeepsSay_WhenElementIsArray")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},\"sil\"]", TestName = "Parse_DropsVisemesButKeepsSay_WhenLaterElementIsString")]
        [TestCase("[{\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsMissing")]
        [TestCase("[{\"t\":null,\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsNull")]
        [TestCase("[{\"t\":\"0\",\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsString")]
        [TestCase("[{\"t\":true,\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsBool")]
        [TestCase("[{\"t\":[0],\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsArray")]
        [TestCase("[{\"t\":-0.001,\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsNegative")]
        [TestCase("[{\"t\":-1,\"v\":\"aa\"},{\"t\":0,\"v\":\"sil\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenFirstTIsNegativeInteger")]
        [TestCase("[{\"t\":NaN,\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsNaN")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},{\"t\":Infinity,\"v\":\"sil\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsInfinity")]
        [TestCase("[{\"t\":-Infinity,\"v\":\"aa\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTIsNegativeInfinity")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},{\"t\":0.2,\"v\":\"oh\"},{\"t\":0.1,\"v\":\"sil\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTDecreases")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},{\"t\":0.1,\"v\":\"oh\"},{\"t\":0.1,\"v\":\"sil\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTRepeats")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},{\"t\":0,\"v\":\"sil\"}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenTRepeatsAtZero")]
        [TestCase("[{\"t\":0}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenVIsMissing")]
        [TestCase("[{\"t\":0,\"v\":null}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenVIsNull")]
        [TestCase("[{\"t\":0,\"v\":1}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenVIsNumber")]
        [TestCase("[{\"t\":0,\"v\":[\"aa\"]}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenVIsArray")]
        [TestCase("[{\"t\":0,\"v\":\"aa\"},{\"t\":0.1,\"v\":false}]", TestName = "Parse_DropsVisemesButKeepsSay_WhenLaterVIsBool")]
        public void Parse_DropsVisemesButKeepsSay_WhenVisemesAreUnusable(string visemes)
        {
            var say = ParseSay(SayJson(id: "11", turn: "4", text: "\"text\"", extra: "\"audio\":\"AAAA\",\"visemes\":" + visemes));

            Assert.That(say.Visemes, Is.Null);
            Assert.That(say.Id, Is.EqualTo(11L));
            Assert.That(say.Turn, Is.EqualTo(4L));
            Assert.That(say.Text, Is.EqualTo("text"));
            Assert.That(say.HasAudio, Is.True);
            Assert.That(say.AudioBase64, Is.EqualTo("AAAA"));
        }

        // ---- Parse: say のフィールドの不正 ----

        [TestCase("true")]
        [TestCase("false")]
        [TestCase("1.5")]
        [TestCase("1.0")]
        [TestCase("-0.5")]
        [TestCase("\"1\"")]
        [TestCase("null")]
        [TestCase("9223372036854775808")]
        [TestCase("-9223372036854775809")]
        [TestCase("123456789012345678901234567890")]
        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("{}")]
        [TestCase("[1]")]
        public void Parse_ReturnsInvalidWithoutId_WhenIdIsNotLongInteger(string literal)
        {
            var invalid = ParseInvalid(SayJson(id: literal));

            Assert.That(invalid.Id.HasValue, Is.False);
        }

        [Test]
        public void Parse_ReturnsInvalidWithoutId_WhenIdIsMissing()
        {
            var invalid = ParseInvalid(SayJson(id: null));

            Assert.That(invalid.Id.HasValue, Is.False);
        }

        [TestCase("true")]
        [TestCase("1.5")]
        [TestCase("1.0")]
        [TestCase("\"2\"")]
        [TestCase("null")]
        [TestCase("9223372036854775808")]
        [TestCase("Infinity")]
        [TestCase("[]")]
        public void Parse_ReturnsInvalidWithId_WhenTurnIsNotLongInteger(string literal)
        {
            var invalid = ParseInvalid(SayJson(id: "42", turn: literal));

            Assert.That(invalid.Id, Is.EqualTo((long?)42L));
        }

        [Test]
        public void Parse_ReturnsInvalidWithId_WhenTurnIsMissing()
        {
            var invalid = ParseInvalid(SayJson(id: "-3", turn: null));

            Assert.That(invalid.Id, Is.EqualTo((long?)-3L));
        }

        [TestCase("1")]
        [TestCase("true")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("[\"a\"]")]
        [TestCase("NaN")]
        public void Parse_ReturnsInvalidWithId_WhenTextIsNotString(string literal)
        {
            var invalid = ParseInvalid(SayJson(id: "9", text: literal));

            Assert.That(invalid.Id, Is.EqualTo((long?)9L));
        }

        [Test]
        public void Parse_ReturnsInvalidWithId_WhenTextIsMissing()
        {
            var invalid = ParseInvalid(SayJson(id: "9", text: null));

            Assert.That(invalid.Id, Is.EqualTo((long?)9L));
        }

        [TestCase("\"id\":3")]
        [TestCase("\"turn\":3")]
        [TestCase("\"text\":\"other\"")]
        [TestCase("\"type\":\"say\"")]
        [TestCase("\"audio\":\"AAAA\",\"audio\":\"BBBB\"")]
        [TestCase("\"visemes\":[{\"t\":0,\"v\":\"aa\"}],\"visemes\":[{\"t\":0,\"v\":\"oh\"}]")]
        public void Parse_ReturnsInvalid_WhenSayHasDuplicateTopLevelKey(string duplicate)
        {
            ParseInvalid(SayJson(extra: duplicate));
        }

        // ---- Parse: say 以外 ----

        [Test]
        public void Parse_ReturnsCancel_WhenTypeIsCancelRegardlessOfOtherFields()
        {
            var inputs = new[]
            {
                "{\"type\":\"cancel\"}",
                "{\"type\":\"cancel\",\"id\":\"garbage\",\"text\":5,\"visemes\":-1,\"audio\":{}}",
                "  {\r\n \"x\": NaN, \"type\" : \"cancel\" }  \r\n",
            };

            foreach (var json in inputs)
            {
                var message = BrainProtocol.Parse(json);

                Assert.That(message, Is.InstanceOf<CancelMessage>(), Visible(json) + " → " + Describe(message));
            }
        }

        [TestCase("{\"type\":\"expression\",\"name\":\"joy\"}")]
        [TestCase("{\"type\":\"motion\",\"name\":\"wave\"}")]
        [TestCase("{\"type\":\"input\",\"text\":\"hi\"}")]
        [TestCase("{\"type\":\"played\",\"id\":1}")]
        [TestCase("{\"type\":\"SAY\",\"id\":1,\"turn\":1,\"text\":\"a\"}")]
        [TestCase("{\"type\":\"Cancel\"}")]
        [TestCase("{\"type\":\" say\",\"id\":1,\"turn\":1,\"text\":\"a\"}")]
        [TestCase("{\"type\":\"\"}")]
        [TestCase("{\"type\":\"2026-10-14T12:00:00Z\"}")]
        public void Parse_ReturnsIgnored_WhenTypeIsUnknownString(string json)
        {
            var message = BrainProtocol.Parse(json);

            Assert.That(message, Is.InstanceOf<IgnoredMessage>(), Describe(message));
            Assert.That(((IgnoredMessage)message).Reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{")]
        [TestCase("}")]
        [TestCase("not json")]
        [TestCase("{\"type\":\"say\"")]
        [TestCase("{\"type\":\"cancel\"} trailing")]
        [TestCase("{\"type\":\"cancel\"}{\"type\":\"cancel\"}")]
        [TestCase("{\"type\":\"cancel\",}x")]
        [TestCase("{\"type\":\"say\",\"id\":1,\"turn\":2,\"text\":\"a\"")]
        public void Parse_ReturnsInvalid_WhenJsonIsBroken(string json)
        {
            var invalid = ParseInvalid(json);

            Assert.That(invalid.Id.HasValue, Is.False);
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenInputIsControlCharacters()
        {
            var inputs = new[] { ((char)0).ToString(), "{" + (char)0 + "}", ((char)0xFEFF).ToString() };

            foreach (var json in inputs)
            {
                ParseInvalid(json);
            }
        }

        [TestCase("[]")]
        [TestCase("[{\"type\":\"cancel\"}]")]
        [TestCase("1")]
        [TestCase("1.5")]
        [TestCase("null")]
        [TestCase("true")]
        [TestCase("\"say\"")]
        [TestCase("\"2026-10-14T12:00:00Z\"")]
        [TestCase("NaN")]
        public void Parse_ReturnsInvalid_WhenJsonIsNotObject(string json)
        {
            ParseInvalid(json);
        }

        [TestCase("{}")]
        [TestCase("{\"id\":1,\"turn\":1,\"text\":\"a\"}")]
        [TestCase("{\"type\":null}")]
        [TestCase("{\"type\":1}")]
        [TestCase("{\"type\":true}")]
        [TestCase("{\"type\":{\"say\":1}}")]
        [TestCase("{\"type\":[\"say\"]}")]
        [TestCase("{\"Type\":\"say\",\"id\":1,\"turn\":1,\"text\":\"a\"}")]
        public void Parse_ReturnsInvalid_WhenTypeIsMissingOrNotString(string json)
        {
            ParseInvalid(json);
        }

        // ---- Parse: どんな入力でも例外を出さない ----

        [Test]
        public void Parse_ReturnsInvalidWithoutThrowing_ForEveryProperPrefixOfValidMessages()
        {
            var messages = new[]
            {
                "{\"type\":\"say\",\"id\":12,\"turn\":3,\"text\":\"あ😀\\n\",\"audio\":\"UklGRg==\",\"visemes\":[{\"t\":0,\"v\":\"aa\"},{\"t\":0.5,\"v\":\"sil\"}]}",
                "{\"type\":\"cancel\"}",
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
        public void Parse_NeverThrowsOrReturnsNull_ForRandomMutationsOfValidSay()
        {
            var baseJson = "{\"type\":\"say\",\"id\":12,\"turn\":3,\"text\":\"テキスト\",\"audio\":\"UklGRg==\","
                + "\"visemes\":[{\"t\":0,\"v\":\"aa\"},{\"t\":0.25,\"v\":\"oh\"},{\"t\":0.5,\"v\":\"sil\"}]}";
            var alphabet = "{}[]\":,0123456789.-eE+tfnul aNIy\\é😀\u0000";
            var random = new Random(20261014);

            for (var round = 0; round < 3000; round++)
            {
                var chars = new StringBuilder(baseJson);
                var edits = 1 + random.Next(4);
                for (var e = 0; e < edits; e++)
                {
                    var position = random.Next(chars.Length + 1);
                    var choice = random.Next(3);
                    if (choice == 0 && position < chars.Length)
                    {
                        chars.Remove(position, 1);
                    }
                    else if (choice == 1 && position < chars.Length)
                    {
                        chars[position] = alphabet[random.Next(alphabet.Length)];
                    }
                    else
                    {
                        chars.Insert(position, alphabet[random.Next(alphabet.Length)]);
                    }
                }

                var mutated = chars.ToString();
                BrainMessage result = null;
                Assert.DoesNotThrow(() => result = BrainProtocol.Parse(mutated), "input: " + mutated);
                Assert.That(result, Is.Not.Null, "input: " + mutated);
            }
        }

        [TestCase("[", "]", 100000)]
        [TestCase("{\"a\":", "}", 100000)]
        public void Parse_ReturnsInvalidWithoutThrowing_WhenJsonIsDeeplyNestedAndUnclosed(string opener, string closer, int depth)
        {
            var nested = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                nested.Append(opener);
            }

            var json = SayJson(extra: "\"deep\":" + nested);
            BrainMessage result = null;

            Assert.DoesNotThrow(() => result = BrainProtocol.Parse(json));
            Assert.That(result, Is.InstanceOf<InvalidMessage>(), Describe(result));
        }

        [TestCase("[", "]", 100000)]
        [TestCase("{\"a\":", "}", 100000)]
        public void Parse_DoesNotThrow_WhenJsonIsDeeplyNestedAndClosed(string opener, string closer, int depth)
        {
            var nested = new StringBuilder();
            for (var i = 0; i < depth; i++)
            {
                nested.Append(opener);
            }

            nested.Append("0");
            for (var i = 0; i < depth; i++)
            {
                nested.Append(closer);
            }

            var json = SayJson(extra: "\"deep\":" + nested);
            BrainMessage result = null;

            Assert.DoesNotThrow(() => result = BrainProtocol.Parse(json));
            Assert.That(result, Is.InstanceOf<SayMessage>().Or.InstanceOf<InvalidMessage>(), Describe(result));
        }

        [TestCase("[{\"t\":1e400,\"v\":\"aa\"}]")]
        [TestCase("[{\"t\":-1e400,\"v\":\"aa\"}]")]
        [TestCase("[{\"t\":1e-400,\"v\":\"aa\"}]")]
        [TestCase("[{\"t\":123456789012345678901234567890,\"v\":\"aa\"}]")]
        public void Parse_DoesNotThrow_WhenVisemeTimeIsOutOfDoubleRange(string visemes)
        {
            var json = SayJson(extra: "\"visemes\":" + visemes);
            BrainMessage result = null;

            Assert.DoesNotThrow(() => result = BrainProtocol.Parse(json));
            Assert.That(result, Is.Not.Null);
            var say = result as SayMessage;
            if (say != null && say.Visemes != null)
            {
                foreach (var key in say.Visemes)
                {
                    Assert.That(double.IsNaN(key.Time) || double.IsInfinity(key.Time) || key.Time < 0, Is.False,
                        "有限で負でない t だけが残ること: " + key.Time);
                }
            }
        }

        // ---- 組み立て ----

        [Test]
        public void MaxOutgoingBytes_Is64KiB()
        {
            Assert.That(BrainProtocol.MaxOutgoingBytes, Is.EqualTo(65536));
        }

        [TestCase(0L)]
        [TestCase(42L)]
        [TestCase(-7L)]
        [TestCase(long.MaxValue)]
        [TestCase(long.MinValue)]
        public void Started_BuildsStartedMessageWithIntegerId_ForAnyId(long id)
        {
            var json = TestJson.ReadObject(BrainProtocol.Started(id));

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "id" }));
            Assert.That(json["type"].Type, Is.EqualTo(JTokenType.String));
            Assert.That((string)json["type"], Is.EqualTo("started"));
            Assert.That(json["id"].Type, Is.EqualTo(JTokenType.Integer));
            Assert.That((long)json["id"], Is.EqualTo(id));
        }

        [TestCase(5L)]
        [TestCase(long.MaxValue)]
        [TestCase(long.MinValue)]
        public void Played_OmitsErrorKey_WhenErrorIsNull(long id)
        {
            var json = TestJson.ReadObject(BrainProtocol.Played(id));

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "id" }));
            Assert.That((string)json["type"], Is.EqualTo("played"));
            Assert.That(json["id"].Type, Is.EqualTo(JTokenType.Integer));
            Assert.That((long)json["id"], Is.EqualTo(id));
        }

        [Test]
        public void Played_OmitsErrorKey_WhenErrorIsExplicitNull()
        {
            var json = TestJson.ReadObject(BrainProtocol.Played(5, null));

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "id" }));
        }

        [Test]
        public void Played_IncludesErrorString_WhenErrorIsGiven()
        {
            var errors = new[]
            {
                "wav: unsupported format",
                "",
                "引用符\"とバックスラッシュ\\と改行\nと😀",
                "2026-10-14T12:00:00Z",
            };

            foreach (var error in errors)
            {
                var json = TestJson.ReadObject(BrainProtocol.Played(8, error));

                Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "id", "error" }), Visible(error));
                Assert.That((string)json["type"], Is.EqualTo("played"));
                Assert.That((long)json["id"], Is.EqualTo(8L));
                Assert.That(json["error"].Type, Is.EqualTo(JTokenType.String), Visible(error));
                Assert.That((string)json["error"], Is.EqualTo(error), Visible(error));
            }
        }

        [Test]
        public void TryBuildInput_RoundTripsText_WhenTextIsAnyString()
        {
            var texts = AnyTexts.Concat(new[]
            {
                "こんにちは",
                "\"quoted\" and \\backslash\\ and /slash/",
                "2026-10-14T12:00:00Z",
                "2026-10-14T12:00:00+09:00",
                "/Date(1760443200000)/",
                "</script><!--",
            });

            foreach (var text in texts)
            {
                string output;
                var ok = BrainProtocol.TryBuildInput(text, out output);

                Assert.That(ok, Is.True, Visible(text));
                var json = TestJson.ReadObject(output);
                Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "text" }), Visible(text));
                Assert.That((string)json["type"], Is.EqualTo("input"));
                Assert.That(json["text"].Type, Is.EqualTo(JTokenType.String), Visible(text));
                Assert.That((string)json["text"], Is.EqualTo(text), Visible(text));
            }
        }

        [Test]
        public void TryBuildInput_RoundTripsText_WhenTextContainsEveryControlCharacter()
        {
            var builder = new StringBuilder();
            for (var c = 0; c < 0x20; c++)
            {
                builder.Append((char)c);
            }

            builder.Append((char)0x7F);
            var text = "a" + builder + "z";

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(ok, Is.True);
            Assert.That((string)TestJson.ReadObject(output)["text"], Is.EqualTo(text));
        }

        private static int Overhead()
        {
            string empty;
            Assert.That(BrainProtocol.TryBuildInput(string.Empty, out empty), Is.True);
            return Encoding.UTF8.GetByteCount(empty);
        }

        [Test]
        public void TryBuildInput_Succeeds_WhenJsonIsExactlyMaxOutgoingBytes()
        {
            var text = new string('a', BrainProtocol.MaxOutgoingBytes - Overhead());

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(ok, Is.True);
            Assert.That(Encoding.UTF8.GetByteCount(output), Is.EqualTo(BrainProtocol.MaxOutgoingBytes));
            Assert.That((string)TestJson.ReadObject(output)["text"], Is.EqualTo(text));
        }

        [Test]
        public void TryBuildInput_Fails_WhenJsonIsOneByteOverMaxOutgoingBytes()
        {
            var text = new string('a', BrainProtocol.MaxOutgoingBytes - Overhead() + 1);

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryBuildInput_CountsUtf8Bytes_WhenTextHasMultibyteCharacters()
        {
            // 3 バイトの「あ」で埋め、端数を ASCII で合わせて、ちょうど上限と 1 バイト超えを作る
            var room = BrainProtocol.MaxOutgoingBytes - Overhead();
            var exact = new string('あ', room / 3) + new string('a', room % 3);
            var over = exact + "a";

            string output;
            Assert.That(BrainProtocol.TryBuildInput(exact, out output), Is.True);
            Assert.That(Encoding.UTF8.GetByteCount(output), Is.EqualTo(BrainProtocol.MaxOutgoingBytes));
            Assert.That(BrainProtocol.TryBuildInput(over, out output), Is.False);
        }

        [Test]
        public void TryBuildInput_Fails_WhenUtf16LengthFitsButUtf8BytesExceed()
        {
            // 😀 は UTF-16 で 2 文字・UTF-8 で 4 バイト
            var text = string.Concat(Enumerable.Repeat("😀", BrainProtocol.MaxOutgoingBytes / 4));

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(text.Length, Is.LessThan(BrainProtocol.MaxOutgoingBytes));
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryBuildInput_Fails_WhenEscapedJsonExceedsLimitThoughRawTextFits()
        {
            // 制御文字は JSON で \u0001 の 6 バイトになる
            var text = new string((char)1, 11000);

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(Encoding.UTF8.GetByteCount(text), Is.LessThan(BrainProtocol.MaxOutgoingBytes));
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryBuildInput_Succeeds_WhenTextIs8000CharactersOfJapanese()
        {
            var text = new string('漢', 8000);

            string output;
            var ok = BrainProtocol.TryBuildInput(text, out output);

            Assert.That(ok, Is.True);
            Assert.That((string)TestJson.ReadObject(output)["text"], Is.EqualTo(text));
        }
    }
}
