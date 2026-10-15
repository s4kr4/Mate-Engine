using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class VisemeTimelineTests
    {
        private const double Tolerance = 1e-5;

        /// <summary>"0:aa 0.1:oh" の形の記述から、Parse を通して検証済みのキー列を作る。</summary>
        internal static IReadOnlyList<VisemeKey> Keys(string spec)
        {
            var items = spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(pair =>
            {
                var parts = pair.Split(':');
                return "{\"t\":" + parts[0] + ",\"v\":\"" + parts[1] + "\"}";
            });
            var json = "{\"type\":\"say\",\"id\":1,\"turn\":1,\"text\":\"\",\"visemes\":[" + string.Join(",", items) + "]}";
            var say = BrainProtocol.Parse(json) as SayMessage;
            Assert.That(say, Is.Not.Null, "テスト用の say を作れない: " + json);
            Assert.That(say.Visemes, Is.Not.Null, "テスト用の visemes を作れない: " + json);
            return say.Visemes;
        }

        private static float[] All(MouthWeights w)
        {
            return new[] { w.Aa, w.Ih, w.Ou, w.Ee, w.Oh };
        }

        private static void AssertWeights(MouthWeights w, float aa, float ih, float ou, float ee, float oh, string because)
        {
            Assert.That(w.Aa, Is.EqualTo(aa).Within(Tolerance), because + " aa");
            Assert.That(w.Ih, Is.EqualTo(ih).Within(Tolerance), because + " ih");
            Assert.That(w.Ou, Is.EqualTo(ou).Within(Tolerance), because + " ou");
            Assert.That(w.Ee, Is.EqualTo(ee).Within(Tolerance), because + " ee");
            Assert.That(w.Oh, Is.EqualTo(oh).Within(Tolerance), because + " oh");
        }

        private static void AssertInvariants(MouthWeights w, string because)
        {
            foreach (var value in All(w))
            {
                Assert.That(float.IsNaN(value), Is.False, because + " NaN");
                Assert.That(value, Is.InRange(0f, 1f), because + " 範囲");
            }

            Assert.That(All(w).Sum(), Is.LessThanOrEqualTo(1f + Tolerance), because + " 合計");
        }

        [TestCase(0.0, 0f, 0f, TestName = "Evaluate_ClosesMouth_AtFirstKeyBeforeBlending")]
        [TestCase(0.025, 0.5f, 0f, TestName = "Evaluate_BlendsFromSilence_HalfwayIntoFirstKey")]
        [TestCase(0.05, 1f, 0f, TestName = "Evaluate_ReachesFirstShape_AfterBlendSeconds")]
        [TestCase(0.1, 1f, 0f, TestName = "Evaluate_KeepsPreviousShape_AtStartOfNextKey")]
        [TestCase(0.125, 0.5f, 0.5f, TestName = "Evaluate_SplitsWeights_HalfwayThroughBlend")]
        [TestCase(0.15, 0f, 1f, TestName = "Evaluate_ReachesNextShape_AfterBlendSeconds")]
        [TestCase(0.19, 0f, 1f, TestName = "Evaluate_HoldsShape_UntilNextKey")]
        [TestCase(0.2, 0f, 1f, TestName = "Evaluate_KeepsShape_AtStartOfFinalSilence")]
        [TestCase(0.225, 0f, 0.5f, TestName = "Evaluate_FadesToSilence_HalfwayThroughFinalBlend")]
        [TestCase(0.25, 0f, 0f, TestName = "Evaluate_ClosesMouth_AfterFinalSilenceBlend")]
        [TestCase(100.0, 0f, 0f, TestName = "Evaluate_ClosesMouth_LongAfterEnd")]
        public void Evaluate_FollowsBlendRule_ForAaOhSilTimeline(double time, float aa, float oh)
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.1:oh 0.2:sil"));

            var weights = timeline.Evaluate(time);

            AssertWeights(weights, aa, 0f, 0f, 0f, oh, "t=" + time);
        }

        [TestCase(-0.001)]
        [TestCase(-1.0)]
        [TestCase(double.NegativeInfinity)]
        public void Evaluate_ReturnsZero_WhenTimeIsNegative(double time)
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.5:sil"));

            AssertWeights(timeline.Evaluate(time), 0f, 0f, 0f, 0f, 0f, "t=" + time);
        }

        [Test]
        public void Evaluate_ReturnsZero_WhenTimeIsNaN()
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.5:sil"));

            AssertWeights(timeline.Evaluate(double.NaN), 0f, 0f, 0f, 0f, 0f, "NaN");
        }

        [Test]
        public void Evaluate_KeepsLastShape_WhenTimeIsPositiveInfinityAndLastKeyIsNotSilence()
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.5:ee"));

            AssertWeights(timeline.Evaluate(double.PositiveInfinity), 0f, 0f, 0f, 1f, 0f, "+Infinity");
        }

        [Test]
        public void Evaluate_ReturnsZero_WhenKeysAreEmpty()
        {
            var timeline = new VisemeTimeline(new VisemeKey[0]);

            AssertWeights(timeline.Evaluate(0.0), 0f, 0f, 0f, 0f, 0f, "t=0");
            AssertWeights(timeline.Evaluate(1.0), 0f, 0f, 0f, 0f, 0f, "t=1");
        }

        [TestCase(0.0)]
        [TestCase(0.1)]
        [TestCase(0.2999)]
        public void Evaluate_ReturnsZero_BeforeFirstKey(double time)
        {
            var timeline = new VisemeTimeline(Keys("0.3:aa 0.6:sil"));

            AssertWeights(timeline.Evaluate(time), 0f, 0f, 0f, 0f, 0f, "t=" + time);
        }

        [TestCase("ih", 0f, 1f, 0f, 0f, 0f)]
        [TestCase("ou", 0f, 0f, 1f, 0f, 0f)]
        [TestCase("ee", 0f, 0f, 0f, 1f, 0f)]
        [TestCase("oh", 0f, 0f, 0f, 0f, 1f)]
        [TestCase("aa", 1f, 0f, 0f, 0f, 0f)]
        [TestCase("sil", 0f, 0f, 0f, 0f, 0f)]
        public void Evaluate_MapsEachShapeToItsWeight_AfterBlend(string shape, float aa, float ih, float ou, float ee, float oh)
        {
            var timeline = new VisemeTimeline(Keys("0:" + shape + " 1:sil"));

            AssertWeights(timeline.Evaluate(0.5), aa, ih, ou, ee, oh, shape);
        }

        [Test]
        public void Evaluate_KeepsFullWeight_WhenSameShapeRepeats()
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.1:aa 0.2:sil"));

            AssertWeights(timeline.Evaluate(0.1), 1f, 0f, 0f, 0f, 0f, "t=0.1");
            AssertWeights(timeline.Evaluate(0.12), 1f, 0f, 0f, 0f, 0f, "t=0.12");
            AssertWeights(timeline.Evaluate(0.15), 1f, 0f, 0f, 0f, 0f, "t=0.15");
        }

        [Test]
        public void Evaluate_BlendsFromPreviousKeyShape_WhenIntervalIsShorterThanBlend()
        {
            // 0.02 秒で aa → oh。直前の形は keys[i-1] の形（途中まで混ざった状態ではない）
            var timeline = new VisemeTimeline(Keys("0:aa 0.02:oh 1:sil"));

            AssertWeights(timeline.Evaluate(0.01), 0.2f, 0f, 0f, 0f, 0f, "t=0.01");
            AssertWeights(timeline.Evaluate(0.03), 0.8f, 0f, 0f, 0f, 0.2f, "t=0.03");
        }

        [Test]
        public void Evaluate_UsesGivenBlendSeconds_WhenBlendIsCustom()
        {
            var timeline = new VisemeTimeline(Keys("0:ih 1:ou"), 0.2);

            AssertWeights(timeline.Evaluate(1.05), 0f, 0.75f, 0.25f, 0f, 0f, "t=1.05");
            AssertWeights(timeline.Evaluate(1.2), 0f, 0f, 1f, 0f, 0f, "t=1.2");
        }

        [TestCase(0.0)]
        [TestCase(-0.05)]
        public void Evaluate_SwitchesInstantly_WhenBlendSecondsIsNotPositive(double blend)
        {
            var timeline = new VisemeTimeline(Keys("0:aa 0.1:oh 0.2:sil"), blend);

            AssertWeights(timeline.Evaluate(0.0), 1f, 0f, 0f, 0f, 0f, "t=0");
            AssertWeights(timeline.Evaluate(0.1), 0f, 0f, 0f, 0f, 1f, "t=0.1");
            AssertWeights(timeline.Evaluate(0.2), 0f, 0f, 0f, 0f, 0f, "t=0.2");
        }

        [Test]
        public void Evaluate_KeepsInvariants_ForRandomTimelinesAndTimes()
        {
            var names = new[] { "aa", "ih", "ou", "ee", "oh", "sil" };
            var random = new Random(31415);

            for (var round = 0; round < 300; round++)
            {
                var count = 1 + random.Next(30);
                var time = random.Next(3) == 0 ? 0.0 : random.NextDouble();
                var spec = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = i == count - 1 && random.Next(4) != 0 ? "sil" : names[random.Next(names.Length)];
                    spec.Add(time.ToString("R", CultureInfo.InvariantCulture) + ":" + name);
                    time += random.Next(5) == 0 ? 1e-6 : random.NextDouble() * 0.2 + 1e-6;
                }

                var keys = Keys(string.Join(" ", spec));
                var blend = random.Next(5) == 0 ? 0.0 : random.NextDouble() * 0.1;
                var timeline = new VisemeTimeline(keys, blend);
                var last = keys[keys.Count - 1];

                for (var probe = 0; probe < 50; probe++)
                {
                    double t;
                    switch (random.Next(6))
                    {
                        case 0:
                            t = keys[random.Next(keys.Count)].Time;
                            break;
                        case 1:
                            t = last.Time + blend + random.NextDouble();
                            break;
                        case 2:
                            t = -random.NextDouble();
                            break;
                        default:
                            t = random.NextDouble() * (last.Time + 0.5);
                            break;
                    }

                    var weights = timeline.Evaluate(t);
                    var because = "round " + round + " t=" + t.ToString("R", CultureInfo.InvariantCulture) + " keys=" + string.Join(" ", spec) + " blend=" + blend;
                    AssertInvariants(weights, because);

                    if (t < 0 || t < keys[0].Time)
                    {
                        AssertWeights(weights, 0f, 0f, 0f, 0f, 0f, because + "（開始前）");
                    }

                    if (last.Shape == VisemeShape.Sil && t >= last.Time + blend)
                    {
                        AssertWeights(weights, 0f, 0f, 0f, 0f, 0f, because + "（最後の sil のあと）");
                    }
                }
            }
        }
    }
}
