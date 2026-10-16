using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class ReplyIdTests
    {
        private static string Describe(ReplyId id)
        {
            return "(c" + id.Connection + ",t" + id.Turn + ")";
        }

        private static string Describe(ReplyId a, ReplyId b)
        {
            return Describe(a) + " と " + Describe(b);
        }

        /// <summary>等値を判定するすべての経路が「等しい」と答え、ハッシュも一致することを確かめる。</summary>
        private static void AssertEqualOnAllPaths(ReplyId a, ReplyId b)
        {
            var because = Describe(a, b);
            IEquatable<ReplyId> equatable = a;

            Assert.That(a.Equals(b), Is.True, because + " を Equals(ReplyId) が等しいと判定しない");
            Assert.That(b.Equals(a), Is.True, because + " を逆向きの Equals(ReplyId) が等しいと判定しない");
            Assert.That(equatable.Equals(b), Is.True, because + " を IEquatable<ReplyId>.Equals が等しいと判定しない");
            Assert.That(a.Equals((object)b), Is.True, because + " を Equals(object) が等しいと判定しない");
            Assert.That(b.Equals((object)a), Is.True, because + " を逆向きの Equals(object) が等しいと判定しない");
            Assert.That(a == b, Is.True, because + " を == が等しいと判定しない");
            Assert.That(b == a, Is.True, because + " を逆向きの == が等しいと判定しない");
            Assert.That(a != b, Is.False, because + " を != が異なると判定する");
            Assert.That(b != a, Is.False, because + " を逆向きの != が異なると判定する");
            Assert.That(EqualityComparer<ReplyId>.Default.Equals(a, b), Is.True, because + " を既定の比較子が等しいと判定しない");
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()), because + " のハッシュが一致しない");
        }

        /// <summary>等値を判定するすべての経路が「等しくない」と答えることを確かめる。</summary>
        private static void AssertNotEqualOnAllPaths(ReplyId a, ReplyId b)
        {
            var because = Describe(a, b);
            IEquatable<ReplyId> equatable = a;

            Assert.That(a.Equals(b), Is.False, because + " を Equals(ReplyId) が等しいと判定する");
            Assert.That(b.Equals(a), Is.False, because + " を逆向きの Equals(ReplyId) が等しいと判定する");
            Assert.That(equatable.Equals(b), Is.False, because + " を IEquatable<ReplyId>.Equals が等しいと判定する");
            Assert.That(a.Equals((object)b), Is.False, because + " を Equals(object) が等しいと判定する");
            Assert.That(b.Equals((object)a), Is.False, because + " を逆向きの Equals(object) が等しいと判定する");
            Assert.That(a == b, Is.False, because + " を == が等しいと判定する");
            Assert.That(b == a, Is.False, because + " を逆向きの == が等しいと判定する");
            Assert.That(a != b, Is.True, because + " を != が異なると判定しない");
            Assert.That(b != a, Is.True, because + " を逆向きの != が異なると判定しない");
            Assert.That(EqualityComparer<ReplyId>.Default.Equals(a, b), Is.False, because + " を既定の比較子が等しいと判定する");
        }

        // ---- 値の保持 ----

        [TestCase(0L, 0L)]
        [TestCase(1L, 2L)]
        [TestCase(2L, 1L)]
        [TestCase(-1L, -2L)]
        [TestCase(4294967296L, 4294967297L)]
        [TestCase(long.MinValue, long.MaxValue)]
        [TestCase(long.MaxValue, long.MinValue)]
        public void Constructor_KeepsConnectionAndTurn(long connection, long turn)
        {
            var id = new ReplyId(connection, turn);

            Assert.That(id.Connection, Is.EqualTo(connection), "Connection が渡した値と違う");
            Assert.That(id.Turn, Is.EqualTo(turn), "Turn が渡した値と違う");
        }

        // ---- 等しい組 ----

        [TestCase(0L, 0L)]
        [TestCase(0L, 1L)]
        [TestCase(3L, 7L)]
        [TestCase(-5L, -9L)]
        [TestCase(4294967296L, -4294967296L)]
        [TestCase(long.MinValue, long.MinValue)]
        [TestCase(long.MaxValue, long.MaxValue)]
        [TestCase(long.MinValue, long.MaxValue)]
        [TestCase(9007199254740993L, long.MaxValue - 1)]
        [TestCase(16777217L, long.MinValue + 1)]
        public void Equality_IsEqual_WhenConnectionAndTurnAreBothEqual(long connection, long turn)
        {
            var a = new ReplyId(connection, turn);
            var b = new ReplyId(connection, turn);

            AssertEqualOnAllPaths(a, b);
        }

        [TestCase(0L, 0L)]
        [TestCase(long.MinValue, long.MaxValue)]
        public void Equality_IsEqual_WhenComparedWithItself(long connection, long turn)
        {
            var a = new ReplyId(connection, turn);

            AssertEqualOnAllPaths(a, a);
        }

        [Test]
        public void Equality_IsEqual_BetweenDefaultAndZeroZero()
        {
            AssertEqualOnAllPaths(default(ReplyId), new ReplyId(0, 0));
        }

        // ---- Connection だけ違う組 ----

        [TestCase(0L, 1L, 1L, TestName = "Equality_IsNotEqual_WhenSameTurnArrivesOnNewConnectionAfterRestart")]
        [TestCase(1L, 2L, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionDiffersByOne")]
        [TestCase(1L, -1L, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionSignDiffers")]
        [TestCase(0L, 4294967296L, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionUpperBitsDiffer")]
        [TestCase(0L, long.MinValue, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionIsZeroVersusMinValue")]
        [TestCase(long.MinValue, long.MaxValue, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionIsMinValueVersusMaxValue")]
        [TestCase(16777216L, 16777217L, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionDiffersByOneAroundTwoToThe24")]
        [TestCase(9007199254740992L, 9007199254740993L, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionDiffersByOneAroundTwoToThe53")]
        [TestCase(long.MaxValue, long.MaxValue - 1, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionDiffersByOneBelowMaxValue")]
        [TestCase(long.MinValue, long.MinValue + 1, 7L, TestName = "Equality_IsNotEqual_WhenOnlyConnectionDiffersByOneAboveMinValue")]
        public void Equality_IsNotEqual_WhenOnlyConnectionDiffers(long connectionA, long connectionB, long turn)
        {
            var a = new ReplyId(connectionA, turn);
            var b = new ReplyId(connectionB, turn);

            AssertNotEqualOnAllPaths(a, b);
        }

        // ---- Turn だけ違う組 ----

        [TestCase(5L, 1L, 2L, TestName = "Equality_IsNotEqual_WhenOnlyTurnDiffersByOne")]
        [TestCase(5L, 1L, -1L, TestName = "Equality_IsNotEqual_WhenOnlyTurnSignDiffers")]
        [TestCase(5L, 1L, 4294967297L, TestName = "Equality_IsNotEqual_WhenOnlyTurnUpperBitsDiffer")]
        [TestCase(5L, 0L, long.MinValue, TestName = "Equality_IsNotEqual_WhenOnlyTurnIsZeroVersusMinValue")]
        [TestCase(5L, long.MinValue, long.MaxValue, TestName = "Equality_IsNotEqual_WhenOnlyTurnIsMinValueVersusMaxValue")]
        [TestCase(5L, 16777216L, 16777217L, TestName = "Equality_IsNotEqual_WhenOnlyTurnDiffersByOneAroundTwoToThe24")]
        [TestCase(5L, 9007199254740992L, 9007199254740993L, TestName = "Equality_IsNotEqual_WhenOnlyTurnDiffersByOneAroundTwoToThe53")]
        [TestCase(5L, long.MaxValue, long.MaxValue - 1, TestName = "Equality_IsNotEqual_WhenOnlyTurnDiffersByOneBelowMaxValue")]
        [TestCase(5L, long.MinValue, long.MinValue + 1, TestName = "Equality_IsNotEqual_WhenOnlyTurnDiffersByOneAboveMinValue")]
        public void Equality_IsNotEqual_WhenOnlyTurnDiffers(long connection, long turnA, long turnB)
        {
            var a = new ReplyId(connection, turnA);
            var b = new ReplyId(connection, turnB);

            AssertNotEqualOnAllPaths(a, b);
        }

        // ---- 両方違う組 ----

        [TestCase(0L, 1L, 1L, 2L)]
        [TestCase(1L, 2L, 2L, 1L)]
        [TestCase(0L, 0L, long.MinValue, long.MinValue)]
        [TestCase(long.MaxValue, long.MinValue, long.MinValue, long.MaxValue)]
        [TestCase(-1L, 4294967296L, 4294967296L, -1L)]
        public void Equality_IsNotEqual_WhenBothConnectionAndTurnDiffer(long connectionA, long turnA, long connectionB, long turnB)
        {
            var a = new ReplyId(connectionA, turnA);
            var b = new ReplyId(connectionB, turnB);

            AssertNotEqualOnAllPaths(a, b);
        }

        [Test]
        public void Equality_IsNotEqual_WhenConnectionAndTurnAreSwapped()
        {
            AssertNotEqualOnAllPaths(new ReplyId(1, 2), new ReplyId(2, 1));
        }

        // ---- Equals(object) の型違い ----

        [Test]
        public void EqualsObject_ReturnsFalse_WhenOtherIsNull()
        {
            var id = new ReplyId(0, 0);

            Assert.That(id.Equals(null), Is.False, "null と等しいと判定する");
        }

        [TestCase(0L)]
        [TestCase(1L)]
        public void EqualsObject_ReturnsFalse_WhenOtherIsBoxedLong(long value)
        {
            var id = new ReplyId(value, value);

            Assert.That(id.Equals((object)value), Is.False, "boxed long と等しいと判定する");
        }

        [Test]
        public void EqualsObject_ReturnsFalse_WhenOtherIsString()
        {
            var id = new ReplyId(1, 2);

            Assert.That(id.Equals((object)"(1,2)"), Is.False, "string と等しいと判定する");
        }

        [Test]
        public void EqualsObject_ReturnsTrue_WhenOtherIsBoxedEqualReplyId()
        {
            var id = new ReplyId(3, 4);
            object boxed = new ReplyId(3, 4);

            Assert.That(id.Equals(boxed), Is.True, "boxed の同じ ReplyId と等しいと判定しない");
        }

        [Test]
        public void EqualsObject_ReturnsFalse_WhenOtherIsBoxedDifferentReplyId()
        {
            var id = new ReplyId(3, 4);
            object boxed = new ReplyId(3, 5);

            Assert.That(id.Equals(boxed), Is.False, "boxed の別の ReplyId と等しいと判定する");
        }

        // ---- 辞書のキーとしての振る舞い ----

        [Test]
        public void DictionaryKey_FindsValue_WhenLookedUpWithEqualReplyId()
        {
            var replies = new Dictionary<ReplyId, string>
            {
                { new ReplyId(0, 1), "再起動前" },
                { new ReplyId(1, 1), "再起動後" },
            };

            Assert.That(replies.Count, Is.EqualTo(2), "接続だけが違う 2 つの返事が同じキーになる");
            Assert.That(replies[new ReplyId(0, 1)], Is.EqualTo("再起動前"));
            Assert.That(replies[new ReplyId(1, 1)], Is.EqualTo("再起動後"));
            Assert.That(replies.ContainsKey(new ReplyId(1, 0)), Is.False, "入れ替えた組がキーとして見つかる");
        }
    }
}
