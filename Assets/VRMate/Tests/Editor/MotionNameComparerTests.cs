using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// モーションの名前を比べる基準。Windows のファイル名は大文字・小文字を区別しないので、
    /// 書き方が違っても同じ .vrma を指す名前は同じ鍵になること。
    /// </summary>
    public class MotionNameComparerTests
    {
        private const string MotionAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

        private static IEqualityComparer<string> Comparer
        {
            get { return MotionFiles.NameComparer; }
        }

        private static char FoldAscii(char c)
        {
            return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
        }

        private static string Fold(string value)
        {
            return new string(value.Select(FoldAscii).ToArray());
        }

        private static string RandomCase(string name, Random random)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsLetter(chars[i]) && random.Next(2) == 0)
                {
                    chars[i] = chars[i] >= 'a' ? (char)(chars[i] - 32) : (char)(chars[i] + 32);
                }
            }

            return new string(chars);
        }

        private static string RandomName(Random random, int length)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < length; i++)
            {
                builder.Append(MotionAlphabet[random.Next(MotionAlphabet.Length)]);
            }

            return builder.ToString();
        }

        private static void AssertSameKey(string left, string right, string because)
        {
            Assert.That(Comparer.Equals(left, right), Is.True, because + ": " + left + " / " + right);
            Assert.That(Comparer.Equals(right, left), Is.True, because + ": " + right + " / " + left);
            Assert.That(Comparer.GetHashCode(left), Is.EqualTo(Comparer.GetHashCode(right)), because + "（ハッシュが違う）: " + left + " / " + right);
        }

        private static void AssertDifferentKey(string left, string right, string because)
        {
            Assert.That(Comparer.Equals(left, right), Is.False, because + ": " + left + " / " + right);
            Assert.That(Comparer.Equals(right, left), Is.False, because + ": " + right + " / " + left);
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

        // ---- 同じ名前 ----

        [TestCase("VRMA_02", "vrma_02")]
        [TestCase("VRMA_02", "Vrma_02")]
        [TestCase("vrma_02", "vRmA_02")]
        [TestCase("wave", "WAVE")]
        [TestCase("bow-deep", "Bow-Deep")]
        [TestCase("a", "A")]
        [TestCase("z", "Z")]
        [TestCase("Iii", "iII")]
        [TestCase("_-0_9", "_-0_9")]
        public void Equals_IsTrueAndHashesMatch_WhenNamesDifferOnlyInAsciiLetterCase(string left, string right)
        {
            Assert.That(CueNames.IsMotion(left) && CueNames.IsMotion(right), Is.True, "前提: どちらもモーションの名前");

            AssertSameKey(left, right, "書き方違い");
        }

        [Test]
        public void Equals_IsTrueAndHashesMatch_ForRandomLetterCasesOfWellFormedNames()
        {
            var random = new Random(20261018);
            var names = new List<string>
            {
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-",
                new string('a', CueNames.MaxMotionLength),
                "VRMA_02",
            };
            for (var i = 0; i < 300; i++)
            {
                names.Add(RandomName(random, 1 + random.Next(CueNames.MaxMotionLength)));
            }

            foreach (var name in names)
            {
                Assert.That(CueNames.IsMotion(name), Is.True, "前提: " + name);
                for (var variant = 0; variant < 8; variant++)
                {
                    AssertSameKey(name, RandomCase(name, random), "ランダムな書き方違い");
                }

                AssertSameKey(name, name.ToUpperInvariant(), "すべて大文字");
                AssertSameKey(name, name.ToLowerInvariant(), "すべて小文字");
            }
        }

        [Test]
        public void Equals_MatchesAsciiCaseFolding_ForEveryPairOfMotionNameCharacters()
        {
            var wrong = new List<string>();
            foreach (var a in MotionAlphabet)
            {
                foreach (var b in MotionAlphabet)
                {
                    var expected = FoldAscii(a) == FoldAscii(b);
                    var alone = Comparer.Equals(a.ToString(), b.ToString());
                    var inside = Comparer.Equals("x" + a + "_9", "x" + b + "_9");
                    if (alone != expected || inside != expected)
                    {
                        wrong.Add(a + "/" + b + " expected " + expected);
                    }

                    if (expected && Comparer.GetHashCode(a.ToString()) != Comparer.GetHashCode(b.ToString()))
                    {
                        wrong.Add(a + "/" + b + " hash");
                    }
                }
            }

            Assert.That(wrong, Is.Empty, string.Join(", ", wrong.Take(20)));
        }

        // ---- 違う名前 ----

        [TestCase("wave", "wavf")]
        [TestCase("wave", "wave2")]
        [TestCase("wave", "wav")]
        [TestCase("wave", "")]
        [TestCase("a_b", "a-b")]
        [TestCase("_", "-")]
        [TestCase("a-b", "ab")]
        [TestCase("co-op", "coop")]
        [TestCase("a_b", "ab")]
        [TestCase("O", "0")]
        [TestCase("l", "1")]
        [TestCase("VRMA_02", "VRMA_20")]
        [TestCase("VRMA_02", "VRMA_02_")]
        public void Equals_IsFalse_WhenNamesDifferBeyondLetterCase(string left, string right)
        {
            AssertDifferentKey(left, right, "違う名前");
        }

        [Test]
        public void Equals_IsFalse_ForRandomNamesThatDifferAfterFolding()
        {
            var random = new Random(7);
            for (var i = 0; i < 2000; i++)
            {
                var left = RandomName(random, 1 + random.Next(6));
                var right = RandomName(random, 1 + random.Next(6));
                if (Fold(left) == Fold(right))
                {
                    continue;
                }

                AssertDifferentKey(left, right, "畳んでも違う");
            }
        }

        // ---- null ----

        [Test]
        public void Equals_HandlesNullWithoutThrowing()
        {
            var result = false;

            Assert.DoesNotThrow(() => result = Comparer.Equals(null, null));
            Assert.That(result, Is.True);
            Assert.DoesNotThrow(() => result = Comparer.Equals(null, "a"));
            Assert.That(result, Is.False);
            Assert.DoesNotThrow(() => result = Comparer.Equals("a", null));
            Assert.That(result, Is.False);
            Assert.That(Comparer.Equals(null, string.Empty), Is.False);
        }

        // ---- 鍵として使う ----

        [Test]
        public void Dictionary_HoldsOneEntry_WhenOneNameIsAddedInSeveralLetterCases()
        {
            var dictionary = new Dictionary<string, int>(MotionFiles.NameComparer);
            var set = new HashSet<string>(MotionFiles.NameComparer);
            var variants = new[] { "VRMA_02", "vrma_02", "Vrma_02", "vRMA_02", "VrMa_02" };

            foreach (var variant in variants)
            {
                int count;
                dictionary[variant] = dictionary.TryGetValue(variant, out count) ? count + 1 : 1;
                set.Add(variant);
            }

            Assert.That(dictionary.Count, Is.EqualTo(1));
            Assert.That(dictionary.Values.Single(), Is.EqualTo(variants.Length));
            Assert.That(set.Count, Is.EqualTo(1));
            Assert.That(set.Contains("VRMA_02"), Is.True);
            Assert.That(dictionary.ContainsKey("vrMA_02"), Is.True);
            Assert.That(dictionary.ContainsKey("VRMA_03"), Is.False);
        }

        [Test]
        public void Dictionary_KeepsDistinctNamesApart()
        {
            var set = new HashSet<string>(MotionFiles.NameComparer) { "a_b", "a-b", "ab", "AB", "aB", "_", "-" };

            Assert.That(set.Count, Is.EqualTo(5), string.Join(",", set));
        }

        // ---- 文化に依存しない ----

        [Test]
        public void Equals_IgnoresOnlyAsciiCase_WhenCurrentCultureIsTurkish()
        {
            WithTurkishCulture(() =>
            {
                AssertSameKey("wavi", "WAVI", "tr-TR でも i と I は同じ");
                AssertSameKey("LIFT_I", "lift_i", "tr-TR でも i と I は同じ");
                foreach (var c in "abcdefghijklmnopqrstuvwxyz")
                {
                    AssertSameKey("m" + c, "M" + char.ToUpperInvariant(c), "tr-TR");
                }

                AssertDifferentKey("a_b", "a-b", "tr-TR");
                AssertDifferentKey("wave", "wavi", "tr-TR");

                var set = new HashSet<string>(MotionFiles.NameComparer) { "idle_i", "IDLE_I", "Idle_I", "iDLE_i" };
                Assert.That(set.Count, Is.EqualTo(1), "tr-TR でも 1 件");
            });
        }
    }
}
