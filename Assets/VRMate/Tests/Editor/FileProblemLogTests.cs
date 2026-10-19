using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// フォルダにいまある問題の集合を読み直しのたびに数え直し、問題ごとに名前つきの行を 1 回だけ出すこと。
    /// 1 回に名前を出すのは 8 件までで、まだ出していないものを最初に現れた順（同じ回に現れたものどうしは Add の順）に選び、
    /// 残りは件数の行にまとめてあとの回で出す。
    /// </summary>
    public class FileProblemLogTests
    {
        private static string Message(string key)
        {
            return "problem: " + key;
        }

        /// <summary>1 回の読み直し: Begin、keys を順に Add、End。</summary>
        private static IReadOnlyList<string> Round(FileProblemLog log, IEnumerable<string> keys)
        {
            log.Begin();
            foreach (var key in keys)
            {
                log.Add(key, Message(key));
            }

            return log.End();
        }

        private static IReadOnlyList<string> Round(FileProblemLog log, params string[] keys)
        {
            return Round(log, (IEnumerable<string>)keys);
        }

        private static string[] Keys(string prefix, int count)
        {
            return Enumerable.Range(0, count).Select(i => prefix + i.ToString("D4")).ToArray();
        }

        private static string Show(IReadOnlyList<string> lines)
        {
            return "[" + string.Join(" | ", lines.Select(l => l == null ? "null" : l)) + "]";
        }

        /// <summary>行に、その数がほかの数字とつながらずに入っていること。</summary>
        private static bool MentionsCount(string line, int count)
        {
            return line != null && Regex.IsMatch(line, "(?<![0-9])" + count + "(?![0-9])");
        }

        /// <summary>名前つきの行が names の message をこの順に並べたもので、remaining が 0 でなければ件数の行が最後に 1 行あること。</summary>
        private static void AssertReport(IReadOnlyList<string> lines, IEnumerable<string> names, int remaining, string because)
        {
            var expected = names.Select(Message).ToList();
            Assert.That(lines, Is.Not.Null, because);
            Assert.That(lines.Count, Is.EqualTo(expected.Count + (remaining > 0 ? 1 : 0)), because + " " + Show(lines));
            Assert.That(lines.Take(expected.Count), Is.EqualTo(expected), because + " " + Show(lines));
            if (remaining > 0)
            {
                var last = lines[lines.Count - 1];
                Assert.That(last.StartsWith("problem: ", StringComparison.Ordinal), Is.False, because + "（最後の行は件数の行）" + Show(lines));
                Assert.That(MentionsCount(last, remaining), Is.True, because + "（件数 " + remaining + " が入っていない）" + Show(lines));
            }
        }

        // ---- 定数 ----

        [Test]
        public void MaxListedPerReport_Is8()
        {
            Assert.That(FileProblemLog.MaxListedPerReport, Is.EqualTo(8));
        }

        // ---- 1 回の読み直し ----

        [Test]
        public void End_ReturnsNothing_WhenNoProblemWasAdded()
        {
            var log = new FileProblemLog();

            Assert.That(Round(log), Is.Empty);
            Assert.That(Round(log), Is.Empty, "2 回目も");
        }

        [Test]
        public void End_ListsOneProblemByItsMessage()
        {
            var log = new FileProblemLog();

            AssertReport(Round(log, "a b.vrma"), new[] { "a b.vrma" }, 0, "1 件");
        }

        [Test]
        public void End_ListsEightProblemsWithoutACountLine()
        {
            var log = new FileProblemLog();
            var keys = Keys("bad", 8);

            AssertReport(Round(log, keys), keys, 0, "8 件はちょうど全部出す");
        }

        [TestCase(9, 1)]
        [TestCase(16, 8)]
        [TestCase(17, 9)]
        [TestCase(100, 92)]
        public void End_ListsTheFirstEightAndCountsTheRest_WhenThereAreMore(int count, int rest)
        {
            var log = new FileProblemLog();
            var keys = Keys("bad", count);

            AssertReport(Round(log, keys), keys.Take(8), rest, count + " 件");
        }

        [Test]
        public void End_ListsNewProblemsInTheOrderOfAddNotInNameOrder()
        {
            var log = new FileProblemLog();
            var keys = new[] { "zeta", "Alpha", "beta", "_x", "9", "ALPHA", "a", "Zeta", "mid", "0" };

            AssertReport(Round(log, keys), keys.Take(8), 2, "同じ回に初めて現れたものは Add の順");
            AssertReport(Round(log, keys.Reverse()), new[] { "mid", "0" }, 0, "残りは最初に現れた回の順のまま（今回の Add の順ではない）");
        }

        [Test]
        public void End_KeepsTheOrderOfProblemsFromEarlierReloads_WhenTheAddOrderChanges()
        {
            var log = new FileProblemLog();
            var keys = Keys("p", 20);
            var random = new Random(7);

            AssertReport(Round(log, keys), keys.Take(8), 12, "1 回目");
            AssertReport(Round(log, keys.Reverse()), keys.Skip(8).Take(8), 4, "逆の順に Add しても、前の回からあるものの順は変わらない");
            AssertReport(Round(log, keys.OrderBy(_ => random.Next())), keys.Skip(16), 0, "並べ替えて Add しても同じ");
        }

        [Test]
        public void End_ListsOlderProblemsBeforeNewOnes_EvenWhenNewOnesAreAddedFirst()
        {
            // 毎回 16 件で、うち 8 件が新しい key として Add の先頭に来る。古い問題も飢えない
            var log = new FileProblemLog();
            var old = Keys("old", 8);

            for (var round = 0; round < 10; round++)
            {
                var fresh = Keys("new" + round + "-", 8);
                var lines = Round(log, fresh.Concat(old));
                if (round == 0)
                {
                    AssertReport(lines, fresh, 8, "1 回目: どれも初めてなので Add の順");
                }
                else if (round == 1)
                {
                    AssertReport(lines, old, 8, "2 回目（⌈16/8⌉ 回目）: 1 回目からある old を先に");
                }
                else
                {
                    AssertReport(lines, fresh, 0, "round " + round + ": old は出し済み、前の回の new は消えたので、今回の new だけ");
                }
            }
        }

        [Test]
        public void End_ListsEachProblemWithinItsBound_WhenNewProblemsKeepComingFirstAndStay()
        {
            // 新しい 16 件が毎回 Add の先頭に増えていき、前の回のものも残り続ける（1 回に出せる 8 件より速く増える）
            var log = new FileProblemLog();
            var present = new List<string>();
            var appeared = new Dictionary<string, int>();
            var bound = new Dictionary<string, int>();
            var listed = new HashSet<string>();

            for (var round = 0; round < 12; round++)
            {
                var fresh = Keys("r" + round + "-", 16);
                present.InsertRange(0, fresh);
                foreach (var key in fresh)
                {
                    appeared[key] = round;
                    bound[key] = (present.Count + 7) / 8;
                }

                var lines = Round(log, present);
                foreach (var line in lines.Where(l => l.StartsWith("problem: ", StringComparison.Ordinal)))
                {
                    Assert.That(listed.Add(line.Substring("problem: ".Length)), Is.True, "2 回出した: " + line);
                }

                foreach (var key in present)
                {
                    if (round - appeared[key] + 1 >= bound[key])
                    {
                        Assert.That(listed.Contains(key), Is.True, "round " + round + ": " + key + " は " + bound[key] + " 回目までに出すはず " + Show(lines));
                    }
                }
            }
        }

        [Test]
        public void End_ListsAReappearedProblemAfterThoseThatStayed_UsingTheReloadItReappearedIn()
        {
            var log = new FileProblemLog();
            var a = Keys("a", 8);
            var b = Keys("b", 9);

            AssertReport(Round(log, a.Concat(new[] { "x" })), a, 1, "1 回目: x は件数の行に入る");
            AssertReport(Round(log, b), b.Take(8), 1, "2 回目: x は消えた。b0008 は件数の行に入る");
            // x は 1 回目に現れたが、いったん消えたので 3 回目に現れたものとして、2 回目からある b0008 のあと
            AssertReport(Round(log, "x", "b0008"), new[] { "b0008", "x" }, 0, "3 回目");
        }

        [Test]
        public void End_ListsTheRemainingProblemsOnLaterReloads_UntilAllWereListedOnce()
        {
            var log = new FileProblemLog();
            var keys = Keys("bad", 17);

            AssertReport(Round(log, keys), keys.Take(8), 9, "1 回目");
            AssertReport(Round(log, keys), keys.Skip(8).Take(8), 1, "2 回目");
            AssertReport(Round(log, keys), keys.Skip(16), 0, "3 回目");
            AssertReport(Round(log, keys), new string[0], 0, "4 回目: もう出したので何も出さない");
            AssertReport(Round(log, keys), new string[0], 0, "5 回目");
        }

        [Test]
        public void End_ListsTheBrokenFileOnTheNextReload_WhenEightMalformedNamesCameFirst()
        {
            // 形の違う名前 8 件のあとに壊れたファイル 1 件。1 回目は件数の行に入り、2 回目に名前が出る
            var log = new FileProblemLog();
            var keys = Keys("malformed ", 8).Concat(new[] { "broken.vrma" }).ToArray();

            AssertReport(Round(log, keys), keys.Take(8), 1, "1 回目");
            AssertReport(Round(log, keys), new[] { "broken.vrma" }, 0, "2 回目");
            AssertReport(Round(log, keys), new string[0], 0, "3 回目");
        }

        // ---- 回ごとの集合の変化 ----

        [Test]
        public void End_ListsAProblemAgain_WhenItDisappearedAndCameBack()
        {
            var log = new FileProblemLog();

            AssertReport(Round(log, "a"), new[] { "a" }, 0, "1 回目");
            AssertReport(Round(log, "b"), new[] { "b" }, 0, "a が消えた");
            AssertReport(Round(log, "a", "b"), new[] { "a" }, 0, "a がまた現れたら新しい問題");
        }

        [Test]
        public void End_ListsAProblemAgain_WhenAReloadHadNoProblemsInBetween()
        {
            var log = new FileProblemLog();

            AssertReport(Round(log, "a", "b"), new[] { "a", "b" }, 0, "1 回目");
            Assert.That(Round(log), Is.Empty, "問題がなくなった");
            AssertReport(Round(log, "b", "a"), new[] { "b", "a" }, 0, "また現れた");
        }

        [Test]
        public void End_ListsOnlyNewProblems_WhenSomeRemainAndSomeAreAdded()
        {
            var log = new FileProblemLog();
            var first = Keys("p", 10);

            AssertReport(Round(log, first), first.Take(8), 2, "1 回目");
            // 出した p0000 と、まだ出していない p0008・p0009 が残り、q が増える。
            // 前の回からあるものが最初に現れた順で先、新しいものはそのあと Add の順
            AssertReport(Round(log, "q1", "p0000", "p0009", "q2", "p0008"), new[] { "p0008", "p0009", "q1", "q2" }, 0, "2 回目");
            AssertReport(Round(log, "p0000", "q1", "r"), new[] { "r" }, 0, "3 回目");
        }

        [Test]
        public void End_ListsAnUnlistedProblemWithoutACountLine_WhenTheOthersDisappeared()
        {
            var log = new FileProblemLog();
            var keys = Keys("p", 9);

            AssertReport(Round(log, keys), keys.Take(8), 1, "1 回目");
            AssertReport(Round(log, "p0008"), new[] { "p0008" }, 0, "9 件目だけが残った");
        }

        [Test]
        public void End_ForgetsListedProblemsThatDisappeared_EvenWhenTheSetIsReplacedEntirely()
        {
            var log = new FileProblemLog();
            var a = Keys("a", 8);
            var b = Keys("b", 8);

            AssertReport(Round(log, a), a, 0, "a");
            AssertReport(Round(log, b), b, 0, "b に入れ替わる");
            AssertReport(Round(log, a), a, 0, "a に戻ると、また新しい問題");
        }

        // ---- 同じ回の重複 ----

        [Test]
        public void Add_CountsTheSameKeyOnceInARound_AndUsesTheFirstMessage()
        {
            var log = new FileProblemLog();

            log.Begin();
            log.Add("wave", "first");
            log.Add("wave", "second");
            log.Add("bow", "bow");
            log.Add("wave", "third");
            var lines = log.End();

            Assert.That(lines, Is.EqualTo(new[] { "first", "bow" }), Show(lines));
        }

        [Test]
        public void Add_DoesNotCountDuplicatesTowardTheEight()
        {
            var log = new FileProblemLog();
            var keys = Keys("p", 8);

            log.Begin();
            foreach (var key in keys)
            {
                log.Add(key, Message(key));
                log.Add(key, Message(key));
            }

            AssertReport(log.End(), keys, 0, "重複を除けば 8 件");
        }

        [Test]
        public void End_CountsUnlistedProblemsWithoutDuplicates()
        {
            var log = new FileProblemLog();
            var keys = Keys("p", 12);

            log.Begin();
            foreach (var key in keys.Concat(keys.Skip(8)).Concat(keys.Skip(10)))
            {
                log.Add(key, Message(key));
            }

            AssertReport(log.End(), keys.Take(8), 4, "残りは 4 件");
        }

        // ---- key と message ----

        [Test]
        public void Add_ComparesKeysOrdinally()
        {
            var log = new FileProblemLog();
            var keys = new[] { "wave", "Wave", "WAVE", "wave ", "I", "i", "" + (char)0x0130, "" + (char)0x0131 };

            AssertReport(Round(log, keys), keys, 0, "大文字小文字だけの違いも別の問題");
        }

        [Test]
        public void Add_TreatsNullKeyAsTheEmptyKey()
        {
            var log = new FileProblemLog();

            log.Begin();
            log.Add(null, "from null");
            log.Add(string.Empty, "from empty");
            log.Add("a", "a");
            var first = log.End();
            Assert.That(first, Is.EqualTo(new[] { "from null", "a" }), Show(first));

            log.Begin();
            log.Add(string.Empty, "from empty");
            log.Add("a", "a");
            Assert.That(log.End(), Is.Empty, "null と空は同じ問題で、もう出した");
        }

        [Test]
        public void Add_ListsNullMessageAsEmptyText()
        {
            var log = new FileProblemLog();

            log.Begin();
            log.Add("a", null);
            log.Add("b", string.Empty);
            var lines = log.End();

            Assert.That(lines, Is.EqualTo(new[] { string.Empty, string.Empty }), Show(lines));
        }

        [Test]
        public void Add_AcceptsVeryLongKeysAndMessages()
        {
            var log = new FileProblemLog();
            var key = new string('k', 100000);
            var message = new string('m', 100000);

            log.Begin();
            log.Add(key, message);
            log.Add(key + "x", message + "x");
            var lines = log.End();

            Assert.That(lines, Is.EqualTo(new[] { message, message + "x" }));
        }

        // ---- 呼び方 ----

        [Test]
        public void Begin_DropsTheProblemsAddedSinceThePreviousBegin()
        {
            var log = new FileProblemLog();

            log.Begin();
            log.Add("old", "old");
            log.Begin();
            log.Add("new", "new");
            var lines = log.End();

            Assert.That(lines, Is.EqualTo(new[] { "new" }), Show(lines));
        }

        [Test]
        public void Begin_KeepsWhatWasAlreadyListed()
        {
            var log = new FileProblemLog();
            AssertReport(Round(log, "a", "b"), new[] { "a", "b" }, 0, "前提");

            log.Begin();
            log.Begin();
            log.Add("a", Message("a"));
            log.Begin();
            log.Add("a", Message("a"));
            log.Add("b", Message("b"));
            var lines = log.End();

            Assert.That(lines, Is.Empty, "Begin では出したものを忘れない " + Show(lines));
        }

        [Test]
        public void End_TreatsProblemsAddedSinceThePreviousEndAsTheRound_WhenBeginWasNotCalled()
        {
            var log = new FileProblemLog();

            log.Add("a", "a");
            log.Add("b", "b");
            Assert.That(log.End(), Is.EqualTo(new[] { "a", "b" }), "一度も Begin しない");

            log.Add("b", "b");
            log.Add("c", "c");
            Assert.That(log.End(), Is.EqualTo(new[] { "c" }), "直前の End 以降に Add されたもの");

            Assert.That(log.End(), Is.Empty, "何も Add されていない");
            log.Add("b", "b");
            Assert.That(log.End(), Is.EqualTo(new[] { "b" }), "何も Add されていない End で、出したものは空になった");
        }

        [Test]
        public void End_StartsTheNextRoundEmpty()
        {
            var log = new FileProblemLog();
            log.Begin();
            log.Add("a", "a");
            log.End();

            log.Add("b", "b");
            var lines = log.End();

            Assert.That(lines, Is.EqualTo(new[] { "b" }), "返した直後に今回の集合は空になる " + Show(lines));
        }

        [Test]
        public void CallsInAnyOrder_DoNotThrow()
        {
            var log = new FileProblemLog();

            Assert.DoesNotThrow(() =>
            {
                log.End();
                log.End();
                log.Begin();
                log.Begin();
                log.Add(null, null);
                log.Add(null, null);
                log.End();
                log.Add("a", null);
                log.Begin();
                log.End();
                log.End();
            });
        }

        [Test]
        public void End_ReturnsANewListEachTime()
        {
            var log = new FileProblemLog();
            var first = Round(log, "a");
            var copy = first.ToList();

            Round(log, "b", "c");

            Assert.That(first, Is.EqualTo(copy), "前に返した行は、あとの回で変わらない");
        }

        // ---- 規模 ----

        [Test]
        public void End_ListsFiveThousandProblemsEightAtATime_AndEachExactlyOnce()
        {
            var log = new FileProblemLog();
            var keys = Keys("bad", 5000);
            var listed = new List<string>();

            for (var round = 0; round < 625; round++)
            {
                var lines = Round(log, keys);
                Assert.That(lines.Count, Is.LessThanOrEqualTo(9), "round " + round);
                var remaining = 5000 - 8 * (round + 1);
                AssertReport(lines, keys.Skip(8 * round).Take(8), remaining, "round " + round);
                listed.AddRange(lines.Take(8));
            }

            Assert.That(listed, Is.EqualTo(keys.Select(Message)), "⌈5000/8⌉ = 625 回で全部を 1 回ずつ");
            Assert.That(Round(log, keys), Is.Empty, "626 回目は何も出さない");
        }

        [Test]
        public void End_ForgetsAFiveThousandProblemReload_AfterAReloadWithOneOtherProblem()
        {
            var log = new FileProblemLog();
            var keys = Keys("bad", 5000);

            AssertReport(Round(log, keys), keys.Take(8), 4992, "5000 件");
            AssertReport(Round(log, "other"), new[] { "other" }, 0, "1 件だけの回");
            AssertReport(Round(log, keys), keys.Take(8), 4992, "前の回の key は残っていないので、また初めから");
        }

        // ---- 判定の不変条件 ----

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        public void End_ListsEachProblemOnceWhileItStays_ForRandomReloads(int seed)
        {
            var random = new Random(seed);
            var pool = Enumerable.Range(0, 60).Select(i => "f" + i).Concat(new[] { "F1", "F2", "f1 ", string.Empty }).ToArray();
            var log = new FileProblemLog();

            // key ごとに、いまの在り続けている間に名前を出したか
            var listedWhilePresent = new HashSet<string>(StringComparer.Ordinal);
            var previous = new List<string>();
            int countLines = 0, boundChecks = 0, laterBoundChecks = 0, reappeared = 0, ninePlus = 0, newFirst = 0;
            var everListed = new HashSet<string>(StringComparer.Ordinal);

            // いまの在り続けている区間が始まった回と、その回の Add の位置。上限は ⌈その回の問題の数 / 8⌉ 回目の End
            var appearedRound = new Dictionary<string, int>(StringComparer.Ordinal);
            var appearedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var bound = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var round = 0; round < 3000; round++)
            {
                // 回ごとの集合: 同じ・増える（前にも後ろにも）・減る・入れ替わる・一部だけ残る・空
                List<string> current;
                var shape = random.Next(10);
                if (shape < 4)
                {
                    current = previous.OrderBy(_ => random.Next(3) == 0 ? random.Next() : 0).ToList();
                }
                else if (shape < 6)
                {
                    var fresh = pool.Where(k => !previous.Contains(k)).OrderBy(_ => random.Next()).Take(random.Next(1, 20)).ToList();
                    current = random.Next(2) == 0 ? fresh.Concat(previous).ToList() : previous.Concat(fresh).ToList();
                }
                else if (shape < 8)
                {
                    current = previous.Where(_ => random.Next(3) != 0).ToList();
                }
                else if (shape < 9)
                {
                    current = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 40)).ToList();
                }
                else
                {
                    current = new List<string>();
                }

                var currentSet = new HashSet<string>(current, StringComparer.Ordinal);
                foreach (var key in listedWhilePresent.ToList())
                {
                    if (!currentSet.Contains(key))
                    {
                        listedWhilePresent.Remove(key);
                    }
                }

                foreach (var key in appearedRound.Keys.ToList())
                {
                    if (!currentSet.Contains(key))
                    {
                        appearedRound.Remove(key);
                        appearedIndex.Remove(key);
                        bound.Remove(key);
                    }
                }

                for (var i = 0; i < current.Count; i++)
                {
                    if (!appearedRound.ContainsKey(current[i]))
                    {
                        appearedRound[current[i]] = round;
                        appearedIndex[current[i]] = i;
                        bound[current[i]] = (current.Count + FileProblemLog.MaxListedPerReport - 1) / FileProblemLog.MaxListedPerReport;
                    }
                }

                // まだ出していないものを、最初に現れた順（同じ回に現れたものどうしは Add の順）に並べる
                var unlistedBefore = current.Where(k => !listedWhilePresent.Contains(k)).OrderBy(k => appearedRound[k]).ThenBy(k => appearedIndex[k]).ToList();
                reappeared += unlistedBefore.Count(everListed.Contains);
                var firstNew = current.FindIndex(k => appearedRound[k] == round);
                if (firstNew >= 0 && current.Skip(firstNew).Any(k => appearedRound[k] < round && !listedWhilePresent.Contains(k)))
                {
                    newFirst++;
                }

                // 呼び方: たまに Begin を省く。たまに余計な Begin と Add を前に入れる。重複の Add を混ぜる
                var call = random.Next(10);
                if (call == 0)
                {
                    log.Begin();
                    log.Add("junk", "junk");
                    log.Begin();
                }
                else if (call != 1)
                {
                    log.Begin();
                }

                foreach (var key in current)
                {
                    log.Add(key.Length == 0 && random.Next(2) == 0 ? null : key, Message(key));
                    if (random.Next(5) == 0)
                    {
                        log.Add(key, "duplicate");
                    }
                }

                var lines = log.End();
                var because = "seed " + seed + " round " + round + " " + Show(lines);

                Assert.That(lines.Count, Is.LessThanOrEqualTo(FileProblemLog.MaxListedPerReport + 1), because + "（9 行まで）");
                Assert.That(lines.Contains("junk") || lines.Contains("duplicate"), Is.False, because + "（今回の問題でないもの）");

                var named = lines.Where(l => l.StartsWith("problem: ", StringComparison.Ordinal)).Select(l => l.Substring("problem: ".Length)).ToList();
                Assert.That(named.Count, Is.LessThanOrEqualTo(FileProblemLog.MaxListedPerReport), because + "（名前は 8 件まで）");

                // まだ出していないものを、その順に 8 件まで
                var expected = unlistedBefore.Take(FileProblemLog.MaxListedPerReport).ToList();
                Assert.That(named, Is.EqualTo(expected), because + "（選び方）");
                Assert.That(lines.Take(named.Count).Select(l => l.Substring("problem: ".Length)), Is.EqualTo(named), because + "（名前の行が先）");

                var remaining = unlistedBefore.Count - named.Count;
                if (remaining > 0)
                {
                    countLines++;
                    Assert.That(lines.Count, Is.EqualTo(named.Count + 1), because + "（件数の行）");
                    Assert.That(MentionsCount(lines[lines.Count - 1], remaining), Is.True, because + "（件数 " + remaining + "）");
                }
                else
                {
                    Assert.That(lines.Count, Is.EqualTo(named.Count), because + "（残りがないのに件数の行）");
                }

                foreach (var key in named)
                {
                    listedWhilePresent.Add(key);
                    everListed.Add(key);
                }

                // どの問題も、現れた回を 1 回目として、遅くとも ⌈現れた回の問題の数 / 8⌉ 回目の End までに出す
                foreach (var key in current)
                {
                    var elapsed = round - appearedRound[key] + 1;
                    if (elapsed >= bound[key])
                    {
                        boundChecks++;
                        laterBoundChecks += elapsed > 1 ? 1 : 0;
                        Assert.That(listedWhilePresent.Contains(key), Is.True, because + "（" + key + " は " + bound[key] + " 回目までに出すはず。いま " + elapsed + " 回目）");
                    }
                }

                if (current.Count > FileProblemLog.MaxListedPerReport)
                {
                    ninePlus++;
                }

                previous = current;
            }

            // 確かめた状態が実際に起きていること
            Assert.That(countLines, Is.GreaterThan(50), "件数の行が出た回が少ない");
            Assert.That(boundChecks, Is.GreaterThan(1000), "上限を確かめた問題が少ない");
            Assert.That(laterBoundChecks, Is.GreaterThan(200), "2 回目以降の End で上限を確かめた問題が少ない");
            Assert.That(newFirst, Is.GreaterThan(50), "新しい問題が、まだ出していない古い問題より前に Add された回が少ない");
            Assert.That(reappeared, Is.GreaterThan(50), "消えてまた現れた問題が少ない");
            Assert.That(ninePlus, Is.GreaterThan(100), "9 件以上の回が少ない");
        }
    }
}
