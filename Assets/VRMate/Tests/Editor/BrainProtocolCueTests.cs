using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 表情とモーション（say の expression / motion、単独の expression / motion メッセージ）の読み方。
    /// </summary>
    public class BrainProtocolCueTests
    {
        private const string Audio = "UklGRg==";
        private const string Visemes = "[{\"t\":0,\"v\":\"aa\"},{\"t\":0.5,\"v\":\"sil\"}]";

        // 理由の文字列は「同じ名前ごとに 1 回だけ警告」の鍵になるので、巨大な値でも長さが有界であること
        private const int MaxReasonLength = 160;

        private static readonly string[] Expressions = { "happy", "angry", "sad", "relaxed", "surprised", "neutral" };

        private static string SayJson(string cueFields)
        {
            return "{\"type\":\"say\",\"id\":7,\"turn\":3,\"text\":\"こんにちは\",\"audio\":\"" + Audio + "\",\"visemes\":" + Visemes
                + (string.IsNullOrEmpty(cueFields) ? string.Empty : "," + cueFields) + "}";
        }

        private static SayMessage ParseSay(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<SayMessage>(), "say として読めない: " + Visible(json) + " → " + Describe(message));
            var say = (SayMessage)message;
            Assert.That(say.Problems, Is.Not.Null, "Problems が null");
            return say;
        }

        private static InvalidMessage ParseInvalid(string json)
        {
            var message = BrainProtocol.Parse(json);
            Assert.That(message, Is.InstanceOf<InvalidMessage>(), "不正として扱われない: " + Visible(json) + " → " + Describe(message));
            var invalid = (InvalidMessage)message;
            Assert.That(invalid.Reason, Is.Not.Null.And.Not.Empty, "Reason が空");
            Assert.That(invalid.Id.HasValue, Is.False, "表情・モーションのメッセージに id は無い");
            return invalid;
        }

        private static string Describe(BrainMessage message)
        {
            var invalid = message as InvalidMessage;
            if (invalid != null)
            {
                return "InvalidMessage(" + invalid.Reason + ")";
            }

            return message == null ? "null" : message.GetType().Name;
        }

        private static string Visible(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7F)
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

        /// <summary>say の M3 のフィールドが、表情・モーションの扱いに関係なくそのまま読めていること。</summary>
        private static void AssertSayFieldsIntact(SayMessage say, string because)
        {
            Assert.That(say.Id, Is.EqualTo(7L), because);
            Assert.That(say.Turn, Is.EqualTo(3L), because);
            Assert.That(say.Text, Is.EqualTo("こんにちは"), because);
            Assert.That(say.HasAudio, Is.True, because);
            Assert.That(say.AudioBase64, Is.EqualTo(Audio), because);
            Assert.That(say.Visemes, Is.Not.Null, because);
            Assert.That(say.Visemes.Count, Is.EqualTo(2), because);
        }

        private static string Reason(BrainMessage message)
        {
            var invalid = message as InvalidMessage;
            if (invalid != null)
            {
                return invalid.Reason;
            }

            var say = message as SayMessage;
            Assert.That(say, Is.Not.Null, Describe(message));
            Assert.That(say.Problems.Count, Is.EqualTo(1), Describe(message));
            return say.Problems[0];
        }

        // 種類ごとに、値を 1 つ入れたメッセージを作る（say の expression / say の motion / 単独の expression / 単独の motion）
        private static readonly Func<string, string>[] CueCarriers =
        {
            literal => SayJson("\"expression\":" + literal),
            literal => SayJson("\"motion\":" + literal),
            literal => "{\"type\":\"expression\",\"name\":" + literal + "}",
            literal => "{\"type\":\"motion\",\"name\":" + literal + "}",
        };

        private static readonly string[] CarrierNames = { "say.expression", "say.motion", "expression", "motion" };

        // ---- SayMessage の組み立て ----

        [Test]
        public void SayMessage_HasNoCuesAndEmptyProblems_WhenConstructedWithSixArguments()
        {
            var say = new SayMessage(1, 2, "text", false, null, null);

            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems, Is.Not.Null);
            Assert.That(say.Problems, Is.Empty);
        }

        [Test]
        public void SayMessage_KeepsCuesAndProblems_WhenGivenByName()
        {
            var say = new SayMessage(1, 2, "text", false, null, null, expression: "sad", motion: "wave", problems: new[] { "p1", "p2" });

            Assert.That(say.Expression, Is.EqualTo("sad"));
            Assert.That(say.Motion, Is.EqualTo("wave"));
            Assert.That(say.Problems, Is.EqualTo(new[] { "p1", "p2" }));
        }

        [Test]
        public void SayMessage_HasEmptyProblems_WhenProblemsArgumentIsNull()
        {
            var say = new SayMessage(1, 2, "text", false, null, null, "happy", null, null);

            Assert.That(say.Expression, Is.EqualTo("happy"));
            Assert.That(say.Problems, Is.Not.Null);
            Assert.That(say.Problems, Is.Empty);
        }

        // ---- say の expression / motion: 無い・null ----

        [Test]
        public void Parse_ReturnsSayWithoutCuesOrProblems_WhenExpressionAndMotionAreMissing()
        {
            var say = ParseSay(SayJson(null));

            AssertSayFieldsIntact(say, "フィールドなし");
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems, Is.Empty);
        }

        [Test]
        public void Parse_ReturnsSayWithoutCuesOrProblems_WhenExpressionAndMotionAreJsonNull()
        {
            var say = ParseSay(SayJson("\"expression\":null,\"motion\":null"));

            AssertSayFieldsIntact(say, "null");
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems, Is.Empty);
        }

        // ---- say の expression / motion: 確かめを通る ----

        [Test]
        public void Parse_KeepsExpression_WhenSayExpressionIsOneOfTheSixNames()
        {
            foreach (var name in Expressions)
            {
                var say = ParseSay(SayJson("\"expression\":" + TestJson.Quote(name)));

                AssertSayFieldsIntact(say, name);
                Assert.That(say.Expression, Is.EqualTo(name));
                Assert.That(say.Motion, Is.Null, name);
                Assert.That(say.Problems, Is.Empty, name);
            }
        }

        [TestCase("wave")]
        [TestCase("VRMA_02")]
        [TestCase("-")]
        [TestCase("a")]
        [TestCase("COM10")]
        [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-")]
        public void Parse_KeepsMotion_WhenSayMotionIsWellFormedName(string name)
        {
            var say = ParseSay(SayJson("\"motion\":" + TestJson.Quote(name)));

            AssertSayFieldsIntact(say, name);
            Assert.That(say.Motion, Is.EqualTo(name));
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Problems, Is.Empty);
        }

        [Test]
        public void Parse_KeepsBothCues_WhenSayHasValidExpressionAndMotion()
        {
            var say = ParseSay(SayJson("\"motion\":\"bow\",\"expression\":\"relaxed\""));

            AssertSayFieldsIntact(say, "両方");
            Assert.That(say.Expression, Is.EqualTo("relaxed"));
            Assert.That(say.Motion, Is.EqualTo("bow"));
            Assert.That(say.Problems, Is.Empty);
        }

        [Test]
        public void Parse_KeepsCues_WhenSayHasNoAudio()
        {
            var say = ParseSay("{\"type\":\"say\",\"id\":1,\"turn\":1,\"text\":\"a\",\"expression\":\"angry\",\"motion\":\"stomp\"}");

            Assert.That(say.HasAudio, Is.False);
            Assert.That(say.Expression, Is.EqualTo("angry"));
            Assert.That(say.Motion, Is.EqualTo("stomp"));
        }

        // ---- say の expression / motion: 捨てるもの ----

        private static readonly string[] BadExpressionLiterals =
        {
            "\"Happy\"",
            "\"HAPPY\"",
            "\" happy\"",
            "\"happy \"",
            "\"happy\\n\"",
            "\"happy\\u0000\"",
            "\"\"",
            "\"joy\"",
            "\"custom\"",
            "\"ｈａｐｐｙ\"",
            "\"../happy\"",
            "123",
            "0",
            "1.5",
            "true",
            "false",
            "[]",
            "[\"happy\"]",
            "{}",
            "{\"name\":\"happy\"}",
        };

        private static readonly string[] BadMotionLiterals =
        {
            "\"../wave\"",
            "\"..\"",
            "\".\"",
            "\"a/b\"",
            "\"a\\\\b\"",
            "\"C:wave\"",
            "\"wave.vrma\"",
            "\"NUL\"",
            "\"con\"",
            "\"COM1\"",
            "\"\"",
            "\" wave\"",
            "\"wave\\u0000\"",
            "\"ｗａｖｅ\"",
            "\"caf\\u00e9\"",
            "\"pose\\u0663\"",
            "\"" + new string('a', 65) + "\"",
            "1",
            "true",
            "[]",
            "[\"wave\"]",
            "{}",
        };

        [Test]
        public void Parse_DropsExpressionWithOneProblemButKeepsSay_WhenSayExpressionIsUnusable()
        {
            foreach (var literal in BadExpressionLiterals)
            {
                var because = "expression=" + Visible(literal);
                var say = ParseSay(SayJson("\"expression\":" + literal));

                AssertSayFieldsIntact(say, because);
                Assert.That(say.Expression, Is.Null, because);
                Assert.That(say.Motion, Is.Null, because);
                Assert.That(say.Problems.Count, Is.EqualTo(1), because);
                Assert.That(say.Problems[0], Is.Not.Null.And.Not.Empty, because);
            }
        }

        [Test]
        public void Parse_DropsMotionWithOneProblemButKeepsSay_WhenSayMotionIsUnusable()
        {
            foreach (var literal in BadMotionLiterals)
            {
                var because = "motion=" + Visible(literal);
                var say = ParseSay(SayJson("\"motion\":" + literal));

                AssertSayFieldsIntact(say, because);
                Assert.That(say.Motion, Is.Null, because);
                Assert.That(say.Expression, Is.Null, because);
                Assert.That(say.Problems.Count, Is.EqualTo(1), because);
                Assert.That(say.Problems[0], Is.Not.Null.And.Not.Empty, because);
            }
        }

        [Test]
        public void Parse_KeepsValidMotion_WhenOnlyExpressionIsUnusable()
        {
            var say = ParseSay(SayJson("\"expression\":\"Joy\",\"motion\":\"wave\""));

            AssertSayFieldsIntact(say, "表情だけ不正");
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.EqualTo("wave"));
            Assert.That(say.Problems.Count, Is.EqualTo(1));
        }

        [Test]
        public void Parse_KeepsValidExpression_WhenOnlyMotionIsUnusable()
        {
            var say = ParseSay(SayJson("\"expression\":\"sad\",\"motion\":\"../../evil\""));

            AssertSayFieldsIntact(say, "モーションだけ不正");
            Assert.That(say.Expression, Is.EqualTo("sad"));
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems.Count, Is.EqualTo(1));
        }

        [Test]
        public void Parse_ReportsTwoDifferentProblems_WhenExpressionAndMotionAreBothUnusable()
        {
            var say = ParseSay(SayJson("\"expression\":\"x/y\",\"motion\":\"x/y\""));

            AssertSayFieldsIntact(say, "両方不正");
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems.Count, Is.EqualTo(2));
            Assert.That(say.Problems[0], Is.Not.EqualTo(say.Problems[1]), "表情とモーションの理由は種類で区別できる");
        }

        [Test]
        public void Parse_KeepsSayAndBoundsProblem_WhenCueStringsAre100000Characters()
        {
            var huge = new string('Q', 100000);

            var say = ParseSay(SayJson("\"expression\":" + TestJson.Quote(huge) + ",\"motion\":" + TestJson.Quote(huge)));

            AssertSayFieldsIntact(say, "長い文字列");
            Assert.That(say.Expression, Is.Null);
            Assert.That(say.Motion, Is.Null);
            Assert.That(say.Problems.Count, Is.EqualTo(2));
            foreach (var problem in say.Problems)
            {
                Assert.That(problem.Length, Is.LessThanOrEqualTo(MaxReasonLength), problem.Substring(0, Math.Min(200, problem.Length)));
                Assert.That(problem.Contains(new string('Q', 41)), Is.False, "生の値を 40 文字より多く含む");
            }
        }

        // ---- 単独の expression / motion ----

        [Test]
        public void Parse_ReturnsExpressionMessage_WhenNameIsOneOfTheSixNames()
        {
            foreach (var name in Expressions)
            {
                var message = BrainProtocol.Parse("{\"type\":\"expression\",\"name\":" + TestJson.Quote(name) + "}");

                Assert.That(message, Is.InstanceOf<ExpressionMessage>(), name + " → " + Describe(message));
                Assert.That(((ExpressionMessage)message).Name, Is.EqualTo(name));
            }
        }

        [TestCase("wave")]
        [TestCase("VRMA_02")]
        [TestCase("_")]
        [TestCase("LPT10")]
        public void Parse_ReturnsMotionMessage_WhenNameIsWellFormed(string name)
        {
            var message = BrainProtocol.Parse("{\"type\":\"motion\",\"name\":" + TestJson.Quote(name) + "}");

            Assert.That(message, Is.InstanceOf<MotionMessage>(), name + " → " + Describe(message));
            Assert.That(((MotionMessage)message).Name, Is.EqualTo(name));
        }

        [Test]
        public void Parse_IgnoresUnknownFields_WhenExpressionOrMotionMessageHasExtraFields()
        {
            var expression = BrainProtocol.Parse("{\"id\":\"x\",\"type\":\"expression\",\"text\":5,\"name\":\"surprised\",\"motion\":\"../x\",\"n\":NaN,\"nested\":{\"type\":\"cancel\"}}");
            var motion = BrainProtocol.Parse("{\"name\":\"bow\",\"expression\":\"Joy\",\"type\":\"motion\",\"list\":[1,{\"a\":null}]}");

            Assert.That(expression, Is.InstanceOf<ExpressionMessage>(), Describe(expression));
            Assert.That(((ExpressionMessage)expression).Name, Is.EqualTo("surprised"));
            Assert.That(motion, Is.InstanceOf<MotionMessage>(), Describe(motion));
            Assert.That(((MotionMessage)motion).Name, Is.EqualTo("bow"));
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenExpressionMessageNameIsMissingOrUnusable()
        {
            ParseInvalid("{\"type\":\"expression\"}");
            ParseInvalid("{\"type\":\"expression\",\"name\":null}");
            ParseInvalid("{\"type\":\"expression\",\"Name\":\"happy\"}");
            foreach (var literal in BadExpressionLiterals)
            {
                ParseInvalid("{\"type\":\"expression\",\"name\":" + literal + "}");
            }
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenMotionMessageNameIsMissingOrUnusable()
        {
            ParseInvalid("{\"type\":\"motion\"}");
            ParseInvalid("{\"type\":\"motion\",\"name\":null}");
            ParseInvalid("{\"type\":\"motion\",\"Name\":\"wave\"}");
            foreach (var literal in BadMotionLiterals)
            {
                ParseInvalid("{\"type\":\"motion\",\"name\":" + literal + "}");
            }
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenExpressionMessageCarriesMotionNameOrViceVersa()
        {
            // 種類ごとに名前の集合が違う
            ParseInvalid("{\"type\":\"expression\",\"name\":\"wave\"}");
            ParseInvalid("{\"type\":\"motion\",\"name\":\"NUL\"}");
            var happyMotion = BrainProtocol.Parse("{\"type\":\"motion\",\"name\":\"happy\"}");
            Assert.That(happyMotion, Is.InstanceOf<MotionMessage>(), "happy はモーションの名前の形にも合う");
        }

        [Test]
        public void Parse_ReturnsInvalid_WhenExpressionOrMotionMessageHasDuplicateName()
        {
            ParseInvalid("{\"type\":\"expression\",\"name\":\"happy\",\"name\":\"sad\"}");
            ParseInvalid("{\"type\":\"motion\",\"name\":\"wave\",\"name\":\"bow\"}");
        }

        [Test]
        public void Parse_KeepsTypeMatchingExact_WhenTypeDiffersOnlyInCaseOrSpacing()
        {
            foreach (var type in new[] { "Expression", "EXPRESSION", " expression", "expression ", "Motion", "motions", "expressions" })
            {
                var message = BrainProtocol.Parse("{\"type\":" + TestJson.Quote(type) + ",\"name\":\"happy\"}");

                Assert.That(message, Is.InstanceOf<IgnoredMessage>(), "type=" + type + " → " + Describe(message));
            }
        }

        // ---- 理由の文字列（警告の鍵） ----

        [Test]
        public void Reason_IsBoundedAndHoldsAtMost40CharactersOfRawValue_WhenNameIsHuge()
        {
            var huge = new string('Q', 100000);
            for (var i = 0; i < CueCarriers.Length; i++)
            {
                var reason = Reason(BrainProtocol.Parse(CueCarriers[i](TestJson.Quote(huge))));

                Assert.That(reason, Is.Not.Null.And.Not.Empty, CarrierNames[i]);
                Assert.That(reason.Length, Is.LessThanOrEqualTo(MaxReasonLength), CarrierNames[i] + ": " + reason.Substring(0, Math.Min(200, reason.Length)));
                Assert.That(reason.Contains(new string('Q', 41)), Is.False, CarrierNames[i] + ": 生の値を 40 文字より多く含む");
            }
        }

        [Test]
        public void Reason_IsBounded_WhenNameIsHugeNonStringValue()
        {
            var hugeArray = "[" + string.Join(",", Enumerable.Repeat("\"QQQQQQQQQQ\"", 10000)) + "]";
            var hugeObject = "{" + string.Join(",", Enumerable.Range(0, 10000).Select(i => "\"k" + i + "\":\"QQQQQQQQQQ\"")) + "}";

            for (var i = 0; i < CueCarriers.Length; i++)
            {
                foreach (var literal in new[] { hugeArray, hugeObject })
                {
                    var reason = Reason(BrainProtocol.Parse(CueCarriers[i](literal)));

                    Assert.That(reason.Length, Is.LessThanOrEqualTo(MaxReasonLength), CarrierNames[i] + ": " + reason.Substring(0, Math.Min(200, reason.Length)));
                }
            }
        }

        [Test]
        public void Reason_IsSameString_WhenSameInputIsParsedAgain()
        {
            var literals = BadExpressionLiterals.Concat(BadMotionLiterals).Concat(new[] { TestJson.Quote(new string('Q', 5000)) }).ToArray();
            for (var i = 0; i < CueCarriers.Length; i++)
            {
                foreach (var literal in literals)
                {
                    var json = CueCarriers[i](literal);
                    var first = BrainProtocol.Parse(json);
                    if (first is SayMessage && ((SayMessage)first).Problems.Count == 0)
                    {
                        continue;
                    }

                    if (first is ExpressionMessage || first is MotionMessage)
                    {
                        continue;
                    }

                    Assert.That(Reason(BrainProtocol.Parse(json)), Is.EqualTo(Reason(first)), CarrierNames[i] + " " + Visible(literal));
                }
            }
        }

        [Test]
        public void Reason_Differs_WhenUnusableNamesDifferWithinFirst40Characters()
        {
            // どれもどちらの種類の名前にも合わない（"!" を含む）
            var stem = new string('Q', 39);
            var pairs = new[]
            {
                new[] { "a!", "b!" },
                new[] { "!a", "!A" },
                new[] { "x/1", "x/2" },
                new[] { stem + "1!" + new string('Z', 20), stem + "2!" + new string('Z', 20) },
                new[] { "Q!", "Q!Q" },
            };

            for (var i = 0; i < CueCarriers.Length; i++)
            {
                foreach (var pair in pairs)
                {
                    var left = Reason(BrainProtocol.Parse(CueCarriers[i](TestJson.Quote(pair[0]))));
                    var right = Reason(BrainProtocol.Parse(CueCarriers[i](TestJson.Quote(pair[1]))));

                    Assert.That(left, Is.Not.EqualTo(right), CarrierNames[i] + ": " + pair[0] + " / " + pair[1]);
                }
            }
        }

        [Test]
        public void Reason_DiffersBetweenExpressionAndMotion_WhenValueIsSame()
        {
            foreach (var value in new[] { "x/y", "Q!", "", ".." })
            {
                var sayExpression = Reason(BrainProtocol.Parse(CueCarriers[0](TestJson.Quote(value))));
                var sayMotion = Reason(BrainProtocol.Parse(CueCarriers[1](TestJson.Quote(value))));
                var expression = Reason(BrainProtocol.Parse(CueCarriers[2](TestJson.Quote(value))));
                var motion = Reason(BrainProtocol.Parse(CueCarriers[3](TestJson.Quote(value))));

                Assert.That(sayExpression, Is.Not.EqualTo(sayMotion), "say: " + value);
                Assert.That(expression, Is.Not.EqualTo(motion), "単独: " + value);
            }
        }

        // ---- IsCue（警告を名前ごとに 1 回にできる不正） ----

        [Test]
        public void Parse_MarksInvalidAsCue_WhenExpressionOrMotionMessageNameIsMissingOrUnusable()
        {
            var shapes = new[] { "{\"type\":\"expression\"}", "{\"type\":\"motion\"}", "{\"type\":\"expression\",\"name\":null}", "{\"type\":\"motion\",\"name\":null}", "{\"type\":\"expression\",\"Name\":\"happy\"}", "{\"type\":\"motion\",\"Name\":\"wave\"}" };
            var all = shapes
                .Concat(BadExpressionLiterals.Select(l => "{\"type\":\"expression\",\"name\":" + l + "}"))
                .Concat(BadMotionLiterals.Select(l => "{\"type\":\"motion\",\"name\":" + l + "}"))
                .Concat(new[] { "{\"type\":\"expression\",\"name\":" + TestJson.Quote(new string('Q', 100000)) + "}", "{\"type\":\"motion\",\"name\":" + TestJson.Quote(new string('Q', 100000)) + "}" });

            foreach (var json in all)
            {
                var invalid = ParseInvalid(json);

                Assert.That(invalid.IsCue, Is.True, Visible(json) + " → " + invalid.Reason);
            }
        }

        [Test]
        public void Parse_DoesNotMarkInvalidAsCue_ForInvalidMessagesOtherThanExpressionOrMotion()
        {
            var inputs = new[]
            {
                null,
                string.Empty,
                "   ",
                "not json",
                "{\"type\":\"expression\",\"name\":\"happy\"",
                "[{\"type\":\"motion\",\"name\":\"wave\"}]",
                "\"expression\"",
                "{}",
                "{\"type\":null,\"name\":\"happy\"}",
                "{\"type\":1,\"name\":\"wave\"}",
                "{\"name\":\"wave\"}",
                "{\"type\":\"say\",\"turn\":1,\"text\":\"a\",\"expression\":\"Joy\"}",
                "{\"type\":\"say\",\"id\":1,\"text\":\"a\",\"motion\":\"../x\"}",
                "{\"type\":\"say\",\"id\":1,\"turn\":1,\"expression\":\"happy\"}",
                "{\"type\":\"say\",\"id\":1,\"turn\":1,\"text\":5,\"motion\":\"wave\"}",
                "{\"type\":\"say\",\"id\":1,\"turn\":1,\"text\":\"a\",\"text\":\"b\"}",
            };

            foreach (var json in inputs)
            {
                var message = BrainProtocol.Parse(json);
                Assert.That(message, Is.InstanceOf<InvalidMessage>(), (json ?? "null") + " → " + Describe(message));

                Assert.That(((InvalidMessage)message).IsCue, Is.False, (json ?? "null") + " → " + ((InvalidMessage)message).Reason);
            }
        }

        // ---- どんな入力でも例外を出さない ----

        [Test]
        public void Parse_ReturnsInvalidWithoutThrowing_ForEveryProperPrefixOfCueMessages()
        {
            var messages = new[]
            {
                "{\"type\":\"expression\",\"name\":\"happy\"}",
                "{\"type\":\"motion\",\"name\":\"VRMA_02\"}",
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
        public void Parse_NeverThrowsOrReturnsNullAndKeepsCuesValid_ForRandomMutationsOfCueMessages()
        {
            var bases = new[]
            {
                "{\"type\":\"say\",\"id\":12,\"turn\":3,\"text\":\"テキスト\",\"expression\":\"happy\",\"motion\":\"VRMA_02\"}",
                "{\"type\":\"expression\",\"name\":\"surprised\"}",
                "{\"type\":\"motion\",\"name\":\"bow-1\"}",
            };
            var alphabet = "{}[]\":,0123456789.-eE+tfnul aNIy\\/é😀\u0000ABCZ_";
            var random = new Random(20261017);

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

                var say = result as SayMessage;
                if (say != null)
                {
                    Assert.That(say.Problems, Is.Not.Null, Visible(json));
                    Assert.That(say.Expression == null || CueNames.IsExpression(say.Expression), Is.True, Visible(json));
                    Assert.That(say.Motion == null || CueNames.IsMotion(say.Motion), Is.True, Visible(json));
                }

                var expression = result as ExpressionMessage;
                if (expression != null)
                {
                    Assert.That(CueNames.IsExpression(expression.Name), Is.True, Visible(json));
                }

                var motion = result as MotionMessage;
                if (motion != null)
                {
                    Assert.That(CueNames.IsMotion(motion.Name), Is.True, Visible(json));
                }
            }
        }
    }
}
