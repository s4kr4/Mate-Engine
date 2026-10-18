using System;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class ExpressionBlendTests
    {
        // 並びは happy, angry, sad, relaxed, surprised
        private static readonly string[] Emotions = { "happy", "angry", "sad", "relaxed", "surprised" };
        private const float Tolerance = 1e-4f;

        private static float[] Evaluate(ExpressionBlend blend, params float[] upstream)
        {
            var output = new float[5];
            blend.Evaluate(upstream, output);
            return output;
        }

        private static float[] OneHot(int index)
        {
            var values = new float[5];
            values[index] = 1f;
            return values;
        }

        /// <summary>本家の値のうち surprised を 0 にしたもの（本家は surprised を書かない）。</summary>
        private static float[] UpstreamSeen(float[] upstream)
        {
            var values = (float[])upstream.Clone();
            values[4] = 0f;
            return values;
        }

        private static string Show(float[] values)
        {
            return "[" + string.Join(", ", values.Select(v => v.ToString("0.#####"))) + "]";
        }

        private static void AssertWeights(float[] actual, float[] expected, string because)
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(Tolerance), because + " " + Emotions[i] + " actual " + Show(actual) + " expected " + Show(expected));
            }
        }

        // ---- 定数 ----

        [Test]
        public void FadeSeconds_IsQuarterSecond()
        {
            Assert.That(ExpressionBlend.FadeSeconds, Is.EqualTo(0.25f));
        }

        // ---- 何も付けていないとき ----

        [Test]
        public void Evaluate_PassesUpstreamThroughWithSurprisedZero_WhenNothingWasSet()
        {
            var blend = new ExpressionBlend();
            var upstream = new[] { 0.3f, 0.1f, 0.2f, 0.7f, 0.9f };

            blend.Advance(1f);

            Assert.That(blend.IsActive, Is.False);
            AssertWeights(Evaluate(blend, upstream), UpstreamSeen(upstream), "何もしていない");
        }

        [Test]
        public void Evaluate_SanitizesUpstream_WhenUpstreamHasNaNOrOutOfRangeValues()
        {
            var blend = new ExpressionBlend();

            var output = Evaluate(blend, float.NaN, 2f, -1f, 0.5f, 0.7f);

            AssertWeights(output, new[] { 0f, 1f, 0f, 0.5f, 0f }, "NaN は 0、範囲外は詰める");
        }

        [Test]
        public void Evaluate_SanitizesInfiniteUpstream()
        {
            var blend = new ExpressionBlend();

            var output = Evaluate(blend, float.PositiveInfinity, float.NegativeInfinity, 0f, 0f, 0f);

            AssertWeights(output, new[] { 1f, 0f, 0f, 0f, 0f }, "無限大は端に詰める");
        }

        [Test]
        public void Evaluate_LeavesOutputUntouchedWithoutThrowing_WhenArraysAreNullOrShort()
        {
            var blend = new ExpressionBlend();
            blend.Set("happy");
            blend.Advance(1f);
            var sentinel = new[] { 0.11f, 0.22f, 0.33f, 0.44f, 0.55f };

            var output = (float[])sentinel.Clone();
            Assert.DoesNotThrow(() => blend.Evaluate(null, output));
            Assert.That(output, Is.EqualTo(sentinel), "upstream が null");

            output = (float[])sentinel.Clone();
            Assert.DoesNotThrow(() => blend.Evaluate(new float[4], output));
            Assert.That(output, Is.EqualTo(sentinel), "upstream が短い");

            Assert.DoesNotThrow(() => blend.Evaluate(new float[5], null));

            var shortOutput = new[] { 0.11f, 0.22f, 0.33f, 0.44f };
            Assert.DoesNotThrow(() => blend.Evaluate(new float[5], shortOutput));
            Assert.That(shortOutput, Is.EqualTo(new[] { 0.11f, 0.22f, 0.33f, 0.44f }), "output が短い");
        }

        // ---- 付ける ----

        [Test]
        public void Evaluate_StartsFromUpstream_RightAfterSet()
        {
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0.6f };
            foreach (var name in Emotions)
            {
                var blend = new ExpressionBlend();

                blend.Set(name);
                blend.Advance(0f);

                AssertWeights(Evaluate(blend, upstream), UpstreamSeen(upstream), name + " の直後");
            }
        }

        [Test]
        public void Evaluate_ShowsOnlyTheSetEmotion_AfterFadeSecondsRegardlessOfUpstream()
        {
            var upstreams = new[]
            {
                new[] { 0.3f, 0f, 0f, 0.7f, 0f },
                new[] { 1f, 1f, 1f, 1f, 1f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { float.NaN, 5f, -5f, 0.5f, 0.5f },
            };

            for (var index = 0; index < Emotions.Length; index++)
            {
                foreach (var upstream in upstreams)
                {
                    var blend = new ExpressionBlend();

                    blend.Set(Emotions[index]);
                    blend.Advance(ExpressionBlend.FadeSeconds);

                    Assert.That(blend.IsActive, Is.True, Emotions[index]);
                    AssertWeights(Evaluate(blend, upstream), OneHot(index), Emotions[index] + " upstream " + Show(upstream));
                }
            }
        }

        [Test]
        public void Evaluate_ShowsOnlyTheSetEmotion_AfterManySmallStepsAddingUpToMoreThanFadeSeconds()
        {
            var blend = new ExpressionBlend();
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0f };

            blend.Set("happy");
            for (var i = 0; i < 70; i++)
            {
                blend.Advance(1f / 240f);
            }

            AssertWeights(Evaluate(blend, upstream), OneHot(0), "70 / 240 秒");
        }

        [Test]
        public void Evaluate_IsBetweenUpstreamAndTarget_HalfwayThroughFade()
        {
            var blend = new ExpressionBlend();
            var upstream = new[] { 0f, 0f, 0f, 0.8f, 0f };

            blend.Set("happy");
            blend.Advance(ExpressionBlend.FadeSeconds / 2f);
            var output = Evaluate(blend, upstream);

            Assert.That(output[0], Is.GreaterThan(0f).And.LessThan(1f), Show(output));
            Assert.That(output[3], Is.GreaterThan(0f).And.LessThan(0.8f), Show(output));
            Assert.That(blend.IsActive, Is.True);
        }

        [Test]
        public void Evaluate_SwitchesToTheNewEmotion_AfterFadeSeconds()
        {
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0f };
            var blend = new ExpressionBlend();
            blend.Set("happy");
            blend.Advance(1f);

            blend.Set("sad");
            AssertWeights(Evaluate(blend, upstream), OneHot(0), "替えた直後は前の表情のまま");
            blend.Advance(ExpressionBlend.FadeSeconds);

            AssertWeights(Evaluate(blend, upstream), OneHot(2), "sad に替わる");
            Assert.That(blend.IsActive, Is.True);
        }

        [Test]
        public void Evaluate_ChainsSeveralSwitches_AndEndsOnTheLast()
        {
            var upstream = new[] { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
            var blend = new ExpressionBlend();

            blend.Set("happy");
            blend.Advance(0.1f);
            blend.Set("angry");
            blend.Advance(0.1f);
            blend.Set("surprised");
            blend.Advance(1f);

            AssertWeights(Evaluate(blend, upstream), OneHot(4), "最後の surprised");
        }

        // ---- 本家に返す ----

        [Test]
        public void Evaluate_ReturnsToUpstreamAndBecomesInactive_AfterNeutralAndFadeSeconds()
        {
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0.9f };
            foreach (var name in Emotions)
            {
                var blend = new ExpressionBlend();
                blend.Set(name);
                blend.Advance(1f);

                blend.Set("neutral");
                Assert.That(blend.IsActive, Is.True, name + ": neutral の直後はまだ効いている");
                blend.Advance(ExpressionBlend.FadeSeconds / 2f);
                Assert.That(blend.IsActive, Is.True, name + ": 戻している途中");
                blend.Advance(ExpressionBlend.FadeSeconds / 2f + 0.001f);

                AssertWeights(Evaluate(blend, upstream), UpstreamSeen(upstream), name + " → neutral");
                Assert.That(blend.IsActive, Is.False, name);
            }
        }

        [Test]
        public void Evaluate_ReturnsSurprisedToZero_AfterNeutral()
        {
            var blend = new ExpressionBlend();
            blend.Set("surprised");
            blend.Advance(1f);
            blend.Set("neutral");

            blend.Advance(1f);

            Assert.That(Evaluate(blend, 0f, 0f, 0f, 0f, 1f)[4], Is.EqualTo(0f), "本家は surprised を書かないので 0 に戻す");
        }

        [Test]
        public void IsActive_IsFalse_WhenNeutralIsSetWithoutAnyExpression()
        {
            var blend = new ExpressionBlend();

            blend.Set("neutral");
            blend.Advance(0.01f);

            Assert.That(blend.IsActive, Is.False);
        }

        [Test]
        public void IsActive_IsTrue_WhileAnExpressionIsHeld()
        {
            var blend = new ExpressionBlend();

            blend.Set("relaxed");
            blend.Advance(0.01f);
            Assert.That(blend.IsActive, Is.True, "付け始め");
            blend.Advance(100f);
            Assert.That(blend.IsActive, Is.True, "保っている");
        }

        [Test]
        public void Set_RestartsExpression_WhenSetDuringNeutralFade()
        {
            var upstream = new[] { 0f, 0f, 0f, 0.7f, 0f };
            var blend = new ExpressionBlend();
            blend.Set("happy");
            blend.Advance(1f);
            blend.Set("neutral");
            blend.Advance(0.1f);

            blend.Set("angry");
            blend.Advance(1f);

            AssertWeights(Evaluate(blend, upstream), OneHot(1), "戻している途中に付け直す");
            Assert.That(blend.IsActive, Is.True);
        }

        // ---- 無視する名前 ----

        [Test]
        public void Set_IgnoresNamesThatAreNotEmotionsOrNeutral()
        {
            var ignored = new[] { null, string.Empty, "Happy", "HAPPY", " sad", "sad ", "joy", "custom", "aa", "Neutral", "neutral" + (char)0 };
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0f };

            var fresh = new ExpressionBlend();
            foreach (var name in ignored)
            {
                fresh.Set(name);
            }

            fresh.Advance(1f);
            Assert.That(fresh.IsActive, Is.False, "何も付けていないまま");
            AssertWeights(Evaluate(fresh, upstream), UpstreamSeen(upstream), "何も付けていないまま");

            var holding = new ExpressionBlend();
            holding.Set("happy");
            holding.Advance(1f);
            foreach (var name in ignored)
            {
                holding.Set(name);
            }

            holding.Advance(1f);
            Assert.That(holding.IsActive, Is.True, "happy のまま");
            AssertWeights(Evaluate(holding, upstream), OneHot(0), "happy のまま");
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Advance_TreatsDeltaAsZero_WhenDeltaIsNegativeOrNotFinite(float delta)
        {
            var upstream = new[] { 0f, 0f, 0f, 0.8f, 0f };
            var blend = new ExpressionBlend();
            blend.Set("happy");
            blend.Advance(ExpressionBlend.FadeSeconds / 2f);
            var before = Evaluate(blend, upstream);

            blend.Advance(delta);

            AssertWeights(Evaluate(blend, upstream), before, "delta " + delta);
        }

        [Test]
        public void Evaluate_FadesBackLinearlyOverFadeSeconds_AfterNeutral()
        {
            var upstreams = new[] { new float[5], new[] { 0.2f, 0.4f, 0.1f, 0.6f, 0.9f } };
            foreach (var upstream in upstreams)
            {
                for (var index = 0; index < Emotions.Length; index++)
                {
                    var blend = new ExpressionBlend();
                    blend.Set(Emotions[index]);
                    blend.Advance(1f);

                    blend.Set("neutral");
                    var seen = UpstreamSeen(upstream);
                    for (var quarter = 1; quarter <= 3; quarter++)
                    {
                        blend.Advance(ExpressionBlend.FadeSeconds / 4f);
                        var remaining = 1f - quarter / 4f;
                        var expected = new float[5];
                        for (var i = 0; i < 5; i++)
                        {
                            expected[i] = seen[i] + ((i == index ? 1f : 0f) - seen[i]) * remaining;
                        }

                        AssertWeights(Evaluate(blend, upstream), expected, Emotions[index] + " upstream " + Show(upstream) + " " + quarter + "/4");
                    }
                }
            }
        }

        // ---- 性質（ランダムな操作列） ----

        [Test]
        public void Blend_KeepsOutputInRangeAndLimitsChangePerAdvance_ForRandomOperationSequences()
        {
            var names = new[] { "happy", "angry", "sad", "relaxed", "surprised", "neutral", "neutral", "Happy", null, "joy" };
            var deltas = new[] { 1f / 240f, 1f / 60f, 1f / 30f, 0.1f, 0f, -0.05f, float.NaN, float.PositiveInfinity, 10f, 0.0001f };
            var upstreams = new[]
            {
                new[] { 0.3f, 0f, 0f, 0.7f, 0f },
                new[] { 1f, 1f, 1f, 1f, 1f },
                new[] { 0f, 0f, 0f, 0f, 0.5f },
                new[] { float.NaN, 2f, -1f, 0.5f, 0.9f },
            };

            for (var seed = 0; seed < 300; seed++)
            {
                var random = new Random(seed);
                var upstream = upstreams[seed % upstreams.Length];
                var blend = new ExpressionBlend();
                var log = new System.Collections.Generic.List<string>();
                var previous = Evaluate(blend, upstream);
                var elapsed = 0f;

                for (var step = 0; step < 300; step++)
                {
                    if (random.Next(5) == 0)
                    {
                        var name = names[random.Next(names.Length)];
                        log.Add("set(" + (name ?? "null") + ")");
                        blend.Set(name);
                        continue;
                    }

                    var delta = deltas[random.Next(deltas.Length)];
                    log.Add("adv(" + delta + ")");
                    blend.Advance(delta);
                    elapsed = delta > 0 && !float.IsInfinity(delta) ? delta : 0f;

                    var output = Evaluate(blend, upstream);
                    var because = "seed " + seed + " step " + step + " " + Show(previous) + " → " + Show(output) + " log: " + string.Join(" ", log.Skip(Math.Max(0, log.Count - 20)));
                    var limit = 2f * elapsed / ExpressionBlend.FadeSeconds + 1e-4f;
                    for (var i = 0; i < 5; i++)
                    {
                        Assert.That(float.IsNaN(output[i]), Is.False, because);
                        Assert.That(output[i], Is.InRange(0f, 1f), because);
                        Assert.That(Math.Abs(output[i] - previous[i]), Is.LessThanOrEqualTo(limit), because + "（" + Emotions[i] + " の変化が大きすぎる）");
                    }

                    previous = output;
                }
            }
        }
    }
}
