using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class MotionFadeTests
    {
        private const float Clip = 7.27f;
        private const float ShortClip = 0.3f;
        private const float Tolerance = 1e-4f;
        private const float Frame = 1f / 240f;

        private static void AssertBounded(MotionFade fade, string because)
        {
            Assert.That(float.IsNaN(fade.Weight), Is.False, because + "（Weight が NaN）");
            Assert.That(fade.Weight, Is.InRange(0f, 1f), because);
            Assert.That(float.IsNaN(fade.Time), Is.False, because + "（Time が NaN）");
            if (!fade.IsActive)
            {
                Assert.That(fade.Weight, Is.EqualTo(0f), because + "（終わっているのに重みがある）");
            }
        }

        private static MotionFade PlayingAt(float seconds, float length = Clip, long token = 1)
        {
            var fade = new MotionFade();
            fade.Play(token, length);
            fade.Advance(seconds, true);
            return fade;
        }

        /// <summary>止まるまで小刻みに進め、各フレームの重みを返す。</summary>
        private static List<float> AdvanceUntilInactive(MotionFade fade, float delta, bool canPlay = true, int maxFrames = 100000)
        {
            var weights = new List<float>();
            for (var i = 0; i < maxFrames && fade.IsActive; i++)
            {
                fade.Advance(delta, canPlay);
                weights.Add(fade.Weight);
            }

            Assert.That(fade.IsActive, Is.False, "終わらない");
            return weights;
        }

        // ---- 定数 ----

        [Test]
        public void Constants_MatchTheFadesChosenInTheSpike()
        {
            Assert.That(MotionFade.FadeIn, Is.EqualTo(0.25f));
            Assert.That(MotionFade.FadeOut, Is.EqualTo(0.3f));
            Assert.That(MotionFade.StopFade, Is.EqualTo(0.15f));
        }

        // ---- 始める ----

        [Test]
        public void New_IsInactiveWithZeroWeight()
        {
            var fade = new MotionFade();

            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Play_StartsTokenAtTimeZeroAndWeightZero_WhenNothingIsPlaying()
        {
            var fade = new MotionFade();

            fade.Play(42, Clip);

            Assert.That(fade.IsActive, Is.True);
            Assert.That(fade.Token, Is.EqualTo(42L));
            Assert.That(fade.Time, Is.EqualTo(0f));
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Play_DoesNothing_WhenLengthIsNotPositiveFinite(float length)
        {
            var fade = new MotionFade();

            fade.Play(1, length);
            fade.Advance(0.1f, true);

            Assert.That(fade.IsActive, Is.False, "length " + length);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void Play_KeepsCurrentMotion_WhenNewLengthIsNotPositiveFinite(float length)
        {
            var fade = PlayingAt(1f);

            fade.Play(2, length);
            fade.Advance(0.5f, true);

            Assert.That(fade.Token, Is.EqualTo(1L), "length " + length);
            Assert.That(fade.Weight, Is.EqualTo(1f).Within(Tolerance), "止め始めていない");
            Assert.That(fade.Time, Is.EqualTo(1.5f).Within(Tolerance));
        }

        // ---- 普通の再生 ----

        [Test]
        public void Advance_RaisesWeightLinearlyOverFadeIn()
        {
            var fade = PlayingAt(0.1f);

            Assert.That(fade.Time, Is.EqualTo(0.1f).Within(Tolerance));
            Assert.That(fade.Weight, Is.EqualTo(0.4f).Within(Tolerance));

            fade.Advance(0.1f, true);
            Assert.That(fade.Weight, Is.EqualTo(0.8f).Within(Tolerance));

            fade.Advance(0.05f, true);
            Assert.That(fade.Weight, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void Advance_HoldsFullWeightBetweenFades()
        {
            var fade = PlayingAt(0.3f);

            for (var t = 0; t < 60; t++)
            {
                fade.Advance(0.1f, true);
                Assert.That(fade.Weight, Is.EqualTo(1f).Within(Tolerance), "time " + fade.Time);
            }
        }

        [Test]
        public void Advance_LowersWeightLinearlyOverFadeOutAndEndsAtLength()
        {
            var fade = PlayingAt(Clip - 0.15f);

            Assert.That(fade.Weight, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(fade.IsActive, Is.True);

            fade.Advance(0.09f, true);
            Assert.That(fade.Weight, Is.EqualTo(0.2f).Within(1e-3f));

            fade.Advance(0.1f, true);
            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Advance_EndsMotion_WhenDeltaIsLongerThanClip()
        {
            var fade = new MotionFade();
            fade.Play(1, Clip);

            fade.Advance(10f, true);

            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Advance_ReachesMatchingPeakAndEnds_WhenClipIsShorterThanBothFades()
        {
            var fade = new MotionFade();
            fade.Play(1, ShortClip);

            var weights = AdvanceUntilInactive(fade, Frame);

            Assert.That(weights, Is.Not.Empty, "再生されない");
            // min(t / 0.25, (0.3 - t) / 0.3) の頂点は t = 0.075 / 0.55 で 0.5454...
            Assert.That(weights.Max(), Is.EqualTo(0.3f / 0.55f).Within(0.02f));
            Assert.That(weights.Count, Is.InRange(70, 74), "0.3 秒を 1/240 秒ずつ");
        }

        [TestCase(0f)]
        [TestCase(-0.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Advance_TreatsDeltaAsZero_WhenDeltaIsNegativeOrNotFinite(float delta)
        {
            var fade = PlayingAt(0.1f);

            fade.Advance(delta, true);

            Assert.That(fade.IsActive, Is.True, "delta " + delta);
            Assert.That(fade.Time, Is.EqualTo(0.1f).Within(Tolerance));
            Assert.That(fade.Weight, Is.EqualTo(0.4f).Within(Tolerance));
        }

        [Test]
        public void Advance_DoesNothing_WhenNothingIsPlaying()
        {
            var fade = new MotionFade();

            fade.Advance(0.5f, true);
            fade.Advance(0.5f, false);

            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Play_StartsAgainFromZero_AfterPreviousMotionEnded()
        {
            var fade = PlayingAt(10f, ShortClip);
            Assert.That(fade.IsActive, Is.False);

            fade.Play(2, Clip);

            Assert.That(fade.IsActive, Is.True);
            Assert.That(fade.Token, Is.EqualTo(2L));
            Assert.That(fade.Time, Is.EqualTo(0f));
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        // ---- 止める ----

        [Test]
        public void Stop_LowersWeightFromCurrentValueOverStopFadeAndEnds()
        {
            var fade = PlayingAt(1f);

            fade.Stop();
            Assert.That(fade.Weight, Is.EqualTo(1f).Within(Tolerance), "Stop だけでは重みは跳ばない");
            Assert.That(fade.IsActive, Is.True);

            fade.Advance(0.075f, true);
            Assert.That(fade.Weight, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(fade.Time, Is.EqualTo(1.075f).Within(1e-3f), "止めている間も時刻は進む");

            fade.Advance(0.0751f, true);
            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Stop_StartsFromPartialWeight_WhenStoppedDuringFadeIn()
        {
            var fade = PlayingAt(0.1f);

            fade.Stop();
            fade.Advance(0.03f, true);

            Assert.That(fade.Weight, Is.EqualTo(0.4f - 0.2f).Within(1e-3f));
            fade.Advance(0.031f, true);
            Assert.That(fade.IsActive, Is.False);
        }

        [Test]
        public void Stop_KeepsLoweringWeight_WhenCanPlayReturns()
        {
            var fade = PlayingAt(1f);

            fade.Advance(0.05f, false);
            var stopping = fade.Weight;
            fade.Advance(0.05f, true);

            Assert.That(stopping, Is.EqualTo(1f - 1f / 3f).Within(1e-3f));
            Assert.That(fade.Weight, Is.EqualTo(1f - 2f / 3f).Within(1e-3f), "止め始めたら戻らない");
        }

        [Test]
        public void Advance_StopsLikeStop_WhenCanPlayIsFalse()
        {
            var fade = PlayingAt(1f);

            var weights = AdvanceUntilInactive(fade, Frame, canPlay: false);

            Assert.That(weights.Count, Is.InRange(35, 37), "0.15 秒を 1/240 秒ずつ");
            for (var i = 1; i < weights.Count; i++)
            {
                Assert.That(weights[i], Is.LessThanOrEqualTo(weights[i - 1]), "frame " + i);
            }
        }

        [Test]
        public void Advance_NeverShowsMotion_WhenPlayIsRequestedOutsideIdle()
        {
            var fade = new MotionFade();
            fade.Play(1, Clip);

            fade.Advance(Frame, false);

            Assert.That(fade.Weight, Is.EqualTo(0f));
            Assert.That(fade.IsActive, Is.False);
        }

        [Test]
        public void Stop_DoesNothing_WhenNothingIsPlaying()
        {
            var fade = new MotionFade();

            fade.Stop();
            fade.Advance(0.1f, true);

            Assert.That(fade.IsActive, Is.False);
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        // ---- 再生中に次を頼む ----

        [Test]
        public void Play_StopsCurrentThenStartsNewFromZero_WhenMotionIsPlaying()
        {
            var fade = PlayingAt(1f);

            fade.Play(2, ShortClip);
            Assert.That(fade.Token, Is.EqualTo(1L), "今のものを下げてから替える");
            Assert.That(fade.Weight, Is.EqualTo(1f).Within(Tolerance), "Play だけでは重みは跳ばない");

            var switched = false;
            for (var frame = 0; frame < 100 && !switched; frame++)
            {
                var before = fade.Weight;
                fade.Advance(Frame, true);
                if (fade.Token == 2)
                {
                    switched = true;
                    Assert.That(fade.Weight, Is.EqualTo(0f), "新しい方は重み 0 から");
                    Assert.That(fade.Time, Is.EqualTo(0f), "新しい方は時刻 0 から");
                    Assert.That(fade.IsActive, Is.True);
                }
                else
                {
                    Assert.That(fade.Weight, Is.LessThanOrEqualTo(before), "frame " + frame);
                }
            }

            Assert.That(switched, Is.True, "替わらない");
            fade.Advance(0.1f, true);
            Assert.That(fade.Weight, Is.EqualTo(0.4f).Within(Tolerance));
        }

        [Test]
        public void Play_SwitchesAfterAboutStopFade_WhenCurrentWeightIsFull()
        {
            var fade = PlayingAt(1f);

            fade.Play(2, Clip);
            var frames = 0;
            while (fade.Token != 2 && frames < 1000)
            {
                fade.Advance(Frame, true);
                frames++;
            }

            Assert.That(frames, Is.InRange(35, 37), "0.15 秒を 1/240 秒ずつ");
        }

        [Test]
        public void Play_KeepsOnlyTheNewestRequest_WhenSeveralArriveWhileStopping()
        {
            var fade = PlayingAt(1f);
            var seen = new HashSet<long>();

            fade.Play(2, Clip);
            fade.Advance(0.05f, true);
            seen.Add(fade.Token);
            fade.Play(3, Clip);
            fade.Advance(0.05f, true);
            seen.Add(fade.Token);
            fade.Play(4, Clip);
            for (var frame = 0; frame < 200; frame++)
            {
                fade.Advance(Frame, true);
                seen.Add(fade.Token);
            }

            Assert.That(fade.Token, Is.EqualTo(4L));
            Assert.That(seen, Is.EquivalentTo(new long[] { 1, 4 }), "途中で頼んだ 2 と 3 は始まらない");
            Assert.That(fade.Weight, Is.GreaterThan(0f));
        }

        [Test]
        public void Play_StartsSameTokenAgainFromZero_WhenSameTokenIsRequestedWhilePlaying()
        {
            var fade = PlayingAt(2f, token: 9);

            fade.Play(9, Clip);
            var restarted = false;
            for (var frame = 0; frame < 100 && !restarted; frame++)
            {
                fade.Advance(Frame, true);
                restarted = fade.Time < 1f;
            }

            Assert.That(restarted, Is.True);
            Assert.That(fade.Token, Is.EqualTo(9L));
            Assert.That(fade.Time, Is.EqualTo(0f));
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Play_StartsNewImmediately_WhenRequestedTwiceBeforeAnyAdvance()
        {
            var fade = new MotionFade();
            fade.Play(1, Clip);
            fade.Play(2, Clip);

            fade.Advance(Frame, true);

            Assert.That(fade.Token, Is.EqualTo(2L), "重み 0 のものはすぐ替わる");
            Assert.That(fade.Weight, Is.EqualTo(0f));
            fade.Advance(0.1f, true);
            Assert.That(fade.Weight, Is.EqualTo(0.4f).Within(Tolerance));
        }

        [Test]
        public void Play_StartsWaitingRequest_WhenCurrentClipReachesItsEndWhileStopping()
        {
            // 0.3 秒のクリップの終わり際に次を頼み、1 フレームで長さを越える
            var fade = PlayingAt(0.25f, ShortClip);
            Assert.That(fade.Weight, Is.EqualTo(0.05f / 0.3f).Within(1e-3f));

            fade.Play(2, Clip);
            fade.Advance(0.1f, true);

            Assert.That(fade.IsActive, Is.True, "待っているものが捨てられた");
            Assert.That(fade.Token, Is.EqualTo(2L));
            Assert.That(fade.Time, Is.EqualTo(0f));
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Play_StartsWaitingRequest_WhenOneAdvanceCoversBothTheStopAndTheRestOfTheClip()
        {
            var fade = PlayingAt(1f);

            fade.Play(2, ShortClip);
            fade.Advance(10f, true);

            Assert.That(fade.IsActive, Is.True, "待っているものが捨てられた");
            Assert.That(fade.Token, Is.EqualTo(2L));
            Assert.That(fade.Weight, Is.EqualTo(0f));
        }

        [Test]
        public void Stop_DropsWaitingRequest_WhenCalledWhileSwitching()
        {
            var fade = PlayingAt(1f);
            fade.Play(2, Clip);
            fade.Advance(0.05f, true);

            fade.Stop();
            AdvanceUntilInactive(fade, Frame);

            Assert.That(fade.Token, Is.Not.EqualTo(2L));
            fade.Advance(1f, true);
            Assert.That(fade.IsActive, Is.False, "捨てた 2 が始まらない");
        }

        [Test]
        public void Advance_DropsWaitingRequest_WhenCanPlayBecomesFalseWhileSwitching()
        {
            var fade = PlayingAt(1f);
            fade.Play(2, Clip);
            fade.Advance(0.05f, true);

            fade.Advance(Frame, false);
            AdvanceUntilInactive(fade, Frame);

            fade.Advance(1f, true);
            Assert.That(fade.IsActive, Is.False, "待機でなくなったら待っているものも捨てる");
        }

        [Test]
        public void Play_StopsThenStarts_WhenRequestedWhileAlreadyStopping()
        {
            var fade = PlayingAt(1f);
            fade.Stop();
            fade.Advance(0.05f, true);
            var weight = fade.Weight;

            fade.Play(2, Clip);
            Assert.That(fade.Weight, Is.EqualTo(weight), "重みは跳ばない");
            for (var frame = 0; frame < 100 && fade.Token != 2; frame++)
            {
                fade.Advance(Frame, true);
            }

            Assert.That(fade.Token, Is.EqualTo(2L));
            Assert.That(fade.IsActive, Is.True);
        }

        // ---- 性質（ランダムな操作列） ----

        [Test]
        public void Fade_KeepsWeightInRangeAndLimitsChangePerAdvance_ForRandomOperationSequences()
        {
            var lengths = new[] { 7.27f, 0.3f, 0.05f, 0.55f, 1f, 0f, -1f, float.NaN, float.PositiveInfinity };
            var deltas = new[] { 1f / 240f, 1f / 60f, 1f / 30f, 0.1f, 0f, -0.05f, float.NaN, float.PositiveInfinity, 10f, 0.0001f };

            for (var seed = 0; seed < 300; seed++)
            {
                var random = new Random(seed);
                var fade = new MotionFade();
                var lengthsByToken = new Dictionary<long, float>();
                var log = new List<string>();
                long? newestRequest = null;
                var stopping = false;
                var nextToken = 1L;

                for (var step = 0; step < 400; step++)
                {
                    var because = "seed " + seed + " step " + step + " log: " + string.Join(" ", log.Skip(Math.Max(0, log.Count - 30)));
                    var op = random.Next(100);
                    var weightBefore = fade.Weight;
                    var tokenBefore = fade.Token;
                    var activeBefore = fade.IsActive;
                    if (op < 8)
                    {
                        var token = nextToken++;
                        var length = lengths[random.Next(lengths.Length)];
                        log.Add("play(" + token + "," + length + ")");
                        fade.Play(token, length);
                        if (length > 0 && !float.IsInfinity(length) && !float.IsNaN(length))
                        {
                            lengthsByToken[token] = length;
                            if (activeBefore)
                            {
                                newestRequest = token;
                                stopping = true;
                            }
                        }

                        Assert.That(fade.Weight, Is.EqualTo(weightBefore), because + "（Play で重みが跳んだ）");
                        if (activeBefore)
                        {
                            Assert.That(fade.Token, Is.EqualTo(tokenBefore), because + "（再生中の Play ですぐ替わった）");
                        }
                    }
                    else if (op < 11)
                    {
                        log.Add("stop");
                        fade.Stop();
                        newestRequest = null;
                        if (activeBefore)
                        {
                            stopping = true;
                        }

                        Assert.That(fade.Weight, Is.EqualTo(weightBefore), because + "（Stop で重みが跳んだ）");
                    }
                    else
                    {
                        var delta = deltas[random.Next(deltas.Length)];
                        var canPlay = random.Next(10) != 0;
                        log.Add("adv(" + delta + "," + canPlay + ")");
                        var requestBefore = newestRequest;
                        fade.Advance(delta, canPlay);
                        if (canPlay && requestBefore.HasValue)
                        {
                            Assert.That(fade.IsActive, Is.True, because + "（待っているものが始まらずに終わった）");
                        }

                        if (!canPlay)
                        {
                            newestRequest = null;
                            if (activeBefore)
                            {
                                stopping = true;
                            }
                        }

                        var effective = delta > 0 && !float.IsInfinity(delta) ? delta : 0f;
                        var change = Math.Abs(fade.Weight - weightBefore);
                        Assert.That(change, Is.LessThanOrEqualTo(effective / MotionFade.StopFade + 1e-4f), because + "（1 回の Advance での変化が大きすぎる: " + weightBefore + " → " + fade.Weight + "）");

                        if (fade.IsActive && (!activeBefore || fade.Token != tokenBefore))
                        {
                            Assert.That(activeBefore, Is.True, because + "（Advance だけで何かが始まった）");
                            Assert.That(newestRequest.HasValue, Is.True, because + "（頼んでいないものに替わった）");
                            Assert.That(fade.Token, Is.EqualTo(newestRequest.Value), because + "（最後に頼んだものではない）");
                            Assert.That(fade.Weight, Is.EqualTo(0f), because + "（替わった直後の重みが 0 でない）");
                            newestRequest = null;
                            stopping = false;
                        }
                        else if (fade.IsActive && stopping)
                        {
                            Assert.That(fade.Weight, Is.LessThanOrEqualTo(weightBefore), because + "（止めている間に重みが上がった）");
                        }

                        if (!fade.IsActive)
                        {
                            newestRequest = null;
                            stopping = false;
                        }
                    }

                    if (fade.IsActive && !activeBefore && op < 8)
                    {
                        Assert.That(fade.Time, Is.EqualTo(0f), because);
                        Assert.That(fade.Weight, Is.EqualTo(0f), because);
                        stopping = false;
                    }

                    AssertBounded(fade, because);
                    if (fade.IsActive)
                    {
                        Assert.That(lengthsByToken.ContainsKey(fade.Token), Is.True, because + "（知らない token）");
                        Assert.That(fade.Time, Is.InRange(0f, lengthsByToken[fade.Token]), because + "（Time が長さの外）");
                    }
                }
            }
        }

        [TestCase(1f / 240f)]
        [TestCase(1f / 60f)]
        [TestCase(0.1f)]
        public void Fade_LimitsChangePerAdvance_AcrossEveryTransition(float delta)
        {
            // 入り → 保つ → 替える（止める → 新しい方の入り）→ 終わり → 待機でなくなる、を通す
            var fade = new MotionFade();
            var weights = new List<float> { fade.Weight };
            Action<int> run = frames =>
            {
                for (var i = 0; i < frames; i++)
                {
                    fade.Advance(delta, true);
                    weights.Add(fade.Weight);
                }
            };

            fade.Play(1, Clip);
            run((int)(1f / delta));
            fade.Play(2, ShortClip);
            run((int)(1f / delta));
            fade.Play(3, Clip);
            run((int)(0.5f / delta));
            fade.Advance(delta, false);
            weights.Add(fade.Weight);
            run((int)(0.5f / delta));

            for (var i = 1; i < weights.Count; i++)
            {
                Assert.That(Math.Abs(weights[i] - weights[i - 1]), Is.LessThanOrEqualTo(delta / MotionFade.StopFade + 1e-4f), "frame " + i + ": " + weights[i - 1] + " → " + weights[i]);
            }

            Assert.That(fade.IsActive, Is.False);
        }
    }
}
