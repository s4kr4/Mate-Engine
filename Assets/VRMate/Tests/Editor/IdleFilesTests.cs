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
    /// 待機のフォルダの直下のファイル名の列から、読む候補を名前の形・重複・本数の上限・名前の順で決めること。
    /// ファイルシステムは使わず、ファイル名の列だけを渡す。
    /// </summary>
    public class IdleFilesTests
    {
        private const string MotionAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

        private static IdleFileSelection Select(params string[] fileNames)
        {
            return IdleFiles.Select(fileNames);
        }

        private static string Show(IEnumerable<string> values)
        {
            return "[" + string.Join(", ", values.Select(v => v == null ? "null" : "\"" + Escape(v) + "\"")) + "]";
        }

        private static string Escape(string value)
        {
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

            return builder.ToString();
        }

        private static void AssertEmpty(IdleFileSelection selection, string because)
        {
            Assert.That(selection, Is.Not.Null, because);
            Assert.That(selection.Names, Is.Empty, because + "（Names）" + Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty, because + "（Malformed）" + Show(selection.Malformed));
            Assert.That(selection.Duplicates, Is.Empty, because + "（Duplicates）" + Show(selection.Duplicates));
            Assert.That(selection.OverLimit, Is.Empty, because + "（OverLimit）" + Show(selection.OverLimit));
        }

        private static string Name(int index)
        {
            return "idle_" + index.ToString("D3", CultureInfo.InvariantCulture);
        }

        private static List<string> Shuffled(IEnumerable<string> values, int seed)
        {
            var random = new Random(seed);
            return values.OrderBy(_ => random.Next()).ToList();
        }

        private static string RandomCase(string name, Random random)
        {
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (random.Next(2) == 0)
                {
                    if (c >= 'a' && c <= 'z')
                    {
                        chars[i] = (char)(c - 32);
                    }
                    else if (c >= 'A' && c <= 'Z')
                    {
                        chars[i] = (char)(c + 32);
                    }
                }
            }

            return new string(chars);
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

        // ---- 定数 ----

        [Test]
        public void MaxFiles_Is32()
        {
            Assert.That(IdleFiles.MaxFiles, Is.EqualTo(32));
        }

        // ---- 空の入力 ----

        [Test]
        public void Select_ReturnsEmptyLists_WhenFileNamesIsNull()
        {
            IdleFileSelection selection = null;

            Assert.DoesNotThrow(() => selection = IdleFiles.Select(null));

            AssertEmpty(selection, "null");
        }

        [Test]
        public void Select_ReturnsEmptyLists_WhenFolderHasNoFiles()
        {
            AssertEmpty(Select(), "空の列");
        }

        [Test]
        public void Select_SkipsNullAndEmptyElementsSilently()
        {
            var selection = Select("wave.vrma", null, string.Empty, "bow.vrma", null);

            Assert.That(selection.Names, Is.EqualTo(new[] { "bow", "wave" }), Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty, Show(selection.Malformed));
            Assert.That(selection.Duplicates, Is.Empty, Show(selection.Duplicates));
            Assert.That(selection.OverLimit, Is.Empty);
        }

        [Test]
        public void Select_AcceptsLazyEnumerables()
        {
            var lazy = Enumerable.Range(0, 3).Reverse().Select(i => Name(i) + ".vrma");

            var selection = IdleFiles.Select(lazy);

            Assert.That(selection.Names, Is.EqualTo(new[] { Name(0), Name(1), Name(2) }));
        }

        // ---- 読む候補 ----

        [Test]
        public void Select_ReturnsNamesWithoutExtensionInNameOrder()
        {
            var selection = Select("wave.vrma", "idle_01.vrma", "Bow.vrma", "breath.vrma");

            Assert.That(selection.Names, Is.EqualTo(new[] { "Bow", "breath", "idle_01", "wave" }), Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty);
            Assert.That(selection.Duplicates, Is.Empty);
            Assert.That(selection.OverLimit, Is.Empty);
        }

        [TestCase("VRMA_02.vrma", "VRMA_02")]
        [TestCase("vrma_02.vrma", "vrma_02")]
        [TestCase("Idle-Breath.vrma", "Idle-Breath")]
        [TestCase("-.vrma", "-")]
        [TestCase("_.vrma", "_")]
        [TestCase("0.vrma", "0")]
        [TestCase("COM10.vrma", "COM10")]
        [TestCase("CONX.vrma", "CONX")]
        [TestCase("CON_.vrma", "CON_")]
        [TestCase("NUL1.vrma", "NUL1")]
        [TestCase("LPT.vrma", "LPT")]
        public void Select_KeepsTheSpellingOfTheName_WhenNameIsWellFormed(string fileName, string expected)
        {
            var selection = Select(fileName);

            Assert.That(selection.Names, Is.EqualTo(new[] { expected }), fileName + " " + Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty, fileName);
        }

        [Test]
        public void Select_AcceptsTheExtensionInEveryLetterCase()
        {
            // ".vrma" の 4 文字の大文字・小文字の 16 通り。Windows ではどれも同じファイルを開く
            for (var mask = 0; mask < 16; mask++)
            {
                var extension = new StringBuilder(".");
                var letters = "vrma";
                for (var i = 0; i < letters.Length; i++)
                {
                    extension.Append((mask & (1 << i)) != 0 ? char.ToUpperInvariant(letters[i]) : letters[i]);
                }

                var fileName = "Wave_1" + extension;
                var selection = Select(fileName);

                Assert.That(selection.Names, Is.EqualTo(new[] { "Wave_1" }), fileName + " " + Show(selection.Names));
                Assert.That(selection.Malformed, Is.Empty, fileName);
                Assert.That(selection.Duplicates, Is.Empty, fileName);
            }
        }

        [Test]
        public void Select_AcceptsNameOf64CharactersButReports65AsMalformed()
        {
            var name64 = new string('a', 60) + "Z_-9";
            var name65 = new string('b', 61) + "Z_-9";
            Assert.That(name64.Length, Is.EqualTo(CueNames.MaxMotionLength), "前提");

            var selection = Select(name64 + ".vrma", name65 + ".vrma");

            Assert.That(selection.Names, Is.EqualTo(new[] { name64 }), Show(selection.Names));
            Assert.That(selection.Malformed, Is.EquivalentTo(new[] { name65 + ".vrma" }), Show(selection.Malformed));
        }

        [Test]
        public void Select_AcceptsEveryCharacterOfTheMotionAlphabet()
        {
            var selection = Select(MotionAlphabet + ".vrma", "x" + MotionAlphabet.Substring(0, 63) + ".VRMA");

            Assert.That(selection.Names, Is.EquivalentTo(new[] { MotionAlphabet, "x" + MotionAlphabet.Substring(0, 63) }), Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty, Show(selection.Malformed));
        }

        // ---- 拡張子が違う ----

        [TestCase("readme.txt")]
        [TestCase("a.vrmax")]
        [TestCase("a.vrma ")]
        [TestCase("a.vrma.")]
        [TestCase("a.vrm")]
        [TestCase("a.vrma.bak")]
        [TestCase("a.vrm a")]
        [TestCase("avrma")]
        [TestCase("a_vrma")]
        [TestCase("vrma")]
        [TestCase("a.vrma.meta")]
        [TestCase("a.VRMA.txt")]
        [TestCase("a.anim")]
        [TestCase("aruka.vrm")]
        [TestCase("a.ｖｒｍａ")]
        [TestCase("a.ＶＲＭＡ")]
        [TestCase("a．vrma")]
        public void Select_IgnoresSilently_WhenNameDoesNotEndWithVrma(string fileName)
        {
            AssertEmpty(Select(fileName), Escape(fileName));
        }

        [Test]
        public void Select_IgnoresSilently_WhenExtensionIsFollowedByControlOrInvisibleCharacters()
        {
            var suffixes = new[] { (char)0, '\t', '\n', '\r', (char)0x7F, (char)0x200B, (char)0xFEFF, (char)0x00A0, (char)0x3000, (char)0x202E };
            foreach (var suffix in suffixes)
            {
                var fileName = "wave.vrma" + suffix;

                AssertEmpty(Select(fileName), Escape(fileName));
            }
        }

        [Test]
        public void Select_IgnoresUnrelatedFiles_WhenFolderHoldsHundredsOfThem()
        {
            var files = new List<string>();
            for (var i = 0; i < 500; i++)
            {
                files.Add("photo_" + i + ".png");
                files.Add("note_" + i + ".txt");
            }

            files.Insert(321, "wave.vrma");
            files.Insert(7, "Bow.VRMA");
            files.Add("breath.vrma");

            var selection = IdleFiles.Select(files);

            Assert.That(selection.Names, Is.EqualTo(new[] { "Bow", "breath", "wave" }), Show(selection.Names));
            Assert.That(selection.Malformed, Is.Empty, "関係のないファイルは警告しない");
            Assert.That(selection.Duplicates, Is.Empty);
            Assert.That(selection.OverLimit, Is.Empty);
        }

        // ---- 名前の形が違う ----

        [TestCase(".vrma")]
        [TestCase(".VRMA")]
        [TestCase("..vrma")]
        [TestCase("a b.vrma")]
        [TestCase(" a.vrma")]
        [TestCase("a .vrma")]
        [TestCase("a.b.vrma")]
        [TestCase("a.vrma.vrma")]
        [TestCase("あ.vrma")]
        [TestCase("待機.vrma")]
        [TestCase("ｗａｖｅ.vrma")]
        [TestCase("ＷＡＶＥ.VRMA")]
        [TestCase("wave１.vrma")]
        [TestCase("é.vrma")]
        [TestCase("ıdle.vrma")]
        [TestCase("İDLE.vrma")]
        [TestCase("ſit.vrma")]
        [TestCase("Kick.vrma")]
        [TestCase("CON.vrma")]
        [TestCase("con.vrma")]
        [TestCase("Con.Vrma")]
        [TestCase("nul.VRMA")]
        [TestCase("NUL.vrma")]
        [TestCase("prn.vrma")]
        [TestCase("AUX.vrma")]
        [TestCase("COM1.vrma")]
        [TestCase("com9.VRMA")]
        [TestCase("COM0.vrma")]
        [TestCase("lpt1.vrma")]
        [TestCase("LPT9.Vrma")]
        [TestCase("sub\\a.vrma")]
        [TestCase("sub/a.vrma")]
        [TestCase("..\\a.vrma")]
        [TestCase("../a.vrma")]
        [TestCase("C:\\Idle\\a.vrma")]
        [TestCase("C:a.vrma")]
        [TestCase("a:b.vrma")]
        [TestCase("a*.vrma")]
        [TestCase("a?.vrma")]
        [TestCase("a+b.vrma")]
        [TestCase("a~1.vrma")]
        public void Select_ReportsNameAsGivenInMalformed_WhenStemIsNotAWellFormedMotionName(string fileName)
        {
            var selection = Select(fileName, "wave.vrma");

            Assert.That(selection.Malformed, Is.EqualTo(new[] { fileName }), Escape(fileName) + " " + Show(selection.Malformed));
            Assert.That(selection.Names, Is.EqualTo(new[] { "wave" }), Escape(fileName) + " " + Show(selection.Names));
            Assert.That(selection.Duplicates, Is.Empty, Escape(fileName));
            Assert.That(selection.OverLimit, Is.Empty, Escape(fileName));
        }

        [Test]
        public void Select_ReportsNameInMalformed_WhenStemHoldsControlBidiOrInvisibleCharacters()
        {
            var inserts = new[]
            {
                (char)0, (char)1, '\t', '\n', '\r', (char)0x1F, (char)0x7F, (char)0x85, (char)0x2028, (char)0x2029,
                (char)0x202E, (char)0x200B, (char)0xFEFF, (char)0x00A0, (char)0x3000,
            };
            foreach (var insert in inserts)
            {
                var fileNames = new[] { "wa" + insert + "ve.vrma", insert + "wave.vrma", "wave" + insert + ".vrma", insert + ".vrma" };
                foreach (var fileName in fileNames)
                {
                    var selection = Select(fileName);

                    Assert.That(selection.Malformed, Is.EqualTo(new[] { fileName }), Escape(fileName));
                    Assert.That(selection.Names, Is.Empty, Escape(fileName));
                }
            }
        }

        [Test]
        public void Select_DoesNotThrow_WhenNameIsVeryLong()
        {
            var fileName = new string('a', 10000) + ".vrma";
            IdleFileSelection selection = null;

            Assert.DoesNotThrow(() => selection = Select(fileName));

            Assert.That(selection.Malformed, Is.EqualTo(new[] { fileName }));
            Assert.That(selection.Names, Is.Empty);
        }

        [Test]
        public void Select_PartitionsStemsExactlyAsCueNamesIsMotion_ForRandomNames()
        {
            // 形に合う文字と合わない文字を混ぜた名前で、Names と Malformed の分かれ方が CueNames.IsMotion と一致すること
            var random = new Random(20261019);
            var alphabet = MotionAlphabet + " .:\\/あｗ１é" + (char)0 + (char)0x7F + (char)0x200B;
            var stems = new HashSet<string>(StringComparer.Ordinal);
            while (stems.Count < 400)
            {
                var length = random.Next(8) == 0 ? 60 + random.Next(8) : 1 + random.Next(6);
                var builder = new StringBuilder();
                for (var i = 0; i < length; i++)
                {
                    var pool = random.Next(5) == 0 ? alphabet : MotionAlphabet;
                    builder.Append(pool[random.Next(pool.Length)]);
                }

                stems.Add(builder.ToString());
            }

            foreach (var stem in stems)
            {
                var fileName = stem + ".vrma";
                var selection = Select(fileName);
                if (CueNames.IsMotion(stem))
                {
                    Assert.That(selection.Names, Is.EqualTo(new[] { stem }), Escape(fileName));
                    Assert.That(selection.Malformed, Is.Empty, Escape(fileName));
                }
                else
                {
                    Assert.That(selection.Malformed, Is.EqualTo(new[] { fileName }), Escape(fileName));
                    Assert.That(selection.Names, Is.Empty, Escape(fileName));
                }
            }
        }

        // ---- 重複 ----

        [Test]
        public void Select_TakesOrdinallySmallestSpellingAndReportsOthersAsDuplicates_WhenNamesDifferOnlyInCase()
        {
            var selection = Select("foo.VRMA", "Foo.vrma");

            Assert.That(selection.Names, Is.EqualTo(new[] { "Foo" }), Show(selection.Names));
            Assert.That(selection.Duplicates, Is.EquivalentTo(new[] { "foo.VRMA" }), Show(selection.Duplicates));
            Assert.That(selection.Malformed, Is.Empty);
            Assert.That(selection.OverLimit, Is.Empty);
        }

        [Test]
        public void Select_ChoosesTheSameSpelling_InEveryInputOrder()
        {
            // 序数では "FOO" < "FoO" < "Foo" < "fOO" < "foo"
            var fileNames = new[] { "foo.vrma", "Foo.Vrma", "FOO.vrma", "fOO.VRMA", "FoO.vrma" };
            for (var seed = 0; seed < 30; seed++)
            {
                var input = Shuffled(fileNames, seed);
                var selection = IdleFiles.Select(input);

                Assert.That(selection.Names, Is.EqualTo(new[] { "FOO" }), Show(input) + " " + Show(selection.Names));
                Assert.That(selection.Duplicates, Is.EquivalentTo(new[] { "foo.vrma", "Foo.Vrma", "fOO.VRMA", "FoO.vrma" }), Show(input) + " " + Show(selection.Duplicates));
            }
        }

        [Test]
        public void Select_KeepsOneName_WhenTheSameStemComesWithDifferentExtensionCase()
        {
            var selection = Select("wave.vrma", "wave.VRMA");

            Assert.That(selection.Names, Is.EqualTo(new[] { "wave" }), Show(selection.Names));
            Assert.That(selection.Duplicates.Count, Is.EqualTo(1), Show(selection.Duplicates));
            Assert.That(new[] { "wave.vrma", "wave.VRMA" }, Has.Member(selection.Duplicates[0]));
        }

        [Test]
        public void Select_KeepsOneName_WhenTheSameFileNameIsGivenTwice()
        {
            var selection = Select("wave.vrma", "wave.vrma");

            Assert.That(selection.Names, Is.EqualTo(new[] { "wave" }), Show(selection.Names));
            Assert.That(selection.Duplicates, Is.EqualTo(new[] { "wave.vrma" }), Show(selection.Duplicates));
        }

        [Test]
        public void Select_KeepsDistinctNamesApart_WhenTheyDifferBeyondLetterCase()
        {
            var selection = Select("a_b.vrma", "a-b.vrma", "ab.vrma", "AB.vrma", "O.vrma", "0.vrma", "l.vrma", "1.vrma");

            Assert.That(selection.Names, Is.EquivalentTo(new[] { "a_b", "a-b", "AB", "O", "0", "l", "1" }), Show(selection.Names));
            Assert.That(selection.Duplicates, Is.EqualTo(new[] { "ab.vrma" }), Show(selection.Duplicates));
        }

        [Test]
        public void Select_DoesNotTreatMalformedNamesAsDuplicates()
        {
            var selection = Select("wave.vrma", "wave .vrma", "WAVE.vrma.vrma");

            Assert.That(selection.Names, Is.EqualTo(new[] { "wave" }));
            Assert.That(selection.Duplicates, Is.Empty, Show(selection.Duplicates));
            Assert.That(selection.Malformed, Is.EquivalentTo(new[] { "wave .vrma", "WAVE.vrma.vrma" }), Show(selection.Malformed));
        }

        // ---- 本数の上限 ----

        [Test]
        public void Select_ReturnsAll32_WhenThereAreExactly32()
        {
            var names = Enumerable.Range(0, 32).Select(Name).ToList();

            var selection = IdleFiles.Select(Shuffled(names.Select(n => n + ".vrma"), 1));

            Assert.That(selection.Names, Is.EqualTo(names), Show(selection.Names));
            Assert.That(selection.OverLimit, Is.Empty, Show(selection.OverLimit));
        }

        [Test]
        public void Select_PutsThe33rdNameInNameOrderIntoOverLimit()
        {
            var names = Enumerable.Range(0, 33).Select(Name).ToList();

            var selection = IdleFiles.Select(Shuffled(names.Select(n => n + ".vrma"), 2));

            Assert.That(selection.Names, Is.EqualTo(names.Take(32)), Show(selection.Names));
            Assert.That(selection.OverLimit, Is.EqualTo(new[] { Name(32) }), Show(selection.OverLimit));
        }

        [Test]
        public void Select_LimitsByNameOrderNotByInputOrder()
        {
            // 名前の順で後ろのものが入力の先頭に来ても、上限で外れるのはそれ
            var input = Enumerable.Range(0, 40).Reverse().Select(i => Name(i) + ".vrma").ToList();

            var selection = IdleFiles.Select(input);

            Assert.That(selection.Names, Is.EqualTo(Enumerable.Range(0, 32).Select(Name)), Show(selection.Names));
            Assert.That(selection.OverLimit, Is.EqualTo(Enumerable.Range(32, 8).Select(Name)), Show(selection.OverLimit));
        }

        [Test]
        public void Select_KeepsOverLimitInNameOrder_WhenThereAreHundreds()
        {
            var names = Enumerable.Range(0, 300).Select(Name).ToList();

            var selection = IdleFiles.Select(Shuffled(names.Select(n => n + ".VRMA"), 3));

            Assert.That(selection.Names.Count, Is.EqualTo(IdleFiles.MaxFiles));
            Assert.That(selection.Names, Is.EqualTo(names.Take(32)));
            Assert.That(selection.OverLimit, Is.EqualTo(names.Skip(32)), "33 番目以降は名前の順で全部");
        }

        [Test]
        public void Select_DoesNotCountDuplicatesTowardTheLimit()
        {
            var input = Enumerable.Range(0, 32).Select(i => Name(i) + ".vrma").ToList();
            input.AddRange(Enumerable.Range(0, 5).Select(i => Name(i).ToUpperInvariant() + ".vrma"));

            var selection = IdleFiles.Select(Shuffled(input, 4));

            // 序数では "IDLE_000" < "idle_000" なので、大文字のほうを採る
            Assert.That(selection.Names, Is.EqualTo(Enumerable.Range(0, 32).Select(i => i < 5 ? Name(i).ToUpperInvariant() : Name(i))), Show(selection.Names));
            Assert.That(selection.OverLimit, Is.Empty, Show(selection.OverLimit));
            Assert.That(selection.Duplicates.Count, Is.EqualTo(5), Show(selection.Duplicates));
        }

        [Test]
        public void Select_DoesNotCountMalformedOrUnrelatedFilesTowardTheLimit()
        {
            var input = Enumerable.Range(0, 32).Select(i => Name(i) + ".vrma").ToList();
            input.AddRange(Enumerable.Range(0, 10).Select(i => "bad name " + i + ".vrma"));
            input.AddRange(Enumerable.Range(0, 10).Select(i => "a" + i + ".png"));

            var selection = IdleFiles.Select(Shuffled(input, 5));

            Assert.That(selection.Names.Count, Is.EqualTo(32), Show(selection.Names));
            Assert.That(selection.OverLimit, Is.Empty, Show(selection.OverLimit));
            Assert.That(selection.Malformed.Count, Is.EqualTo(10));
        }

        [Test]
        public void Select_RemovesDuplicatesBeforeApplyingTheLimit()
        {
            // 31 本 + 大文字・小文字違いの 2 本（1 本と数える）= 32 本
            var input = Enumerable.Range(0, 31).Select(i => Name(i) + ".vrma").ToList();
            input.Add("zz.vrma");
            input.Add("ZZ.vrma");

            var selection = IdleFiles.Select(input);

            Assert.That(selection.Names.Count, Is.EqualTo(32), Show(selection.Names));
            Assert.That(selection.Names.Last(), Is.EqualTo("ZZ"));
            Assert.That(selection.OverLimit, Is.Empty, Show(selection.OverLimit));
            Assert.That(selection.Duplicates, Is.EqualTo(new[] { "zz.vrma" }));
        }

        // ---- 名前の順 ----

        [Test]
        public void Select_SortsByOrdinalIgnoringCase_WhichFoldsToUpperCase()
        {
            // 大文字に寄せて序数で比べる: '-'(2D) < 数字 < 英字 < '_'(5F)。"aZ" < "a_b"、"a-b" < "aa"
            var input = new[] { "c", "B", "a", "aZ", "a_b", "a-b", "aa", "_x", "-x", "9", "Zz" };
            var expected = new[] { "-x", "9", "a", "a-b", "aa", "aZ", "a_b", "B", "c", "Zz", "_x" };

            for (var seed = 0; seed < 10; seed++)
            {
                var shuffled = Shuffled(input.Select(n => n + ".vrma"), seed);
                var selection = IdleFiles.Select(shuffled);

                Assert.That(selection.Names, Is.EqualTo(expected), Show(shuffled) + " → " + Show(selection.Names));
            }
        }

        [TestCase("aZ", "a_b")]
        [TestCase("a-b", "aa")]
        [TestCase("a", "B")]
        [TestCase("B", "c")]
        [TestCase("Z", "_")]
        [TestCase("-", "0")]
        [TestCase("9", "A")]
        [TestCase("a", "a-")]
        [TestCase("idle_10", "idle_9")]
        [TestCase("IDLE_1", "idle_2")]
        public void NameOrder_PutsLeftBeforeRight(string left, string right)
        {
            Assert.That(IdleFiles.NameOrder.Compare(left, right), Is.LessThan(0), left + " < " + right);
            Assert.That(IdleFiles.NameOrder.Compare(right, left), Is.GreaterThan(0), right + " > " + left);
        }

        [Test]
        public void NameOrder_IsZeroExactlyWhenNameComparerSaysEqual()
        {
            var random = new Random(31);
            for (var i = 0; i < 3000; i++)
            {
                var builder = new StringBuilder();
                var length = 1 + random.Next(4);
                for (var j = 0; j < length; j++)
                {
                    builder.Append("aAbB_-zZ0"[random.Next(9)]);
                }

                var left = builder.ToString();
                var right = random.Next(3) == 0 ? RandomCase(left, random) : left.Substring(0, Math.Max(1, left.Length - random.Next(2))) + "aAbB_-zZ0"[random.Next(9)];
                var equal = MotionFiles.NameComparer.Equals(left, right);

                Assert.That(IdleFiles.NameOrder.Compare(left, right) == 0, Is.EqualTo(equal), left + " / " + right);
                Assert.That(Math.Sign(IdleFiles.NameOrder.Compare(left, right)), Is.EqualTo(-Math.Sign(IdleFiles.NameOrder.Compare(right, left))), left + " / " + right);
            }
        }

        [Test]
        public void Select_SortsAndDeduplicatesTheSame_WhenCurrentCultureIsTurkish()
        {
            var input = new[] { "idle_i.vrma", "IDLE_I.vrma", "j.vrma", "H.vrma", "a-b.vrma", "aa.vrma", "aZ.vrma", "a_b.vrma", "Iz.vrma", "iy.vrma" };
            var invariant = IdleFiles.Select(input);
            Assert.That(invariant.Names, Is.EqualTo(new[] { "a-b", "aa", "aZ", "a_b", "H", "IDLE_I", "iy", "Iz", "j" }), "前提（いまの文化）: " + Show(invariant.Names));

            WithTurkishCulture(() =>
            {
                var selection = IdleFiles.Select(input);

                Assert.That(selection.Names, Is.EqualTo(invariant.Names), "tr-TR: " + Show(selection.Names));
                Assert.That(selection.Duplicates, Is.EqualTo(new[] { "idle_i.vrma" }), "tr-TR でも i と I は同じ名前: " + Show(selection.Duplicates));
                Assert.That(IdleFiles.NameOrder.Compare("idle_i", "IDLE_I"), Is.EqualTo(0), "tr-TR");
                Assert.That(IdleFiles.NameOrder.Compare("iy", "Iz"), Is.LessThan(0), "tr-TR");
            });
        }

        // ---- 全体の性質 ----

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void Select_SatisfiesItsContract_ForRandomFolders(int seed)
        {
            var random = new Random(seed);
            var stems = new[] { "wave", "Bow", "idle_01", "idle_02", "breath", "a-b", "a_b", "aZ", "zz", "CON", "a b", "あ", "a.b", "" };
            var extensions = new[] { ".vrma", ".VRMA", ".Vrma", ".vrmA", ".txt", ".vrm", ".vrma ", "" };

            for (var round = 0; round < 60; round++)
            {
                var input = new List<string>();
                var count = random.Next(120);
                for (var i = 0; i < count; i++)
                {
                    var stem = random.Next(3) == 0 ? stems[random.Next(stems.Length)] : "n" + random.Next(60);
                    input.Add(RandomCase(stem, random) + extensions[random.Next(extensions.Length)]);
                }

                var selection = IdleFiles.Select(input);
                var because = "seed " + seed + " round " + round;

                var vrma = input.Where(f => f.EndsWith(".vrma", StringComparison.OrdinalIgnoreCase)).ToList();
                var accepted = selection.Names.Concat(selection.OverLimit).ToList();

                // 拡張子が .vrma のものは、ちょうど 1 つのリストに入る。それ以外はどこにも入らない
                var accounted = selection.Malformed.Concat(selection.Duplicates).ToList();
                Assert.That(accounted.Count + accepted.Count, Is.EqualTo(vrma.Count), because + "（数が合わない）");
                Assert.That(selection.Malformed.All(f => vrma.Contains(f)), Is.True, because);
                Assert.That(selection.Duplicates.All(f => vrma.Contains(f)), Is.True, because);

                // 形
                Assert.That(selection.Malformed.All(f => !CueNames.IsMotion(f.Substring(0, f.Length - 5))), Is.True, because + "（Malformed に形の合う名前）");
                Assert.That(accepted.All(CueNames.IsMotion), Is.True, because + "（候補に形の違う名前）");
                Assert.That(vrma.Count(f => !CueNames.IsMotion(f.Substring(0, f.Length - 5))), Is.EqualTo(selection.Malformed.Count), because);

                // 重複の除去
                Assert.That(accepted.Distinct(MotionFiles.NameComparer).Count(), Is.EqualTo(accepted.Count), because + "（候補に同じ名前）");
                foreach (var duplicate in selection.Duplicates)
                {
                    var stem = duplicate.Substring(0, duplicate.Length - 5);
                    var kept = accepted.Single(a => MotionFiles.NameComparer.Equals(a, stem));
                    Assert.That(string.CompareOrdinal(kept, stem), Is.LessThanOrEqualTo(0), because + " " + duplicate + " より " + kept + " が小さい");
                }

                // 上限と順
                Assert.That(selection.Names.Count, Is.EqualTo(Math.Min(IdleFiles.MaxFiles, accepted.Count)), because);
                for (var i = 1; i < accepted.Count; i++)
                {
                    Assert.That(StringComparer.OrdinalIgnoreCase.Compare(accepted[i - 1], accepted[i]), Is.LessThan(0), because + " " + accepted[i - 1] + " / " + accepted[i]);
                }

                // 入力の順に依存しない
                var again = IdleFiles.Select(Shuffled(input, round));
                Assert.That(again.Names, Is.EqualTo(selection.Names), because + "（並べ替えた入力）");
                Assert.That(again.OverLimit, Is.EqualTo(selection.OverLimit), because + "（並べ替えた入力）");
                Assert.That(again.Malformed, Is.EquivalentTo(selection.Malformed), because + "（並べ替えた入力）");
                // 名前の部分まで同じもの（拡張子の書き方だけが違う）は、どちらを採ってもよい
                Assert.That(again.Duplicates.Select(f => f.Substring(0, f.Length - 5)), Is.EquivalentTo(selection.Duplicates.Select(f => f.Substring(0, f.Length - 5))), because + "（並べ替えた入力）");
            }
        }
    }
}
