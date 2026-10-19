using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        // 話す間用は 0 番目の T だけを使う。T の混ぜの開始点は 5 秒
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
