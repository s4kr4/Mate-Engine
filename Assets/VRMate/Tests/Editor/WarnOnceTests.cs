using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class WarnOnceTests
    {
        [Test]
        public void MaxKeys_Is256()
        {
            Assert.That(WarnOnce.MaxKeys, Is.EqualTo(256));
        }

        [Test]
        public void ShouldWarn_ReturnsTrueOnlyTheFirstTime_ForEachKey()
        {
            var warn = new WarnOnce();

            Assert.That(warn.ShouldWarn("motion: not found 'wave'"), Is.True);
            Assert.That(warn.ShouldWarn("motion: not found 'wave'"), Is.False);
            Assert.That(warn.ShouldWarn("motion: not found 'wave'"), Is.False);
            Assert.That(warn.ShouldWarn("motion: not found 'bow'"), Is.True);
            Assert.That(warn.ShouldWarn("motion: not found 'bow'"), Is.False);
        }

        [Test]
        public void ShouldWarn_ComparesKeysOrdinally()
        {
            var warn = new WarnOnce();
            var composed = "caf" + (char)0x00E9;
            var decomposed = "cafe" + (char)0x0301;

            Assert.That(warn.ShouldWarn("wave"), Is.True);
            Assert.That(warn.ShouldWarn("Wave"), Is.True, "大文字小文字は別の鍵");
            Assert.That(warn.ShouldWarn("WAVE"), Is.True);
            Assert.That(warn.ShouldWarn("wave "), Is.True, "空白の違いは別の鍵");
            Assert.That(warn.ShouldWarn(composed), Is.True);
            Assert.That(warn.ShouldWarn(decomposed), Is.True, "正規化しない");
            Assert.That(warn.ShouldWarn("wave" + (char)0), Is.True);
            Assert.That(warn.ShouldWarn("I"), Is.True);
            Assert.That(warn.ShouldWarn("i"), Is.True);
            Assert.That(warn.ShouldWarn("" + (char)0x0130), Is.True);
            Assert.That(warn.ShouldWarn("" + (char)0x0131), Is.True);
        }

        [Test]
        public void ShouldWarn_TreatsNullAsEmptyKey()
        {
            var warn = new WarnOnce();

            Assert.That(warn.ShouldWarn(null), Is.True);
            Assert.That(warn.ShouldWarn(string.Empty), Is.False);
            Assert.That(warn.ShouldWarn(null), Is.False);

            var other = new WarnOnce();
            Assert.That(other.ShouldWarn(string.Empty), Is.True);
            Assert.That(other.ShouldWarn(null), Is.False);
        }

        [Test]
        public void ShouldWarn_KeepsInstancesIndependent()
        {
            var first = new WarnOnce();
            var second = new WarnOnce();

            Assert.That(first.ShouldWarn("k"), Is.True);
            Assert.That(second.ShouldWarn("k"), Is.True);
        }

        [Test]
        public void ShouldWarn_RemembersExactly256Keys_AndRefusesNewKeysAfterThat()
        {
            var warn = new WarnOnce();
            var keys = Enumerable.Range(0, 256).Select(i => "key " + i).ToArray();

            var firstTimes = keys.Select(k => warn.ShouldWarn(k)).ToArray();

            Assert.That(firstTimes.All(b => b), Is.True, "256 個目まではすべて警告する: false at " + string.Join(",", firstTimes.Select((b, i) => b ? -1 : i).Where(i => i >= 0)));
            Assert.That(warn.ShouldWarn("key 256"), Is.False, "257 個目の新しい鍵");
            Assert.That(warn.ShouldWarn("another"), Is.False);
            Assert.That(warn.ShouldWarn(null), Is.False, "覚えていない空の鍵も新しい鍵");
            Assert.That(keys.Any(k => warn.ShouldWarn(k)), Is.False, "覚えた鍵は 2 回目以降 false");
            Assert.That(warn.ShouldWarn("key 256"), Is.False, "断った鍵を 2 回目に覚え直さない");
        }

        [Test]
        public void ShouldWarn_StaysBounded_WhenManyDistinctKeysArrive()
        {
            var warn = new WarnOnce();

            var warned = Enumerable.Range(0, 100000).Count(i => warn.ShouldWarn("motion: unknown 'name" + i + "'"));

            Assert.That(warned, Is.EqualTo(256));
        }

        [Test]
        public void ShouldWarn_CountsRepeatedKeysOnce_TowardTheLimit()
        {
            var warn = new WarnOnce();
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 255; i++)
                {
                    warn.ShouldWarn("key " + i);
                }
            }

            Assert.That(warn.ShouldWarn("last"), Is.True, "同じ鍵を何度聞いても 1 個として数える");
            Assert.That(warn.ShouldWarn("over"), Is.False);
        }
    }
}
