using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace VRMate.Tests
{
    public class CueNamesTests
    {
        private static readonly string[] Expressions = { "happy", "angry", "sad", "relaxed", "surprised", "neutral" };

        private static string Visible(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            foreach (var c in value)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    builder.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.Length > 80 ? builder.ToString(0, 80) + "...(" + value.Length + ")" : builder.ToString();
        }

        private static bool IsAsciiMotionChar(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
        }

        /// <summary>大文字と小文字のあらゆる組み合わせ（"con" → con, coN, cOn, ...）。</summary>
        private static IEnumerable<string> CaseVariants(string name)
        {
            var letters = name.Select((c, i) => new { c, i }).Where(x => char.IsLetter(x.c)).Select(x => x.i).ToArray();
            for (var mask = 0; mask < 1 << letters.Length; mask++)
            {
                var chars = name.ToCharArray();
                for (var bit = 0; bit < letters.Length; bit++)
                {
                    var index = letters[bit];
                    chars[index] = (mask & (1 << bit)) != 0 ? char.ToUpperInvariant(chars[index]) : char.ToLowerInvariant(chars[index]);
                }

                yield return new string(chars);
            }
        }

        private static IEnumerable<string> ReservedDeviceNames()
        {
            var names = new List<string> { "CON", "PRN", "AUX", "NUL" };
            for (var digit = 0; digit <= 9; digit++)
            {
                names.Add("COM" + digit);
                names.Add("LPT" + digit);
            }

            return names;
        }

        // ---- 表情の名前 ----

        [Test]
        public void IsExpression_ReturnsTrue_ForEachOfTheSixNames()
        {
            foreach (var name in Expressions)
            {
                Assert.That(CueNames.IsExpression(name), Is.True, name);
            }
        }

        [Test]
        public void IsExpression_ReturnsFalse_WhenNameIsNotAnExactOrdinalMatch()
        {
            var names = new List<string>
            {
                null,
                string.Empty,
                " ",
                "Happy",
                "HAPPY",
                "Neutral",
                "SURPRISED",
                " happy",
                "happy ",
                "\thappy",
                "happy\n",
                "happy\r\n",
                "happy" + (char)0,
                (char)0 + "happy",
                "happ",
                "happyy",
                "joy",
                "custom",
                "aa",
                "smile",
                "ｈａｐｐｙ",
                "surpr" + (char)0x0131 + "sed",
                "happ" + (char)0x0443,
                "happy" + (char)0x200B,
                (char)0xFEFF + "sad",
                "sad" + (char)0x3000,
                "neutral" + (char)0x00A0,
                "happy,sad",
                "happy sad",
            };

            foreach (var name in names)
            {
                Assert.That(CueNames.IsExpression(name), Is.False, Visible(name));
            }
        }

        // ---- モーションの名前: 長さ ----

        [Test]
        public void MaxMotionLength_Is64()
        {
            Assert.That(CueNames.MaxMotionLength, Is.EqualTo(64));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(63)]
        [TestCase(64)]
        public void IsMotion_ReturnsTrue_WhenAsciiNameLengthIsFromOneTo64(int length)
        {
            var name = new string('a', length);

            Assert.That(CueNames.IsMotion(name), Is.True, "length " + length);
            Assert.That(CueNames.IsMotion(new string('-', length)), Is.True, "'-' x " + length);
            Assert.That(CueNames.IsMotion(new string('9', length)), Is.True, "'9' x " + length);
        }

        [TestCase(65)]
        [TestCase(66)]
        [TestCase(1000)]
        [TestCase(100000)]
        public void IsMotion_ReturnsFalse_WhenNameIsLongerThan64(int length)
        {
            Assert.That(CueNames.IsMotion(new string('a', length)), Is.False, "length " + length);
            Assert.That(CueNames.IsMotion(new string('_', length)), Is.False, "'_' x " + length);
        }

        [Test]
        public void IsMotion_ReturnsFalse_WhenNameIsNullOrEmpty()
        {
            Assert.That(CueNames.IsMotion(null), Is.False);
            Assert.That(CueNames.IsMotion(string.Empty), Is.False);
        }

        // ---- モーションの名前: 文字 ----

        [TestCase("wave")]
        [TestCase("VRMA_02")]
        [TestCase("bow-deep")]
        [TestCase("_")]
        [TestCase("-")]
        [TestCase("--")]
        [TestCase("-rf")]
        [TestCase("0")]
        [TestCase("Az09_-zA")]
        [TestCase("COM10")]
        [TestCase("LPT10")]
        [TestCase("COM")]
        [TestCase("LPT")]
        [TestCase("CONX")]
        [TestCase("CON_")]
        [TestCase("_CON")]
        [TestCase("CON-1")]
        [TestCase("NULL")]
        [TestCase("AUXX")]
        [TestCase("PRN0")]
        [TestCase("COMA")]
        [TestCase("CO")]
        public void IsMotion_ReturnsTrue_WhenNameUsesOnlyAsciiLettersDigitsUnderscoreAndHyphen(string name)
        {
            Assert.That(CueNames.IsMotion(name), Is.True, name);
        }

        [Test]
        public void IsMotion_AcceptsExactlyAsciiLettersDigitsUnderscoreAndHyphen_ForEverySingleUtf16CodeUnit()
        {
            var wrong = new List<string>();
            for (var code = 0; code <= char.MaxValue; code++)
            {
                var c = (char)code;
                var expected = IsAsciiMotionChar(c);
                if (CueNames.IsMotion(c.ToString()) != expected)
                {
                    wrong.Add("U+" + code.ToString("X4") + " alone expected " + expected);
                }

                if (CueNames.IsMotion("ab" + c + "cd") != expected)
                {
                    wrong.Add("U+" + code.ToString("X4") + " inside expected " + expected);
                }
            }

            Assert.That(wrong, Is.Empty, string.Join(", ", wrong.Take(20)) + (wrong.Count > 20 ? " ...(" + wrong.Count + ")" : string.Empty));
        }

        [Test]
        public void IsMotion_SeparatesAllowedCharactersFromTheirAsciiNeighbours()
        {
            // 範囲の端と、その前後の文字
            var allowed = "09AZaz_-";
            var neighbours = "/:@[`{^,.";

            foreach (var c in allowed)
            {
                Assert.That(CueNames.IsMotion("x" + c + "y"), Is.True, c.ToString());
            }

            foreach (var c in neighbours)
            {
                Assert.That(CueNames.IsMotion("x" + c + "y"), Is.False, c.ToString());
                Assert.That(CueNames.IsMotion(c.ToString()), Is.False, c.ToString());
            }
        }

        [Test]
        public void IsMotion_ReturnsFalse_WhenNameHasLettersOrDigitsOutsideAscii()
        {
            var names = new[]
            {
                "ｗａｖｅ",
                "ＷＡＶＥ",
                "０１２",
                "wav" + (char)0xFF45,
                "stra" + (char)0x00DF + "e",
                "caf" + (char)0x00E9,
                "cafe" + (char)0x0301,
                "pose" + (char)0x0663,
                (char)0x0660 + "1",
                "pose" + (char)0x00B2,
                "pose" + (char)0x2160,
                "pose" + (char)0x0131,
                "pose" + (char)0x212A,
                "モーション",
                "wave" + "😀",
                "wave" + (char)0xD83D,
            };

            foreach (var name in names)
            {
                Assert.That(CueNames.IsMotion(name), Is.False, Visible(name));
            }
        }

        [Test]
        public void IsMotion_ReturnsFalse_WhenNameHasPathOrSpecialCharacters()
        {
            var names = new[]
            {
                ".",
                "..",
                "...",
                "a.b",
                "wave.vrma",
                ".hidden",
                "wave.",
                "/",
                "\\",
                "a/b",
                "a\\b",
                "../wave",
                "..\\wave",
                "/wave",
                "\\wave",
                "C:wave",
                "C:",
                ":",
                "wave:stream",
                "wave*",
                "wave?",
                "wave\"",
                "<wave>",
                "wave|x",
                "~wave",
                "wave%20",
            };

            foreach (var name in names)
            {
                Assert.That(CueNames.IsMotion(name), Is.False, Visible(name));
            }
        }

        [Test]
        public void IsMotion_ReturnsFalse_WhenNameHasWhitespaceOrControlCharacters()
        {
            var names = new[]
            {
                " ",
                " wave",
                "wave ",
                "wa ve",
                "\twave",
                "wave\t",
                "wave\n",
                "\nwave",
                "wave\r\n",
                "wave" + (char)0,
                (char)0 + "wave",
                "wa" + (char)0 + "ve",
                "wave" + (char)0x1B,
                "wave" + (char)0x7F,
                "wave" + (char)0x0085,
                "wave" + (char)0x00A0,
                "wave" + (char)0x2028,
                "wave" + (char)0x200B,
                (char)0xFEFF + "wave",
                "wave" + (char)0x202E,
                "wave" + (char)0x3000,
            };

            foreach (var name in names)
            {
                Assert.That(CueNames.IsMotion(name), Is.False, Visible(name));
            }
        }

        // ---- モーションの名前: Windows の予約デバイス名 ----

        [Test]
        public void IsMotion_ReturnsFalse_ForReservedDeviceNamesInEveryLetterCase()
        {
            var accepted = new List<string>();
            var checkedCount = 0;
            foreach (var reserved in ReservedDeviceNames())
            {
                foreach (var variant in CaseVariants(reserved))
                {
                    checkedCount++;
                    if (CueNames.IsMotion(variant))
                    {
                        accepted.Add(variant);
                    }
                }
            }

            Assert.That(checkedCount, Is.EqualTo(4 * 8 + 20 * 8), "確かめた名前の数");
            Assert.That(accepted, Is.Empty, string.Join(", ", accepted));
        }

        [TestCase("CON")]
        [TestCase("con")]
        [TestCase("Con")]
        [TestCase("NUL")]
        [TestCase("COM1")]
        [TestCase("LPT9")]
        [TestCase("com0")]
        [TestCase("lpt0")]
        [TestCase("aux")]
        [TestCase("prn")]
        public void IsMotion_ReturnsFalse_WhenNameIsReservedDeviceName(string name)
        {
            Assert.That(CueNames.IsMotion(name), Is.False, name);
        }
    }
}
