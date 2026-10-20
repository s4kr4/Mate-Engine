using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class SitProbeOffsetTests
    {
        private const float SceneValue = -0.375f;

        /// <summary>ProbeOffset の値を持ち、setter が呼ばれるたびに書き込みとして記録する。</summary>
        private sealed class FakeTarget : ISitProbeTarget
        {
            private readonly string _name;
            private readonly string _equalityKey;
            private readonly List<float> _writes = new List<float>();
            private float _value;

            /// <param name="equalityKey">null でなければ、同じ鍵を持つ別のインスタンスと Equals / GetHashCode で等しくなる。</param>
            public FakeTarget(string name, float value, string equalityKey = null)
            {
                _name = name;
                _value = value;
                _equalityKey = equalityKey;
            }

            public float ProbeOffset
            {
                get { return _value; }
                set
                {
                    _writes.Add(value);
                    _value = value;
                }
            }

            public float Value
            {
                get { return _value; }
            }

            public int WriteCount
            {
                get { return _writes.Count; }
            }

            /// <summary>シーンが値を決め直したことにする（書き込みとしては数えない）。</summary>
            public void SetByScene(float value)
            {
                _value = value;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(this, obj))
                {
                    return true;
                }

                var other = obj as FakeTarget;
                return other != null && _equalityKey != null && _equalityKey == other._equalityKey;
            }

            public override int GetHashCode()
            {
                return _equalityKey == null ? base.GetHashCode() : _equalityKey.GetHashCode();
            }

            public override string ToString()
            {
                return _name;
            }
        }

        private sealed class Original
        {
            public Original(string label, float value)
            {
                Label = label;
                Value = value;
            }

            public string Label { get; private set; }

            public float Value { get; private set; }
        }

        private static int Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static float FromBits(int bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        /// <summary>本家の値としてありうる float。NaN は符号・ペイロードの違うものを含める。</summary>
        private static readonly Original[] Originals =
        {
            new Original("-0.375", SceneValue),
            new Original("+0", 0f),
            new Original("-0", FromBits(unchecked((int)0x80000000))),
            new Original("0.5", 0.5f),
            new Original("float.NaN", float.NaN),
            new Original("NaN 0x7FC00000", FromBits(0x7FC00000)),
            new Original("NaN 0x7FC00123", FromBits(0x7FC00123)),
            new Original("NaN 0xFFC00456", FromBits(unchecked((int)0xFFC00456))),
            new Original("+Inf", float.PositiveInfinity),
            new Original("-Inf", float.NegativeInfinity),
            new Original("Epsilon", float.Epsilon),
            new Original("-Epsilon", -float.Epsilon),
            new Original("MaxValue", float.MaxValue),
            new Original("MinValue", float.MinValue),
        };

        private static void AssertSameBits(float expected, FakeTarget target, string because)
        {
            var actual = target.Value;
            Assert.That(
                Bits(actual),
                Is.EqualTo(Bits(expected)),
                because + "（" + target + " が本家の値に戻っていない: 期待 0x" + Bits(expected).ToString("X8") + " / 実際 0x" + Bits(actual).ToString("X8") + " = " + actual + "）");
        }

        private static void AssertZero(FakeTarget target, string because)
        {
            Assert.That(target.Value, Is.EqualTo(0f), because + "（" + target + " が 0 でない）");
        }

        // ---- 座る・立つ ----

        [Test]
        public void Apply_WritesZero_WhenSitting()
        {
            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();

                offset.Apply(target, true);

                AssertZero(target, "本家の値 " + original.Label);
            }
        }

        [Test]
        public void Apply_RestoresOriginalBits_WhenSittingEnds()
        {
            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();

                offset.Apply(target, false);
                AssertSameBits(original.Value, target, "本家の値 " + original.Label + "、座る前");
                offset.Apply(target, true);
                AssertZero(target, "本家の値 " + original.Label + "、座った");
                offset.Apply(target, false);

                AssertSameBits(original.Value, target, "本家の値 " + original.Label + "、立った");
            }
        }

        [Test]
        public void Apply_KeepsOriginalBits_WhileNeverSitting()
        {
            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();

                for (var frame = 0; frame < 5; frame++)
                {
                    offset.Apply(target, false);
                    AssertSameBits(original.Value, target, "本家の値 " + original.Label + "、frame " + frame);
                }
            }
        }

        [Test]
        public void Apply_ReturnsToSameOriginal_AcrossRepeatedRoundTrips()
        {
            var sittings = new[] { true, false, true, true, false, false, true, true, true, false, true, false, false, false, true, false };

            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();

                for (var frame = 0; frame < sittings.Length; frame++)
                {
                    offset.Apply(target, sittings[frame]);

                    var because = "本家の値 " + original.Label + "、frame " + frame + " sitting=" + sittings[frame];
                    if (sittings[frame])
                    {
                        AssertZero(target, because);
                    }
                    else
                    {
                        AssertSameBits(original.Value, target, because);
                    }
                }
            }
        }

        [Test]
        public void Apply_KeepsFirstSeenValueAsOriginal_WhileSittingRepeats()
        {
            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();
                offset.Apply(target, false);

                for (var frame = 0; frame < 10; frame++)
                {
                    offset.Apply(target, true);
                    AssertZero(target, "本家の値 " + original.Label + "、座って frame " + frame);
                }

                offset.Apply(target, false);

                AssertSameBits(original.Value, target, "本家の値 " + original.Label + "、座っている間に 0 を覚え直した");
            }
        }

        [Test]
        public void Apply_RemembersValueBeforeWritingZero_WhenFirstCallIsSitting()
        {
            foreach (var original in Originals)
            {
                var target = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();

                offset.Apply(target, true);
                AssertZero(target, "本家の値 " + original.Label + "、最初から座っている");
                offset.Apply(target, true);
                offset.Apply(target, false);

                AssertSameBits(original.Value, target, "本家の値 " + original.Label + "、最初の呼び出しの時点の値");
            }
        }

        // ---- ターゲットの切り替え ----

        [Test]
        public void Apply_RestoresPreviousTarget_WhenAnotherTargetArrivesWhileSitting()
        {
            foreach (var original in Originals)
            {
                var because = "A の本家の値 " + original.Label;
                var a = new FakeTarget("A", original.Value);
                var b = new FakeTarget("B", SceneValue);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);

                offset.Apply(b, true);

                AssertSameBits(original.Value, a, because);
                AssertZero(b, because);

                var aWrites = a.WriteCount;
                offset.Apply(b, false);
                AssertSameBits(SceneValue, b, because);
                offset.Apply(b, true);
                offset.Apply(b, true);
                offset.Apply(b, false);
                offset.Apply(b, true);

                Assert.That(a.WriteCount, Is.EqualTo(aWrites), because + "（手放した A に書き込んだ）");
                AssertSameBits(original.Value, a, because);
                AssertZero(b, because);
            }
        }

        [Test]
        public void Apply_RestoresPreviousTarget_WhenAnotherTargetArrivesNotSitting()
        {
            foreach (var original in Originals)
            {
                var because = "A の本家の値 " + original.Label;
                var a = new FakeTarget("A", original.Value);
                var b = new FakeTarget("B", 0.5f);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);

                offset.Apply(b, false);

                AssertSameBits(original.Value, a, because);
                AssertSameBits(0.5f, b, because);

                var aWrites = a.WriteCount;
                offset.Apply(b, true);
                offset.Apply(b, false);

                Assert.That(a.WriteCount, Is.EqualTo(aWrites), because + "（手放した A に書き込んだ）");
                AssertSameBits(original.Value, a, because);
            }
        }

        [Test]
        public void Apply_KeepsEachTargetsOriginal_WhenSwitchingBackToEarlierTarget()
        {
            foreach (var original in Originals)
            {
                var because = "A の本家の値 " + original.Label;
                var a = new FakeTarget("A", original.Value);
                var b = new FakeTarget("B", float.NaN);
                var offset = new SitProbeOffset();

                for (var round = 0; round < 3; round++)
                {
                    var roundBecause = because + "、round " + round;
                    offset.Apply(a, true);
                    AssertZero(a, roundBecause);
                    AssertSameBits(float.NaN, b, roundBecause);

                    offset.Apply(b, true);
                    AssertSameBits(original.Value, a, roundBecause);
                    AssertZero(b, roundBecause);

                    offset.Apply(a, true);
                    AssertZero(a, roundBecause);
                    AssertSameBits(float.NaN, b, roundBecause);

                    offset.Apply(a, false);
                    AssertSameBits(original.Value, a, roundBecause + "、A に戻ってから立った");
                }
            }
        }

        [Test]
        public void Apply_TellsTargetsApartByReference_WhenTheyCompareEqual()
        {
            var a = new FakeTarget("A", SceneValue, "twin");
            var twin = new FakeTarget("A'", SceneValue, "twin");
            Assert.That(a.Equals(twin) && a.GetHashCode() == twin.GetHashCode(), Is.True, "前提: Equals で等しい別インスタンス");
            var offset = new SitProbeOffset();
            offset.Apply(a, true);

            offset.Apply(twin, true);

            AssertSameBits(SceneValue, a, "Equals が等しくても別のターゲット");
            AssertZero(twin, "Equals が等しくても別のターゲット");

            var aWrites = a.WriteCount;
            offset.Apply(twin, false);
            AssertSameBits(SceneValue, twin, "twin が立った");
            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "手放した A に書き込んだ");

            offset.Apply(a, true);
            AssertZero(a, "A に戻った");
            AssertSameBits(SceneValue, twin, "A に戻った");
        }

        [Test]
        public void Apply_KeepsOwnOriginal_WhenTargetsCompareEqualButHoldDifferentValues()
        {
            var a = new FakeTarget("A", SceneValue, "twin");
            var twin = new FakeTarget("A'", 0.5f, "twin");
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Apply(twin, true);

            offset.Apply(twin, false);

            AssertSameBits(0.5f, twin, "twin の本家の値は twin を受け取った時点の値");
            AssertSameBits(SceneValue, a, "A の本家の値");
        }

        // ---- null ----

        [Test]
        public void Apply_RestoresPreviousTarget_WhenTargetBecomesNull()
        {
            foreach (var original in Originals)
            {
                foreach (var sittingWithNull in new[] { true, false })
                {
                    var because = "A の本家の値 " + original.Label + "、Apply(null, " + sittingWithNull + ")";
                    var a = new FakeTarget("A", original.Value);
                    var offset = new SitProbeOffset();
                    offset.Apply(a, true);

                    offset.Apply(null, sittingWithNull);

                    AssertSameBits(original.Value, a, because);
                }
            }
        }

        [Test]
        public void Apply_WritesNothing_WhileTargetIsNull()
        {
            var a = new FakeTarget("A", SceneValue);
            var untouched = new FakeTarget("B", 0.5f);
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Apply(null, true);
            var aWrites = a.WriteCount;

            offset.Apply(null, true);
            offset.Apply(null, false);
            offset.Apply(null, true);
            offset.Release();
            offset.Apply(null, true);

            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "null の間に A に書き込んだ");
            Assert.That(untouched.WriteCount, Is.EqualTo(0));
            AssertSameBits(SceneValue, a, "null の間");
        }

        [Test]
        public void Apply_DoesNotThrow_WhenFirstTargetIsNull()
        {
            var offset = new SitProbeOffset();

            Assert.DoesNotThrow(() => offset.Apply(null, true));
            Assert.DoesNotThrow(() => offset.Apply(null, false));
        }

        [Test]
        public void Apply_TakesTargetAgain_AfterNull()
        {
            foreach (var original in Originals)
            {
                var because = "A の本家の値 " + original.Label;
                var a = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);
                offset.Apply(null, true);

                offset.Apply(a, true);
                AssertZero(a, because + "、受け取り直して座った");
                offset.Apply(a, false);

                AssertSameBits(original.Value, a, because + "、受け取り直して立った");
            }
        }

        // ---- Release ----

        [Test]
        public void Release_RestoresCurrentTarget_WhenSitting()
        {
            foreach (var original in Originals)
            {
                var a = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);

                offset.Release();

                AssertSameBits(original.Value, a, "本家の値 " + original.Label);
            }
        }

        [Test]
        public void Release_KeepsOriginal_WhenNotSitting()
        {
            foreach (var original in Originals)
            {
                var a = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);
                offset.Apply(a, false);

                offset.Release();

                AssertSameBits(original.Value, a, "本家の値 " + original.Label);
            }
        }

        [Test]
        public void Release_WritesNothingMore_WhenCalledAgain()
        {
            var a = new FakeTarget("A", SceneValue);
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Release();
            var aWrites = a.WriteCount;

            Assert.DoesNotThrow(() => offset.Release());
            Assert.DoesNotThrow(() => offset.Release());

            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "手放した A に書き込んだ");
            AssertSameBits(SceneValue, a, "Release を重ねた");
        }

        [Test]
        public void Release_DoesNotThrow_WhenNoTargetWasEverReceived()
        {
            var offset = new SitProbeOffset();

            Assert.DoesNotThrow(() => offset.Release());
            Assert.DoesNotThrow(() => offset.Release());
        }

        [Test]
        public void Release_WritesNothing_AfterTargetBecameNull()
        {
            var a = new FakeTarget("A", SceneValue);
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Apply(null, false);
            var aWrites = a.WriteCount;

            Assert.DoesNotThrow(() => offset.Release());

            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "null で手放した A に書き込んだ");
            AssertSameBits(SceneValue, a, "Apply(null) のあとの Release");
        }

        [Test]
        public void Release_RestoresOnlyCurrentTarget_AfterSwitch()
        {
            var a = new FakeTarget("A", SceneValue);
            var b = new FakeTarget("B", 0.5f);
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Apply(b, true);
            var aWrites = a.WriteCount;

            offset.Release();

            AssertSameBits(0.5f, b, "今のターゲット B");
            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "前に手放した A に書き込んだ");
            AssertSameBits(SceneValue, a, "前に手放した A");
        }

        [Test]
        public void Apply_StopsWritingToReleasedTarget_WhenAnotherTargetArrives()
        {
            var a = new FakeTarget("A", SceneValue);
            var b = new FakeTarget("B", 0.5f);
            var offset = new SitProbeOffset();
            offset.Apply(a, true);
            offset.Release();
            var aWrites = a.WriteCount;

            offset.Apply(b, true);
            offset.Apply(b, false);
            offset.Release();

            Assert.That(a.WriteCount, Is.EqualTo(aWrites), "Release で手放した A に書き込んだ");
            AssertSameBits(SceneValue, a, "Release で手放した A");
        }

        [Test]
        public void Apply_TakesCurrentValueAsOriginal_WhenTargetIsReceivedAgainAfterRelease()
        {
            foreach (var original in Originals)
            {
                var because = "受け取り直したときの値 " + original.Label;
                var a = new FakeTarget("A", SceneValue);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);
                offset.Release();
                a.SetByScene(original.Value);

                offset.Apply(a, true);
                AssertZero(a, because);
                offset.Apply(a, false);
                AssertSameBits(original.Value, a, because);
                offset.Apply(a, true);
                offset.Apply(a, false);

                AssertSameBits(original.Value, a, because + "、往復のあと");
            }
        }

        [Test]
        public void Apply_WorksAsBefore_WhenSameTargetIsReceivedAgainAfterRelease()
        {
            foreach (var original in Originals)
            {
                var because = "本家の値 " + original.Label;
                var a = new FakeTarget("A", original.Value);
                var offset = new SitProbeOffset();
                offset.Apply(a, true);
                offset.Release();

                offset.Apply(a, true);
                AssertZero(a, because);
                offset.Apply(a, true);
                offset.Apply(a, false);

                AssertSameBits(original.Value, a, because);
            }
        }

        // ---- 性質（ランダムな操作列） ----

        [Test]
        public void SitProbeOffset_KeepsZeroOnlyOnSittingCurrentTargetAndWritesOnlyToTargetsInPlay_ForRandomOperationSequences()
        {
            for (var seed = 0; seed < 300; seed++)
            {
                var random = new Random(seed);
                var targets = new[]
                {
                    new FakeTarget("A", SceneValue),
                    new FakeTarget("B", float.NaN),
                    new FakeTarget("C", FromBits(unchecked((int)0x80000000))),
                    new FakeTarget("D", float.NegativeInfinity, "twin"),
                    new FakeTarget("D'", 0.5f, "twin"),
                    new FakeTarget("E", FromBits(0x7FC00123)),
                };
                var originals = targets.Select(t => t.Value).ToArray();
                var offset = new SitProbeOffset();
                var log = new List<string>();
                FakeTarget current = null;
                var lastSitting = false;

                for (var step = 0; step < 200; step++)
                {
                    var writesBefore = targets.Select(t => t.WriteCount).ToArray();
                    var previous = current;
                    var op = random.Next(100);
                    if (op < 8)
                    {
                        log.Add("release");
                        offset.Release();
                        current = null;
                    }
                    else
                    {
                        var pick = random.Next(targets.Length + 1);
                        var target = pick == targets.Length ? null : targets[pick];
                        var sitting = random.Next(2) == 0;
                        log.Add("apply(" + (target == null ? "null" : target.ToString()) + "," + sitting + ")");
                        offset.Apply(target, sitting);
                        current = target;
                        lastSitting = sitting;
                    }

                    var because = "seed " + seed + " step " + step + " log: " + string.Join(" ", log.Skip(Math.Max(0, log.Count - 20)));
                    for (var i = 0; i < targets.Length; i++)
                    {
                        var target = targets[i];
                        if (ReferenceEquals(target, current) && lastSitting)
                        {
                            AssertZero(target, because);
                        }
                        else
                        {
                            AssertSameBits(originals[i], target, because);
                        }

                        if (!ReferenceEquals(target, previous) && !ReferenceEquals(target, current))
                        {
                            Assert.That(target.WriteCount, Is.EqualTo(writesBefore[i]), because + "（関係のない " + target + " に書き込んだ）");
                        }
                    }
                }
            }
        }
    }
}
