using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 話している間だけ、口を抑える表情（overrideMouth が blend / block）を弱めること。
    /// </summary>
    public class ExpressionBlendSpeakingTests
    {
        // 並びは happy, angry, sad, relaxed, surprised
        private static readonly string[] Emotions = { "happy", "angry", "sad", "relaxed", "surprised" };
        private const int Happy = 0;
        private const int Angry = 1;
        private const int Sad = 2;
        private const int Relaxed = 3;
        private const int Surprised = 4;
        private const float Frame = 1f / 60f;
        private const float Tolerance = 1e-4f;

        // aruka と同じく happy と surprised が blend。sad は block、ほかは none
        private static readonly MouthOverride[] Aruka = { MouthOverride.Blend, MouthOverride.None, MouthOverride.Block, MouthOverride.None, MouthOverride.Blend };
        private static readonly float[] Upstream = { 0.3f, 0f, 0f, 0.7f, 0f };

        private static float[] Evaluate(ExpressionBlend blend, float[] upstream = null)
        {
            var output = new float[5];
            blend.Evaluate(upstream ?? Upstream, output);
            return output;
        }

        private static float[] Weights(int index, float value)
        {
            var values = new float[5];
            values[index] = value;
            return values;
        }

        private static float[] UpstreamSeen(float[] upstream)
        {
            var values = (float[])upstream.Clone();
            values[Surprised] = 0f;
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

        private static ExpressionBlend ArukaBlend()
        {
            var blend = new ExpressionBlend();
            blend.SetMouthOverrides((MouthOverride[])Aruka.Clone());
            return blend;
        }

        /// <summary>毎フレーム SetSpeaking を呼びながら進め、各フレームの出力を返す。</summary>
        private static List<float[]> Run(ExpressionBlend blend, float seconds, bool speaking, float frame = Frame)
        {
            var outputs = new List<float[]>();
            var frames = (int)Math.Ceiling(seconds / frame);
            for (var i = 0; i < frames; i++)
            {
                blend.SetSpeaking(speaking);
                blend.Advance(frame);
                outputs.Add(Evaluate(blend));
            }

            return outputs;
        }

        // ---- 定数 ----

        [Test]
        public void Constants_HalveMouthBlockingExpressionsAndHoldThroughPausesBetweenSentences()
        {
            Assert.That(ExpressionBlend.SpeakingWeight, Is.EqualTo(0.5f));
            Assert.That(ExpressionBlend.SpeakingHoldSeconds, Is.EqualTo(0.6f));
        }

        // ---- 弱める ----

        [Test]
        public void Evaluate_CapsBlendEmotionAtSpeakingWeight_AfterSpeakingForFadeSecondsRegardlessOfUpstream()
        {
            var upstreams = new[]
            {
                new[] { 0.3f, 0f, 0f, 0.7f, 0f },
                new[] { 1f, 1f, 1f, 1f, 1f },
                new[] { 0f, 0f, 0f, 0f, 0f },
                new[] { float.NaN, 5f, -5f, 0.5f, 0.5f },
            };

            foreach (var index in new[] { Happy, Surprised })
            {
                foreach (var upstream in upstreams)
                {
                    var blend = ArukaBlend();
                    blend.Set(Emotions[index]);
                    blend.Advance(1f);

                    blend.SetSpeaking(true);
                    blend.Advance(ExpressionBlend.FadeSeconds);

                    AssertWeights(Evaluate(blend, upstream), Weights(index, ExpressionBlend.SpeakingWeight), Emotions[index] + " upstream " + Show(upstream));
                }
            }
        }

        [Test]
        public void Evaluate_CapsBlendEmotion_WhenSpeakingStartedBeforeSet()
        {
            var blend = ArukaBlend();
            blend.SetSpeaking(true);
            blend.Advance(1f);

            blend.Set("happy");
            blend.Advance(ExpressionBlend.FadeSeconds);

            AssertWeights(Evaluate(blend), Weights(Happy, ExpressionBlend.SpeakingWeight), "Set の前から話している");
        }

        [Test]
        public void Evaluate_LowersBlendEmotionGradually_WhenSpeakingStarts()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            blend.Advance(1f);
            var previous = Evaluate(blend);

            blend.SetSpeaking(true);
            Assert.That(Evaluate(blend)[Happy], Is.EqualTo(previous[Happy]).Within(Tolerance), "SetSpeaking だけでは跳ばない");
            foreach (var output in Run(blend, ExpressionBlend.FadeSeconds, true, 1f / 240f))
            {
                Assert.That(Math.Abs(output[Happy] - previous[Happy]), Is.LessThanOrEqualTo(2f / 240f / ExpressionBlend.FadeSeconds + Tolerance), Show(output));
                previous = output;
            }

            Assert.That(previous[Happy], Is.EqualTo(ExpressionBlend.SpeakingWeight).Within(Tolerance));
        }

        [Test]
        public void Evaluate_ZeroesBlockEmotion_AfterSpeakingForFadeSeconds()
        {
            var blend = ArukaBlend();
            blend.Set("sad");
            blend.Advance(1f);

            Run(blend, ExpressionBlend.FadeSeconds + Frame, true);

            AssertWeights(Evaluate(blend), new float[5], "block の sad は 0、ほかも 0（本家の値は使わない）");
            Assert.That(blend.IsActive, Is.True);
        }

        [Test]
        public void Evaluate_KeepsNoneEmotionAtFullWeight_WhileSpeaking()
        {
            foreach (var index in new[] { Angry, Relaxed })
            {
                var blend = ArukaBlend();
                blend.Set(Emotions[index]);
                blend.Advance(1f);

                Run(blend, 2f, true);

                AssertWeights(Evaluate(blend), Weights(index, 1f), Emotions[index] + " は none");
            }
        }

        [Test]
        public void Evaluate_ReturnsToFullWeight_AfterSpeakingEndsPlusHoldPlusFade()
        {
            foreach (var index in new[] { Happy, Sad })
            {
                var blend = ArukaBlend();
                blend.Set(Emotions[index]);
                blend.Advance(1f);
                Run(blend, 1f, true);

                var outputs = Run(blend, ExpressionBlend.SpeakingHoldSeconds + ExpressionBlend.FadeSeconds + 0.05f, false);

                AssertWeights(outputs.Last(), Weights(index, 1f), Emotions[index] + " は話し終えたら 1 に戻る");
            }
        }

        [Test]
        public void Evaluate_ReturnsToFullWeight_WhenSetSpeakingFalseIsCalledEveryFrame()
        {
            // false を呼ぶたびに数え直さない（最後に false になってからの時間）
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            var outputs = Run(blend, ExpressionBlend.SpeakingHoldSeconds + ExpressionBlend.FadeSeconds + 0.1f, false);

            Assert.That(outputs.Last()[Happy], Is.EqualTo(1f).Within(Tolerance), Show(outputs.Last()));
        }

        [Test]
        public void Evaluate_KeepsCap_DuringTheHoldAfterSpeakingEnds()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            var outputs = Run(blend, ExpressionBlend.SpeakingHoldSeconds - 0.05f, false);

            Assert.That(outputs.Max(o => o[Happy]), Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance), "切れ目の間も弱めたまま");
        }

        [TestCase(0.1f)]
        [TestCase(0.5f)]
        [TestCase(0.55f)]
        public void Evaluate_NeverExceedsSpeakingWeight_WhenPausesBetweenSentencesAreShorterThanHold(float pause)
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            blend.Advance(1f);
            var outputs = new List<float[]>();
            Run(blend, ExpressionBlend.FadeSeconds + Frame, true);

            for (var sentence = 0; sentence < 5; sentence++)
            {
                outputs.AddRange(Run(blend, 1.2f, true));
                outputs.AddRange(Run(blend, pause, false));
            }

            Assert.That(outputs.Max(o => o[Happy]), Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance), "pause " + pause);
        }

        [Test]
        public void Evaluate_RisesAboveSpeakingWeight_WhenPauseIsClearlyLongerThanHold()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            var outputs = Run(blend, ExpressionBlend.SpeakingHoldSeconds + 0.1f, false);

            Assert.That(outputs.Last()[Happy], Is.GreaterThan(ExpressionBlend.SpeakingWeight + 0.2f), Show(outputs.Last()));
        }

        [Test]
        public void Evaluate_KeepsCap_WhenSpeakingTogglesEveryFrame()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            blend.Advance(1f);
            var outputs = new List<float[]>();

            for (var frame = 0; frame < 240; frame++)
            {
                blend.SetSpeaking(frame % 2 == 0);
                blend.Advance(Frame);
                if (frame * Frame >= ExpressionBlend.FadeSeconds + Frame)
                {
                    outputs.Add(Evaluate(blend));
                }
            }

            Assert.That(outputs.Max(o => o[Happy]), Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance));
            Assert.That(outputs.Last()[Happy], Is.EqualTo(ExpressionBlend.SpeakingWeight).Within(Tolerance));
        }

        [Test]
        public void Evaluate_DoesNotCountHold_WithoutAdvance()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            blend.SetSpeaking(false);
            for (var i = 0; i < 100; i++)
            {
                blend.Advance(0f);
                blend.Advance(float.NaN);
                blend.Advance(-1f);
                blend.Advance(float.PositiveInfinity);
                blend.SetSpeaking(false);
            }

            blend.Advance(ExpressionBlend.SpeakingHoldSeconds - 0.1f);
            Assert.That(Evaluate(blend)[Happy], Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance), "保つ時間は Advance で進めた時間で数える");
        }

        [Test]
        public void Evaluate_DoesNotCap_WhenSpeakingWasNeverTrue()
        {
            var blend = ArukaBlend();
            blend.Set("happy");

            var outputs = Run(blend, ExpressionBlend.FadeSeconds + 0.05f, false);

            AssertWeights(outputs.Last(), Weights(Happy, 1f), "一度も話していない");
        }

        // ---- 表情を替える ----

        [Test]
        public void Evaluate_KeepsBlendSumWithinSpeakingWeight_WhenSwitchingBetweenBlendEmotionsWhileSpeaking()
        {
            var pairs = new[] { new[] { Happy, Surprised }, new[] { Surprised, Happy } };
            foreach (var pair in pairs)
            {
                var blend = ArukaBlend();
                blend.Set(Emotions[pair[0]]);
                Run(blend, 1f, true);

                blend.Set(Emotions[pair[1]]);
                var outputs = Run(blend, 0.5f, true, 1f / 240f);

                foreach (var output in outputs)
                {
                    Assert.That(output[Happy] + output[Surprised], Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance), Emotions[pair[0]] + " → " + Emotions[pair[1]] + " " + Show(output));
                }

                AssertWeights(outputs.Last(), Weights(pair[1], ExpressionBlend.SpeakingWeight), Emotions[pair[1]] + " に替わる");
            }
        }

        [Test]
        public void Evaluate_KeepsBlockAtZeroAndBlendWithinCap_WhenSwitchingFromBlendToBlockWhileSpeaking()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            blend.Set("sad");
            var outputs = Run(blend, 0.5f, true, 1f / 240f);

            foreach (var output in outputs)
            {
                Assert.That(output[Sad], Is.LessThanOrEqualTo(Tolerance), Show(output));
                Assert.That(output[Happy] + output[Surprised], Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance), Show(output));
            }

            AssertWeights(outputs.Last(), new float[5], "sad は話している間 0");
        }

        [Test]
        public void Evaluate_RaisesBlendOnlyToCap_WhenSwitchingFromNoneToBlendWhileSpeaking()
        {
            var blend = ArukaBlend();
            blend.Set("relaxed");
            Run(blend, 1f, true);

            blend.Set("happy");
            var outputs = Run(blend, 0.5f, true, 1f / 240f);

            Assert.That(outputs.Max(o => o[Happy]), Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + Tolerance));
            AssertWeights(outputs.Last(), Weights(Happy, ExpressionBlend.SpeakingWeight), "relaxed → happy");
        }

        // ---- neutral と IsActive ----

        [Test]
        public void Evaluate_ReturnsToUpstreamAndBecomesInactive_WhenSpeakingStartsDuringNeutralFade()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            blend.Advance(1f);
            blend.Set("neutral");
            blend.Advance(ExpressionBlend.FadeSeconds / 2f);

            Run(blend, ExpressionBlend.FadeSeconds, true);

            AssertWeights(Evaluate(blend), UpstreamSeen(Upstream), "neutral で本家に返る");
            Assert.That(blend.IsActive, Is.False);
        }

        [Test]
        public void Evaluate_ReturnsToUpstream_WhenNeutralIsSetWhileSpeakingWithCappedEmotion()
        {
            var upstream = new[] { 0.3f, 0f, 0f, 0.7f, 0.9f };
            var blend = ArukaBlend();
            blend.Set("surprised");
            Run(blend, 1f, true);

            blend.Set("neutral");
            Run(blend, ExpressionBlend.FadeSeconds + Frame, true);

            AssertWeights(Evaluate(blend, upstream), UpstreamSeen(upstream), "surprised も 0 に戻る");
            Assert.That(blend.IsActive, Is.False);
        }

        [Test]
        public void IsActive_IsUnaffectedBySpeaking()
        {
            var idle = ArukaBlend();
            Run(idle, 1f, true);
            Assert.That(idle.IsActive, Is.False, "話しているだけでは効いていない");
            AssertWeights(Evaluate(idle), UpstreamSeen(Upstream), "話しているだけなら本家の値");

            var holding = ArukaBlend();
            holding.Set("sad");
            Run(holding, 1f, true);
            Assert.That(holding.IsActive, Is.True, "block で 0 にしていても付けている");
        }

        [Test]
        public void Evaluate_FadesBackLinearlyFromLoweredWeight_WhenNeutralIsSetWhileSpeaking()
        {
            var cases = new[] { new { Index = Happy, Held = ExpressionBlend.SpeakingWeight }, new { Index = Relaxed, Held = 1f } };
            foreach (var c in cases)
            {
                var blend = ArukaBlend();
                blend.Set(Emotions[c.Index]);
                Run(blend, 1f, true);
                AssertWeights(Evaluate(blend, new float[5]), Weights(c.Index, c.Held), "neutral の前");

                blend.Set("neutral");
                for (var quarter = 1; quarter <= 3; quarter++)
                {
                    blend.SetSpeaking(true);
                    blend.Advance(ExpressionBlend.FadeSeconds / 4f);

                    AssertWeights(Evaluate(blend, new float[5]), Weights(c.Index, c.Held * (1f - quarter / 4f)), Emotions[c.Index] + " " + quarter + "/4");
                }
            }
        }

        // ---- 効きの量 0 からの Set（頭脳の側の重みはその時点の目標に置く） ----

        private static readonly float[] ZeroUpstream = new float[5];

        /// <summary>話している状態のまま 0.05 秒ずつ FadeSeconds まで進め、各時点で「効きの量 × 置いた重み」を確かめる。</summary>
        private static void AssertFadeInFromPlacedWeight(ExpressionBlend blend, int index, float placed, bool speaking, string because)
        {
            var elapsed = 0f;
            for (var step = 1; step <= 5; step++)
            {
                blend.SetSpeaking(speaking);
                blend.Advance(0.05f);
                elapsed += 0.05f;
                var amount = Math.Min(1f, elapsed / ExpressionBlend.FadeSeconds);

                AssertWeights(Evaluate(blend, ZeroUpstream), Weights(index, amount * placed), because + " t=" + elapsed);
            }
        }

        [Test]
        public void Set_PlacesBlendEmotionAtSpeakingWeight_WhenSetFromZeroAmountWhileSpeaking()
        {
            var blend = ArukaBlend();
            Run(blend, 1f, true);

            blend.Set("happy");

            AssertFadeInFromPlacedWeight(blend, Happy, ExpressionBlend.SpeakingWeight, true, "blend の happy");
        }

        [Test]
        public void Set_PlacesBlockEmotionAtZero_WhenSetFromZeroAmountWhileSpeaking()
        {
            var blend = ArukaBlend();
            Run(blend, 1f, true);

            blend.Set("sad");

            AssertFadeInFromPlacedWeight(blend, Sad, 0f, true, "block の sad");
        }

        [Test]
        public void Set_PlacesNoneEmotionAtFullWeight_WhenSetFromZeroAmountWhileSpeaking()
        {
            var blend = ArukaBlend();
            Run(blend, 1f, true);

            blend.Set("relaxed");

            AssertFadeInFromPlacedWeight(blend, Relaxed, 1f, true, "none の relaxed");
        }

        [Test]
        public void Set_PlacesEmotionAtFullWeight_WhenSetFromZeroAmountWithoutSpeaking()
        {
            foreach (var index in new[] { Happy, Sad })
            {
                var blend = ArukaBlend();

                blend.Set(Emotions[index]);

                AssertFadeInFromPlacedWeight(blend, index, 1f, false, Emotions[index] + " を話していないときに付ける");
            }
        }

        [Test]
        public void Set_PlacesEmotionAtFullWeight_WhenSetAfterSpeakingAndHoldHaveEnded()
        {
            var blend = ArukaBlend();
            Run(blend, 1f, true);
            Run(blend, ExpressionBlend.SpeakingHoldSeconds + 0.1f, false);

            blend.Set("happy");

            AssertFadeInFromPlacedWeight(blend, Happy, 1f, false, "保つ時間も過ぎてから付ける");
        }

        [Test]
        public void Set_PlacesBlendEmotionAtSpeakingWeight_WhenSetWhileSpeakingAfterNeutralFadeFinished()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);
            blend.Set("neutral");
            Run(blend, ExpressionBlend.FadeSeconds + 0.05f, true);
            Assert.That(blend.IsActive, Is.False, "neutral で戻し終えている");

            blend.Set("surprised");

            AssertFadeInFromPlacedWeight(blend, Surprised, ExpressionBlend.SpeakingWeight, true, "neutral のあとに話しながら付ける");
        }

        [Test]
        public void Set_PlacesBlendEmotionAtSpeakingWeight_WhenSetDuringTheHoldAfterSpeaking()
        {
            var blend = ArukaBlend();
            Run(blend, 1f, true);
            Run(blend, 0.2f, false);

            blend.Set("happy");

            // 保つ時間の中なので、まだ話している扱い
            var elapsed = 0f;
            for (var step = 1; step <= 5; step++)
            {
                blend.SetSpeaking(false);
                blend.Advance(0.05f);
                elapsed += 0.05f;
                AssertWeights(Evaluate(blend, ZeroUpstream), Weights(Happy, Math.Min(1f, elapsed / ExpressionBlend.FadeSeconds) * ExpressionBlend.SpeakingWeight), "t=" + elapsed);
            }
        }

        // ---- 設定 ----

        [Test]
        public void SetMouthOverrides_TreatsNullShortOrOutOfRangeAsNone()
        {
            var cases = new Dictionary<string, MouthOverride[]>
            {
                { "null", null },
                { "長さ 0", new MouthOverride[0] },
                { "長さ 4", new[] { MouthOverride.Blend, MouthOverride.Block, MouthOverride.Blend, MouthOverride.Block } },
                { "範囲外", new[] { (MouthOverride)99, (MouthOverride)(-1), (MouthOverride)3, (MouthOverride)int.MaxValue, (MouthOverride)int.MinValue } },
            };

            foreach (var pair in cases)
            {
                for (var index = 0; index < 5; index++)
                {
                    var blend = new ExpressionBlend();
                    blend.SetMouthOverrides(pair.Value);
                    blend.Set(Emotions[index]);
                    blend.Advance(1f);

                    Run(blend, 2f, true);

                    AssertWeights(Evaluate(blend), Weights(index, 1f), pair.Key + " " + Emotions[index]);
                }
            }
        }

        [Test]
        public void SetMouthOverrides_ReadsEachEntryOnItsOwn_WhenValuesAreMixedWithOutOfRange()
        {
            var overrides = new[] { MouthOverride.Blend, (MouthOverride)99, MouthOverride.Block, (MouthOverride)(-1), MouthOverride.Blend };
            var expected = new[] { ExpressionBlend.SpeakingWeight, 1f, 0f, 1f, ExpressionBlend.SpeakingWeight };

            for (var index = 0; index < 5; index++)
            {
                var blend = new ExpressionBlend();
                blend.SetMouthOverrides(overrides);
                blend.Set(Emotions[index]);
                Run(blend, 1f, true);

                AssertWeights(Evaluate(blend), Weights(index, expected[index]), Emotions[index]);
            }
        }

        [Test]
        public void SetMouthOverrides_UsesFirstFiveEntries_WhenArrayIsLonger()
        {
            var overrides = new[] { MouthOverride.Blend, MouthOverride.None, MouthOverride.Block, MouthOverride.None, MouthOverride.Blend, MouthOverride.Block, MouthOverride.Blend };
            var blend = new ExpressionBlend();
            blend.SetMouthOverrides(overrides);
            blend.Set("happy");

            Run(blend, 1f, true);

            AssertWeights(Evaluate(blend), Weights(Happy, ExpressionBlend.SpeakingWeight), "長さ 7");
        }

        [Test]
        public void SetMouthOverrides_FollowsNewSettingGradually_WhenCalledWhileSpeaking()
        {
            var blend = new ExpressionBlend();
            blend.Set("happy");
            Run(blend, 1f, true);
            var previous = Evaluate(blend);
            AssertWeights(previous, Weights(Happy, 1f), "設定する前は none");

            blend.SetMouthOverrides(Aruka);
            Assert.That(Evaluate(blend)[Happy], Is.EqualTo(1f).Within(Tolerance), "設定しただけでは跳ばない");
            foreach (var output in Run(blend, ExpressionBlend.FadeSeconds, true, 1f / 240f))
            {
                Assert.That(Math.Abs(output[Happy] - previous[Happy]), Is.LessThanOrEqualTo(2f / 240f / ExpressionBlend.FadeSeconds + Tolerance), Show(output));
                previous = output;
            }

            Assert.That(previous[Happy], Is.EqualTo(ExpressionBlend.SpeakingWeight).Within(Tolerance));

            blend.SetMouthOverrides(null);
            Run(blend, ExpressionBlend.FadeSeconds, true);
            AssertWeights(Evaluate(blend), Weights(Happy, 1f), "null に戻すと none");
        }

        [Test]
        public void SetMouthOverrides_CanBeReplaced_WhenAvatarChanges()
        {
            var blend = ArukaBlend();
            blend.Set("happy");
            Run(blend, 1f, true);

            blend.SetMouthOverrides(new[] { MouthOverride.Block, MouthOverride.None, MouthOverride.None, MouthOverride.None, MouthOverride.None });
            Run(blend, ExpressionBlend.FadeSeconds, true);

            AssertWeights(Evaluate(blend), new float[5], "新しいモデルでは happy が block");
        }

        // ---- 前回の振る舞いのまま（設定が無い・話していない） ----

        private static readonly MouthOverride[][] NoneEquivalents =
        {
            null,
            new MouthOverride[0],
            new[] { MouthOverride.Blend, MouthOverride.Block, MouthOverride.Blend, MouthOverride.Block },
            new[] { (MouthOverride)99, (MouthOverride)(-1), (MouthOverride)7, (MouthOverride)(-100), (MouthOverride)3 },
            new[] { MouthOverride.None, MouthOverride.None, MouthOverride.None, MouthOverride.None, MouthOverride.None },
        };

        [Test]
        public void Blend_MatchesBlendWithoutSpeakingSupport_WhenOverridesAreAbsentOrUnusable()
        {
            var names = new[] { "happy", "angry", "sad", "relaxed", "surprised", "neutral", "Happy", null };
            var deltas = new[] { 1f / 240f, 1f / 60f, 0.1f, 0f, -0.05f, float.NaN, float.PositiveInfinity, 0.7f, 10f };
            var variants = new List<MouthOverride[]>(NoneEquivalents) { Aruka };

            for (var variant = 0; variant < variants.Count; variant++)
            {
                for (var seed = 0; seed < 100; seed++)
                {
                    var random = new Random(seed);
                    var plain = new ExpressionBlend();
                    var tested = new ExpressionBlend();
                    var setOverrides = variant < NoneEquivalents.Length || seed % 2 == 0;
                    if (setOverrides)
                    {
                        tested.SetMouthOverrides(variants[variant]);
                    }

                    // 最後の変種（aruka の設定）は、一度も話さなければ前回のまま
                    var speakingAllowed = variant < NoneEquivalents.Length;
                    for (var step = 0; step < 200; step++)
                    {
                        var op = random.Next(10);
                        if (op < 2)
                        {
                            var name = names[random.Next(names.Length)];
                            plain.Set(name);
                            tested.Set(name);
                        }
                        else if (op < 5)
                        {
                            tested.SetSpeaking(speakingAllowed && random.Next(2) == 0);
                        }
                        else
                        {
                            var delta = deltas[random.Next(deltas.Length)];
                            plain.Advance(delta);
                            tested.Advance(delta);
                        }

                        var expected = Evaluate(plain);
                        var actual = Evaluate(tested);
                        var because = "variant " + variant + " seed " + seed + " step " + step;
                        for (var i = 0; i < 5; i++)
                        {
                            Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-5f), because + " " + Show(actual) + " expected " + Show(expected));
                        }

                        Assert.That(tested.IsActive, Is.EqualTo(plain.IsActive), because);
                    }
                }
            }
        }

        // ---- 性質（ランダムな操作列） ----

        [Test]
        public void Blend_KeepsRangeRateAndSpeakingCap_ForRandomOperationSequencesWithSpeakingAndOverrides()
        {
            var names = new[] { "happy", "angry", "sad", "relaxed", "surprised", "neutral", "happy", "surprised", "Happy", null };
            var deltas = new[] { 1f / 240f, 1f / 60f, 1f / 30f, 0.1f, 0f, -0.05f, float.NaN, float.PositiveInfinity, 0.7f, 10f };
            var settings = new[]
            {
                Aruka,
                new[] { MouthOverride.Blend, MouthOverride.Blend, MouthOverride.Blend, MouthOverride.Blend, MouthOverride.Blend },
                new[] { MouthOverride.Block, MouthOverride.Blend, MouthOverride.None, MouthOverride.Block, MouthOverride.Blend },
                null,
                new[] { (MouthOverride)99, MouthOverride.Blend, (MouthOverride)(-1), MouthOverride.Block, MouthOverride.None },
            };
            var upstreams = new[]
            {
                new[] { 0.3f, 0f, 0f, 0.7f, 0f },
                new[] { 1f, 1f, 1f, 1f, 1f },
                new[] { float.NaN, 2f, -1f, 0.5f, 0.9f },
            };
            // 境界のぶれを避けるための余裕
            const float Margin = 0.02f;

            for (var seed = 0; seed < 400; seed++)
            {
                var random = new Random(seed);
                var upstream = upstreams[seed % upstreams.Length];
                var blend = new ExpressionBlend();
                var log = new List<string>();
                var previous = Evaluate(blend, upstream);
                MouthOverride[] current = null;
                var speakingFlag = false;
                var sinceSpeakingFalse = float.PositiveInfinity;
                var speakingRun = 0f;
                var sinceEmotionSet = 0f;
                var emotionSet = false;
                var sinceOverrides = float.PositiveInfinity;

                for (var step = 0; step < 400; step++)
                {
                    var op = random.Next(20);
                    if (op < 3)
                    {
                        var name = names[random.Next(names.Length)];
                        log.Add("set(" + (name ?? "null") + ")");
                        blend.Set(name);
                        if (name == "neutral")
                        {
                            emotionSet = false;
                        }
                        else if (Emotions.Contains(name))
                        {
                            emotionSet = true;
                            sinceEmotionSet = 0f;
                        }

                        continue;
                    }

                    if (op < 4)
                    {
                        current = settings[random.Next(settings.Length)];
                        log.Add("overrides(" + (current == null ? "null" : string.Join("/", current.Select(o => ((int)o).ToString()))) + ")");
                        blend.SetMouthOverrides(current);
                        sinceOverrides = 0f;
                        continue;
                    }

                    if (op < 9)
                    {
                        var speaking = random.Next(3) != 0;
                        log.Add("speaking(" + speaking + ")");
                        blend.SetSpeaking(speaking);
                        if (speakingFlag && !speaking)
                        {
                            sinceSpeakingFalse = 0f;
                        }

                        speakingFlag = speaking;
                        continue;
                    }

                    var delta = deltas[random.Next(deltas.Length)];
                    log.Add("adv(" + delta + ")");
                    blend.Advance(delta);
                    var elapsed = delta > 0 && !float.IsInfinity(delta) ? delta : 0f;
                    if (!speakingFlag)
                    {
                        sinceSpeakingFalse += elapsed;
                    }

                    // 確かに話していると言える間（保つ時間の終わり際は数えない）だけ、話し続けた時間を数える
                    var surelySpeaking = speakingFlag || sinceSpeakingFalse < ExpressionBlend.SpeakingHoldSeconds - Margin - elapsed;
                    speakingRun = surelySpeaking ? speakingRun + elapsed : 0f;
                    sinceEmotionSet += elapsed;
                    sinceOverrides += elapsed;

                    var output = Evaluate(blend, upstream);
                    var because = "seed " + seed + " step " + step + " " + Show(previous) + " → " + Show(output) + " log: " + string.Join(" ", log.Skip(Math.Max(0, log.Count - 25)));
                    var limit = 2f * elapsed / ExpressionBlend.FadeSeconds + Tolerance;
                    for (var i = 0; i < 5; i++)
                    {
                        Assert.That(float.IsNaN(output[i]), Is.False, because);
                        Assert.That(output[i], Is.InRange(0f, 1f), because);
                        Assert.That(Math.Abs(output[i] - previous[i]), Is.LessThanOrEqualTo(limit), because + "（" + Emotions[i] + " の変化が大きすぎる）");
                    }

                    // 判定の不変条件: 話し続けて FadeSeconds 以上・効きの量が 1 なら、blend の合計は SpeakingWeight 以下、block は 0
                    var settled = speakingRun >= ExpressionBlend.FadeSeconds + Margin
                        && emotionSet && sinceEmotionSet >= ExpressionBlend.FadeSeconds + Margin
                        && sinceOverrides >= ExpressionBlend.FadeSeconds + Margin;
                    if (settled && current != null && current.Length >= 5)
                    {
                        var blendSum = 0f;
                        for (var i = 0; i < 5; i++)
                        {
                            if (current[i] == MouthOverride.Blend)
                            {
                                blendSum += output[i];
                            }
                            else if (current[i] == MouthOverride.Block)
                            {
                                Assert.That(output[i], Is.LessThanOrEqualTo(1e-3f), because + "（話している間の block が 0 でない: " + Emotions[i] + "）");
                            }
                        }

                        Assert.That(blendSum, Is.LessThanOrEqualTo(ExpressionBlend.SpeakingWeight + 1e-3f), because + "（話している間の blend の合計が大きすぎる）");
                    }

                    previous = output;
                }
            }
        }
    }
}
