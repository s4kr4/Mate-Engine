using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// 歩くクリップの中で待機の姿勢に近い時刻を選ぶこと。differences[i] は時刻 i × interval での待機の姿勢との差で、
    /// 最後の次は最初に戻る（クリップはくり返す）。差が最小の時刻を歩き出しの時刻に、周期的に見た極小のうち
    /// 差が「最小 + 0.25 ×（最大 − 最小）」以下のものの時刻を、待機に近い時刻として小さい順に返す。
    /// NaN・無限・負の差は使わない。各テストの仕様文は Description に書く。
    /// </summary>
    public class WalkRestTimesTests
    {
        private const float TimeTolerance = 1e-4f;

        private static readonly float[] Bad = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f };

        /// <summary>呼び出し側が渡す一覧。前の結果が残っていることを確かめられるよう、使わない値を入れておく。</summary>
        private static List<float> Dirty()
        {
            return new List<float> { 99f, -5f, float.NaN };
        }

        private static float[] Times(float interval, params int[] indices)
        {
            return indices.Select(i => i * interval).ToArray();
        }

        private static void AssertFound(float[] differences, float interval, float expectedStart, float[] expectedRests, string because = "")
        {
            var rests = Dirty();
            float start;

            var found = WalkRestTimes.TryFind(differences, interval, rests, out start);

            Assert.That(found, Is.True, because + ": 見つからない");
            Assert.That(start, Is.EqualTo(expectedStart).Within(TimeTolerance), because + ": 歩き出しの時刻");
            Assert.That(rests, Is.EqualTo(expectedRests).Within(TimeTolerance), because + ": 待機に近い時刻 [" + string.Join(", ", rests) + "]");
        }

        // ---- 見つからない ----

        [TestCase("null")]
        [TestCase("空")]
        [TestCase("すべて NaN・無限・負")]
        [Description("使える差（有限で 0 以上）が 1 つもなければ見つからず（false）、一覧は空になる")]
        public void TryFind_ReturnsFalseAndEmptiesTheList_WhenNoDifferenceIsUsable(string kind)
        {
            var differences = kind == "null" ? null : (kind == "空" ? new float[0] : Bad);
            var rests = Dirty();
            float start;

            var found = WalkRestTimes.TryFind(differences, 0.1f, rests, out start);

            Assert.That(found, Is.False);
            Assert.That(rests, Is.Empty);
        }

        [TestCase(0f)]
        [TestCase(-0.1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [Description("取り出しの間隔が正の有限でなければ見つからず（false）、一覧は空になる")]
        public void TryFind_ReturnsFalseAndEmptiesTheList_WhenIntervalIsNotPositiveFinite(float interval)
        {
            var rests = Dirty();
            float start;

            var found = WalkRestTimes.TryFind(new[] { 5f, 2f, 4f, 6f }, interval, rests, out start);

            Assert.That(found, Is.False);
            Assert.That(rests, Is.Empty);
        }

        // ---- 歩き出しの時刻 ----

        [Test]
        [Description("歩き出しの時刻は差が最小の時刻（添字 × 間隔）である")]
        public void TryFind_StartsAtTheTimeOfTheSmallestDifference()
        {
            var rests = new List<float>();
            float start;

            var found = WalkRestTimes.TryFind(new[] { 5f, 7f, 6f, 9f, 1.5f, 8f, 4f }, 0.25f, rests, out start);

            Assert.That(found, Is.True);
            Assert.That(start, Is.EqualTo(1f).Within(TimeTolerance));
        }

        [Test]
        [Description("差が最小の時刻が 2 つ以上あれば、小さい方の時刻から歩き出す")]
        public void TryFind_StartsAtTheEarlierTime_WhenTheSmallestDifferenceTies()
        {
            AssertFound(new[] { 3f, 1f, 4f, 1f, 5f }, 0.5f, 0.5f, Times(0.5f, 1, 3));
        }

        [Test]
        [Description("差 0 は使える値で、最小になる")]
        public void TryFind_UsesZeroDifference()
        {
            AssertFound(new[] { 3f, 6f, 0f, 5f, 2f, 7f }, 0.1f, 0.2f, Times(0.1f, 2));
        }

        [Test]
        [Description("時刻は添字 × 間隔で、1/30 秒おきの取り出しでもその時刻を返す")]
        public void TryFind_ReturnsIndexTimesInterval_ForOneThirtiethIntervals()
        {
            const float Interval = 1f / 30f;
            var differences = Enumerable.Range(0, 90).Select(i => 10f + 5f * (float)Math.Cos(2.0 * Math.PI * (i - 37) / 45.0)).ToArray();

            // 周期 45 の山と谷: 谷は 37 − 22.5 = 14.5 と 59.5 のあたり。cos の対称で 14 と 15、59 と 60 が同じ値になりうるので、値で最小を決める
            var rests = new List<float>();
            float start;
            var found = WalkRestTimes.TryFind(differences, Interval, rests, out start);

            var minimum = differences.Min();
            var first = Array.IndexOf(differences, minimum);
            Assert.That(found, Is.True);
            Assert.That(start, Is.EqualTo(first * Interval).Within(TimeTolerance));
            Assert.That(rests.Count, Is.EqualTo(2), "谷が 2 つ: [" + string.Join(", ", rests) + "]");
            Assert.That(rests.All(t => Math.Abs(t / Interval - Math.Round(t / Interval)) < 1e-3), Is.True, "添字 × 間隔でない時刻");
        }

        // ---- 待機に近い時刻 ----

        [Test]
        [Description("待機に近い時刻は、周期的に見た極小（両隣以下で、少なくとも片側より小さい）のうち、差が 最小 + 0.25 × (最大 − 最小) 以下のものの時刻を小さい順に並べたものである")]
        public void TryFind_ListsLocalMinimaWithinAQuarterOfTheRange()
        {
            // 極小は添字 1（2）・6（3）・8（4.5）。添字 10 の 6 は右隣（添字 0 の 5）より大きく極小でない。
            // 閾値 2 + 0.25 × (10 − 2) = 4 なので、4.5 は極小だが含めない
            AssertFound(new[] { 5f, 2f, 4f, 6f, 9f, 7f, 3f, 8f, 4.5f, 10f, 6f }, 0.1f, 0.1f, Times(0.1f, 1, 6));
        }

        [Test]
        [Description("差がちょうど 最小 + 0.25 × (最大 − 最小) の極小は含め、わずかに大きい極小は含めない")]
        public void TryFind_IncludesMinimumExactlyAtTheThresholdAndExcludesOneJustAbove()
        {
            // 最小 0・最大 8 → 閾値ちょうど 2
            AssertFound(new[] { 0f, 5f, 2f, 5f, 8f, 5f, 2.001f, 5f }, 0.5f, 0f, Times(0.5f, 0, 2));
        }

        [Test]
        [Description("最後の差と最初の差は隣どうしとして比べるので、最初の時刻は最後の差より大きければ極小にならない")]
        public void TryFind_ComparesTheFirstWithTheLast_WhenTheFirstIsNotAMinimumAcrossTheWrap()
        {
            // 0 の 2 は右隣 5 より小さいが、左隣（最後）の 1 より大きい。極小は 5 の 1 だけ。閾値 1 + 0.25 × 8 = 3
            AssertFound(new[] { 2f, 5f, 9f, 6f, 3f, 1f }, 0.2f, 1f, Times(0.2f, 5));
        }

        [Test]
        [Description("最初の時刻も、最後の差と次の差のどちらより小さければ極小になる")]
        public void TryFind_TreatsTheFirstAsAMinimum_WhenItIsSmallerThanTheLastAndTheNext()
        {
            // 0 の 1 は左隣（最後）3 と右隣 4 より小さい。最後の 3 は左隣 6 より小さいが右隣（最初）1 より大きい
            AssertFound(new[] { 1f, 4f, 9f, 6f, 3f }, 0.3f, 0f, Times(0.3f, 0));
        }

        [Test]
        [Description("最後の時刻も、その前の差と最初の差のどちらより小さければ極小になる")]
        public void TryFind_TreatsTheLastAsAMinimum_WhenItIsSmallerThanThePreviousAndTheFirst()
        {
            AssertFound(new[] { 4f, 6f, 8f, 6f, 1f }, 0.25f, 1f, Times(0.25f, 4));
        }

        [Test]
        [Description("同じ差が続く平らな底は、最初の 1 つだけを待機に近い時刻にする")]
        public void TryFind_TakesTheFirstOfAFlatBottom()
        {
            // 1 が 3 つ続く底。閾値 1 + 0.25 × 7 = 2.75
            AssertFound(new[] { 4f, 1f, 1f, 1f, 6f, 8f }, 0.1f, 0.1f, Times(0.1f, 1));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(5)]
        [Description("差がすべて同じなら極小はないとして、待機に近い時刻は最小の時刻（0）だけにする")]
        public void TryFind_ListsOnlyTheStartTime_WhenAllDifferencesAreEqual(int count)
        {
            AssertFound(Enumerable.Repeat(3f, count).ToArray(), 0.1f, 0f, new[] { 0f }, count + " 個");
        }

        [Test]
        [Description("渡した一覧に前の値が入っていても、最初に空にしてから入れる")]
        public void TryFind_ClearsTheGivenListFirst()
        {
            var rests = new List<float> { 0.7f, 0.1f };
            float start;

            WalkRestTimes.TryFind(new[] { 5f, 1f, 5f }, 0.5f, rests, out start);

            Assert.That(rests, Is.EqualTo(new[] { 0.5f }).Within(TimeTolerance));
        }

        // ---- 使わない差 ----

        // 使える差の並び（周期的）: 5（0）・2（2）・6（3）・9（4）・3（5）・8（6）。極小は 2 の 2 と 5 の 3。閾値 2 + 0.25 × 7 = 3.75。
        // 不正な値を 1 に置くと 2 の隣になり、そのまま比べると 2 の 2 が極小でなくなる（NaN・+∞）か、最小・最大が変わる（−∞・負）。
        // +∞ を最大に使えば、0 の 5 が閾値に入るうえ（左隣 8 と右隣 +∞ より小さい）極小にもなる
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(-1f)]
        [TestCase(-1e-6f)]
        [Description("NaN・無限・負の差は使わず、その時刻は極小にならず、隣の比べる相手はその先の使える差にする")]
        public void TryFind_SkipsUnusableDifferences_AndComparesWithTheNextUsableNeighbour(float bad)
        {
            AssertFound(new[] { 5f, bad, 2f, 6f, 9f, 3f, 8f }, 0.5f, 1f, Times(0.5f, 2, 5), "不正な値 " + bad);
        }

        [Test]
        [Description("使えない差が周期の端をまたいで続いても、最後の使える差と最初の使える差を隣どうしとして比べる")]
        public void TryFind_ComparesAcrossUnusableDifferencesAtTheWrap()
        {
            // 使える並び（添字）: 2（1）・7（2）・4（3）・9（4）。2 の左隣は周期の端をまたいだ 9（4）なので 2 は極小。
            // 4 も極小（7 と 9 より小さい）だが、閾値 2 + 0.25 × 7 = 3.75 より大きい
            AssertFound(new[] { float.NaN, 2f, 7f, 4f, 9f, -3f, float.PositiveInfinity }, 0.5f, 0.5f, Times(0.5f, 1));
        }

        [Test]
        [Description("使える差が 1 つだけなら、その時刻から歩き出し、待機に近い時刻もその時刻だけにする")]
        public void TryFind_UsesTheOnlyUsableDifference()
        {
            AssertFound(new[] { float.NaN, -2f, 4f, float.PositiveInfinity }, 0.5f, 1f, Times(0.5f, 2));
        }

        [Test]
        [Description("使える差がすべて同じなら、使えない差が混ざっていても、待機に近い時刻は使える差の最小の時刻だけにする")]
        public void TryFind_ListsOnlyTheStartTime_WhenAllUsableDifferencesAreEqual()
        {
            AssertFound(new[] { float.NaN, 3f, -1f, 3f, 3f }, 0.2f, 0.2f, Times(0.2f, 1));
        }

        // ---- 判定の不変条件 ----

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [Description("長さ・値・使えない差の位置を乱数で変えても、歩き出しの時刻は使える差の最小（同じなら小さい時刻）で、待機に近い時刻は周期的に見た極小のうち閾値以下のものを小さい順に並べたものである")]
        public void TryFind_MatchesTheDefinition_ForRandomDifferences(int seed)
        {
            var random = new Random(seed * 3571);
            var checkedLists = 0;
            for (var trial = 0; trial < 2000; trial++)
            {
                var count = 1 + random.Next(trial % 10 == 0 ? 400 : 12);
                var differences = new float[count];
                var used = new HashSet<float>();
                for (var i = 0; i < count; i++)
                {
                    if (random.Next(6) == 0)
                    {
                        differences[i] = Bad[random.Next(Bad.Length)];
                        continue;
                    }

                    // 値はすべて違えて、平らな底を作らない（平らな底は別のテストで確かめる）
                    float value;
                    do
                    {
                        value = (float)(random.NextDouble() * 50.0);
                    }
                    while (!used.Add(value));

                    differences[i] = value;
                }

                var interval = (float)(0.01 + random.NextDouble() * 0.2);
                var rests = Dirty();
                float start;
                var found = WalkRestTimes.TryFind(differences, interval, rests, out start);

                var usable = Enumerable.Range(0, count).Where(i => differences[i] >= 0f && !float.IsInfinity(differences[i])).ToList();
                var because = "seed " + seed + " trial " + trial + " [" + string.Join(", ", differences) + "] 間隔 " + interval;
                if (usable.Count == 0)
                {
                    Assert.That(found, Is.False, because);
                    Assert.That(rests, Is.Empty, because);
                    continue;
                }

                var min = usable.Min(i => differences[i]);
                var max = usable.Max(i => differences[i]);
                var threshold = min + 0.25f * (max - min);
                var expected = new List<float>();
                if (usable.Count == 1)
                {
                    expected.Add(usable[0] * interval);
                }
                else
                {
                    for (var j = 0; j < usable.Count; j++)
                    {
                        var here = differences[usable[j]];
                        var previous = differences[usable[(j + usable.Count - 1) % usable.Count]];
                        var next = differences[usable[(j + 1) % usable.Count]];
                        if (here < previous && here < next && here <= threshold)
                        {
                            expected.Add(usable[j] * interval);
                        }
                    }
                }

                Assert.That(found, Is.True, because);
                Assert.That(start, Is.EqualTo(usable.First(i => differences[i] == min) * interval).Within(TimeTolerance), because + ": 歩き出しの時刻");
                Assert.That(rests, Is.EqualTo(expected).Within(TimeTolerance), because + ": 待機に近い時刻 [" + string.Join(", ", rests) + "]");
                Assert.That(rests, Is.Ordered, because + ": 小さい順でない");
                checkedLists++;
            }

            Assert.That(checkedLists, Is.GreaterThan(1500), "確かめた一覧が少ない");
        }
    }
}
