using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 自作の待機のクリップの順・混ぜ・話している間の移り方・待機の出入り・一覧の差し替えを、毎フレームの層（最大 2 つ）で返すこと。
    /// 層は Runtime が層 0、層 1 の順に「姿勢 = lerp(姿勢, クリップの姿勢, Weight)」で写すので、
    /// テストでは Weight から最終の姿勢に占める割合（寄与）を計算して確かめる。
    /// </summary>
    public class IdleSequenceTests
    {
        private const float Frame = 1f / 60f;
        private const float FineFrame = 1f / 240f;
        private const float Tolerance = 1e-3f;

        // フレームの境目で 1〜2 フレームずれてよい量
        private const float Slack = 2.5f * Frame;

        // 寄与の 1 回の Advance での変化の上限（頭打ち後の delta に掛ける）
        private const float RateBound = 1f / IdleSequence.LeaveIdleSeconds + 1f / IdleSequence.CrossFadeSeconds + 1f / IdleSequence.TalkFadeSeconds;
        private const float ContinuityTolerance = 1e-4f;

        private const long A = 1;
        private const long B = 2;
        private const long C = 3;
        private const long T = 10;
        private const long U = 11;
        private const long X = 21;
        private const long Y = 22;
        private const long Z = 23;

        // 混ぜの開始点は A 3 秒・B 4 秒・C 5 秒。A→B は 3 秒、B→C は 7 秒、C→A（最初に戻る）は 12 秒で始まる
        private static IdleClip[] Normal3()
        {
            return new[] { new IdleClip(A, 4f), new IdleClip(B, 5f), new IdleClip(C, 6f) };
        }

        // 話す間用は 0 番目の T だけを使う（名前がどちらも null で同じなので、一覧で先の T）。T の混ぜの開始点は 5 秒
        private static IdleClip[] Talk2()
        {
            return new[] { new IdleClip(T, 6f), new IdleClip(U, 2f) };
        }

        private static IdleClip[] Clips(params IdleClip[] clips)
        {
            return clips;
        }

        private static IdleClip[] None()
        {
            return new IdleClip[0];
        }

        /// <summary>クリップの始まりはそれぞれ 1 フレームまで遅れてよいので、それより前の混ぜの数だけずれを許す。</summary>
        private static float Drift(int earlierCrossFades)
        {
            return Frame * (earlierCrossFades + 2);
        }

        private static float Clamped(float delta)
        {
            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                return 0f;
            }

            return Math.Min(delta, IdleSequence.MaxDeltaSeconds);
        }

        /// <summary>1 フレームの層と、本家・層ごとの寄与。</summary>
        private sealed class Snapshot
        {
            public IdleLayer[] Layers;
            public float[] Contributions;
            public float Base;

            public static Snapshot Of(IdleSequence sequence)
            {
                var count = sequence.LayerCount;
                var snapshot = new Snapshot { Layers = new IdleLayer[count], Contributions = new float[count], Base = 1f };
                for (var i = 0; i < count; i++)
                {
                    var layer = sequence.GetLayer(i);
                    snapshot.Layers[i] = layer;
                    var weight = layer.Weight;
                    snapshot.Base *= 1f - weight;
                    for (var j = 0; j < i; j++)
                    {
                        snapshot.Contributions[j] *= 1f - weight;
                    }

                    snapshot.Contributions[i] = weight;
                }

                return snapshot;
            }

            public int Count
            {
                get { return Layers.Length; }
            }

            public long[] Tokens
            {
                get { return Layers.Select(l => l.Token).ToArray(); }
            }

            /// <summary>自作の待機の寄与の合計（= o）。</summary>
            public float Own
            {
                get { return 1f - Base; }
            }

            public bool Has(long token)
            {
                return Layers.Any(l => l.Token == token);
            }

            /// <summary>同じ Token の層の寄与の和。</summary>
            public float Of(long token)
            {
                var sum = 0f;
                for (var i = 0; i < Layers.Length; i++)
                {
                    if (Layers[i].Token == token)
                    {
                        sum += Contributions[i];
                    }
                }

                return sum;
            }

            public IdleLayer Single(long token)
            {
                var layers = Layers.Where(l => l.Token == token).ToList();
                Assert.That(layers.Count, Is.EqualTo(1), "Token " + token + " の層が 1 つでない: " + ToString());
                return layers[0];
            }

            public override string ToString()
            {
                var parts = new List<string>();
                for (var i = 0; i < Layers.Length; i++)
                {
                    parts.Add("{token " + Layers[i].Token + " time " + Layers[i].Time.ToString("0.####") + " weight " + Layers[i].Weight.ToString("0.####") + " share " + Contributions[i].ToString("0.####") + "}");
                }

                return "[" + string.Join(", ", parts) + "] base " + Base.ToString("0.####");
            }
        }

        /// <summary>
        /// 毎フレーム SetTalking を渡してから Advance し、層の範囲と寄与の連続性を毎フレーム確かめる。
        /// </summary>
        private sealed class Driver
        {
            public readonly IdleSequence Sequence = new IdleSequence();
            public readonly Dictionary<long, float> Lengths = new Dictionary<long, float>();
            public double Elapsed;
            public bool Talking;
            public bool Idle = true;

            /// <summary>Step のたびに、その層と頭打ち後の delta で呼ぶ（null なら呼ばない）。</summary>
            public Action<Snapshot, float> AfterStep;

            private Snapshot _previous;

            public Driver()
            {
                _previous = Snapshot.Of(Sequence);
            }

            public Driver(IdleClip[] normal, IdleClip[] talk) : this()
            {
                SetClips(normal, talk);
            }

            public Snapshot Now
            {
                get { return Snapshot.Of(Sequence); }
            }

            public void SetClips(IdleClip[] normal, IdleClip[] talk)
            {
                foreach (var clip in (normal ?? None()).Concat(talk ?? None()))
                {
                    if (clip.Length > 0f && !float.IsInfinity(clip.Length))
                    {
                        Lengths[clip.Token] = clip.Length;
                    }
                }

                Sequence.SetClips(normal, talk);
            }

            public void EndTalking()
            {
                Talking = false;
                Sequence.EndTalking();
            }

            public Snapshot Step(float delta)
            {
                Sequence.SetTalking(Talking);
                Sequence.Advance(delta, Idle);
                Elapsed += Clamped(delta);
                var now = Snapshot.Of(Sequence);
                var because = "t=" + Elapsed.ToString("0.####") + " " + now;
                AssertBounded(now, Lengths, because);
                AssertContinuous(_previous, now, Clamped(delta), because);
                _previous = now;
                if (AfterStep != null)
                {
                    AfterStep(now, Clamped(delta));
                }

                return now;
            }

            public List<Snapshot> Run(double seconds, float frame = Frame)
            {
                var frames = (int)Math.Round(seconds / frame);
                var snapshots = new List<Snapshot>();
                for (var i = 0; i < frames; i++)
                {
                    snapshots.Add(Step(frame));
                }

                return snapshots;
            }

            public Snapshot RunTo(double time, float frame = Frame)
            {
                var now = Now;
                while (Elapsed < time - 1e-6)
                {
                    now = Step(frame);
                }

                return now;
            }

            /// <summary>RunTo と同じだが、通ったフレームをすべて返す。</summary>
            public List<Snapshot> CollectTo(double time, float frame = Frame)
            {
                var snapshots = new List<Snapshot>();
                while (Elapsed < time - 1e-6)
                {
                    snapshots.Add(Step(frame));
                }

                return snapshots;
            }
        }

        private static void AssertBounded(Snapshot snapshot, Dictionary<long, float> lengths, string because)
        {
            Assert.That(snapshot.Count, Is.InRange(0, 2), because + "（LayerCount）");
            for (var i = 0; i < snapshot.Count; i++)
            {
                var layer = snapshot.Layers[i];
                Assert.That(float.IsNaN(layer.Weight) || float.IsInfinity(layer.Weight), Is.False, because + "（Weight が有限でない）");
                Assert.That(layer.Weight, Is.InRange(0f, 1f), because + "（Weight の範囲）");
                Assert.That(float.IsNaN(layer.Time) || float.IsInfinity(layer.Time), Is.False, because + "（Time が有限でない）");
                float length;
                Assert.That(lengths.TryGetValue(layer.Token, out length), Is.True, because + "（与えていない Token " + layer.Token + "）");
                Assert.That(layer.Time, Is.InRange(0f, length), because + "（Time がクリップの長さの外）");
            }

            if (snapshot.Count > 0)
            {
                Assert.That(snapshot.Contributions.Sum(), Is.GreaterThan(0f), because + "（全体の重みが 0 なのに層がある）");
            }
        }

        private static void AssertContinuous(Snapshot previous, Snapshot now, float clampedDelta, string because)
        {
            var limit = clampedDelta * RateBound + ContinuityTolerance;
            Assert.That(Math.Abs(now.Base - previous.Base), Is.LessThanOrEqualTo(limit), because + "（本家の寄与が跳んだ。前 " + previous + "）");
            foreach (var token in previous.Tokens.Concat(now.Tokens).Distinct())
            {
                Assert.That(Math.Abs(now.Of(token) - previous.Of(token)), Is.LessThanOrEqualTo(limit), because + "（Token " + token + " の寄与が跳んだ。前 " + previous + "）");
            }
        }

        private static void AssertTokens(Snapshot snapshot, long[] expected, string because)
        {
            Assert.That(snapshot.Tokens, Is.EqualTo(expected), because + " " + snapshot);
        }

        /// <summary>各フレームで最後の層（混ぜの間は入ってくる層）の Token を、続く同じものをまとめて並べる。</summary>
        private static List<long> IncomingOrder(IEnumerable<Snapshot> snapshots)
        {
            var order = new List<long>();
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Count == 0)
                {
                    continue;
                }

                var token = snapshot.Layers[snapshot.Count - 1].Token;
                if (order.Count == 0 || order[order.Count - 1] != token)
                {
                    order.Add(token);
                }
            }

            return order;
        }

        /// <summary>待機の度合いと自作の待機の度合いがどちらも 1 になるまで進めた状態（A の Time は 1.2）。</summary>
        private static Driver Started()
        {
            return Started(Normal3(), Talk2());
        }

        private static Driver Started(IdleClip[] normal, IdleClip[] talk)
        {
            var driver = new Driver(normal, talk);
            driver.RunTo(1.2);
            var now = driver.Now;
            Assert.That(now.Own, Is.EqualTo(1f).Within(Tolerance), "前提: 自作の待機が全体に出ている " + now);
            return driver;
        }

        /// <summary>話し始めて T だけになった状態（T の Time は 1）。</summary>
        private static Driver TalkingOnT()
        {
            var driver = Started();
            driver.Talking = true;
            driver.RunTo(2.2);
            var now = driver.Now;
            AssertTokens(now, new[] { T }, "前提: T だけ");
            return driver;
        }

        // ---- 定数 ----

        [Test]
        public void Constants_MatchThePlan()
        {
            Assert.That(IdleSequence.CrossFadeSeconds, Is.EqualTo(1f));
            Assert.That(IdleSequence.TalkFadeSeconds, Is.EqualTo(0.5f));
            Assert.That(IdleSequence.TalkHoldSeconds, Is.EqualTo(1.5f));
            Assert.That(IdleSequence.LeaveIdleSeconds, Is.EqualTo(0.15f));
            Assert.That(IdleSequence.EnterIdleSeconds, Is.EqualTo(0.25f));
            Assert.That(IdleSequence.MaxDeltaSeconds, Is.EqualTo(1f / 15f));
        }

        [Test]
        public void IdleClip_KeepsTokenAndLength()
        {
            var clip = new IdleClip(long.MaxValue, 2.5f);

            Assert.That(clip.Token, Is.EqualTo(long.MaxValue));
            Assert.That(clip.Length, Is.EqualTo(2.5f));
        }

        // ---- 初めの状態 ----

        [Test]
        public void New_HasNoLayersAndNoReloadRequest()
        {
            var sequence = new IdleSequence();

            Assert.That(sequence.LayerCount, Is.EqualTo(0));
            Assert.That(sequence.TakeReloadRequest(), Is.False);
        }

        [Test]
        public void GetLayer_ThrowsArgumentOutOfRange_WhenIndexIsOutsideLayerCount()
        {
            var sequence = new IdleSequence();
            Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetLayer(0), "層がない");
            Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetLayer(-1), "層がない");

            var one = Started();
            Assert.That(one.Sequence.LayerCount, Is.EqualTo(1), "前提");
            Assert.DoesNotThrow(() => one.Sequence.GetLayer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => one.Sequence.GetLayer(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => one.Sequence.GetLayer(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => one.Sequence.GetLayer(int.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => one.Sequence.GetLayer(int.MinValue));

            one.RunTo(3.5);
            Assert.That(one.Sequence.LayerCount, Is.EqualTo(2), "前提: 混ぜの間");
            Assert.DoesNotThrow(() => one.Sequence.GetLayer(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => one.Sequence.GetLayer(2));
        }

        [Test]
        public void Advance_ShowsNothing_WhenNoClipsWereGiven()
        {
            var driver = new Driver();
            for (var i = 0; i < 600; i++)
            {
                driver.Talking = i % 90 < 45;
                driver.Idle = i % 200 < 150;
                Assert.That(driver.Step(Frame).Count, Is.EqualTo(0), "frame " + i);
            }
        }

        [Test]
        public void Advance_ShowsNothing_WhenNormalListIsNullOrEmptyEvenWithTalkClips()
        {
            foreach (var normal in new[] { null, None(), Clips(new IdleClip(X, 0f), new IdleClip(Y, float.NaN)) })
            {
                var driver = new Driver(normal, Talk2());
                driver.Talking = true;

                var snapshots = driver.Run(3f);

                Assert.That(snapshots.All(s => s.Count == 0), Is.True, "通常の一覧が空なら話す間用も使わない");
            }

            var nulls = new Driver(null, null);
            Assert.That(nulls.Run(1f).All(s => s.Count == 0), Is.True, "null と null");
        }

        [Test]
        public void SetClips_DoesNotShowAnything_BeforeTheFirstAdvance()
        {
            var sequence = new IdleSequence();

            sequence.SetClips(Normal3(), Talk2());

            Assert.That(sequence.LayerCount, Is.EqualTo(0), "待機の度合いも自作の待機の度合いも 0 から");
        }

        // ---- 始まり ----

        [Test]
        public void Advance_StartsFirstClipAtTimeZero_AndRaisesItsShareByIdleAndOwnFades()
        {
            var driver = new Driver(Normal3(), Talk2());

            var early = driver.RunTo(0.1);
            AssertTokens(early, new[] { A }, "0.1 秒");
            Assert.That(early.Single(A).Time, Is.EqualTo(0.1f).Within(Tolerance));
            // 待機の度合い 0.1 / 0.25 = 0.4、自作の待機の度合い 0.1 / 1 = 0.1
            Assert.That(early.Own, Is.EqualTo(0.04f).Within(Tolerance), early.ToString());

            var half = driver.RunTo(0.5);
            Assert.That(half.Own, Is.EqualTo(0.5f).Within(Tolerance), "0.5 秒: 待機の度合い 1 × 自作の待機の度合い 0.5 " + half);
            Assert.That(half.Single(A).Time, Is.EqualTo(0.5f).Within(Tolerance));

            var full = driver.RunTo(1.05);
            Assert.That(full.Own, Is.EqualTo(1f).Within(Tolerance), full.ToString());
            Assert.That(full.Single(A).Weight, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void Advance_StartsFirstClipOnlyWhenIdle_WhenClipsArriveOutsideIdle()
        {
            var driver = new Driver(Normal3(), Talk2());
            driver.Idle = false;
            Assert.That(driver.Run(2f).All(s => s.Count == 0), Is.True, "待機でない間は見えない");

            driver.Idle = true;
            var now = driver.Run(0.5f).Last();

            Assert.That(now.Single(A).Time, Is.EqualTo(0.5f).Within(Tolerance), "待機でない間は時計が止まっている " + now);
            Assert.That(now.Own, Is.EqualTo(0.5f).Within(Tolerance), "自作の待機の度合いも 0.5 " + now);
        }

        // ---- 通常の順 ----

        [Test]
        public void Advance_PlaysClipsInListOrderAndReturnsToFirstAfterLast()
        {
            var driver = new Driver(Normal3(), Talk2());

            var snapshots = driver.CollectTo(26.0);

            // A→B 3 秒、B→C 7 秒、C→A 12 秒、A→B 15 秒、B→C 19 秒、C→A 24 秒
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { A, B, C, A, B, C, A }), string.Join(",", IncomingOrder(snapshots)));
            Assert.That(snapshots.Any(s => s.Has(T) || s.Has(U)), Is.False, "話していないのに話す間用");
        }

        [Test]
        public void Advance_CrossFadesToNextClipOverOneSecond_FromTheCrossFadeStartPoint()
        {
            var driver = Started();

            var before = driver.RunTo(2.9);
            AssertTokens(before, new[] { A }, "開始点の前");
            Assert.That(before.Single(A).Time, Is.EqualTo(2.9f).Within(Tolerance));

            var quarter = driver.RunTo(3.25);
            AssertTokens(quarter, new[] { A, B }, "混ぜの間は出ていく A が層 0、入ってくる B が層 1");
            Assert.That(quarter.Layers[1].Time, Is.EqualTo(0.25f).Within(Slack), "B は Time 0 から " + quarter);
            Assert.That(quarter.Layers[0].Time, Is.EqualTo(3.25f).Within(Tolerance), "混ぜの間も A は進む");
            Assert.That(quarter.Of(B), Is.EqualTo(0.25f).Within(Slack), quarter.ToString());
            Assert.That(quarter.Of(A), Is.EqualTo(0.75f).Within(Slack), quarter.ToString());
            Assert.That(quarter.Base, Is.EqualTo(0f).Within(Tolerance), "本家は見えない");

            var threeQuarters = driver.RunTo(3.75);
            Assert.That(threeQuarters.Of(B) - quarter.Of(B), Is.EqualTo(0.5f).Within(Tolerance), "1 秒で混ぜる " + threeQuarters);

            var after = driver.RunTo(4.1);
            AssertTokens(after, new[] { B }, "混ぜが終わったら出ていく層を外す");
            Assert.That(after.Layers[0].Time, Is.EqualTo(1.1f).Within(Slack));
            Assert.That(after.Of(B), Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void GetLayer_GivesWeightsWhoseLerpChainMatchesTheShares_DuringCrossFade()
        {
            var driver = Started();
            var now = driver.RunTo(3.5);

            AssertTokens(now, new[] { A, B }, "前提");
            var incoming = now.Layers[1].Weight;
            var outgoing = now.Layers[0].Weight;
            // o = 1 のとき、層 1 の Weight = c、層 0 の Weight = (1 - c) / (1 - c) = 1
            Assert.That(outgoing, Is.EqualTo(1f).Within(Tolerance), now.ToString());
            Assert.That(incoming, Is.EqualTo(0.5f).Within(Slack), now.ToString());
        }

        [Test]
        public void Advance_RepeatsTheOnlyClipWithItself_WhenListHasOneClip()
        {
            var driver = new Driver(Clips(new IdleClip(A, 4f)), None());

            var first = driver.RunTo(3.5);
            AssertTokens(first, new[] { A, A }, "1 本だけなら自分自身へ混ぜる");
            Assert.That(first.Layers[0].Time, Is.EqualTo(3.5f).Within(Tolerance), "終わり近く");
            Assert.That(first.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack), "0 近く");
            Assert.That(first.Of(A), Is.EqualTo(1f).Within(Tolerance), "同じ Token の寄与の和");

            var single = driver.RunTo(4.2);
            AssertTokens(single, new[] { A }, "混ぜのあと");
            Assert.That(single.Layers[0].Time, Is.EqualTo(1.2f).Within(Slack));

            var second = driver.RunTo(6.5);
            AssertTokens(second, new[] { A, A }, "繰り返す");
            Assert.That(second.Layers[0].Time, Is.EqualTo(3.5f).Within(Slack));
            Assert.That(second.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack));
        }

        [Test]
        public void Advance_StartsTheNextCrossFadeRightAfterTheCurrentOne_WhenClipsAreShorterThanTheCrossFade()
        {
            var driver = new Driver(Clips(new IdleClip(A, 0.01f), new IdleClip(B, 0.01f), new IdleClip(C, 0.01f)), None());

            var snapshots = driver.CollectTo(6.5);

            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { B, C, A, B, C, A, B }), "1 秒ごとに次へ: " + string.Join(",", IncomingOrder(snapshots)));
            var at = new Func<double, Snapshot>(t => snapshots[(int)Math.Round(t / Frame) - 1]);
            AssertTokens(at(1.5), new[] { B, C }, "1.5 秒");
            AssertTokens(at(2.5), new[] { C, A }, "2.5 秒");
            Assert.That(at(1.5).Layers.All(l => l.Time == 0.01f), Is.True, "Time は長さで止まる " + at(1.5));
            Assert.That(at(1.5).Of(C), Is.EqualTo(0.5f).Within(Slack), at(1.5).ToString());
        }

        [Test]
        public void Advance_CrossFadesImmediately_WhenClipIsExactlyOneSecond()
        {
            var driver = new Driver(Clips(new IdleClip(A, 1f), new IdleClip(B, 1f)), None());

            var half = driver.RunTo(0.5);
            AssertTokens(half, new[] { A, B }, "開始点は 0");
            Assert.That(half.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack));

            var next = driver.RunTo(1.5);
            AssertTokens(next, new[] { B, A }, "B から A へ");
            Assert.That(next.Layers[0].Time, Is.EqualTo(1f), "B は終わりの姿勢で止まる");
            Assert.That(next.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack));
        }

        [Test]
        public void Advance_HoldsTheLastPoseOfOutgoingClip_WhenItEndsBeforeTheCrossFade()
        {
            var driver = new Driver(Clips(new IdleClip(A, 0.5f), new IdleClip(B, 4f)), None());

            var now = driver.RunTo(0.8);

            AssertTokens(now, new[] { A, B }, "混ぜの途中");
            Assert.That(now.Layers[0].Time, Is.EqualTo(0.5f), "A は長さで止まる");
            Assert.That(now.Layers[1].Time, Is.EqualTo(0.8f).Within(Slack));
        }

        [Test]
        public void Advance_PlaysLongClipToItsEnd()
        {
            var driver = new Driver(Clips(new IdleClip(A, 300f), new IdleClip(B, 2f)), None());
            const float Coarse = 1f / 15f;

            var before = driver.RunTo(298.5, Coarse);
            AssertTokens(before, new[] { A }, "300 秒のクリップの途中");
            Assert.That(before.Layers[0].Time, Is.EqualTo(298.5f).Within(0.05f));

            var crossing = driver.RunTo(299.5, Coarse);
            AssertTokens(crossing, new[] { A, B }, "終わりの 1 秒前から次へ");
        }

        // ---- delta ----

        [TestCase(0f)]
        [TestCase(-0.1f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Advance_TreatsDeltaAsZero_WhenDeltaIsNotPositiveFinite(float delta)
        {
            foreach (var idle in new[] { true, false })
            {
                var driver = Started();
                driver.RunTo(3.5);
                var before = driver.Now;

                driver.Sequence.Advance(delta, idle);
                var after = driver.Now;

                Assert.That(after.Tokens, Is.EqualTo(before.Tokens), "delta " + delta + " idle " + idle);
                for (var i = 0; i < after.Count; i++)
                {
                    Assert.That(after.Layers[i].Time, Is.EqualTo(before.Layers[i].Time), "delta " + delta + " idle " + idle + " Time");
                    Assert.That(after.Layers[i].Weight, Is.EqualTo(before.Layers[i].Weight), "delta " + delta + " idle " + idle + " Weight");
                }
            }
        }

        [TestCase(1f / 165f, 1f / 165f)]
        [TestCase(1f / 60f, 1f / 60f)]
        [TestCase(1f / 15f, 1f / 15f)]
        [TestCase(0.07f, 1f / 15f)]
        [TestCase(0.1f, 1f / 15f)]
        [TestCase(0.333f, 1f / 15f)]
        [TestCase(1f, 1f / 15f)]
        [TestCase(1000f, 1f / 15f)]
        [TestCase(float.MaxValue, 1f / 15f)]
        public void Advance_MovesTimeByDeltaCappedAtMaxDelta(float delta, float expected)
        {
            var driver = Started();
            var before = driver.Now.Single(A).Time;

            driver.Sequence.Advance(delta, true);

            Assert.That(driver.Now.Single(A).Time - before, Is.EqualTo(expected).Within(1e-5f), "delta " + delta);
        }

        [Test]
        public void Advance_CapsDeltaForLeavingIdle()
        {
            var driver = Started();

            driver.Sequence.Advance(1f, false);
            var now = driver.Now;

            // 1 - (1/15) / 0.15
            Assert.That(now.Own, Is.EqualTo(1f - (1f / 15f) / IdleSequence.LeaveIdleSeconds).Within(Tolerance), now.ToString());
        }

        [Test]
        public void Advance_CapsDeltaForCrossFadeProgress()
        {
            var driver = Started();
            var before = driver.RunTo(3.5);

            driver.Sequence.Advance(1f, true);
            var now = driver.Now;

            Assert.That(now.Of(B) - before.Of(B), Is.EqualTo(1f / 15f).Within(Tolerance), now.ToString());
        }

        [Test]
        public void Advance_CapsDeltaForTheTalkHold()
        {
            var driver = TalkingOnT();
            driver.Talking = false;

            // 21 回 × 1/15 秒 = 1.4 秒 < 1.5 秒
            for (var i = 0; i < 21; i++)
            {
                var now = driver.Step(5f);
                Assert.That(now.Tokens, Is.EqualTo(new[] { T }), "保ちの間（頭打ち後の和で " + (i + 1) / 15f + " 秒）" + now);
            }

            for (var i = 0; i < 3; i++)
            {
                driver.Step(5f);
            }

            Assert.That(driver.Now.Has(B), Is.True, "頭打ち後の和で 1.6 秒たてば通常に戻り始める " + driver.Now);
        }

        // ---- 待機から外れる・戻る ----

        [Test]
        public void Advance_LowersOwnShareLinearlyOverLeaveIdleSeconds_AndThenShowsNothing()
        {
            var driver = Started();
            driver.Idle = false;

            var snapshots = driver.Run(IdleSequence.LeaveIdleSeconds / 2f, FineFrame);
            Assert.That(snapshots.Last().Own, Is.EqualTo(0.5f).Within(Tolerance), snapshots.Last().ToString());
            for (var i = 1; i < snapshots.Count; i++)
            {
                Assert.That(snapshots[i].Own, Is.LessThanOrEqualTo(snapshots[i - 1].Own), "frame " + i);
            }

            driver.Run(IdleSequence.LeaveIdleSeconds / 2f + FineFrame, FineFrame);
            Assert.That(driver.Sequence.LayerCount, Is.EqualTo(0), "0.15 秒（+1 フレーム）で本家だけ");
            Assert.That(driver.Run(3f).All(s => s.Count == 0), Is.True, "待機でない間は何も写さない");
        }

        [Test]
        public void Advance_RaisesOwnShareLinearlyOverEnterIdleSeconds_WhenIdleReturns()
        {
            var driver = Started();
            driver.Idle = false;
            driver.Run(1f);

            driver.Idle = true;
            var half = driver.Run(IdleSequence.EnterIdleSeconds / 2f, FineFrame).Last();
            Assert.That(half.Own, Is.EqualTo(0.5f).Within(Tolerance), half.ToString());

            var full = driver.Run(IdleSequence.EnterIdleSeconds / 2f + FineFrame, FineFrame).Last();
            Assert.That(full.Own, Is.EqualTo(1f).Within(Tolerance), full.ToString());
        }

        [Test]
        public void Advance_StopsTheClockOnlyAfterLeavingCompletes_AndResumesWhereItStopped()
        {
            var driver = Started();
            var start = driver.Now.Single(A).Time;

            driver.Idle = false;
            driver.Run(IdleSequence.LeaveIdleSeconds);
            driver.Run(5f);
            driver.Idle = true;
            var now = driver.Step(Frame);

            // 外れていく 0.15 秒の間は進み、そのあと止まる。戻った最初のフレームで 1 フレーム進む
            AssertTokens(now, new[] { A }, "待機から外れている間は次へ進まない");
            Assert.That(now.Layers[0].Time, Is.EqualTo(start + IdleSequence.LeaveIdleSeconds + Frame).Within(Slack), now.ToString());
        }

        [Test]
        public void Advance_FreezesCrossFadeProgressWhileAway_AndContinuesItAfterReturning()
        {
            var driver = Started();
            var mid = driver.RunTo(3.5);
            var c0 = mid.Of(B);

            driver.Idle = false;
            driver.Run(IdleSequence.LeaveIdleSeconds);
            driver.Run(3f);
            driver.Idle = true;
            var now = driver.Run(0.1f).Last();

            // 待機の度合い p = 0.4、混ぜの進み c = c0 + 0.15 + 0.1
            var p = 0.1f / IdleSequence.EnterIdleSeconds;
            var c = c0 + IdleSequence.LeaveIdleSeconds + 0.1f;
            AssertTokens(now, new[] { A, B }, "混ぜの続き");
            Assert.That(now.Base, Is.EqualTo(1f - p).Within(Tolerance), "本家は 1 - o " + now);
            Assert.That(now.Of(B), Is.EqualTo(p * c).Within(0.02f), "入ってくる層は o·c " + now);
            Assert.That(now.Of(A), Is.EqualTo(p * (1f - c)).Within(0.02f), "出ていく層は o(1 - c) " + now);
        }

        [Test]
        public void GetLayer_GivesSharesOfOneMinusO_OTimesOneMinusC_AndOTimesC_WhenLeavingDuringCrossFade()
        {
            var driver = Started();
            var mid = driver.RunTo(3.5, FineFrame);
            var c0 = mid.Of(B);

            driver.Idle = false;
            var now = driver.Run(IdleSequence.LeaveIdleSeconds / 2f, FineFrame).Last();

            var o = 0.5f;
            var c = c0 + IdleSequence.LeaveIdleSeconds / 2f;
            AssertTokens(now, new[] { A, B }, "前提");
            Assert.That(now.Base, Is.EqualTo(1f - o).Within(2e-3f), now.ToString());
            Assert.That(now.Of(A), Is.EqualTo(o * (1f - c)).Within(2e-3f), "層 0 の寄与 " + now);
            Assert.That(now.Of(B), Is.EqualTo(o * c).Within(2e-3f), "層 1 の寄与 " + now);
            Assert.That(now.Layers[1].Weight, Is.EqualTo(o * c).Within(2e-3f), "層 1 の Weight = o·c");
            Assert.That(now.Layers[0].Weight, Is.EqualTo(o * (1f - c) / (1f - o * c)).Within(2e-3f), "層 0 の Weight = o(1-c)/(1-o·c)");
        }

        [Test]
        public void Advance_KeepsSharesContinuous_WhenIdleTogglesEveryFewFrames()
        {
            var driver = Started();
            var pattern = new[] { true, false, false, true, false, true, true, false, false, false };

            for (var i = 0; i < 1200; i++)
            {
                driver.Idle = pattern[i % pattern.Length];
                driver.Step(Frame);
            }

            Assert.That(driver.Now.Count, Is.InRange(0, 2));
        }

        // ---- 話している間（話す間用の一覧がある） ----

        [Test]
        public void SetTalking_DoesNotChangeLayers_UntilTheNextAdvance()
        {
            var driver = Started();
            var before = driver.Now;

            driver.Sequence.SetTalking(true);
            var after = driver.Now;

            Assert.That(after.Tokens, Is.EqualTo(before.Tokens));
            Assert.That(after.Layers[0].Weight, Is.EqualTo(before.Layers[0].Weight));
        }

        [Test]
        public void Advance_MovesToFirstTalkClipFromTimeZeroOverTalkFadeSeconds_WhenTalkingStarts()
        {
            var driver = Started();
            driver.Talking = true;

            var quarter = driver.Run(0.25f).Last();
            AssertTokens(quarter, new[] { A, T }, "A から T へ");
            Assert.That(quarter.Layers[1].Time, Is.EqualTo(0.25f).Within(Slack), "T は Time 0 から " + quarter);
            Assert.That(quarter.Of(T), Is.EqualTo(0.5f).Within(2f * Slack), "0.5 秒で移る " + quarter);

            var done = driver.Run(0.3f).Last();
            AssertTokens(done, new[] { T }, "移り終えた");
            Assert.That(done.Layers[0].Time, Is.EqualTo(0.55f).Within(Slack));
        }

        [Test]
        public void Advance_LoopsTalkClipWithItselfAndNeverShowsNormalOrOtherTalkClips_WhileTalking()
        {
            var driver = TalkingOnT();

            var snapshots = driver.Run(20f);

            Assert.That(snapshots.Any(s => s.Has(A) || s.Has(B) || s.Has(C)), Is.False, "話している間は通常のクリップに戻らない");
            Assert.That(snapshots.Any(s => s.Has(U)), Is.False, "話す間用は 0 番目だけ");
            Assert.That(snapshots.All(s => s.Count >= 1), Is.True);

            // T は 1.2 秒に始まり、T の Time 5 秒（6.2 秒）から自分自身へ 1 秒で混ぜる
            var loop = new Driver(Normal3(), Talk2());
            loop.RunTo(1.2);
            loop.Talking = true;
            var now = loop.RunTo(1.2 + 5.5);
            AssertTokens(now, new[] { T, T }, "T から T へ");
            Assert.That(now.Layers[0].Time, Is.EqualTo(5.5f).Within(Slack), now.ToString());
            Assert.That(now.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack), now.ToString());
            Assert.That(now.Of(T), Is.EqualTo(1f).Within(Tolerance));
        }

        [TestCase(0.3f)]
        [TestCase(1f)]
        [TestCase(1.49f)]
        public void Advance_StaysOnTalkClip_WhenPausesBetweenSentencesAreShorterThanTheHold(float pause)
        {
            var driver = TalkingOnT();

            var snapshots = new List<Snapshot>();
            for (var sentence = 0; sentence < 3; sentence++)
            {
                driver.Talking = false;
                snapshots.AddRange(driver.Run(pause, FineFrame));
                driver.Talking = true;
                snapshots.AddRange(driver.Run(1f, FineFrame));
            }

            Assert.That(snapshots.Any(s => s.Has(A) || s.Has(B) || s.Has(C)), Is.False, "pause " + pause);
        }

        [Test]
        public void Advance_StartsReturningToTheNextNormalClip_WhenThePauseIsJustLongerThanTheHold()
        {
            var driver = TalkingOnT();
            driver.Talking = false;

            var pause = driver.Run(1.49f, FineFrame);
            Assert.That(pause.Any(s => s.Has(A) || s.Has(B) || s.Has(C)), Is.False, "1.49 秒ではまだ話している扱い");

            var now = driver.Run(0.02f, FineFrame).Last();
            Assert.That(now.Has(B), Is.True, "1.51 秒で戻り始める " + now);
            Assert.That(now.Has(A) || now.Has(C), Is.False, "話し始める前の位置（A）の次 " + now);
            Assert.That(now.Of(B), Is.GreaterThan(0f).And.LessThan(0.05f), now.ToString());
        }

        [Test]
        public void Advance_ReturnsToTheNextNormalClipOverOneSecond_AfterTheHold()
        {
            var driver = TalkingOnT();
            driver.Talking = false;

            driver.Run(IdleSequence.TalkHoldSeconds);
            var half = driver.Run(0.5f).Last();
            AssertTokens(half, new[] { T, B }, "T から B へ");
            Assert.That(half.Of(B), Is.EqualTo(0.5f).Within(Slack), "1 秒で移る " + half);
            Assert.That(half.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack), "B は Time 0 から " + half);

            var done = driver.Run(0.55f).Last();
            AssertTokens(done, new[] { B }, "移り終えた");
        }

        [Test]
        public void EndTalking_StartsReturningAtTheNextAdvanceWithoutWaitingForTheHold()
        {
            var driver = TalkingOnT();

            driver.EndTalking();
            var now = driver.Run(0.5f).Last();

            AssertTokens(now, new[] { T, B }, "cancel・切断ではすぐ戻り始める");
            Assert.That(now.Of(B), Is.EqualTo(0.5f).Within(Slack), now.ToString());
        }

        [Test]
        public void EndTalking_EndsTheHoldEarly_WhenCalledDuringTheHold()
        {
            var driver = TalkingOnT();
            driver.Talking = false;
            var hold = driver.Run(0.5f);
            Assert.That(hold.Any(s => s.Has(B)), Is.False, "前提: 保ちの間");

            driver.EndTalking();
            var now = driver.Run(0.25f).Last();

            Assert.That(now.Of(B), Is.EqualTo(0.25f).Within(Slack), now.ToString());
        }

        [Test]
        public void Advance_CountsTheTalkHoldWhileTheClockIsStopped()
        {
            var driver = TalkingOnT();
            driver.Talking = false;
            driver.Idle = false;
            driver.Run(IdleSequence.LeaveIdleSeconds);
            var away = driver.Run(2f);
            Assert.That(away.All(s => s.Count == 0), Is.True, "前提: 本家だけ");

            driver.Idle = true;
            var now = driver.Run(0.3f).Last();

            // 保ちは待機から外れている間に終わっているので、戻ったらすぐ B へ移り始める
            Assert.That(now.Has(B), Is.True, now.ToString());
            Assert.That(now.Of(B), Is.EqualTo(0.3f).Within(2f * Slack), now.ToString());
        }

        [Test]
        public void EndTalking_DoesNothing_WhenNotTalking()
        {
            var driver = Started();

            driver.EndTalking();
            var before = driver.RunTo(2.9);
            AssertTokens(before, new[] { A }, "そのまま A");
            Assert.That(before.Layers[0].Time, Is.EqualTo(2.9f).Within(Tolerance));

            var cross = driver.RunTo(3.5);
            AssertTokens(cross, new[] { A, B }, "通常どおり次へ");
        }

        [Test]
        public void SetTalking_TalksAgain_WhenCalledWithTrueAfterEndTalking()
        {
            var driver = TalkingOnT();

            driver.EndTalking();
            driver.Talking = true;
            var snapshots = driver.Run(1f);

            Assert.That(snapshots.Any(s => s.Has(B) || s.Has(A) || s.Has(C)), Is.False, "EndTalking のあとの SetTalking(true) でまた話している扱い");
        }

        [Test]
        public void Advance_ReversesTheReturnAndKeepsTalkClipRunning_WhenTalkingResumesDuringTheReturn()
        {
            var driver = TalkingOnT();
            var talkTime = driver.Now.Single(T).Time;

            driver.EndTalking();
            var returning = driver.Run(0.4f).Last();
            AssertTokens(returning, new[] { T, B }, "前提: 戻る途中");
            var b0 = returning.Of(B);

            driver.Talking = true;
            var reversing = driver.Run(0.25f);

            for (var i = 1; i < reversing.Count; i++)
            {
                Assert.That(reversing[i].Of(B), Is.LessThanOrEqualTo(reversing[i - 1].Of(B) + ContinuityTolerance), "B は 0 へ戻る frame " + i + " " + reversing[i]);
            }

            // 0.5 秒で移る速さで c を 0 へ戻す: b0 / 2 秒
            var gone = reversing.FindIndex(s => !s.Has(B));
            Assert.That(gone, Is.GreaterThanOrEqualTo(0), "B が外れない " + reversing.Last());
            Assert.That((gone + 1) * Frame, Is.EqualTo(b0 * IdleSequence.TalkFadeSeconds).Within(Slack), "B が外れるまでの時間");
            var now = reversing.Last();
            AssertTokens(now, new[] { T }, "T だけに戻る");
            Assert.That(now.Layers[0].Time, Is.EqualTo(talkTime + 0.4f + 0.25f).Within(Slack), "T は始め直さずに続く " + now);
        }

        [Test]
        public void Advance_FinishesNormalCrossFadeAtTalkSpeedThenMovesToTalkClip_WhenTalkingStartsDuringCrossFade()
        {
            var driver = Started();
            var mid = driver.RunTo(3.5);
            AssertTokens(mid, new[] { A, B }, "前提: A から B へ混ぜている途中");
            var a0 = mid.Of(A);

            driver.Talking = true;
            var faster = driver.Run(0.1f).Last();
            AssertTokens(faster, new[] { A, B }, "いまの混ぜを終える");
            Assert.That(faster.Of(A), Is.EqualTo(a0 - 0.1f / IdleSequence.TalkFadeSeconds).Within(Tolerance), "0.5 秒で移る速さで終える " + faster);

            var toTalk = driver.Run(0.25f).Last();
            AssertTokens(toTalk, new[] { B, T }, "そのあと B から T へ");

            var talking = driver.Run(0.5f).Last();
            AssertTokens(talking, new[] { T }, "T だけ");

            // 話し始める前の位置は B なので、話し終えたら C へ
            driver.EndTalking();
            var back = driver.Run(0.5f).Last();
            AssertTokens(back, new[] { T, C }, "B の次の C へ");
        }

        [Test]
        public void SetClips_UsesTheFirstValidTalkClip_WhenTalkListHasInvalidLengths()
        {
            var driver = Started(Normal3(), Clips(new IdleClip(X, 0f), new IdleClip(Y, float.NaN), new IdleClip(T, 6f)));
            driver.Talking = true;

            var now = driver.Run(1f).Last();

            AssertTokens(now, new[] { T }, "長さが正で有限でないものは除く");
        }

        [Test]
        public void Advance_ReturnsToTheNextAfterTheLastPositionBeforeTalking_WhenClipBeforeTalkingWasLast()
        {
            // C の途中で話して戻ると、最初の A へ
            var driver = Started();
            driver.RunTo(8.5);
            AssertTokens(driver.Now, new[] { C }, "前提");
            driver.Talking = true;
            driver.Run(1f);

            driver.EndTalking();
            var now = driver.Run(0.5f).Last();

            AssertTokens(now, new[] { T, A }, "最後の次は最初");
        }

        // ---- 話している間（話す間用の一覧が空） ----

        [Test]
        public void Advance_RepeatsTheCurrentClipInsteadOfMovingOn_WhileTalkingWithoutTalkClips()
        {
            foreach (var talk in new[] { null, None(), Clips(new IdleClip(X, 0f)) })
            {
                var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), talk);
                driver.Talking = true;

                var repeat = driver.RunTo(3.5);
                AssertTokens(repeat, new[] { A, A }, "同じクリップへ混ぜる");
                Assert.That(repeat.Layers[0].Time, Is.EqualTo(3.5f).Within(Tolerance));
                Assert.That(repeat.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack), "Time 0 から");

                var snapshots = driver.CollectTo(15.0);
                Assert.That(snapshots.Any(s => s.Has(B)), Is.False, "話している間は次へ進まない");
            }
        }

        [Test]
        public void Advance_MovesOnAtTheEndOfTheCurrentClipNotImmediately_WhenTalkingEndsWithoutTalkClips()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), None());
            driver.Talking = true;
            driver.RunTo(5.0);
            var repeated = driver.Now;
            AssertTokens(repeated, new[] { A }, "前提: 3 秒に A から A へ混ぜた");
            Assert.That(repeated.Layers[0].Time, Is.EqualTo(2f).Within(Slack));

            driver.EndTalking();
            var waiting = driver.CollectTo(5.9);
            Assert.That(waiting.Any(s => s.Has(B)), Is.False, "話し終えても切り替えない");

            var next = driver.RunTo(6.5);
            AssertTokens(next, new[] { A, B }, "いまのクリップの開始点で通常どおり次へ");
            Assert.That(next.Layers[1].Time, Is.EqualTo(0.5f).Within(Slack));
        }

        // ---- 一覧 ----

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void SetClips_DropsClipsWhoseLengthIsNotPositiveFinite(float length)
        {
            var driver = new Driver(Clips(new IdleClip(X, length), new IdleClip(A, 4f), new IdleClip(Y, length), new IdleClip(B, 5f)), None());

            var snapshots = driver.CollectTo(16.0);

            Assert.That(snapshots.Any(s => s.Has(X) || s.Has(Y)), Is.False, "length " + length);
            // A→B 3 秒、B→A 7 秒、A→B 10 秒、B→A 14 秒
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { A, B, A, B, A }), "length " + length + ": " + string.Join(",", IncomingOrder(snapshots)));
        }

        [Test]
        public void SetClips_ActivatesImmediately_WhenEarlierListsWereEmptyOrInvalid()
        {
            var driver = new Driver(Clips(new IdleClip(X, 0f)), None());
            Assert.That(driver.Run(1f).All(s => s.Count == 0), Is.True, "全部除かれたら空");

            driver.SetClips(Normal3(), None());
            var now = driver.Run(0.5f).Last();

            AssertTokens(now, new[] { A }, "空のときの SetClips はすぐ使う");
            Assert.That(now.Layers[0].Time, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(now.Own, Is.EqualTo(0.5f).Within(Tolerance), "自作の待機の度合いは 0 から 1 秒で 1 へ " + now);
        }

        [Test]
        public void SetClips_TreatsRepeatedTokensAsSeparatePositions()
        {
            var driver = new Driver(Clips(new IdleClip(A, 4f), new IdleClip(A, 4f)), None());
            var requests = new List<double>();

            for (var i = 0; i < (int)Math.Round(11.0 / Frame); i++)
            {
                driver.Step(Frame);
                if (driver.Sequence.TakeReloadRequest())
                {
                    requests.Add(driver.Elapsed);
                }
            }

            AssertTokens(driver.RunTo(12.5), new[] { A, A }, "A から A へ混ぜ続ける");
            // 2 番目（最後の位置）へ移るのは 3 秒と 9 秒。6 秒は最後から最初へ戻るとき
            Assert.That(requests.Count, Is.EqualTo(2), string.Join(", ", requests));
            Assert.That(requests[0], Is.EqualTo(3.0).Within(Drift(0)));
            Assert.That(requests[1], Is.EqualTo(9.0).Within(Drift(2)));
        }

        [Test]
        public void SetClips_WaitsUntilTheOrderReturnsToFirst_WhenAListIsActive()
        {
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(X, 4f), new IdleClip(Y, 4f)), None());

            var snapshots = driver.CollectTo(13.1);
            var after = driver.CollectTo(20.0);
            snapshots.AddRange(after);

            // B 3 秒、C 7 秒、最初に戻る 12 秒で X、15 秒で Y、18 秒で X
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { A, B, C, X, Y, X }), string.Join(",", IncomingOrder(snapshots)));
            Assert.That(after.Any(s => s.Has(A) || s.Has(B) || s.Has(C)), Is.False, "最初に戻ったあとは前の一覧を使わない");
        }

        [Test]
        public void SetClips_ReplacesAnEarlierPendingList()
        {
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(X, 4f)), None());
            driver.RunTo(2.0);
            driver.SetClips(Clips(new IdleClip(Y, 4f)), None());

            var snapshots = driver.CollectTo(17.0);

            Assert.That(snapshots.Any(s => s.Has(X)), Is.False, "置き換えられた保留は使わない");
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { A, B, C, Y }), string.Join(",", IncomingOrder(snapshots)));
        }

        [Test]
        public void SetClips_ReplacesTalkListTogetherWithNormalList_WhenTheOrderReturnsToFirst()
        {
            var driver = Started();
            driver.SetClips(Normal3(), Clips(new IdleClip(U, 6f)));

            // 最初に戻る前に話す: 前の一覧の T
            driver.Talking = true;
            var first = driver.Run(1f);
            driver.EndTalking();
            first.AddRange(driver.Run(1.1f));
            Assert.That(first.Any(s => s.Has(T)), Is.True, "保留の間は前の話す間用の一覧");
            Assert.That(first.Any(s => s.Has(U)), Is.False, "保留の間は前の話す間用の一覧");

            // 1.2 秒に話し始め 2.2 秒に B へ戻る。B 6.2 秒・C 11.2 秒で最初に戻る
            driver.RunTo(13.0);
            driver.Talking = true;
            var second = driver.Run(1f);
            Assert.That(second.Any(s => s.Has(U)), Is.True, "最初に戻ったあとは新しい話す間用の一覧 " + second.Last());
            Assert.That(second.Any(s => s.Has(T)), Is.False, "最初に戻ったあとは新しい話す間用の一覧");
        }

        [Test]
        public void Advance_FadesOwnIdleOutWithTheLastClip_WhenTheListBecomesEmptyAtReturnToFirst()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), Talk2());
            driver.SetClips(None(), Talk2());

            // B は 3 秒に始まり、B の Time 4 秒（7 秒）で最初に戻る
            var before = driver.RunTo(6.9);
            AssertTokens(before, new[] { B }, "最初に戻るまでは B");
            Assert.That(before.Own, Is.EqualTo(1f).Within(Tolerance));

            var half = driver.RunTo(7.5);
            AssertTokens(half, new[] { B }, "最後のクリップを続ける");
            Assert.That(half.Layers[0].Time, Is.EqualTo(4.5f).Within(Slack));
            Assert.That(half.Own, Is.EqualTo(0.5f).Within(Slack), "1 秒で本家へ " + half);

            var fading = driver.CollectTo(8.2);
            var last = fading.Last(s => s.Count > 0);
            Assert.That(last.Layers[0].Time, Is.EqualTo(5f).Within(Slack), "最後のクリップの終わりと 0 になる時が同じ " + last);
            Assert.That(fading.Last().Count, Is.EqualTo(0), "本家だけ");

            driver.Talking = true;
            var after = driver.Run(5f);
            driver.Talking = false;
            after.AddRange(driver.Run(5f));
            Assert.That(after.All(s => s.Count == 0), Is.True, "一覧が空の間は話す間用も使わない");
        }

        [Test]
        public void SetClips_StartsTheNewListImmediately_AfterOwnIdleHasFadedOut()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), None());
            driver.SetClips(None(), None());
            driver.RunTo(9.0);
            Assert.That(driver.Now.Count, Is.EqualTo(0), "前提: 本家だけ");
            driver.Sequence.TakeReloadRequest();

            driver.SetClips(Clips(new IdleClip(X, 4f), new IdleClip(Y, 4f)), None());
            var now = driver.Run(0.5f).Last();

            AssertTokens(now, new[] { X }, "すぐ使う");
            Assert.That(now.Layers[0].Time, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(now.Own, Is.EqualTo(0.5f).Within(Tolerance), "1 秒で自作の待機へ " + now);
        }

        // ---- 読み直しの要求 ----

        private static List<double> RequestTimes(Driver driver, double until, Action<Driver> onRequest = null)
        {
            var times = new List<double>();
            while (driver.Elapsed < until - 1e-6)
            {
                driver.Step(Frame);
                if (driver.Sequence.TakeReloadRequest())
                {
                    times.Add(driver.Elapsed);
                    if (onRequest != null)
                    {
                        onRequest(driver);
                    }
                }
            }

            return times;
        }

        [Test]
        public void TakeReloadRequest_IsTrueOnce_WhenMovingToTheLastClipOfTheList()
        {
            var driver = new Driver(Normal3(), None());

            var times = RequestTimes(driver, 25.0);

            // B→C は 7 秒と 19 秒
            Assert.That(times.Count, Is.EqualTo(2), string.Join(", ", times));
            Assert.That(times[0], Is.EqualTo(7.0).Within(Drift(1)));
            Assert.That(times[1], Is.EqualTo(19.0).Within(Drift(4)));
        }

        [Test]
        public void TakeReloadRequest_LowersTheRequest()
        {
            var driver = new Driver(Normal3(), None());
            driver.RunTo(8.0);

            Assert.That(driver.Sequence.TakeReloadRequest(), Is.True, "取るまで立ったまま");
            Assert.That(driver.Sequence.TakeReloadRequest(), Is.False, "取ったら下りる");
            driver.RunTo(10.0);
            Assert.That(driver.Sequence.TakeReloadRequest(), Is.False);
        }

        [Test]
        public void TakeReloadRequest_IsTrueEveryTimeTheOnlyClipStarts_WhenListHasOneClip()
        {
            var driver = new Driver(Clips(new IdleClip(A, 4f)), None());

            var times = RequestTimes(driver, 10.0);

            // 一覧が active になって始めたとき、3 秒、6 秒、9 秒
            Assert.That(times.Count, Is.EqualTo(4), string.Join(", ", times));
            Assert.That(times[0], Is.LessThanOrEqualTo(Slack), "始めたとき");
            Assert.That(times[1], Is.EqualTo(3.0).Within(Drift(0)));
            Assert.That(times[2], Is.EqualTo(6.0).Within(Drift(1)));
            Assert.That(times[3], Is.EqualTo(9.0).Within(Drift(2)));
        }

        [Test]
        public void TakeReloadRequest_IsTrueEveryTimeTheOnlyClipStarts_WhileTalkingWithoutTalkClips()
        {
            var driver = new Driver(Clips(new IdleClip(A, 4f)), None());
            driver.Talking = true;

            var times = RequestTimes(driver, 10.0);

            Assert.That(times.Count, Is.EqualTo(4), "話している間に同じクリップを繰り返すときも " + string.Join(", ", times));
            Assert.That(times[0], Is.LessThanOrEqualTo(Slack), "始めたとき");
            Assert.That(times[1], Is.EqualTo(3.0).Within(Drift(0)));
            Assert.That(times[2], Is.EqualTo(6.0).Within(Drift(1)));
            Assert.That(times[3], Is.EqualTo(9.0).Within(Drift(2)));
        }

        [Test]
        public void TakeReloadRequest_IsTrue_WhenReturningFromTalkMovesToTheLastClip()
        {
            var driver = new Driver(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), Talk2());
            var times = RequestTimes(driver, 1.2);
            driver.Talking = true;
            times.AddRange(RequestTimes(driver, 2.2));
            Assert.That(times, Is.Empty, "前提: まだ最後の位置へ移っていない");

            driver.EndTalking();
            times.AddRange(RequestTimes(driver, 2.5));

            Assert.That(times.Count, Is.EqualTo(1), "話し終えて最後の B へ移り始めたとき " + string.Join(", ", times));
            Assert.That(times[0], Is.EqualTo(2.2).Within(Slack));
        }

        [Test]
        public void TakeReloadRequest_IsTrue_WhenTheOrderReturnsToAPendingListOfOneClip()
        {
            var driver = new Driver(Normal3(), None());
            var times = RequestTimes(driver, 1.2);
            driver.SetClips(Clips(new IdleClip(X, 4f)), None());

            times.AddRange(RequestTimes(driver, 16.0));

            // B→C 7 秒、C→X 12 秒（X は 1 本だけなので最後の位置）、X→X 15 秒
            Assert.That(times.Count, Is.EqualTo(3), string.Join(", ", times));
            Assert.That(times[0], Is.EqualTo(7.0).Within(Drift(1)));
            Assert.That(times[1], Is.EqualTo(12.0).Within(Drift(2)));
            Assert.That(times[2], Is.EqualTo(15.0).Within(Drift(3)));
        }

        [Test]
        public void SetClips_UsesTheReloadedListAtTheNextReturnToFirst_WhenRuntimeReloadsOnRequest()
        {
            // Runtime と同じく、要求が立つたびにフォルダを読み直して SetClips する
            var reloads = new Queue<IdleClip[]>(new[]
            {
                Clips(new IdleClip(X, 4f), new IdleClip(Y, 4f)),
                Clips(new IdleClip(Z, 4f)),
                Clips(new IdleClip(Z, 4f)),
                Clips(new IdleClip(Z, 4f)),
            });
            var driver = new Driver(Normal3(), None());
            var snapshots = new List<Snapshot>();
            while (driver.Elapsed < 20.5)
            {
                snapshots.Add(driver.Step(Frame));
                if (driver.Sequence.TakeReloadRequest())
                {
                    driver.SetClips(reloads.Dequeue(), None());
                }
            }

            // 7 秒（C へ）に読み直し → 12 秒に X、15 秒（Y へ）に読み直し → 18 秒に Z
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { A, B, C, X, Y, Z }), string.Join(",", IncomingOrder(snapshots)));
        }

        // ---- 一覧が空の間の読み直し ----

        // 一覧が空の間に読み直しを求める間隔（IdleSequence.EmptyReloadSeconds）
        private const double EmptyReload = 5.0;

        /// <summary>frame ずつ進め、要求が立ったときの時刻を返す。eachFrame は毎フレームの Step の前に呼ぶ。</summary>
        private static List<double> RequestTimes(Driver driver, double until, float frame, Action<Driver> eachFrame)
        {
            var times = new List<double>();
            while (driver.Elapsed < until - 1e-6)
            {
                if (eachFrame != null)
                {
                    eachFrame(driver);
                }

                driver.Step(frame);
                if (driver.Sequence.TakeReloadRequest())
                {
                    times.Add(driver.Elapsed);
                }
            }

            return times;
        }

        private static void AssertEveryEmptyReload(List<double> times, double first, int count, float frame)
        {
            Assert.That(times.Count, Is.EqualTo(count), "要求の時刻: " + string.Join(", ", times));
            for (var i = 0; i < count; i++)
            {
                // 数え直すたびに 1 フレームまでずれてよい
                Assert.That(times[i], Is.EqualTo(first + i * EmptyReload).Within(frame * (i + 2)), "要求の時刻: " + string.Join(", ", times));
            }
        }

        [Test]
        public void Constants_EmptyReloadSecondsIsFive()
        {
            // 定数がないときもほかのテストのコンパイルを止めないように、リフレクションで読む
            var field = typeof(IdleSequence).GetField("EmptyReloadSeconds", BindingFlags.Public | BindingFlags.Static);

            Assert.That(field, Is.Not.Null, "IdleSequence.EmptyReloadSeconds がない");
            Assert.That(field.IsLiteral, Is.True, "const");
            Assert.That(field.GetValue(null), Is.EqualTo(5f));
        }

        [Test]
        public void TakeReloadRequest_IsTrueEveryFiveSeconds_WhileNoClipsWereEverGiven()
        {
            var driver = new Driver();

            var early = RequestTimes(driver, 4.99, FineFrame, null);
            Assert.That(early, Is.Empty, "4.99 秒では立たない: " + string.Join(", ", early));

            var times = RequestTimes(driver, 15.5, FineFrame, null);
            AssertEveryEmptyReload(times, 5.0, 3, FineFrame);
        }

        [Test]
        public void TakeReloadRequest_IsFalseUntilTheNextFiveSeconds_AfterItWasTaken()
        {
            var driver = new Driver();
            driver.RunTo(5.1);
            Assert.That(driver.Sequence.TakeReloadRequest(), Is.True, "前提: 5 秒で立つ");

            driver.RunTo(9.9);
            Assert.That(driver.Sequence.TakeReloadRequest(), Is.False, "取ったあとは次の 5 秒まで立たない");
            driver.RunTo(10.1);
            Assert.That(driver.Sequence.TakeReloadRequest(), Is.True, "10 秒");
        }

        [Test]
        public void TakeReloadRequest_KeepsCountingEmptyTime_WhileAwayFromIdle()
        {
            var driver = new Driver();
            driver.Idle = false;

            var times = RequestTimes(driver, 15.5, Frame, null);

            AssertEveryEmptyReload(times, 5.0, 3, Frame);
        }

        [Test]
        public void TakeReloadRequest_CountsCappedDeltas_WhileEmpty()
        {
            var driver = new Driver();

            // 1 回 1 秒の Advance は 1/15 秒として数える。74 回 = 4.93 秒、76 回 = 5.07 秒
            var times = new List<int>();
            for (var i = 1; i <= 76; i++)
            {
                driver.Step(1f);
                if (driver.Sequence.TakeReloadRequest())
                {
                    times.Add(i);
                }
            }

            Assert.That(times, Is.EqualTo(new[] { 75 }).Or.EqualTo(new[] { 76 }), "頭打ち後の delta の和で 5 秒: " + string.Join(", ", times));
        }

        [Test]
        public void TakeReloadRequest_DoesNotRestartTheCount_WhenEmptyListsAreGivenAgain()
        {
            var driver = new Driver();
            var lists = new[] { None(), null, Clips(new IdleClip(X, 0f), new IdleClip(Y, float.NaN)), Clips(new IdleClip(Z, -1f)) };
            var calls = 0;

            // 0.5 秒ごとに、空・null・不正な長さだけの一覧を渡す（話す間用は空でない）
            var times = RequestTimes(driver, 15.5, Frame, d =>
            {
                if (Math.Abs(d.Elapsed / 0.5 - Math.Round(d.Elapsed / 0.5)) < 1e-4)
                {
                    d.SetClips(lists[calls++ % lists.Length], Talk2());
                }
            });

            Assert.That(calls, Is.GreaterThan(20), "前提: 何度も渡した");
            AssertEveryEmptyReload(times, 5.0, 3, Frame);
            Assert.That(driver.Now.Count, Is.EqualTo(0), "空のままなので本家だけ");
        }

        [Test]
        public void TakeReloadRequest_CountsFromTheMomentTheListBecameEmptyAtTheReturnToFirst()
        {
            var driver = new Driver();
            var empty = RequestTimes(driver, 3.0, Frame, null);
            Assert.That(empty, Is.Empty, "前提");

            // 3 秒に A・B を渡す。A→B（最後の位置）は 6 秒、B から最初に戻るのは 10 秒で、そこで空になる
            driver.SetClips(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), None());
            driver.SetClips(None(), None());
            var times = RequestTimes(driver, 21.0, Frame, null);

            Assert.That(times.Count, Is.EqualTo(3), "6 秒（B へ）、15 秒・20 秒（空になってから 5 秒ごと）: " + string.Join(", ", times));
            Assert.That(times[0], Is.EqualTo(6.0).Within(Drift(0)), string.Join(", ", times));
            // 空になった 10 秒から数える。本家へ戻る 1 秒の間も数える
            Assert.That(times[1], Is.EqualTo(15.0).Within(Drift(2)), string.Join(", ", times));
            Assert.That(times[2], Is.EqualTo(20.0).Within(Drift(3)), string.Join(", ", times));
        }

        [Test]
        public void TakeReloadRequest_StopsTheEmptyCount_WhenANonEmptyListArrives()
        {
            var driver = new Driver();
            RequestTimes(driver, 3.0, Frame, null);

            // B は 300 秒なので、6 秒（B へ移る）のあとはしばらく通常の要求も立たない
            driver.SetClips(Clips(new IdleClip(A, 4f), new IdleClip(B, 300f)), None());
            var times = RequestTimes(driver, 30.0, Frame, null);

            Assert.That(times.Count, Is.EqualTo(1), "空でなくなったら空の間の要求は立たない: " + string.Join(", ", times));
            Assert.That(times[0], Is.EqualTo(6.0).Within(Drift(0)));
        }

        // ---- 本家へ戻っていく途中の SetClips ----

        [Test]
        public void SetClips_CrossFadesFromTheFadingClipToTheNewFirstClip_WhenAListArrivesWhileOwnIdleFadesOut()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), None());
            driver.SetClips(None(), None());
            var fading = driver.RunTo(7.5);
            AssertTokens(fading, new[] { B }, "前提: 一覧が空になり B が本家へ戻っていく");
            Assert.That(fading.Own, Is.EqualTo(0.5f).Within(Slack), "前提 " + fading);

            driver.SetClips(Clips(new IdleClip(X, 4f), new IdleClip(Y, 4f)), None());
            // 寄与の跳びは Driver.Step が毎フレーム確かめる
            var mixing = driver.Run(0.5f);

            Assert.That(mixing.All(s => s.Has(B)), Is.True, "混ぜの間は古いクリップが層に残る " + mixing.FirstOrDefault(s => !s.Has(B)));
            var half = mixing.Last();
            AssertTokens(half, new[] { B, X }, "古い B から新しい 0 番目の X へ");
            Assert.That(half.Of(X), Is.EqualTo(0.5f).Within(2f * Slack), "1 秒で混ぜる " + half);
            Assert.That(half.Own, Is.EqualTo(1f).Within(2f * Slack), "自作の待機の度合いは上がり直す " + half);

            var done = driver.Run(0.55f).Last();
            AssertTokens(done, new[] { X }, "混ぜ終えた");
        }

        // ---- 話す間からの戻りと位置 ----

        [Test]
        public void Advance_ReturnsToTheClipAfterThePositionBeforeTalking_WhenTalkingResumesDuringTheReturnAndEndsAgain()
        {
            // B の途中で話し、C へ戻る途中でまた話し始め、T だけになってから話し終える
            var driver = Started();
            AssertTokens(driver.RunTo(4.5), new[] { B }, "前提");
            driver.Talking = true;
            driver.Run(1f);
            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { T, C }, "前提: B の次の C へ戻る途中");
            driver.Talking = true;
            AssertTokens(driver.Run(0.5f).Last(), new[] { T }, "前提: 戻りを取り消して T だけ");

            driver.EndTalking();
            var now = driver.Run(0.3f).Last();

            AssertTokens(now, new[] { T, C }, "話し始める前の位置（B）の次の C へ。1 本飛ばさない");
        }

        [Test]
        public void Advance_PlaysTheReturnClipOnceAndMovesOn_WhenTalkingEndsAgainWhileTheReturnIsBeingTakenBack()
        {
            var driver = Started();
            driver.RunTo(4.5);
            driver.Talking = true;
            driver.Run(1f);
            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { T, C }, "前提: C へ戻る途中");
            driver.Talking = true;
            var takingBack = driver.Run(0.05f).Last();
            AssertTokens(takingBack, new[] { T, C }, "前提: 取り消しの途中");

            driver.EndTalking();
            var snapshots = driver.CollectTo(12.5);

            // C は 5.5 秒に始まり、C の Time 5 秒（10.5 秒）で次へ。最後の次は最初の A
            Assert.That(IncomingOrder(snapshots), Is.EqualTo(new[] { C, A }), "C を 2 回再生しない: " + string.Join(",", IncomingOrder(snapshots)));
        }

        [Test]
        public void Advance_ReturnsToTheFirstClipOfTheNewList_WhenTalkingResumesDuringAReturnThatWrappedToANewList()
        {
            const long W = 24;
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(W, 4f), new IdleClip(X, 4f), new IdleClip(Y, 4f), new IdleClip(Z, 4f)), Talk2());
            AssertTokens(driver.RunTo(8.5), new[] { C }, "前提: 最後の C");
            driver.Talking = true;
            driver.Run(1f);

            // 話し終えると最初に戻り、保留の 4 本の一覧になる
            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { T, W }, "前提: 新しい一覧の最初の W へ戻る途中");
            driver.Talking = true;
            AssertTokens(driver.Run(0.5f).Last(), new[] { T }, "前提: 戻りを取り消して T だけ");

            driver.EndTalking();
            var now = driver.Run(0.3f).Last();

            AssertTokens(now, new[] { T, W }, "新しい一覧で、戻る前の位置は最後（Z）なので、その次の W へ");
        }

        [Test]
        public void Advance_LoopsTheTalkClipIntoItselfOverOneSecond()
        {
            var driver = TalkingOnT();

            // T は 1.2 秒に始まり、T の Time 5 秒（6.2 秒）から自分自身へ混ぜる
            var now = driver.RunTo(6.2 + 0.25);

            AssertTokens(now, new[] { T, T }, "前提");
            Assert.That(now.Contributions[1], Is.EqualTo(0.25f).Within(Slack), "入ってくる T は 1 秒で混ぜる " + now);
        }

        [Test]
        public void Advance_FinishesTheCrossFadeAtOneSecondSpeed_WhenTalkingEndsBeforeTheTalkClipIsReached()
        {
            var driver = Started();
            var mid = driver.RunTo(3.5);
            AssertTokens(mid, new[] { A, B }, "前提");
            driver.Talking = true;
            var hurried = driver.Run(0.1f).Last();
            Assert.That(hurried.Of(B) - mid.Of(B), Is.EqualTo(0.2f).Within(Tolerance), "前提: 話し始めて 0.5 秒の速さ " + hurried);

            driver.EndTalking();
            var snapshots = driver.Run(0.2f);
            var now = snapshots.Last();

            Assert.That(now.Of(B) - hurried.Of(B), Is.EqualTo(0.2f).Within(Tolerance), "話し終えたら 1 秒の速さに戻る " + now);
            Assert.That(snapshots.Any(s => s.Has(T)), Is.False, "T へは移らない");
        }

        // ---- 話し始めの話す間用のクリップ ----

        private const long Rest = 40;

        /// <summary>通常の一覧は 4 秒の Rest 1 本だけ。Rest は Time 3 秒で自分自身へ混ぜ、そのたびに最初に戻る（保留の一覧はそこで入れ替わる）。</summary>
        private static IdleClip[] RestOnly()
        {
            return new[] { new IdleClip(Rest, 4f) };
        }

        /// <summary>負の Token は乱数で作る通常のクリップ。</summary>
        private static bool IsNormalToken(long token)
        {
            return token < 0 || token == A || token == B || token == C || token == Rest;
        }

        private static IdleClip Talk(long token, string name, float length = 6f)
        {
            return new IdleClip(token, length, name);
        }

        /// <summary>長さ 6 秒の話す間用の一覧。Token は first から一覧の順に 1 ずつ振る。</summary>
        private static IdleClip[] Named(long first, params string[] names)
        {
            var clips = new IdleClip[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                clips[i] = Talk(first + i, names[i]);
            }

            return clips;
        }

        private static string ShowName(string name)
        {
            return name == null ? "null" : "\"" + name + "\"";
        }

        private static string ShowClips(IEnumerable<IdleClip> clips)
        {
            return "[" + string.Join(", ", clips.Select(c => c.Token + ":" + ShowName(c.Name) + (c.Length > 0f && !float.IsInfinity(c.Length) ? "" : "（長さ " + c.Length + "）"))) + "]";
        }

        /// <summary>
        /// 話し始めに使う話す間用のクリップ: 有効なもの（長さが正で有限）のうち、名前が前回の話し始めの名前より IdleFiles.NameOrder で
        /// 大きいものの中で最小のもの。そのようなものがないか、まだ話し始めていなければ名前が最小のもの。同じ名前が複数なら一覧で先のもの。
        /// 有効なものがなければ null。
        /// </summary>
        private static IdleClip? ExpectedTalkStart(IdleClip[] talk, bool startedBefore, string previous)
        {
            IdleClip? smallest = null;
            IdleClip? next = null;
            foreach (var clip in talk)
            {
                if (!(clip.Length > 0f) || float.IsInfinity(clip.Length))
                {
                    continue;
                }

                if (smallest == null || IdleFiles.NameOrder.Compare(clip.Name, smallest.Value.Name) < 0)
                {
                    smallest = clip;
                }

                if (startedBefore && IdleFiles.NameOrder.Compare(clip.Name, previous) > 0 && (next == null || IdleFiles.NameOrder.Compare(clip.Name, next.Value.Name) < 0))
                {
                    next = clip;
                }
            }

            return next ?? smallest;
        }

        /// <summary>
        /// 通常のクリップだけが見えている状態から話し始め、1.2 秒（通常のクリップ同士の混ぜの途中でも、それを終えて話す間用へ移り終える長さ）進める。
        /// 話す間用のクリップだけになったらその Token を、話す間用のクリップが一度も見えなければ null を返す。
        /// </summary>
        private static long? StartTalking(Driver driver)
        {
            var before = driver.Now;
            Assert.That(before.Tokens.All(IsNormalToken), Is.True, "前提: 通常のクリップだけ " + before);

            driver.Talking = true;
            var snapshots = driver.Run(1.2f);

            var talkTokens = snapshots.SelectMany(s => s.Tokens).Where(t => !IsNormalToken(t)).Distinct().ToList();
            Assert.That(talkTokens.Count, Is.LessThanOrEqualTo(1), "話し始めてから移り終えるまでに話す間用のクリップが替わった: " + string.Join(",", talkTokens));
            if (talkTokens.Count == 0)
            {
                return null;
            }

            var now = snapshots.Last();
            AssertTokens(now, new[] { talkTokens[0] }, "話す間用のクリップへ移り終えた");
            return talkTokens[0];
        }

        private static long TalkClipAtStart(Driver driver)
        {
            var token = StartTalking(driver);
            Assert.That(token.HasValue, Is.True, "話す間用のクリップへ移らない " + driver.Now);
            return token.Value;
        }

        /// <summary>
        /// 話し終え（EndTalking か、保持が切れるまで待つ）、通常のクリップだけに戻るまで進める。話す間用が自分自身へ混ぜている途中なら
        /// それを終えてから戻るので、待つのは 2.5 秒（その混ぜの 1 秒 + 戻りの 1 秒 + 余裕）まで。
        /// </summary>
        private static void StopTalking(Driver driver, bool viaEndTalking)
        {
            if (viaEndTalking)
            {
                driver.EndTalking();
            }
            else
            {
                driver.Talking = false;
                driver.Run(IdleSequence.TalkHoldSeconds + 0.05f);
            }

            var back = driver.Step(Frame);
            for (var waited = Frame; waited < 2.5f && !(back.Count > 0 && back.Tokens.All(IsNormalToken)); waited += Frame)
            {
                back = driver.Step(Frame);
            }

            Assert.That(back.Count, Is.GreaterThan(0), "前提: 自作の待機が見えている " + back);
            Assert.That(back.Tokens.All(IsNormalToken), Is.True, "前提: 通常のクリップだけに戻った " + back);
        }

        /// <summary>話し始めと話し終えを count 回繰り返し、話し始めごとに移った話す間用のクリップの Token を返す。</summary>
        private static List<long> TalkStarts(Driver driver, int count, bool viaEndTalking = true)
        {
            var tokens = new List<long>();
            for (var i = 0; i < count; i++)
            {
                tokens.Add(TalkClipAtStart(driver));
                StopTalking(driver, viaEndTalking);
            }

            return tokens;
        }

        /// <summary>
        /// 話していない間に話す間用の一覧を読み直し（通常の一覧は RestOnly のまま）、最初に戻って入れ替わるまで進める。
        /// Rest は 1 本だけなので、最初に戻るたびに読み直しの要求が立つ。
        /// </summary>
        private static void Reload(Driver driver, IdleClip[] before, IdleClip[] after)
        {
            driver.Sequence.TakeReloadRequest();
            driver.SetClips(RestOnly(), after);
            var now = driver.Run(3.5f).Last();

            Assert.That(driver.Sequence.TakeReloadRequest(), Is.True, "前提: 最初に戻って一覧が入れ替わった");
            foreach (var clip in before.Where(b => after.All(a => a.Token != b.Token)))
            {
                Assert.That(driver.Sequence.IsInUse(clip.Token), Is.False, "前提: 前の話す間用の一覧はもう使っていない（Token " + clip.Token + "）");
            }

            Assert.That(now.Tokens.All(IsNormalToken), Is.True, "前提: 通常のクリップだけ " + now);
        }

        /// <summary>tr-TR が実際に i / I をトルコ語の規則で扱うときだけ、その文化で動かす。扱わない環境では SKIP。</summary>
        private static void WithTurkishCulture(Action action)
        {
            CultureInfo turkish;
            try
            {
                turkish = new CultureInfo("tr-TR");
            }
            catch (CultureNotFoundException)
            {
                Assert.Ignore("tr-TR が無い環境");
                return;
            }

            if (turkish.TextInfo.ToUpper('i') != (char)0x0130 || string.Compare("i", "I", turkish, CompareOptions.IgnoreCase) == 0)
            {
                Assert.Ignore("tr-TR がトルコ語の大文字・小文字の規則で動かない環境");
            }

            var thread = Thread.CurrentThread;
            var culture = thread.CurrentCulture;
            var uiCulture = thread.CurrentUICulture;
            try
            {
                thread.CurrentCulture = turkish;
                thread.CurrentUICulture = turkish;
                action();
            }
            finally
            {
                thread.CurrentCulture = culture;
                thread.CurrentUICulture = uiCulture;
            }
        }

        [Test]
        public void IdleClip_KeepsTokenLengthAndName()
        {
            var clip = new IdleClip(long.MinValue, 0.25f, "Idle_Talk-01");
            var unnamed = new IdleClip(7, 1f, null);

            Assert.That(clip.Token, Is.EqualTo(long.MinValue));
            Assert.That(clip.Length, Is.EqualTo(0.25f));
            Assert.That(clip.Name, Is.EqualTo("Idle_Talk-01"), "綴りのまま");
            Assert.That(unnamed.Name, Is.Null, "名前は null でもよい");
        }

        [Test]
        public void IdleClip_HasNoName_WhenMadeFromTokenAndLength()
        {
            Assert.That(new IdleClip(7, 1f).Name, Is.Null);
            Assert.That(default(IdleClip).Name, Is.Null);
        }

        [Test]
        public void Advance_StartsWithTheTalkClipWhoseNameIsSmallest_WhenTalkingStartsForTheFirstTime()
        {
            var talk = Named(100, "idle_c", "idle_a", "idle_b");
            Assert.That(IdleFiles.NameOrder.Compare("idle_a", "idle_b"), Is.LessThan(0), "前提");
            Assert.That(IdleFiles.NameOrder.Compare("idle_a", "idle_c"), Is.LessThan(0), "前提");
            var driver = Started(RestOnly(), talk);

            var token = TalkClipAtStart(driver);

            Assert.That(token, Is.EqualTo(101L), "一覧の順ではなく名前が最小の idle_a " + ShowClips(talk));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Advance_MovesToTheTalkClipWithTheNextNameAndWrapsToTheSmallest_EachTimeTalkingStartsAgain(bool viaEndTalking)
        {
            // 一覧の順は b, c, a。名前の順は a(102), b(100), c(101)
            var talk = Named(100, "idle_b", "idle_c", "idle_a");
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, 7, viaEndTalking);

            Assert.That(starts, Is.EqualTo(new[] { 102L, 100L, 101L, 102L, 100L, 101L, 102L }), "話し終え方 " + (viaEndTalking ? "EndTalking" : "保持が切れる") + ": " + string.Join(",", starts));
        }

        [Test]
        public void Advance_UsesTheOnlyValidTalkClipEveryTime_WhenTheTalkListHasOneValidClip()
        {
            var talk = Clips(Talk(100, "idle_a", 0f), Talk(101, "idle_b"), Talk(102, "idle_c", float.NaN));
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, 4);

            Assert.That(starts, Is.EqualTo(new[] { 101L, 101L, 101L, 101L }), "有効なのは idle_b だけ: " + string.Join(",", starts));
        }

        [Test]
        public void Advance_SkipsTalkClipsWhoseLengthIsNotPositiveFinite_WhenChoosingTheNextName()
        {
            var talk = Clips(Talk(100, "idle_a"), Talk(101, "idle_b", 0f), Talk(102, "idle_c"), Talk(103, "idle_d", float.PositiveInfinity), Talk(104, "idle_e", -1f));
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, 4);

            Assert.That(starts, Is.EqualTo(new[] { 100L, 102L, 100L, 102L }), "長さが正で有限でないものは除いてから選ぶ " + ShowClips(talk) + ": " + string.Join(",", starts));
        }

        [Test]
        public void Advance_UsesTheEarlierOfTalkClipsWithTheSameName_IgnoringLetterCase()
        {
            // idle_a は 101 と 104、idle_b は 100・102・103 が同じ名前。それぞれ一覧で先のものだけを使う
            var talk = Clips(Talk(100, "idle_b"), Talk(101, "idle_a"), Talk(102, "IDLE_B"), Talk(103, "idle_b"), Talk(104, "Idle_A"));
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, 4);

            Assert.That(starts, Is.EqualTo(new[] { 101L, 100L, 101L, 100L }), ShowClips(talk) + ": " + string.Join(",", starts));
        }

        [Test]
        public void Advance_GoesThroughNullAndEmptyNamesInTheComparerOrder()
        {
            var talk = Named(100, "idle_a", "", null);
            Assert.That(IdleFiles.NameOrder.Compare(null, ""), Is.LessThan(0), "前提: null は空の名前より前");
            Assert.That(IdleFiles.NameOrder.Compare("", "idle_a"), Is.LessThan(0), "前提");
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, 5);

            Assert.That(starts, Is.EqualTo(new[] { 102L, 101L, 100L, 102L, 101L }), "null、空、idle_a の順で、最後の次は最初: " + string.Join(",", starts));
        }

        [Test]
        public void Advance_UsesTheFirstTalkClipEveryTime_WhenAllTalkClipsHaveTheSameName()
        {
            foreach (var talk in new[] { Talk2(), Clips(Talk(100, "idle_x"), Talk(101, "IDLE_X")) })
            {
                var driver = Started(RestOnly(), talk);

                var starts = TalkStarts(driver, 4);

                var first = talk[0].Token;
                Assert.That(starts, Is.EqualTo(new[] { first, first, first, first }), "名前がどれも同じ（どれも null を含む）なら一覧で先のもの " + ShowClips(talk) + ": " + string.Join(",", starts));
            }
        }

        [Test]
        public void Advance_LoopsTheTalkClipChosenAtTheStartIntoItself_AndDoesNotMoveToTheNextNameWhileTalking()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(101L), "前提: 2 回目の話し始めは idle_b");

            var talking = driver.Run(12f);

            Assert.That(talking.All(s => s.Tokens.All(t => t == 101L)), Is.True, "話している間は idle_b だけ " + talking.FirstOrDefault(s => s.Tokens.Any(t => t != 101L)));
            Assert.That(talking.Any(s => s.Count == 2), Is.True, "前提: idle_b から idle_b へ混ぜた");
            StopTalking(driver, true);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(102L), "繰り返しは話し始めではないので次は idle_c");
        }

        [Test]
        public void Advance_KeepsTheTalkClip_WhenTheNextSentenceStartsWithinTheHold()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(101L), "前提: 2 回目の話し始めは idle_b");

            // 次の文は 2 回（名前は 3 つなので、次の文ごとに進むと次の話し始めが idle_c にならない）
            var snapshots = new List<Snapshot>();
            for (var sentence = 0; sentence < 2; sentence++)
            {
                driver.Talking = false;
                snapshots.AddRange(driver.Run(1f));
                driver.Talking = true;
                snapshots.AddRange(driver.Run(1f));
            }

            Assert.That(snapshots.All(s => s.Tokens.All(t => t == 101L)), Is.True, "保持の間に次の文が来ても idle_b のまま " + snapshots.FirstOrDefault(s => s.Tokens.Any(t => t != 101L)));
            StopTalking(driver, true);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(102L), "保持の間の次の文は話し始めではないので次は idle_c");
        }

        [Test]
        public void Advance_KeepsTheTalkClipAndDoesNotMoveToTheNextName_WhenTalkingResumesDuringTheReturn()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(100L), "前提");
            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { 100L, Rest }, "前提: Rest へ戻る途中");

            driver.Talking = true;
            var resumed = driver.Run(1f);

            Assert.That(resumed.All(s => s.Tokens.All(t => t == 100L || t == Rest)), Is.True, "ほかの話す間用へ替えない " + resumed.FirstOrDefault(s => s.Tokens.Any(t => t != 100L && t != Rest)));
            AssertTokens(resumed.Last(), new[] { 100L }, "戻りを取り消して idle_a だけ");
            StopTalking(driver, true);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(101L), "戻りの取り消しは話し始めではないので次は idle_b");
        }

        [Test]
        public void Advance_LoopsTheTalkClipChosenAtTheStart_WhenTalkingResumesDuringAReturnThatReplacedTheTalkList()
        {
            // A は Time 3 秒（3 秒）から B へ混ぜ、4 秒で B だけになる。B は最後なので、次に最初に戻るところで保留が入れ替わる
            var normal = Clips(new IdleClip(A, 4f), new IdleClip(B, 5f));
            var talk = Named(100, "idle_a", "idle_b");
            var reloaded = Named(200, "idle_c", "idle_d");
            var driver = Started(normal, talk);
            driver.SetClips(normal, reloaded);
            AssertTokens(driver.RunTo(4.5), new[] { B }, "前提: 保留のまま B へ移った");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(100L), "前提: 話し始めは今の一覧の idle_a");

            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { 100L, A }, "前提: 最初の A へ戻る途中");
            Assert.That(driver.Sequence.IsInUse(101L), Is.False, "前提: 最初に戻って一覧が入れ替わった（idle_b はもう使っていない）");
            Assert.That(driver.Sequence.IsInUse(200L), Is.True, "前提: 新しい一覧を使っている");
            driver.Talking = true;
            AssertTokens(driver.Run(0.5f).Last(), new[] { 100L }, "前提: 戻りを取り消して idle_a だけ");

            // idle_a（6 秒）は Time 2 秒。Time 5 秒から自分自身へ混ぜるので、9 秒の間に 2 回繰り返す
            var loops = 0;
            var previousCount = 1;
            var frames = (int)Math.Round(9.0 / Frame);
            for (var i = 0; i < frames; i++)
            {
                var now = driver.Step(Frame);
                var at = "t=" + driver.Elapsed.ToString("0.###") + " " + now;
                Assert.That(now.Tokens.All(t => t == 100L), Is.True, "話している間は、一覧からなくなっても話し始めの idle_a を繰り返す " + at);
                Assert.That(driver.Sequence.IsInUse(100L), Is.True, "写っている間は使用中 " + at);
                if (now.Count == 2 && previousCount == 1)
                {
                    loops++;
                }

                previousCount = now.Count;
            }

            Assert.That(loops, Is.GreaterThanOrEqualTo(2), "前提: idle_a から idle_a へ 2 回以上混ぜた");
            StopTalking(driver, true);
            Assert.That(driver.Sequence.IsInUse(100L), Is.False, "写らなくなり、一覧にもない");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(200L), "新しい一覧で、前回の idle_a の次の idle_c");
        }

        [Test]
        public void Advance_LoopsTheTalkClipChosenAtTheStart_WhenTalkingResumesDuringAReturnThatSwappedInAListKeepingItsToken()
        {
            // A は Time 3 秒（3 秒）から B へ混ぜ、4 秒で B だけになる。B は最後なので、次に最初に戻るところで保留が入れ替わる
            var normal = Clips(new IdleClip(A, 4f), new IdleClip(B, 5f));
            var talk = Named(100, "idle_a", "idle_b");
            // Runtime は変わっていないファイルに同じ Token を使う。idle_a はそのままで、その前と後ろに名前が増え、idle_b は消えた
            var reloaded = new[] { Talk(99, "idle_0"), Talk(100, "idle_a"), Talk(201, "idle_z") };
            var driver = Started(normal, talk);
            driver.SetClips(normal, reloaded);
            AssertTokens(driver.RunTo(4.5), new[] { B }, "前提: 保留のまま B へ移った");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(100L), "前提: 話し始めは今の一覧の idle_a");

            driver.EndTalking();
            AssertTokens(driver.Run(0.3f).Last(), new[] { 100L, A }, "前提: 最初の A へ戻る途中");
            Assert.That(driver.Sequence.IsInUse(101L), Is.False, "前提: 最初に戻って一覧が入れ替わった（idle_b はもう使っていない）");
            Assert.That(driver.Sequence.IsInUse(201L), Is.True, "前提: 新しい一覧を使っている");
            driver.Talking = true;
            AssertTokens(driver.Run(0.5f).Last(), new[] { 100L }, "前提: 戻りを取り消して idle_a だけ");

            // idle_a（6 秒）は Time 2 秒。Time 5 秒から自分自身へ混ぜるので、9 秒の間に 2 回繰り返す
            var loops = 0;
            var previousCount = 1;
            var frames = (int)Math.Round(9.0 / Frame);
            for (var i = 0; i < frames; i++)
            {
                var now = driver.Step(Frame);
                var at = "t=" + driver.Elapsed.ToString("0.###") + " " + now;
                Assert.That(now.Tokens.All(t => t == 100L), Is.True, "話している間は、入れ替わった一覧の前後の名前へ移らず話し始めの idle_a を繰り返す " + at);
                if (now.Count == 2 && previousCount == 1)
                {
                    loops++;
                }

                previousCount = now.Count;
            }

            Assert.That(loops, Is.GreaterThanOrEqualTo(2), "前提: idle_a から idle_a へ 2 回以上混ぜた");
            StopTalking(driver, true);
            Assert.That(driver.Sequence.IsInUse(100L), Is.True, "idle_a は新しい一覧にもある");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(201L), "繰り返しは話し始めではないので、前回の idle_a の次の idle_z");
        }

        [Test]
        public void Advance_MovesToTheTalkClipWithTheNextName_WhenTalkingStartsDuringACrossFadeOfNormalClips()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(Normal3(), talk);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(100L), "前提");
            StopTalking(driver, true);
            // B は 2.4 秒に始まり、B の Time 4 秒（6.4 秒）から C へ混ぜる
            AssertTokens(driver.RunTo(6.7), new[] { B, C }, "前提: B から C へ混ぜている途中");

            var token = TalkClipAtStart(driver);

            Assert.That(token, Is.EqualTo(101L), "混ぜを終えてから話す間用へ移るのも話し始めなので idle_b");
        }

        [Test]
        public void Advance_KeepsTheTalkClip_WhenLeavingIdleAndComingBackWhileTalking()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提");
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(101L), "前提: 2 回目の話し始めは idle_b");

            driver.Idle = false;
            var away = driver.Run(1f);
            driver.Idle = true;
            var back = driver.Run(1f);

            Assert.That(away.Last().Count, Is.EqualTo(0), "前提: 本家だけ");
            Assert.That(away.Concat(back).All(s => s.Tokens.All(t => t == 101L)), Is.True, "待機の出入りの間も idle_b のまま");
            AssertTokens(back.Last(), new[] { 101L }, "待機に戻っても idle_b のまま");
            StopTalking(driver, true);
            Assert.That(TalkClipAtStart(driver), Is.EqualTo(102L), "待機の出入りでは進まないので次は idle_c");
        }

        [Test]
        public void Advance_StartsAgainFromTheSmallestName_WhenTheSequenceIsCreatedAgain()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var first = Started(RestOnly(), talk);
            Assert.That(TalkStarts(first, 2), Is.EqualTo(new[] { 100L, 101L }), "前提");

            var again = Started(RestOnly(), talk);

            Assert.That(TalkClipAtStart(again), Is.EqualTo(100L), "作り直したら最初から");
        }

        [Test]
        public void Advance_MovesToTheNameAfterTheLastUsedOne_WhenTheLastUsedNameIsGoneAfterAReload()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 2), Is.EqualTo(new[] { 100L, 101L }), "前提: 前回は idle_b");

            var reloaded = Named(200, "idle_a", "idle_c");
            Reload(driver, talk, reloaded);
            var starts = TalkStarts(driver, 2);

            Assert.That(starts, Is.EqualTo(new[] { 201L, 200L }), "idle_b がなくなってもその次の idle_c、そのあと最初の idle_a: " + string.Join(",", starts));
        }

        [Test]
        public void Advance_WrapsToTheSmallestName_WhenTheLastUsedNameWasTheLargestAndIsGoneAfterAReload()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 3), Is.EqualTo(new[] { 100L, 101L, 102L }), "前提: 前回は最後の idle_c");

            var reloaded = Named(200, "idle_a", "idle_b");
            Reload(driver, talk, reloaded);

            Assert.That(TalkClipAtStart(driver), Is.EqualTo(200L), "idle_c より大きい名前がないので最初の idle_a");
        }

        [Test]
        public void Advance_MovesToTheNameAfterTheLastUsedOne_WhenNamesAreAddedBeforeAndAfterItByAReload()
        {
            var talk = Named(100, "idle_b", "idle_d");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提: 前回は idle_b（一覧の 0 番目）");

            var reloaded = Named(200, "idle_a", "idle_b", "idle_c", "idle_d");
            Reload(driver, talk, reloaded);
            var starts = TalkStarts(driver, 3);

            Assert.That(starts, Is.EqualTo(new[] { 202L, 203L, 200L }), "一覧の位置ではなく名前で idle_b の次の idle_c から: " + string.Join(",", starts));
        }

        [Test]
        public void Advance_MovesToANameAddedAfterTheLargest_WhenTheLargestWasUsedLast()
        {
            var talk = Named(100, "idle_a", "idle_b");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 2), Is.EqualTo(new[] { 100L, 101L }), "前提: 前回は最後の idle_b");

            var reloaded = Named(200, "idle_a", "idle_b", "idle_c");
            Reload(driver, talk, reloaded);
            var starts = TalkStarts(driver, 2);

            Assert.That(starts, Is.EqualTo(new[] { 202L, 200L }), "増えた idle_c が idle_b の次: " + string.Join(",", starts));
        }

        [Test]
        public void Advance_TreatsTheLastUsedNameInAnotherLetterCaseAsTheSameName_AfterAReload()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 2), Is.EqualTo(new[] { 100L, 101L }), "前提: 前回は idle_b");

            var upper = Named(200, "idle_a", "IDLE_B", "IDLE_C");
            Reload(driver, talk, upper);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 202L }), "IDLE_B は idle_b と同じ名前なので、その次の IDLE_C");

            var mixed = Named(300, "idle_a", "Idle_C");
            Reload(driver, upper, mixed);

            Assert.That(TalkClipAtStart(driver), Is.EqualTo(300L), "Idle_C は前回の IDLE_C と同じ名前で、より大きい名前がないので最初の idle_a");
        }

        [Test]
        public void Advance_ContinuesAfterTheLastUsedName_WhenTalkClipsComeBackAfterTheTalkListWasEmpty()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提: 前回は idle_a");

            Reload(driver, talk, None());
            Assert.That(StartTalking(driver), Is.Null, "前提: 話す間用がない間は通常のクリップのまま");
            StopTalking(driver, true);

            var reloaded = Named(200, "idle_a", "idle_b", "idle_c");
            Reload(driver, None(), reloaded);

            Assert.That(TalkClipAtStart(driver), Is.EqualTo(201L), "前回の idle_a を覚えたまま次の idle_b");
        }

        [Test]
        public void Advance_ContinuesAfterTheLastUsedName_WhenTheNormalListWasEmptyInBetween()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提: 前回は idle_a");

            driver.SetClips(None(), None());
            Assert.That(driver.Run(4.5f).Last().Count, Is.EqualTo(0), "前提: 一覧が空になって本家だけ");
            var reloaded = Named(200, "idle_a", "idle_b", "idle_c");
            driver.SetClips(RestOnly(), reloaded);
            var shown = driver.Run(1.2f).Last();
            Assert.That(shown.Own, Is.EqualTo(1f).Within(Tolerance), "前提: 空のときの SetClips はすぐ使う " + shown);

            Assert.That(TalkClipAtStart(driver), Is.EqualTo(201L), "前回の idle_a を覚えたまま次の idle_b");
        }

        [Test]
        public void Advance_ContinuesAfterTheLastUsedName_WhenEmptyNormalListsAreGivenAgainWhileTheNormalListIsEmpty()
        {
            var talk = Named(100, "idle_a", "idle_b", "idle_c");
            var driver = Started(RestOnly(), talk);
            Assert.That(TalkStarts(driver, 1), Is.EqualTo(new[] { 100L }), "前提: 前回は idle_a");
            driver.SetClips(None(), talk);
            Assert.That(driver.Run(4.5f).Last().Count, Is.EqualTo(0), "前提: 最初に戻って通常の一覧が空になり、本家だけ");
            driver.Sequence.TakeReloadRequest();

            // Runtime は一覧が空の間、5 秒ごとの要求で読み直して空の通常の一覧を渡す。話す間用の一覧は空のこともある
            var lists = new[] { talk, None(), talk, None(), talk };
            for (var i = 0; i < lists.Length; i++)
            {
                driver.Run(5f);
                Assert.That(driver.Sequence.TakeReloadRequest(), Is.True, "前提: 一覧が空の間の読み直しの要求（" + i + " 回目）");
                driver.SetClips(None(), lists[i]);
                Assert.That(driver.Now.Count, Is.EqualTo(0), "前提: 本家だけのまま（" + i + " 回目）");
            }

            driver.SetClips(RestOnly(), talk);
            var shown = driver.Run(1.2f).Last();
            Assert.That(shown.Own, Is.EqualTo(1f).Within(Tolerance), "前提: 空のときの SetClips はすぐ使う " + shown);

            Assert.That(TalkClipAtStart(driver), Is.EqualTo(101L), "空の一覧の読み直しが何度あっても前回の idle_a を覚えたまま次の idle_b");
        }

        [Test]
        public void Advance_FollowsTheComparerOrder_ForNamesWithSymbolsDigitsAndLetterCase()
        {
            // 大文字に揃えて序数で比べるので、- と数字は英字より前、_ は英字より後ろ
            var order = new[] { "idle", "idle-2", "idle2", "IdleA", "idleb", "idle_", "idle_2" };
            for (var i = 1; i < order.Length; i++)
            {
                Assert.That(IdleFiles.NameOrder.Compare(order[i - 1], order[i]), Is.LessThan(0), "前提: " + order[i - 1] + " < " + order[i]);
            }

            // 一覧は名前の順に並べない。Token は order の位置に 100 を足したもの
            var talk = new[] { 6, 4, 0, 3, 5, 2, 1 }.Select(i => Talk(100 + i, order[i])).ToArray();
            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, order.Length + 1);

            Assert.That(starts, Is.EqualTo(new[] { 100L, 101L, 102L, 103L, 104L, 105L, 106L, 100L }), ShowClips(talk) + ": " + string.Join(",", starts));
        }

        [Test]
        public void Advance_TreatsTheLastUsedNameInAnotherLetterCaseAsTheSameName_WhenCurrentCultureIsTurkish()
        {
            WithTurkishCulture(() =>
            {
                // tr-TR の文化の比較では I と i は別の文字（I は ı の大文字）だが、序数の比較では同じ名前
                var talk = Named(100, "idle_h", "IDLE_I", "idle_j");
                var driver = Started(RestOnly(), talk);
                Assert.That(TalkStarts(driver, 2), Is.EqualTo(new[] { 100L, 101L }), "前提: 前回は IDLE_I");

                var reloaded = Named(200, "idle_h", "idle_i", "idle_j");
                Reload(driver, talk, reloaded);

                Assert.That(TalkClipAtStart(driver), Is.EqualTo(202L), "idle_i は前回の IDLE_I と同じ名前なので、その次の idle_j");
            });
        }

        [Test]
        public void Advance_GoesThroughMaxFilesTalkClipsInNameOrderAndBackToTheFirst()
        {
            // 名前は idle_00〜idle_31（5 の倍数は大文字）。一覧には 13 ずつ飛ばした順で並べる。Token は名前の番号に 100 を足したもの
            var count = IdleFiles.MaxFiles;
            var talk = new IdleClip[count];
            for (var i = 0; i < count; i++)
            {
                var n = i * 13 % count;
                talk[i] = Talk(100 + n, (n % 5 == 0 ? "IDLE_" : "idle_") + n.ToString("00", CultureInfo.InvariantCulture));
            }

            var byNumber = talk.OrderBy(c => c.Token).ToArray();
            for (var i = 1; i < count; i++)
            {
                Assert.That(IdleFiles.NameOrder.Compare(byNumber[i - 1].Name, byNumber[i].Name), Is.LessThan(0), "前提: 番号の順が名前の順");
            }

            var driver = Started(RestOnly(), talk);

            var starts = TalkStarts(driver, count + 1);

            var expected = Enumerable.Range(0, count).Select(n => 100L + n).Concat(new[] { 100L }).ToArray();
            Assert.That(starts, Is.EqualTo(expected), string.Join(",", starts));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void Advance_StartsTalkingWithTheClipAfterTheLastUsedName_ForRandomTalkListsAndReloads(int seed)
        {
            var random = new Random(seed);
            var names = new[] { "idle_a", "IDLE_A", "Idle_b", "idle_b", "idle-c", "idle_c", "idle2", "idle_10", "idle_9", "IDLE_Z", "", null };
            var lengths = new[] { 6f, 6f, 6f, 6f, 6f, 0f, -1f, float.NaN, float.PositiveInfinity };
            long nextToken = 1000;
            Func<IdleClip[]> randomTalk = () =>
            {
                var count = random.Next(4) == 0 ? random.Next(3) : random.Next(IdleFiles.MaxFiles + 1);
                var clips = new IdleClip[count];
                for (var i = 0; i < count; i++)
                {
                    clips[i] = Talk(nextToken++, names[random.Next(names.Length)], lengths[random.Next(lengths.Length)]);
                }

                return clips;
            };

            var talk = randomTalk();
            var driver = Started(RestOnly(), talk);
            var startedBefore = false;
            string previous = null;
            int starts = 0, wraps = 0, reloads = 0, withoutTalkClips = 0, previousGone = 0;
            for (var step = 0; step < 80; step++)
            {
                if (random.Next(3) == 0)
                {
                    var next = randomTalk();
                    Reload(driver, talk, next);
                    talk = next;
                    reloads++;
                    if (startedBefore && !talk.Any(c => c.Length > 0f && !float.IsInfinity(c.Length) && IdleFiles.NameOrder.Compare(c.Name, previous) == 0))
                    {
                        previousGone++;
                    }

                    continue;
                }

                var expected = ExpectedTalkStart(talk, startedBefore, previous);
                var because = "seed " + seed + " step " + step + " 前回 " + (startedBefore ? ShowName(previous) : "なし") + " 一覧 " + ShowClips(talk);
                var actual = StartTalking(driver);
                if (expected == null)
                {
                    Assert.That(actual, Is.Null, because + "（有効な話す間用がない）");
                    withoutTalkClips++;
                }
                else
                {
                    Assert.That(actual, Is.EqualTo(expected.Value.Token), because + "（期待 " + expected.Value.Token + ":" + ShowName(expected.Value.Name) + "）");
                    if (startedBefore && IdleFiles.NameOrder.Compare(expected.Value.Name, previous) <= 0)
                    {
                        wraps++;
                    }

                    startedBefore = true;
                    previous = expected.Value.Name;
                    starts++;
                }

                StopTalking(driver, random.Next(2) == 0);
            }

            // 確かめた状態が実際に起きていること
            Assert.That(starts, Is.GreaterThan(30), "話し始めが少ない");
            Assert.That(wraps, Is.GreaterThan(2), "最初に戻る話し始めが少ない");
            Assert.That(reloads, Is.GreaterThan(10), "読み直しが少ない");
            Assert.That(withoutTalkClips, Is.GreaterThan(0), "有効な話す間用がない話し始めが一度もない");
            Assert.That(previousGone, Is.GreaterThan(2), "前回の名前がなくなる読み直しが少ない");
        }

        /// <summary>
        /// 通常の一覧（空・1 本・2 本以上）と話す間用の一覧を乱数で読み直しながら、話し始めのクリップが規則どおりで、話している間に
        /// 替わらないこと。話している間には、保持の間の次の文・待機の出入り・戻りの取り消し・読み直し（話し終えて最初に戻るところで
        /// 入れ替わることがある）を混ぜる。読み直しのあとは、前の一覧のクリップが使われなくなるまで話さずに進める。
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void Advance_StartsWithTheClipAfterTheLastUsedNameAndKeepsIt_ForRandomListsReturnsAndIdleChanges(int seed)
        {
            var random = new Random(seed);
            var names = new[] { "idle_a", "IDLE_A", "Idle_b", "idle_b", "idle-c", "idle_c", "idle2", "idle_10", "IDLE_Z", "", null };
            var talkLengths = new[] { 3f, 6f, 6f, 0f, float.NaN };
            var normalLengths = new[] { 1.5f, 2.5f, 4f };
            long nextTalkToken = 1000;
            long nextNormalToken = -1;
            Func<IdleClip, bool> valid = c => c.Length > 0f && !float.IsInfinity(c.Length);
            Func<int, IdleClip[]> randomNormal = least =>
            {
                var count = Math.Max(least, new[] { 0, 1, 1, 2, 3 }[random.Next(5)]);
                var clips = new IdleClip[count];
                for (var i = 0; i < count; i++)
                {
                    clips[i] = new IdleClip(nextNormalToken--, normalLengths[random.Next(normalLengths.Length)]);
                }

                return clips;
            };
            Func<bool, IdleClip[]> randomTalk = withValid =>
            {
                var count = random.Next(4) == 0 ? random.Next(2) : 1 + random.Next(6);
                var clips = new List<IdleClip>();
                for (var i = 0; i < count; i++)
                {
                    clips.Add(Talk(nextTalkToken++, names[random.Next(names.Length)], talkLengths[random.Next(talkLengths.Length)]));
                }

                if (withValid && !clips.Any(valid))
                {
                    clips.Add(Talk(nextTalkToken++, names[random.Next(names.Length)]));
                }

                return clips.ToArray();
            };

            var normal = randomNormal(1);
            var talk = randomTalk(false);
            var driver = Started(normal, talk);
            Action<IdleClip[], IdleClip[], string> waitUntilReplaced = (oldNormal, oldTalk, because) =>
            {
                var old = oldNormal.Concat(oldTalk).Select(c => c.Token).ToList();
                for (var waited = 0.0; old.Any(driver.Sequence.IsInUse); waited += Frame)
                {
                    Assert.That(waited, Is.LessThan(20.0), because + "（前提: 前の一覧のクリップが使われなくなる） " + driver.Now);
                    driver.Step(Frame);
                }
            };

            var startedBefore = false;
            string previous = null;
            int starts = 0, wraps = 0, reloads = 0, emptyReloads = 0, withoutTalkClips = 0, resumedReturns = 0, loopsAfterReplacedReturn = 0;
            var normalSizes = new HashSet<int>();
            for (var step = 0; step < 80; step++)
            {
                var because = "seed " + seed + " step " + step + " 前回 " + (startedBefore ? ShowName(previous) : "なし") + " 通常 " + normal.Length + " 本 話す間用 " + ShowClips(talk);
                if (random.Next(3) == 0)
                {
                    // 一覧が空の間は、Runtime の読み直しと同じく空の通常の一覧が続くことがある
                    var nextNormal = normal.Length == 0 ? (random.Next(3) == 0 ? None() : randomNormal(1)) : randomNormal(0);
                    var nextTalk = randomTalk(false);
                    if (normal.Length == 0 && nextNormal.Length == 0 && startedBefore)
                    {
                        emptyReloads++;
                    }

                    driver.SetClips(nextNormal, nextTalk);
                    waitUntilReplaced(normal, talk, because);
                    normal = nextNormal;
                    talk = nextTalk;
                    reloads++;
                    continue;
                }

                var expected = normal.Length > 0 ? ExpectedTalkStart(talk, startedBefore, previous) : null;
                var actual = StartTalking(driver);
                if (expected == null)
                {
                    Assert.That(actual, Is.Null, because + "（話す間用へ移らない）");
                    withoutTalkClips++;
                    if (normal.Length > 0)
                    {
                        StopTalking(driver, random.Next(2) == 0);
                    }
                    else
                    {
                        driver.EndTalking();
                        driver.Run(0.5f);
                    }

                    continue;
                }

                Assert.That(actual, Is.EqualTo(expected.Value.Token), because + "（期待 " + expected.Value.Token + ":" + ShowName(expected.Value.Name) + "）");
                if (startedBefore && IdleFiles.NameOrder.Compare(expected.Value.Name, previous) <= 0)
                {
                    wraps++;
                }

                startedBefore = true;
                previous = expected.Value.Name;
                starts++;
                normalSizes.Add(Math.Min(normal.Length, 2));
                var chosen = expected.Value.Token;
                Action<IEnumerable<Snapshot>, string> assertOnlyChosen = (snapshots, what) =>
                {
                    var wrong = snapshots.FirstOrDefault(s => s.Tokens.Any(t => t != chosen));
                    Assert.That(wrong, Is.Null, because + "（" + what + "）話し始めの " + chosen + " だけが写る: " + wrong);
                };

                var variant = random.Next(6);
                IdleClip[] replacedNormal = null;
                IdleClip[] replacedTalk = null;
                var replacedAtReturn = false;
                var talking = new List<Snapshot>();
                if (variant == 1)
                {
                    driver.Talking = false;
                    var pause = driver.Run(0.2f + (float)random.NextDouble());
                    driver.Talking = true;
                    assertOnlyChosen(pause.Concat(driver.Run(1f)), "保持の間の次の文");
                }
                else if (variant == 2)
                {
                    driver.Idle = false;
                    var away = driver.Run(0.3f + (float)random.NextDouble());
                    driver.Idle = true;
                    assertOnlyChosen(away.Concat(driver.Run(1f)), "待機の出入り");
                }
                else if (variant >= 3)
                {
                    if (variant >= 4)
                    {
                        replacedNormal = randomNormal(1);
                        replacedTalk = randomTalk(true);
                        driver.SetClips(replacedNormal, replacedTalk);
                        reloads++;
                    }

                    if (variant == 3 || random.Next(4) != 0)
                    {
                        driver.EndTalking();
                        var returning = driver.Run(0.3f);
                        Assert.That(returning.All(s => s.Tokens.All(t => t == chosen || IsNormalToken(t))), Is.True, because + "（戻りの間に別の話す間用が写った） " + returning.Last());
                        if (returning.Last().Tokens.Any(IsNormalToken))
                        {
                            resumedReturns++;
                        }

                        replacedAtReturn = replacedNormal != null && returning.Any(s => s.Tokens.Any(t => replacedNormal.Any(c => c.Token == t)));
                        driver.Talking = true;
                        var resumed = driver.Run(1f);
                        Assert.That(resumed.All(s => s.Tokens.All(t => t == chosen || IsNormalToken(t))), Is.True, because + "（戻りの取り消しの間に別の話す間用が写った） " + resumed.Last());
                        var last = resumed.Last();
                        Assert.That(last.Count > 0 && last.Tokens.All(t => t == chosen), Is.True, because + " 戻りを取り消して話し始めの " + chosen + " だけ " + last);
                        talking.AddRange(resumed.SkipWhile(s => s.Tokens.Any(t => t != chosen)));
                    }
                }

                var extra = driver.Run(random.Next(7));
                assertOnlyChosen(extra, "話し続ける");
                talking.AddRange(extra);
                if (replacedAtReturn && talking.Any(s => s.Count == 2))
                {
                    loopsAfterReplacedReturn++;
                }

                StopTalking(driver, random.Next(2) == 0);
                if (replacedNormal != null)
                {
                    waitUntilReplaced(normal, talk, because);
                    normal = replacedNormal;
                    talk = replacedTalk;
                }
            }

            // 確かめた状態が実際に起きていること
            Assert.That(starts, Is.GreaterThan(15), "話し始めが少ない");
            Assert.That(wraps, Is.GreaterThan(1), "最初に戻る話し始めが少ない");
            Assert.That(reloads, Is.GreaterThan(10), "読み直しが少ない");
            Assert.That(withoutTalkClips, Is.GreaterThan(0), "話す間用へ移らない話し始めが一度もない");
            Assert.That(emptyReloads, Is.GreaterThan(0), "通常の一覧が空の間に空の一覧を渡す読み直しが一度もない");
            Assert.That(normalSizes, Is.EquivalentTo(new[] { 1, 2 }), "通常の一覧が 1 本と 2 本以上の両方で話し始めていない");
            Assert.That(resumedReturns, Is.GreaterThan(2), "戻りの取り消しが少ない");
            Assert.That(loopsAfterReplacedReturn, Is.GreaterThan(0), "戻りで一覧が入れ替わったあとに話す間用を繰り返したことが一度もない");
        }

        // ---- Runtime と同じ契約の読み直し ----

        private static bool IsValidClip(IdleClip clip)
        {
            return clip.Length > 0f && !float.IsInfinity(clip.Length);
        }

        /// <summary>
        /// IdlePlayer が読むフォルダ 1 つ分。読み直すたびに、ファイルごとにそのまま・書き換え・消すのいずれかにし、名前を足す。
        /// 一覧は名前の IdleFiles.NameOrder の順で、名前は大小を区別せずに重ならない。読み込んだクリップが残っていて書き換えていない
        /// ファイルは前と同じ Token（パスは大小を区別せずに引くので、綴りだけ変わっても同じ Token）。書き換えたファイル・新しいファイルは
        /// 新しい Token。最後に渡した一覧になく使用中でもないクリップは捨て、その Token は二度と使わない。
        /// marker を与えたフォルダは、空でない限りその名前のファイルを読み直しのたびに書き換える（名前は一覧の最初に来るものにする）。
        /// </summary>
        private sealed class ContractFolder
        {
            private sealed class File
            {
                public string Name;
                public int Version;
                public float Length;
            }

            private sealed class Loaded
            {
                public long Token;
                public int Version;
            }

            private readonly Random _random;
            private readonly string[] _names;
            private readonly float[] _lengths;
            private readonly string _marker;
            private readonly long _tokenStep;
            private long _nextToken;
            private int _nextVersion;
            private readonly Dictionary<string, File> _disk = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, File> _gone = new Dictionary<string, File>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Loaded> _loaded = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<long> _alive = new HashSet<long>();
            private HashSet<long> _given = new HashSet<long>();
            private List<long> _retiring = new List<long>();

            public ContractFolder(Random random, string[] names, float[] lengths, string marker, long firstToken, long tokenStep)
            {
                _random = random;
                _names = names;
                _lengths = lengths;
                _marker = marker;
                _nextToken = firstToken;
                _tokenStep = tokenStep;
            }

            public bool IsEmpty
            {
                get { return _disk.Count == 0; }
            }

            /// <summary>読み込み済みのクリップを、綴りだけ変えたファイルで同じ Token のまま渡した回数。</summary>
            public int RespelledKept;

            /// <summary>消えたあと書き換えずに戻ったファイルに、残っていた前の Token を渡した回数。</summary>
            public int ReturnedKept;

            /// <summary>フォルダを変え（empty なら全部消し）、Runtime と同じく名前の順の一覧を返す。</summary>
            public IdleClip[] Read(bool empty)
            {
                var respelled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (empty)
                {
                    foreach (var file in _disk.Values)
                    {
                        _gone[file.Name] = file;
                    }

                    _disk.Clear();
                }
                else
                {
                    var wasEmpty = _disk.Count == 0;
                    foreach (var file in _disk.Values.ToList())
                    {
                        var roll = _random.Next(10);
                        if (file.Name == _marker)
                        {
                            Rewrite(file);
                        }
                        else if (roll < 3)
                        {
                            _disk.Remove(file.Name);
                            _gone[file.Name] = file;
                        }
                        else if (roll < 5)
                        {
                            Rewrite(file);
                        }
                        else if (_random.Next(3) == 0)
                        {
                            var spelled = Spell(file.Name);
                            if (spelled != file.Name)
                            {
                                file.Name = spelled;
                                respelled.Add(spelled);
                            }
                        }
                    }

                    if (_marker != null && !_disk.ContainsKey(_marker))
                    {
                        var marker = new File { Name = _marker };
                        Rewrite(marker);
                        _disk[_marker] = marker;
                    }

                    var adds = _random.Next(3) + (wasEmpty && _marker == null ? 1 : 0);
                    for (var i = 0; i < adds; i++)
                    {
                        var free = _names.Where(n => !_disk.ContainsKey(n)).ToList();
                        if (free.Count == 0)
                        {
                            break;
                        }

                        var name = free[_random.Next(free.Count)];
                        File back;
                        if (_gone.TryGetValue(name, out back) && _random.Next(2) == 0)
                        {
                            // 消える前と同じ中身のファイルが戻った。綴りは変わっていることもある
                            back.Name = _random.Next(2) == 0 ? back.Name : Spell(name);
                            _disk[name] = back;
                        }
                        else
                        {
                            var added = new File { Name = Spell(name) };
                            Rewrite(added);
                            _disk[name] = added;
                        }

                        _gone.Remove(name);
                    }
                }

                var clips = new List<IdleClip>();
                foreach (var file in _disk.Values.OrderBy(f => f.Name, IdleFiles.NameOrder))
                {
                    Loaded loaded;
                    long token;
                    if (_loaded.TryGetValue(file.Name, out loaded) && loaded.Version == file.Version && _alive.Contains(loaded.Token))
                    {
                        token = loaded.Token;
                        if (respelled.Contains(file.Name))
                        {
                            RespelledKept++;
                        }
                        else if (!_given.Contains(token))
                        {
                            ReturnedKept++;
                        }
                    }
                    else
                    {
                        token = _nextToken;
                        _nextToken += _tokenStep;
                        _alive.Add(token);
                        _loaded[file.Name] = new Loaded { Token = token, Version = file.Version };
                    }

                    clips.Add(new IdleClip(token, file.Length, file.Name));
                }

                _given = new HashSet<long>(clips.Select(c => c.Token));
                _retiring = _alive.Where(t => !_given.Contains(t)).ToList();
                return clips.ToArray();
            }

            /// <summary>Runtime と同じく、最後に渡した一覧になく使用中でもないクリップを捨てる。</summary>
            public void Free(IdleSequence sequence)
            {
                for (var i = _retiring.Count - 1; i >= 0; i--)
                {
                    var token = _retiring[i];
                    if (sequence.IsInUse(token))
                    {
                        continue;
                    }

                    _retiring.RemoveAt(i);
                    _alive.Remove(token);
                    foreach (var stale in _loaded.Where(e => e.Value.Token == token).Select(e => e.Key).ToList())
                    {
                        _loaded.Remove(stale);
                    }
                }
            }

            private void Rewrite(File file)
            {
                file.Version = ++_nextVersion;
                file.Length = file.Name == _marker ? _lengths.Where(l => l > 0f).ElementAt(_random.Next(_lengths.Count(l => l > 0f))) : _lengths[_random.Next(_lengths.Length)];
            }

            private string Spell(string name)
            {
                switch (_random.Next(3))
                {
                    case 0:
                        return name.ToLowerInvariant();
                    case 1:
                        return name.ToUpperInvariant();
                    default:
                        return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
                }
            }
        }

        /// <summary>
        /// IdleSequence が使っている一覧をテストの側で写す。使っている通常の一覧に有効なクリップがあれば SetClips は保留になり、
        /// 最初に戻るところで入れ替わる。保留の通常の一覧の最初の有効なクリップはいつも新しい Token なので、それが層に写れば
        /// 入れ替わった。また、使っている一覧にあって保留の一覧にない Token は、入れ替わったあとは層に写っていなければ使用中でない。
        /// </summary>
        private sealed class ListsInUse
        {
            public IdleClip[] Normal = new IdleClip[0];
            public IdleClip[] Talk = new IdleClip[0];
            public bool HasPending;
            public IdleClip[] PendingNormal = new IdleClip[0];
            public IdleClip[] PendingTalk = new IdleClip[0];

            public bool HasNormal
            {
                get { return Normal.Any(IsValidClip); }
            }

            // 保留の通常の一覧の最初の有効なクリップと、使っている一覧の有効なクリップのうち保留の一覧にない Token
            private long? _pendingFirst;
            private long[] _leaving = new long[0];

            public void Given(IdleClip[] normal, IdleClip[] talk)
            {
                if (HasNormal)
                {
                    HasPending = true;
                    PendingNormal = normal;
                    PendingTalk = talk;
                    _pendingFirst = normal.Where(IsValidClip).Select(c => (long?)c.Token).FirstOrDefault();
                    var pending = new HashSet<long>(normal.Concat(talk).Select(c => c.Token));
                    _leaving = Normal.Concat(Talk).Where(IsValidClip).Select(c => c.Token).Where(t => !pending.Contains(t)).ToArray();
                    return;
                }

                HasPending = false;
                Normal = normal;
                Talk = talk;
            }

            /// <summary>保留の一覧が入れ替わったと分かったら、それを使っている一覧にして true。</summary>
            public bool Follow(IdleSequence sequence, Snapshot now)
            {
                if (!HasPending)
                {
                    return false;
                }

                var swapped = _pendingFirst.HasValue && now.Has(_pendingFirst.Value);
                for (var i = 0; i < _leaving.Length && !swapped; i++)
                {
                    swapped = !sequence.IsInUse(_leaving[i]);
                }

                if (!swapped)
                {
                    return false;
                }

                Normal = PendingNormal;
                Talk = PendingTalk;
                HasPending = false;
                return true;
            }

            /// <summary>
            /// まだ入れ替わっていないと言える（または保留がない）。入れ替わったあとなら、保留の一覧にない Token は層に写っているものしか
            /// 使用中でないので、層にない使用中のものがあれば入れ替わっていない。
            /// </summary>
            public bool Certain(IdleSequence sequence, Snapshot now)
            {
                return !HasPending || (now.Count > 0 && _leaving.Any(t => sequence.IsInUse(t) && !now.Has(t)));
            }

            public override string ToString()
            {
                return "使用中 通常 " + ShowClips(Normal) + " 話す間用 " + ShowClips(Talk) + (HasPending ? " 保留 通常 " + ShowClips(PendingNormal) + " 話す間用 " + ShowClips(PendingTalk) : " 保留なし");
            }
        }

        /// <summary>
        /// Runtime と同じ契約で作った一覧（変わっていないファイルは同じ Token、名前の順、名前は重ならない）を、要求のたびと任意の
        /// ときに読み直しながら、話し始め・保持の間の次の文・戻りの取り消し・待機の出入りを混ぜる。毎フレーム次を確かめる。
        /// 話し始め: 写った話す間用のクリップは、使っている一覧で ExpectedTalkStart が選ぶもの。
        /// 話している間: 話し始めから話す間用のクリップが層から消えるまで、写る話す間用のクリップは 1 つだけ（入れ替わりがあっても）。
        /// 使用中: 層に写っている Token は IsInUse。
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void Advance_ShowsOneTalkClipFromEachStartUntilItLeaves_ForReloadsThatKeepTheTokensOfUnchangedFiles(int seed)
        {
            var random = new Random(seed);
            var talkNames = new[] { "idle_0", "idle_a", "idle_b", "idle-c", "idle2", "idle_10", "idle_9", "idlea", "idle_m", "idle_z" };
            // 通常のフォルダは idle（一覧の最初）と 2 つまでのファイル。通常の一覧は空・1 本・2 本以上になる
            var normalFolder = new ContractFolder(random, new[] { "idle_n1", "idle_n2" }, new[] { 1.5f, 2.5f, 4f, 4f, 0f }, "idle", -1, -1);
            var talkFolder = new ContractFolder(random, talkNames, new[] { 2.5f, 4f, 6f, 6f, 0f }, null, 1000, 1);
            var lists = new ListsInUse();
            var driver = new Driver();
            var sequence = driver.Sequence;
            var deltas = new[] { 1f / 60f, 1f / 30f, 1f / 30f, 1f / 20f };
            Func<Snapshot> step = () => driver.Step(deltas[random.Next(deltas.Length)]);
            Func<double, Func<Snapshot, bool>, bool> runUntil = (seconds, done) =>
            {
                var until = driver.Elapsed + seconds;
                while (driver.Elapsed < until)
                {
                    if (done(step()))
                    {
                        return true;
                    }
                }

                return false;
            };
            Action<double> run = seconds => runUntil(seconds, s => false);

            // 待機の度合いは idle と delta だけで決まるので、テストの側でも同じ式で追う。0 より大きいのに層がなければ自作の待機が消えた
            var idleShare = 0f;
            var requested = false;
            var startedBefore = false;
            string previous = null;
            long? shown = null;
            var returning = false;
            var swappedKeeping = false;
            var swappedDropping = false;
            var loopCounted = false;
            var emptyReloadsSinceStart = 0;
            int starts = 0, wraps = 0, swapsKeeping = 0, swapsDropping = 0, loopsAfterKeeping = 0, loopsAfterDropping = 0;
            int takenBack = 0, hiddenWhileShown = 0, emptyReloads = 0, startsAfterEmptyReloads = 0, replacedPending = 0, skippedReloads = 0;
            var normalSizes = new HashSet<int>();
            driver.AfterStep = (now, delta) =>
            {
                idleShare = driver.Idle ? Math.Min(1f, idleShare + delta / IdleSequence.EnterIdleSeconds) : Math.Max(0f, idleShare - delta / IdleSequence.LeaveIdleSeconds);
                Func<string> because = () => "seed " + seed + " t=" + driver.Elapsed.ToString("0.###") + " " + now + " 前回 " + (startedBefore ? ShowName(previous) : "なし") + " " + lists;
                var tokens = now.Tokens;
                foreach (var token in tokens)
                {
                    if (!sequence.IsInUse(token))
                    {
                        Assert.That(sequence.IsInUse(token), Is.True, because() + "（写っている Token " + token + " が使用中でない）");
                    }
                }

                if (lists.Follow(sequence, now) && shown.HasValue)
                {
                    if (lists.Talk.Any(c => c.Token == shown.Value))
                    {
                        swapsKeeping++;
                        swappedKeeping = true;
                    }
                    else
                    {
                        swapsDropping++;
                        swappedDropping = true;
                    }
                }

                if (now.Count == 0)
                {
                    if (idleShare > 0f)
                    {
                        shown = null;
                    }
                    else if (shown.HasValue)
                    {
                        hiddenWhileShown++;
                    }
                }
                else
                {
                    var talkTokens = tokens.Where(t => !IsNormalToken(t)).Distinct().ToList();
                    if (shown.HasValue && talkTokens.Count == 0)
                    {
                        shown = null;
                    }
                    else if (shown.HasValue)
                    {
                        if (talkTokens.Count != 1 || talkTokens[0] != shown.Value)
                        {
                            Assert.That(talkTokens, Is.EqualTo(new[] { shown.Value }), because() + "（話し始めの " + shown.Value + " が層から消える前に別の話す間用のクリップが写った）");
                        }

                        if (now.Count == 2 && !IsNormalToken(tokens[0]) && IsNormalToken(tokens[1]))
                        {
                            returning = true;
                        }
                        else if (now.Count == 1 && returning)
                        {
                            returning = false;
                            takenBack++;
                        }

                        if (now.Count == 2 && tokens[0] == shown.Value && tokens[1] == shown.Value && !loopCounted && (swappedKeeping || swappedDropping))
                        {
                            loopCounted = true;
                            loopsAfterKeeping += swappedKeeping ? 1 : 0;
                            loopsAfterDropping += swappedDropping ? 1 : 0;
                        }
                    }
                    else if (talkTokens.Count > 0)
                    {
                        var expected = lists.HasNormal ? ExpectedTalkStart(lists.Talk, startedBefore, previous) : null;
                        Assert.That(expected.HasValue, Is.True, because() + "（話す間用のクリップへ移らないはず）");
                        Assert.That(talkTokens, Is.EqualTo(new[] { expected.Value.Token }), because() + "（話し始めは " + expected.Value.Token + ":" + ShowName(expected.Value.Name) + "）");
                        if (startedBefore && IdleFiles.NameOrder.Compare(expected.Value.Name, previous) <= 0)
                        {
                            wraps++;
                        }

                        if (startedBefore && emptyReloadsSinceStart >= 2)
                        {
                            startsAfterEmptyReloads++;
                        }

                        startedBefore = true;
                        previous = expected.Value.Name;
                        starts++;
                        normalSizes.Add(Math.Min(lists.Normal.Count(IsValidClip), 2));
                        emptyReloadsSinceStart = 0;
                        shown = expected.Value.Token;
                        returning = false;
                        swappedKeeping = false;
                        swappedDropping = false;
                        loopCounted = false;
                    }
                }

                normalFolder.Free(sequence);
                talkFolder.Free(sequence);
                if (sequence.TakeReloadRequest())
                {
                    requested = true;
                }
            };

            Func<bool> reload = () =>
            {
                // 保留が入れ替わったかどうかを言えるまで待つ。本家だけが見えている間は言えないので読み直さない
                var giveUp = driver.Elapsed + 5.0;
                for (var now = driver.Now; !lists.Certain(sequence, now); now = step())
                {
                    if (driver.Elapsed >= giveUp || now.Count == 0)
                    {
                        skippedReloads++;
                        return false;
                    }
                }

                var normal = normalFolder.Read(normalFolder.IsEmpty ? random.Next(3) != 0 : random.Next(5) == 0);
                var talk = talkFolder.Read(random.Next(12) == 0);
                if (!lists.HasNormal && normal.Length == 0)
                {
                    emptyReloads++;
                    emptyReloadsSinceStart++;
                }

                replacedPending += lists.HasPending ? 1 : 0;
                lists.Given(normal, talk);
                driver.SetClips(normal, talk);
                normalFolder.Free(sequence);
                talkFolder.Free(sequence);
                return true;
            };

            Action endTalking = () =>
            {
                if (random.Next(2) == 0)
                {
                    driver.EndTalking();
                }
                else
                {
                    driver.Talking = false;
                }
            };

            Assert.That(reload(), Is.True, "前提: 最初の一覧");
            run(1.2);
            for (var round = 0; round < 150; round++)
            {
                if (requested)
                {
                    // Runtime は要求のあと、フォルダを読み終えてから SetClips する
                    requested = false;
                    run(random.NextDouble());
                    reload();
                }

                if (!lists.HasNormal && !lists.HasPending)
                {
                    // 一覧が空の間は 5 秒ごとの要求を待つ。話しても話す間用のクリップは写らない
                    driver.Talking = random.Next(2) == 0;
                    runUntil(IdleSequence.EmptyReloadSeconds + 0.5, s => requested);
                    continue;
                }

                var action = random.Next(10);
                if (action < 2)
                {
                    driver.Talking = false;
                    if (action == 0)
                    {
                        reload();
                    }

                    run(0.5 + 3.0 * random.NextDouble());
                    continue;
                }

                if (action == 2 && !shown.HasValue)
                {
                    driver.Idle = false;
                    run(0.3 + random.NextDouble());
                    driver.Idle = true;
                    run(1.0);
                    continue;
                }

                driver.Talking = true;
                run(1.2 + 1.3 * random.NextDouble());
                var variant = random.Next(20);
                if (variant < 3)
                {
                    // 保持の間の次の文
                    driver.Talking = false;
                    run(0.2 + random.NextDouble());
                    driver.Talking = true;
                    run(1.0);
                }
                else if (variant < 5)
                {
                    driver.Idle = false;
                    run(0.3 + random.NextDouble());
                    driver.Idle = true;
                    run(1.0);
                }
                else if (variant < 17)
                {
                    // 話している間に読み直す（保留になり、戻りで最初に戻るところで入れ替わることがある）。戻りの途中で話し直す
                    if (variant >= 9)
                    {
                        reload();
                    }

                    endTalking();
                    if (runUntil(IdleSequence.TalkHoldSeconds + 2.5, s => s.Count == 2 && !IsNormalToken(s.Layers[0].Token) && IsNormalToken(s.Layers[1].Token)))
                    {
                        run(0.8 * random.NextDouble());
                        driver.Talking = true;
                        run(2.0 + 5.0 * random.NextDouble());
                    }
                }

                run(4.0 * random.NextDouble());
                endTalking();
                runUntil(IdleSequence.TalkHoldSeconds + 3.0, s => !shown.HasValue);
            }

            // 確かめた組み合わせが実際に起きていること
            var counts = "話し始め " + starts + " 一周 " + wraps + " 入れ替わり（残る " + swapsKeeping + " 残らない " + swapsDropping + "） そのあとの繰り返し（残る " + loopsAfterKeeping + " 残らない " + loopsAfterDropping + "） 戻りの取り消し " + takenBack
                + " 見えない間 " + hiddenWhileShown + " 空の間の読み直し " + emptyReloads + " そのあとの話し始め " + startsAfterEmptyReloads + " 保留の置き換え " + replacedPending + " 綴りだけ変えて同じ Token " + talkFolder.RespelledKept + " 戻ったファイルに前の Token " + talkFolder.ReturnedKept + " 見送り " + skippedReloads;
            Assert.That(starts, Is.GreaterThan(40), "話し始めが少ない " + counts);
            Assert.That(wraps, Is.GreaterThan(10), "最初の名前へ戻る話し始めが少ない " + counts);
            Assert.That(swapsKeeping, Is.GreaterThan(2), "話す間用のクリップが見えている間に、その Token が残る一覧へ入れ替わったことが少ない " + counts);
            Assert.That(swapsDropping, Is.GreaterThan(5), "話す間用のクリップが見えている間に、その Token が残らない一覧へ入れ替わったことが少ない " + counts);
            Assert.That(loopsAfterKeeping, Is.GreaterThan(0), "Token が残る一覧へ入れ替わったあと、戻りを取り消して繰り返したことがない " + counts);
            Assert.That(loopsAfterDropping, Is.GreaterThan(1), "Token が残らない一覧へ入れ替わったあと、戻りを取り消して繰り返したことが少ない " + counts);
            Assert.That(takenBack, Is.GreaterThan(10), "戻りの取り消しが少ない " + counts);
            Assert.That(hiddenWhileShown, Is.GreaterThan(0), "話している間に待機から外れたことがない " + counts);
            Assert.That(emptyReloads, Is.GreaterThan(5), "通常の一覧が空の間の読み直しが少ない " + counts);
            Assert.That(startsAfterEmptyReloads, Is.GreaterThan(1), "通常の一覧が空の間に 2 回以上読み直したあとの話し始めが少ない " + counts);
            Assert.That(replacedPending, Is.GreaterThan(5), "保留の一覧を置き換えたことが少ない " + counts);
            Assert.That(talkFolder.RespelledKept, Is.GreaterThan(5), "綴りだけ変わったファイルに同じ Token を渡したことが少ない " + counts);
            Assert.That(normalSizes, Is.EquivalentTo(new[] { 1, 2 }), "通常の一覧が 1 本と 2 本以上の両方で話し始めていない " + counts);
        }

        // ---- 使っているクリップ ----

        [Test]
        public void IsInUse_IsFalse_ForTokensNeverGiven()
        {
            var sequence = new IdleSequence();
            Assert.That(sequence.IsInUse(A), Is.False, "一覧を与える前");
            Assert.That(sequence.IsInUse(0), Is.False);

            var driver = Started();

            Assert.That(driver.Sequence.IsInUse(999), Is.False, "一度も渡していない");
            Assert.That(driver.Sequence.IsInUse(X), Is.False, "一度も渡していない");
            Assert.That(driver.Sequence.IsInUse(long.MinValue), Is.False);
        }

        [Test]
        public void IsInUse_IsTrue_ForEveryTokenOfTheActiveLists_EvenIfNotShownYet()
        {
            var driver = Started();

            AssertTokens(driver.Now, new[] { A }, "前提: A だけが層にある");
            foreach (var token in new[] { A, B, C, T, U })
            {
                Assert.That(driver.Sequence.IsInUse(token), Is.True, "active の一覧にある Token " + token);
            }
        }

        [Test]
        public void IsInUse_IsFalse_ForClipsDroppedForInvalidLength()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(X, 0f), new IdleClip(Y, float.NaN)), Clips(new IdleClip(T, 6f), new IdleClip(Z, float.PositiveInfinity)));

            Assert.That(driver.Sequence.IsInUse(A), Is.True);
            Assert.That(driver.Sequence.IsInUse(T), Is.True);
            Assert.That(driver.Sequence.IsInUse(X), Is.False, "長さ 0 は一覧から除かれる");
            Assert.That(driver.Sequence.IsInUse(Y), Is.False, "長さ NaN は一覧から除かれる");
            Assert.That(driver.Sequence.IsInUse(Z), Is.False, "長さ ∞ は一覧から除かれる");
        }

        [Test]
        public void IsInUse_IsTrue_ForTokensOnlyInThePendingLists_AndFalseOnceThePendingIsReplaced()
        {
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(X, 4f), new IdleClip(Y, 4f)), Clips(new IdleClip(Z, 3f)));

            Assert.That(driver.Sequence.IsInUse(X), Is.True, "保留の通常の一覧");
            Assert.That(driver.Sequence.IsInUse(Y), Is.True, "保留の通常の一覧");
            Assert.That(driver.Sequence.IsInUse(Z), Is.True, "保留の話す間用の一覧");
            Assert.That(driver.Sequence.IsInUse(B), Is.True, "active はまだ使っている");

            driver.SetClips(Clips(new IdleClip(Y, 4f)), None());

            Assert.That(driver.Sequence.IsInUse(X), Is.False, "置き換えられた保留");
            Assert.That(driver.Sequence.IsInUse(Z), Is.False, "置き換えられた保留の話す間用");
            Assert.That(driver.Sequence.IsInUse(Y), Is.True, "新しい保留");
        }

        [Test]
        public void IsInUse_IsTrueForTheOutgoingClipOfThePreviousList_UntilItsCrossFadeEnds()
        {
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(X, 4f)), None());

            // C→X（最初に戻る）は 12 秒に始まる。C は層にだけ残る
            var crossing = driver.RunTo(12.5);
            AssertTokens(crossing, new[] { C, X }, "前提");
            Assert.That(driver.Sequence.IsInUse(C), Is.True, "層にある");
            Assert.That(driver.Sequence.IsInUse(A), Is.False, "前の一覧にだけあった");
            Assert.That(driver.Sequence.IsInUse(B), Is.False, "前の一覧にだけあった");
            Assert.That(driver.Sequence.IsInUse(T), Is.False, "前の話す間用の一覧にだけあった");
            Assert.That(driver.Sequence.IsInUse(U), Is.False, "前の話す間用の一覧にだけあった");

            var after = driver.RunTo(13.2);
            AssertTokens(after, new[] { X }, "前提: 混ぜが終わった");
            Assert.That(driver.Sequence.IsInUse(C), Is.False, "混ぜが終わって外れ、一覧にもない");
            Assert.That(driver.Sequence.IsInUse(X), Is.True);
        }

        [Test]
        public void IsInUse_IsTrueForAClipThatWillShowAgain_WhileAwayFromIdleHidesAllLayers()
        {
            var driver = Started();
            driver.SetClips(Clips(new IdleClip(X, 4f)), None());
            driver.RunTo(12.5);
            AssertTokens(driver.Now, new[] { C, X }, "前提");

            driver.Idle = false;
            driver.Run(1f);
            Assert.That(driver.Now.Count, Is.EqualTo(0), "前提: 本家だけ");
            Assert.That(driver.Sequence.IsInUse(C), Is.True, "時計が止まっているだけで、戻れば C の混ぜの続きが写る");

            driver.Idle = true;
            var back = driver.Run(0.1f).Last();
            Assert.That(back.Has(C), Is.True, "前提: C がまた写る " + back);
        }

        [Test]
        public void IsInUse_IsTrueForTheLastClipWhileOwnIdleFadesOut_AndFalseAfterwards()
        {
            var driver = Started(Clips(new IdleClip(A, 4f), new IdleClip(B, 5f)), Talk2());
            driver.SetClips(None(), None());

            var fading = driver.RunTo(7.5);
            AssertTokens(fading, new[] { B }, "前提: 一覧が空になり B だけが続く");
            Assert.That(driver.Sequence.IsInUse(B), Is.True, "層にある");
            Assert.That(driver.Sequence.IsInUse(A), Is.False);
            Assert.That(driver.Sequence.IsInUse(T), Is.False, "話す間用の一覧も空になった");

            driver.RunTo(8.5);
            Assert.That(driver.Now.Count, Is.EqualTo(0), "前提: 本家だけ");
            Assert.That(driver.Sequence.IsInUse(B), Is.False, "もう写らない");
        }

        [Test]
        public void IsInUse_IsTrueForTheTalkClipWhileReturningFromTalk_AfterTheTalkListWasReplacedAtTheWrap()
        {
            // C の途中で話し、話し終えて最初に戻るときに保留が反映される。T は出ていく層にだけ残る
            var driver = Started();
            driver.RunTo(8.5);
            driver.SetClips(Clips(new IdleClip(X, 4f)), Clips(new IdleClip(U, 6f)));
            driver.Talking = true;
            driver.Run(1f);
            driver.EndTalking();

            var returning = driver.Run(0.5f).Last();
            AssertTokens(returning, new[] { T, X }, "前提");
            Assert.That(driver.Sequence.IsInUse(T), Is.True, "出ていく層にある");
            Assert.That(driver.Sequence.IsInUse(U), Is.True, "新しい active の話す間用の一覧");
            Assert.That(driver.Sequence.IsInUse(C), Is.False, "前の一覧にだけあった");

            driver.Run(0.6f);
            Assert.That(driver.Sequence.IsInUse(T), Is.False, "混ぜが終わって外れ、一覧にもない");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void IsInUse_NeverLetsADiscardedClipShowAgain_WhenRuntimeFreesClipsNotGivenLastAndNotInUse(int seed)
        {
            // Runtime と同じ流れ: 毎フレーム Advance。要求が立てば少し遅れてフォルダを読み直し、SetClips する。
            // 最後に渡した一覧にもなく、IsInUse でもない Token は捨てる。捨てた Token は二度と渡さない
            var random = new Random(seed);
            var lengths = new[] { 0.01f, 0.3f, 1f, 1.3f, 2.5f, 4f, 6f };
            var deltas = new[] { 1f / 165f, 1f / 60f, 1f / 60f, 1f / 30f, 1f / 15f, 0.333f, 0f, float.NaN };
            var driver = new Driver();
            var alive = new Dictionary<long, float>();
            var discarded = new HashSet<long>();
            var lastGiven = new HashSet<long>();
            long nextToken = 1000;
            var reloadAt = -1.0;
            int keptOnlyByInUse = 0, discards = 0, reloads = 0;

            Func<int, IdleClip[]> newList = max =>
            {
                var clips = new List<IdleClip>();
                var count = random.Next(max + 1);
                for (var i = 0; i < count; i++)
                {
                    // 読み直しでは、読み込み済みのクリップをそのまま使うことも、新しく読むこともある
                    if (alive.Count > 0 && random.Next(3) == 0)
                    {
                        var reuse = alive.Keys.ElementAt(random.Next(alive.Count));
                        clips.Add(new IdleClip(reuse, alive[reuse]));
                        continue;
                    }

                    var token = nextToken++;
                    var length = lengths[random.Next(lengths.Length)];
                    alive[token] = length;
                    clips.Add(new IdleClip(token, length));
                }

                return clips.ToArray();
            };

            Action give = () =>
            {
                var normal = random.Next(8) == 0 ? None() : newList(4);
                var talk = newList(2);
                lastGiven = new HashSet<long>(normal.Concat(talk).Select(c => c.Token));
                driver.SetClips(normal, talk);
            };

            Action free = () =>
            {
                foreach (var token in alive.Keys.ToList())
                {
                    if (lastGiven.Contains(token))
                    {
                        continue;
                    }

                    if (driver.Sequence.IsInUse(token))
                    {
                        keptOnlyByInUse++;
                        continue;
                    }

                    alive.Remove(token);
                    discarded.Add(token);
                    discards++;
                }
            };

            give();
            free();
            for (var frame = 0; frame < 30000; frame++)
            {
                var roll = random.Next(1000);
                if (roll < 25)
                {
                    driver.Talking = !driver.Talking;
                }
                else if (roll < 28)
                {
                    driver.EndTalking();
                }
                else if (roll < 40)
                {
                    driver.Idle = !driver.Idle;
                }
                else if (roll < 42)
                {
                    // アバターの読み込み直しなど、要求なしの読み直し
                    give();
                    free();
                }

                var now = driver.Step(deltas[random.Next(deltas.Length)]);
                var because = "seed " + seed + " frame " + frame + " " + now;
                Assert.That(now.Tokens.Any(discarded.Contains), Is.False, because + "（捨てたクリップを指している）");

                if (driver.Sequence.TakeReloadRequest() && reloadAt < 0)
                {
                    reloadAt = driver.Elapsed + random.NextDouble() * 2.0;
                }

                if (reloadAt >= 0 && driver.Elapsed >= reloadAt)
                {
                    reloadAt = -1;
                    reloads++;
                    give();
                }

                free();
                foreach (var token in discarded)
                {
                    Assert.That(driver.Sequence.IsInUse(token), Is.False, because + "（捨てた Token " + token + " が使用中）");
                }
            }

            // 確かめた状態が実際に起きていること
            Assert.That(reloads, Is.GreaterThan(5), "読み直しが少ない");
            Assert.That(discards, Is.GreaterThan(10), "捨てたクリップが少ない");
            Assert.That(keptOnlyByInUse, Is.GreaterThan(100), "最後に渡した一覧になく IsInUse だけで残した場面が少ない");
        }

        // ---- 判定の不変条件 ----

        [Test]
        public void Advance_KeepsSharesContinuous_ThroughCrossFadesAndTheTalkLoop()
        {
            var driver = new Driver(Normal3(), Talk2());
            driver.CollectTo(14.0, FineFrame);
            driver.Talking = true;
            driver.Run(8f, FineFrame);
            driver.EndTalking();
            driver.Run(3f, FineFrame);

            // 毎フレームの確かめは Driver.Step が行う
            Assert.That(driver.Now.Count, Is.InRange(1, 2));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void Advance_KeepsLayersBoundedAndContinuous_ForRandomOperations(int seed)
        {
            var random = new Random(seed);
            var deltas = new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1f / 165f, 1f / 60f, 1f / 60f, 1f / 60f, 1f / 30f, 1f / 15f, 0.07f, 0.1f, 0.333f };
            // 数分のクリップは順を止めてしまうので、まれにだけ混ぜる
            var lengths = new[] { 0.01f, 0.3f, 1f, 1.3f, 2.5f, 4f, 6f, 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            var driver = new Driver();
            var invalid = new HashSet<long>();
            long nextToken = 100;
            Func<int, IdleClip[]> randomList = max =>
            {
                var count = random.Next(max + 1);
                var clips = new List<IdleClip>();
                for (var i = 0; i < count; i++)
                {
                    if (clips.Count > 0 && random.Next(6) == 0)
                    {
                        clips.Add(clips[random.Next(clips.Count)]);
                        continue;
                    }

                    var length = random.Next(100) == 0 ? 300f : lengths[random.Next(lengths.Length)];
                    var token = nextToken++;
                    if (!(length > 0f) || float.IsInfinity(length))
                    {
                        invalid.Add(token);
                    }

                    clips.Add(new IdleClip(token, length));
                }

                return random.Next(15) == 0 ? null : clips.ToArray();
            };

            Func<IdleClip[]> validList = () =>
            {
                var count = 1 + random.Next(3);
                var clips = new IdleClip[count];
                for (var i = 0; i < count; i++)
                {
                    clips[i] = new IdleClip(nextToken++, new[] { 0.3f, 1f, 2.5f, 4f }[random.Next(4)]);
                }

                return clips;
            };

            var everGiven = false;
            var awaySeconds = 0f;
            int twoLayers = 0, talkLayers = 0, hiddenAfterGiven = 0, reloads = 0;
            int setClipsWhileFadingOut = 0, talkingWhileMixing = 0, endTalkingWhileMixing = 0, idleWhileMixing = 0;
            var talkTokens = new HashSet<long>();

            // 待機の度合い p は idle と delta だけで決まるので、テストの側でも同じ式で追う。
            // p が 1 のまま自作の待機の寄与が下がっていれば、一覧が空になって本家へ戻っていく途中
            var idleShare = 0f;
            var fadingOut = false;
            var mixing = false;
            for (var frame = 0; frame < 24000; frame++)
            {
                var roll = random.Next(1000);
                if (fadingOut && random.Next(3) == 0)
                {
                    // 本家へ戻っていく途中に空でない一覧が来る
                    setClipsWhileFadingOut++;
                    everGiven = true;
                    driver.SetClips(validList(), randomList(2));
                }
                else if (roll < 4 || (!everGiven && roll < 40))
                {
                    // 空の一覧を多めに混ぜ、本家へ戻っていく途中を何度も起こす
                    var normal = random.Next(6) == 0 ? None() : randomList(4);
                    var talk = randomList(2);
                    foreach (var clip in talk ?? None())
                    {
                        talkTokens.Add(clip.Token);
                    }

                    everGiven |= (normal ?? None()).Any(c => c.Length > 0f && !float.IsInfinity(c.Length));
                    driver.SetClips(normal, talk);
                }
                else if (roll < 30)
                {
                    talkingWhileMixing += mixing ? 1 : 0;
                    driver.Talking = !driver.Talking;
                }
                else if (roll < 33)
                {
                    endTalkingWhileMixing += mixing ? 1 : 0;
                    driver.EndTalking();
                }
                else if (roll < 48)
                {
                    idleWhileMixing += mixing ? 1 : 0;
                    driver.Idle = !driver.Idle;
                }

                var before = driver.Now;
                var delta = deltas[random.Next(deltas.Length)];
                var now = driver.Step(delta);
                awaySeconds = driver.Idle ? 0f : awaySeconds + Clamped(delta);
                var idleBefore = idleShare;
                idleShare = driver.Idle ? Math.Min(1f, idleShare + Clamped(delta) / IdleSequence.EnterIdleSeconds) : Math.Max(0f, idleShare - Clamped(delta) / IdleSequence.LeaveIdleSeconds);
                fadingOut = idleBefore >= 1f && idleShare >= 1f && now.Count > 0 && now.Own < before.Own - 1e-6f;
                mixing = now.Count == 2;
                var because = "seed " + seed + " frame " + frame + " " + now;

                Assert.That(now.Tokens.Any(invalid.Contains), Is.False, because + "（長さが正で有限でないクリップ）");
                if (!everGiven)
                {
                    Assert.That(now.Count, Is.EqualTo(0), because + "（一覧を与える前）");
                }

                if (awaySeconds >= IdleSequence.LeaveIdleSeconds + ContinuityTolerance)
                {
                    Assert.That(now.Count, Is.EqualTo(0), because + "（待機でない時間が " + awaySeconds + " 秒）");
                }

                if (now.Count == 2)
                {
                    twoLayers++;
                }

                if (now.Tokens.Any(talkTokens.Contains))
                {
                    talkLayers++;
                }

                if (everGiven && now.Count == 0)
                {
                    hiddenAfterGiven++;
                }

                if (random.Next(10) == 0 && driver.Sequence.TakeReloadRequest())
                {
                    reloads++;
                }
            }

            // 確かめた状態が実際に起きていること
            Assert.That(twoLayers, Is.GreaterThan(100), "混ぜの間のフレームが少ない");
            Assert.That(talkLayers, Is.GreaterThan(100), "話す間用のフレームが少ない");
            Assert.That(hiddenAfterGiven, Is.GreaterThan(100), "本家だけのフレームが少ない");
            Assert.That(reloads, Is.GreaterThan(0), "読み直しの要求が一度も立たない");

            // 遷移（フェード）の途中に入力が来る組み合わせが実際に起きていること
            Assert.That(setClipsWhileFadingOut, Is.GreaterThanOrEqualTo(2), "本家へ戻っていく途中の SetClips が少ない");
            Assert.That(talkingWhileMixing, Is.GreaterThan(30), "混ぜの途中の SetTalking が少ない");
            Assert.That(endTalkingWhileMixing, Is.GreaterThan(5), "混ぜの途中の EndTalking が少ない");
            Assert.That(idleWhileMixing, Is.GreaterThan(20), "混ぜの途中の待機の切り替えが少ない");
        }
    }
}
