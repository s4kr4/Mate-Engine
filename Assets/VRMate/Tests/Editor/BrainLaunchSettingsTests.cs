using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// brain.json の読み取りと確かめ。ファイルが無ければ Missing、読めない・64 KiB を超える・中身が確かめを通らなければ Invalid、
    /// 通れば Valid。中身は BOM を読み飛ばした UTF-8 の JSON オブジェクトで、command と workingDirectory は必須の絶対パス、
    /// arguments は省略できる文字列。失敗のときは起動しない理由を 1 行で返し、成功のときは null を返す。
    /// ファイルは毎回 Path.GetTempPath() の下の新しいフォルダに作る。
    /// </summary>
    public class BrainLaunchSettingsTests
    {
        private const string FolderPrefix = "vrmate-brain-launch-tests-";

        private const string Cmd = @"C:\Brain\python.exe";
        private const string Wd = @"D:\Brain";

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private string _folder;

        [SetUp]
        public void CreateFolder()
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            _folder = Path.Combine(temp, FolderPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void DeleteFolder()
        {
            var folder = _folder;
            _folder = null;
            if (folder == null || !Directory.Exists(folder))
            {
                return;
            }

            // 自分で作ったフォルダ以外は消さない
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(folder);
            if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith(FolderPrefix, StringComparison.Ordinal))
            {
                Assert.Fail("片付けるフォルダが一時フォルダの下でない: " + full);
            }

            Directory.Delete(full, true);
        }

        // ---- JSON を組み立てる道具 ----

        /// <summary>文字列を JSON の文字列リテラルにする（制御文字は \uXXXX にエスケープされる）。</summary>
        private static string S(string value)
        {
            return JsonConvert.ToString(value);
        }

        private static string Member(string key, string rawValue)
        {
            return S(key) + ":" + rawValue;
        }

        private static string Obj(params string[] members)
        {
            return "{" + string.Join(",", members) + "}";
        }

        private static string ValidDoc()
        {
            return Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd)));
        }

        /// <summary>必須の 2 つのキーのうち key だけを rawValue にし、もう一方は正しい値にした文書。</summary>
        private static string DocWith(string key, string rawValue)
        {
            var members = new List<string>();
            members.Add(Member("command", key == "command" ? rawValue : S(Cmd)));
            members.Add(Member("workingDirectory", key == "workingDirectory" ? rawValue : S(Wd)));
            if (key == "arguments")
            {
                members.Add(Member("arguments", rawValue));
            }

            return Obj(members.ToArray());
        }

        /// <summary>key を書かない文書。</summary>
        private static string DocWithout(string key)
        {
            var members = new List<string>();
            if (key != "command")
            {
                members.Add(Member("command", S(Cmd)));
            }

            if (key != "workingDirectory")
            {
                members.Add(Member("workingDirectory", S(Wd)));
            }

            return Obj(members.ToArray());
        }

        private static byte[] Utf8(string text)
        {
            return StrictUtf8.GetBytes(text);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>BOM の有無を選び、"pad" キーの文字列で全体がちょうど totalBytes バイトになる正しい文書。</summary>
        private static byte[] PaddedValid(int totalBytes, bool bom)
        {
            var head = Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd))).TrimEnd('}') + ",\"pad\":\"";
            const string tail = "\"}";
            var fixedBytes = Utf8(head).Length + Utf8(tail).Length + (bom ? Bom.Length : 0);
            var pad = new string('x', totalBytes - fixedBytes);
            var body = Utf8(head + pad + tail);
            var bytes = bom ? Concat(Bom, body) : body;
            Assert.That(bytes.Length, Is.EqualTo(totalBytes), "テスト側の組み立て");
            return bytes;
        }

        private static string Show(byte[] content)
        {
            if (content == null)
            {
                return "null";
            }

            var shown = content.Length <= 160
                ? BitConverter.ToString(content)
                : BitConverter.ToString(content, 0, 80) + "...(" + content.Length + " bytes)";
            return shown;
        }

        // ---- アサーション ----

        /// <summary>problem が 1 行の理由であること: 非 null・空白だけでない・行の区切りを含まない。</summary>
        private static void AssertOneLine(string problem, string where)
        {
            Assert.That(problem, Is.Not.Null, where + ": problem が null");
            Assert.That(problem.Trim(), Is.Not.Empty, where + ": problem が空・空白だけ");
            foreach (var c in new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' })
            {
                Assert.That(problem.IndexOf(c) < 0, Is.True, where + ": problem に行の区切り U+" + ((int)c).ToString("X4") + " がある: " + problem);
            }
        }

        private static void AssertAccepted(byte[] content, string command, string arguments, string workingDirectory, string because)
        {
            BrainLaunchSettings settings;
            string problem;
            var ok = BrainLaunchSettings.TryParse(content, out settings, out problem);

            var where = because + "（中身 " + Show(content) + "）";
            Assert.That(ok, Is.True, where + ": 受け付けなかった（problem: " + problem + "）");
            Assert.That(problem, Is.Null, where + ": 成功なのに problem が null でない");
            Assert.That(settings, Is.Not.Null, where + ": settings が null");
            Assert.That(settings.Command, Is.EqualTo(command), where + ": Command");
            Assert.That(settings.Arguments, Is.EqualTo(arguments), where + ": Arguments");
            Assert.That(settings.WorkingDirectory, Is.EqualTo(workingDirectory), where + ": WorkingDirectory");
        }

        private static void AssertAccepted(string json, string because)
        {
            AssertAccepted(Utf8(json), Cmd, "", Wd, because);
        }

        /// <summary>
        /// 受け付けないこと。key があれば problem にそのキー名が出ること、secret があれば problem に値が出ないことも確かめる。
        /// </summary>
        private static string AssertRejected(byte[] content, string because, string key = null, string secret = null)
        {
            BrainLaunchSettings settings;
            string problem;
            var ok = BrainLaunchSettings.TryParse(content, out settings, out problem);

            var where = because + "（中身 " + Show(content) + "）";
            Assert.That(ok, Is.False, where + ": 受け付けた");
            Assert.That(settings, Is.Null, where + ": 失敗なのに settings が null でない");
            AssertOneLine(problem, where);
            if (key != null)
            {
                Assert.That(problem.Contains(key), Is.True, where + ": problem にキー名 " + key + " が出ていない: " + problem);
            }

            if (secret != null)
            {
                Assert.That(problem.Contains(secret), Is.False, where + ": problem に値そのものが出ている: " + problem);
            }

            return problem;
        }

        private static string AssertRejected(string json, string because, string key = null, string secret = null)
        {
            return AssertRejected(Utf8(json), because, key, secret);
        }

        private string WriteFile(string name, byte[] data)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        // ---- 定数 ----

        [Test]
        public void MaxFileBytes_Is64KiB()
        {
            Assert.That(BrainLaunchSettings.MaxFileBytes, Is.EqualTo(65536));
        }

        // ---- TryParse: 受け付ける形 ----

        [Test]
        public void TryParse_AcceptsRequiredKeys_WithArgumentsEmpty_WhenArgumentsOmitted()
        {
            AssertAccepted(ValidDoc(), "arguments を書かない");
        }

        [Test]
        public void TryParse_TreatsNullArguments_AsEmpty()
        {
            AssertAccepted(Utf8(DocWith("arguments", "null")), Cmd, "", Wd, "arguments が null");
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\t")]
        [TestCase("   ")]
        [TestCase("-u -m brain")]
        [TestCase("  -u   main.py  ")]
        [TestCase("--name \"あいう 😀\" --flag")]
        [TestCase("a\nb")]
        [TestCase("C:\\x y\\z \"q\"")]
        public void TryParse_KeepsArguments_AsWritten(string arguments)
        {
            AssertAccepted(Utf8(DocWith("arguments", S(arguments))), Cmd, arguments, Wd, "arguments をそのまま返す");
        }

        private static IEnumerable<string> AbsolutePaths()
        {
            yield return @"C:\Brain\python.exe";
            yield return @"c:\brain\python.exe";
            yield return @"C:/Brain/python.exe";
            yield return @"z:/";
            yield return @"Z:\";
            yield return @"C:\";
            yield return @"A:\a";
            yield return @"C:\Program Files\Brain Dir\python.exe";
            yield return @"C:\a/b\c.exe";
            yield return @"C:\a\..\b.exe";
            yield return @"D:\頭脳\起動 😀.exe";
            yield return @"\\server\share\brain.exe";
            yield return @"\\server\share";
            yield return @"\\192.168.0.10\共有\brain";
        }

        [Test]
        public void TryParse_AcceptsAbsolutePath_AsWritten(
            [Values("command", "workingDirectory")] string key,
            [ValueSource(nameof(AbsolutePaths))] string path)
        {
            var expectedCommand = key == "command" ? path : Cmd;
            var expectedWd = key == "workingDirectory" ? path : Wd;
            AssertAccepted(Utf8(DocWith(key, S(path))), expectedCommand, "", expectedWd, key + " = " + path);
        }

        [Test]
        public void TryParse_Accepts_WithAndWithoutBom()
        {
            AssertAccepted(Utf8(ValidDoc()), Cmd, "", Wd, "BOM なし");
            AssertAccepted(Concat(Bom, Utf8(ValidDoc())), Cmd, "", Wd, "BOM あり");
        }

        [Test]
        public void TryParse_Accepts_HandEditedLayout()
        {
            var json = "\r\n\t{\r\n  \"command\" : " + S(Cmd) + " ,\r\n\r\n  \"workingDirectory\":\t" + S(Wd) + "\r\n}\r\n\r\n  ";
            AssertAccepted(json, "CRLF・タブ・前後の空白で書いた文書");
        }

        [Test]
        public void TryParse_IgnoresComments()
        {
            var json = "// brain settings\r\n/* block */ {" + Member("command", S(Cmd)) + ", /* mid */ "
                + Member("workingDirectory", S(Wd)) + " // tail\r\n} /* after */ // end";
            AssertAccepted(json, "コメント入り");
        }

        [Test]
        public void TryParse_IgnoresUnknownKeys_WhateverTheirType()
        {
            var json = Obj(
                Member("command", S(Cmd)),
                Member("Command", "5"),
                Member("COMMAND", S("relative")),
                Member("Arguments", "[1,2]"),
                Member("WorkingDirectory", "null"),
                Member("env", Obj(Member("A", S("B")))),
                Member("note", S("メモ")),
                Member("n", "1.5e3"),
                Member("flag", "false"),
                Member("nested", "[[[{\"a\":[1]}]]]"),
                Member("workingDirectory", S(Wd)));
            AssertAccepted(json, "知らないキー（大文字違いを含む）は無視");
        }

        [Test]
        public void TryParse_AcceptsModerateNesting_InUnknownKey()
        {
            var nested = new string('[', 10) + new string(']', 10);
            AssertAccepted(Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd)), Member("x", nested)), "入れ子 10 段");
        }

        // ---- TryParse: 中身全体の形 ----

        [Test]
        public void TryParse_RejectsNull()
        {
            AssertRejected((byte[])null, "content が null");
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\r\n")]
        [TestCase("\t \r\n ")]
        [TestCase("// only a comment")]
        public void TryParse_RejectsEmptyOrBlank(string text)
        {
            AssertRejected(text, "空・空白だけ");
            AssertRejected(Concat(Bom, Utf8(text)), "BOM + 空・空白だけ");
        }

        [TestCase("{")]
        [TestCase("}")]
        [TestCase("{\"command\":\"C:\\\\a\"")]
        [TestCase("{\"command\":\"C:\\\\a")]
        [TestCase("{\"command\" \"C:\\\\a\"}")]
        [TestCase("{\"command\":}")]
        [TestCase("{\"command\":\"C:\\\\a\",,\"workingDirectory\":\"D:\\\\b\"}")]
        [TestCase("{\"command\":\"C:\\a\",\"workingDirectory\":\"D:\\\\b\"}")]
        [TestCase("not json")]
        public void TryParse_RejectsBrokenJson(string text)
        {
            AssertRejected(text, "JSON として壊れている");
        }

        [TestCase(" x")]
        [TestCase(" {}")]
        [TestCase("{}")]
        [TestCase("]")]
        [TestCase(" null")]
        [TestCase(",")]
        [TestCase("\0")]
        [TestCase(" /* c */ 1")]
        public void TryParse_RejectsTrailingContent_AfterTheObject(string trailing)
        {
            AssertRejected(ValidDoc() + trailing, "値の後に余計な内容");
        }

        [TestCase("[]")]
        [TestCase("[{\"command\":\"C:\\\\a\",\"workingDirectory\":\"D:\\\\b\"}]")]
        [TestCase("\"C:\\\\a\"")]
        [TestCase("1")]
        [TestCase("null")]
        [TestCase("true")]
        public void TryParse_RejectsRootThatIsNotAnObject(string text)
        {
            AssertRejected(text, "ルートがオブジェクトでない");
        }

        [Test]
        public void TryParse_RejectsDuplicateKeys()
        {
            AssertRejected(
                Obj(Member("command", S(Cmd)), Member("command", S(Cmd)), Member("workingDirectory", S(Wd))),
                "command が 2 回（同じ値）");
            AssertRejected(
                Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd)), Member("workingDirectory", S(@"E:\other"))),
                "workingDirectory が 2 回");
            AssertRejected(
                Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd)), Member("arguments", S("a")), Member("arguments", S("b"))),
                "arguments が 2 回");
            AssertRejected(
                Obj(Member("command", S(Cmd)), Member("x", "1"), Member("workingDirectory", S(Wd)), Member("x", "1")),
                "知らないキーが 2 回");
        }

        [Test]
        public void TryParse_RejectsDeepNesting()
        {
            var nested = new string('[', 200) + new string(']', 200);
            AssertRejected(Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd)), Member("x", nested)), "入れ子 200 段");
        }

        // ---- TryParse: 文字コード ----

        [Test]
        public void TryParse_RejectsInvalidUtf8_AnywhereInTheContent()
        {
            var cases = new Dictionary<string, byte[]>
            {
                { "0xFF", new byte[] { 0xFF } },
                { "続きのバイトだけ 0x80", new byte[] { 0x80 } },
                { "冗長な表現 C0 AF", new byte[] { 0xC0, 0xAF } },
                { "サロゲートの符号化 ED A0 80", new byte[] { 0xED, 0xA0, 0x80 } },
                { "途中で切れた 3 バイト列 E3 81", new byte[] { 0xE3, 0x81 } },
                { "U+10FFFF を超える F4 90 80 80", new byte[] { 0xF4, 0x90, 0x80, 0x80 } },
            };

            foreach (var c in cases)
            {
                // 知らないキーの文字列の中
                var inUnknown = Concat(
                    Utf8(Obj(Member("command", S(Cmd)), Member("workingDirectory", S(Wd))).TrimEnd('}') + ",\"note\":\"a"),
                    c.Value,
                    Utf8("b\"}"));
                AssertRejected(inUnknown, "知らないキーの値に不正な UTF-8（" + c.Key + "）");

                // command の値の中
                var inCommand = Concat(Utf8("{\"command\":\"C:\\\\a"), c.Value, Utf8("\",\"workingDirectory\":" + S(Wd) + "}"));
                AssertRejected(inCommand, "command の値に不正な UTF-8（" + c.Key + "）");

                // 最後の空白のあと
                AssertRejected(Concat(Utf8(ValidDoc() + " "), c.Value), "末尾に不正な UTF-8（" + c.Key + "）");
            }
        }

        [Test]
        public void TryParse_RejectsUtf16()
        {
            var json = ValidDoc();
            AssertRejected(Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(json)), "UTF-16 LE（BOM 付き）");
            AssertRejected(Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(json)), "UTF-16 BE（BOM 付き）");
            AssertRejected(Encoding.Unicode.GetBytes(json), "UTF-16 LE（BOM なし。NUL が交じる）");
        }

        // ---- TryParse: 大きさ ----

        [Test]
        public void TryParse_Accepts_ExactlyMaxFileBytes()
        {
            AssertAccepted(PaddedValid(BrainLaunchSettings.MaxFileBytes, false), Cmd, "", Wd, "BOM なしでちょうど 65536 バイト");
            AssertAccepted(PaddedValid(BrainLaunchSettings.MaxFileBytes, true), Cmd, "", Wd, "BOM 込みでちょうど 65536 バイト");
        }

        [Test]
        public void TryParse_Rejects_OneByteOverMaxFileBytes()
        {
            AssertRejected(PaddedValid(BrainLaunchSettings.MaxFileBytes + 1, false), "BOM なしで 65537 バイト");
            AssertRejected(PaddedValid(BrainLaunchSettings.MaxFileBytes + 1, true), "BOM 込みで 65537 バイト");
            AssertRejected(PaddedValid(1024 * 1024, false), "1 MiB");
        }

        /// <summary>
        /// ちょうど 65536 バイトの正しい文書（BOM の有無を選ぶ）の後ろに、JSON として無害な空白を足した中身。
        /// 先頭 65536 バイトだけを見れば受け付けられる形なので、大きさの判定だけがこれを拒める。
        /// </summary>
        private static byte[] MaxSizeValidWithTrailing(byte[] trailing, bool bom)
        {
            var head = PaddedValid(BrainLaunchSettings.MaxFileBytes, bom);
            AssertAccepted(head, Cmd, "", Wd, "テスト側の組み立て: 先頭 65536 バイトは正しい文書");
            return Concat(head, trailing);
        }

        [TestCase(new byte[] { 0x20 }, false)]
        [TestCase(new byte[] { 0x0A }, false)]
        [TestCase(new byte[] { 0x0D, 0x0A }, false)]
        [TestCase(new byte[] { 0x09 }, false)]
        [TestCase(new byte[] { 0x20, 0x20, 0x20 }, false)]
        [TestCase(new byte[] { 0x20 }, true)]
        [TestCase(new byte[] { 0x0A }, true)]
        [TestCase(new byte[] { 0x20, 0x20, 0x20 }, true)]
        public void TryParse_Rejects_MaxSizeValidDocument_FollowedByWhitespace(byte[] trailing, bool bom)
        {
            var content = MaxSizeValidWithTrailing(trailing, bom);
            AssertRejected(content, (bom ? "BOM 込み" : "BOM なし") + "で 65536 バイトの正しい文書 + 末尾の空白 " + BitConverter.ToString(trailing));
        }

        // ---- TryParse: 必須キー ----

        [TestCase("command")]
        [TestCase("workingDirectory")]
        public void TryParse_RejectsMissingRequiredKey(string key)
        {
            AssertRejected(DocWithout(key), key + " が無い", key);
        }

        [Test]
        public void TryParse_RejectsEmptyObject()
        {
            AssertRejected("{}", "空のオブジェクト");
        }

        [TestCase("command", "Command")]
        [TestCase("command", "COMMAND")]
        [TestCase("command", "command ")]
        [TestCase("workingDirectory", "workingdirectory")]
        [TestCase("workingDirectory", "WorkingDirectory")]
        [TestCase("workingDirectory", "WORKINGDIRECTORY")]
        [TestCase("workingDirectory", "working_directory")]
        public void TryParse_RejectsRequiredKey_WrittenWithOtherCase(string key, string writtenAs)
        {
            var other = key == "command" ? Member("workingDirectory", S(Wd)) : Member("command", S(Cmd));
            var value = key == "command" ? S(Cmd) : S(Wd);
            AssertRejected(Obj(other, Member(writtenAs, value)), key + " を " + writtenAs + " と書いた", key);
        }

        private static IEnumerable<string> NonStringValues()
        {
            yield return "null";
            yield return "0";
            yield return "1";
            yield return "-1.5";
            yield return "true";
            yield return "false";
            yield return "[]";
            yield return "[" + S(@"C:\a") + "]";
            yield return "{}";
            yield return Obj(Member("path", S(@"C:\a")));
        }

        [Test]
        public void TryParse_RejectsRequiredKey_ThatIsNotAString(
            [Values("command", "workingDirectory")] string key,
            [ValueSource(nameof(NonStringValues))] string rawValue)
        {
            AssertRejected(DocWith(key, rawValue), key + " = " + rawValue, key);
        }

        [TestCase("0")]
        [TestCase("true")]
        [TestCase("false")]
        [TestCase("[]")]
        [TestCase("[\"-u\"]")]
        [TestCase("{}")]
        public void TryParse_RejectsArguments_ThatIsNotAString(string rawValue)
        {
            AssertRejected(DocWith("arguments", rawValue), "arguments = " + rawValue, "arguments");
        }

        private static IEnumerable<string> NotAbsolutePaths()
        {
            // 空・空白だけ
            yield return "";
            yield return " ";
            yield return "\t";
            yield return "\u3000";
            // 相対
            yield return "python.exe";
            yield return @"brain\python.exe";
            yield return "brain/python.exe";
            yield return @".\python.exe";
            yield return @"..\python.exe";
            // ドライブ相対・ドライブだけ
            yield return "C:";
            yield return "C:python.exe";
            yield return @"C:..\python.exe";
            // ルート相対・スラッシュ始まり
            yield return @"\python.exe";
            yield return "/python.exe";
            yield return "/c/brain/python.exe";
            // サーバー名の無い UNC
            yield return @"\\";
            yield return @"\\\";
            yield return @"\\\share";
            yield return @"\\\share\a";
            yield return @"\\/";
            yield return @"\\/share";
            yield return @"\\/share/a";
            yield return @"\\/share\a";
            // ドライブ文字が A–Z / a–z でない
            yield return @"1:\a";
            yield return @"Ａ:\a";
            yield return @"é:\a";
            yield return @"CC:\a";
            yield return @":\a";
            yield return @"C;\a";
            yield return @"C|\a";
            // 前後の空白（行区切りを含む空白文字の集合）
            yield return @" C:\a";
            yield return @"C:\a ";
            yield return "\tC:\\a";
            yield return "C:\\a\t";
            yield return "C:\\a\n";
            yield return "\r\nC:\\a";
            yield return "\u00A0C:\\a";
            yield return "C:\\a\u00A0";
            yield return "C:\\a\u3000";
            yield return "C:\\a\u2028";
            yield return "C:\\a\u0085";
            yield return @" \\server\share";
            // 見えない文字が先頭にある
            yield return "\uFEFFC:\\a";
            yield return "\u200BC:\\a";
            // 制御文字入り
            yield return "C:\\a\u0000b";
            yield return "C:\\a\u0001b";
            yield return "C:\\a\u001Fb";
            yield return "\\\\server\\sh\u0007are";
        }

        [Test]
        public void TryParse_RejectsPath_ThatIsNotAbsolute(
            [Values("command", "workingDirectory")] string key,
            [ValueSource(nameof(NotAbsolutePaths))] string path)
        {
            AssertRejected(DocWith(key, S(path)), key + " = " + S(path), key);
        }

        [Test]
        public void TryParse_RejectsPath_ContainingAnyInvalidPathChar(
            [Values("command", "workingDirectory")] string key)
        {
            var invalid = Path.GetInvalidPathChars();
            Assert.That(invalid, Is.Not.Empty, "Path.GetInvalidPathChars() が空");
            foreach (var c in invalid)
            {
                var path = @"C:\Brain" + c + "x";
                AssertRejected(DocWith(key, S(path)), key + " に U+" + ((int)c).ToString("X4"), key);
            }
        }

        [TestCase("command")]
        [TestCase("workingDirectory")]
        public void TryParse_DoesNotPutTheValue_IntoTheProblem(string key)
        {
            AssertRejected(DocWith(key, S(@"relative\SECRET-7f3a\x")), key + " が相対パス", key, "SECRET-7f3a");
            AssertRejected(DocWith(key, S(@"C:\SECRET-7f3a" + "\u0001")), key + " に制御文字", key, "SECRET-7f3a");
        }

        // ---- Load ----

        [Test]
        public void Load_ReturnsMissing_WhenNoFileOrFolderHasThatName()
        {
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(Path.Combine(_folder, "brain.json"), out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Missing));
            Assert.That(settings, Is.Null, "settings");
            Assert.That(problem, Is.Null, "problem");
        }

        [Test]
        public void Load_ReturnsMissing_WhenTheParentFolderDoesNotExist()
        {
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(Path.Combine(_folder, "VRMate", "brain.json"), out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Missing));
            Assert.That(settings, Is.Null, "settings");
            Assert.That(problem, Is.Null, "problem");
        }

        [Test]
        public void Load_ReturnsValid_ForAGoodFile_AndLeavesItUntouched()
        {
            var content = Concat(Bom, Utf8(
                "{\r\n  \"command\": " + S(@"C:\Brain\.venv\Scripts\python.exe") + ",\r\n  \"arguments\": " + S("-u -m brain")
                + ",\r\n  \"workingDirectory\": " + S(@"C:\Brain") + "\r\n}\r\n"));
            var path = WriteFile("brain.json", content);
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Valid), "problem: " + problem);
            Assert.That(problem, Is.Null, "problem");
            Assert.That(settings, Is.Not.Null, "settings");
            Assert.That(settings.Command, Is.EqualTo(@"C:\Brain\.venv\Scripts\python.exe"));
            Assert.That(settings.Arguments, Is.EqualTo("-u -m brain"));
            Assert.That(settings.WorkingDirectory, Is.EqualTo(@"C:\Brain"));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(content), "ファイルが書き換わった");
        }

        [Test]
        public void Load_ReturnsInvalid_WhenTheContentFailsTheCheck()
        {
            var path = WriteFile("brain.json", Utf8(DocWith("workingDirectory", S(@"relative\SECRET-7f3a"))));
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid));
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, "workingDirectory が相対パス");
            Assert.That(problem.Contains("workingDirectory"), Is.True, "problem にキー名が無い: " + problem);
            Assert.That(problem.Contains("SECRET-7f3a"), Is.False, "problem に値が出ている: " + problem);
        }

        [TestCase(new byte[0])]
        [TestCase(new byte[] { 0xEF, 0xBB, 0xBF })]
        [TestCase(new byte[] { 0x20, 0x0D, 0x0A })]
        [TestCase(new byte[] { 0x7B })]
        [TestCase(new byte[] { 0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00 })]
        public void Load_ReturnsInvalid_ForEmptyBlankBrokenOrUtf16File(byte[] content)
        {
            var path = WriteFile("brain.json", content);
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid), Show(content));
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, Show(content));
        }

        [Test]
        public void Load_ReturnsInvalid_WhenAFolderHasThatName()
        {
            var path = Path.Combine(_folder, "brain.json");
            Directory.CreateDirectory(path);
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid));
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, "同名のフォルダ");
        }

        [Test]
        public void Load_ReturnsInvalid_WhenTheFileIsLockedByAnotherHandle()
        {
            var path = WriteFile("brain.json", Utf8(ValidDoc()));
            BrainLaunchSettings settings;
            string problem;
            BrainLaunchLoad result;

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                // 前提: この環境で FileShare.None が読み取りを本当に拒むこと
                var blocked = false;
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                    }
                }
                catch (IOException)
                {
                    blocked = true;
                }

                if (!blocked)
                {
                    Assert.Ignore("この環境では FileShare.None で開いたファイルを別のハンドルから読めるため、ロックを再現できない");
                }

                result = BrainLaunchSettings.Load(path, out settings, out problem);
            }

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid));
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, "ロックされたファイル");
        }

        [Test]
        public void Load_ReturnsValid_AtExactlyMaxFileBytes()
        {
            var path = WriteFile("brain.json", PaddedValid(BrainLaunchSettings.MaxFileBytes, false));
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Valid), "problem: " + problem);
            Assert.That(problem, Is.Null, "problem");
            Assert.That(settings, Is.Not.Null, "settings");
            Assert.That(settings.Command, Is.EqualTo(Cmd));
        }

        [TestCase(65537)]
        [TestCase(4 * 1024 * 1024)]
        public void Load_ReturnsInvalid_WhenTheFileIsLargerThanMaxFileBytes(int size)
        {
            var path = WriteFile("brain.json", PaddedValid(size, false));
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid), size + " バイト");
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, size + " バイト");
        }

        [TestCase(new byte[] { 0x20 }, false)]
        [TestCase(new byte[] { 0x0A }, false)]
        [TestCase(new byte[] { 0x0D, 0x0A }, false)]
        [TestCase(new byte[] { 0x09 }, false)]
        [TestCase(new byte[] { 0x20, 0x20, 0x20 }, false)]
        [TestCase(new byte[] { 0x20 }, true)]
        [TestCase(new byte[] { 0x0A }, true)]
        [TestCase(new byte[] { 0x20, 0x20, 0x20 }, true)]
        public void Load_ReturnsInvalid_ForMaxSizeValidDocument_FollowedByWhitespace(byte[] trailing, bool bom)
        {
            var content = MaxSizeValidWithTrailing(trailing, bom);
            var path = WriteFile("brain.json", content);
            var where = (bom ? "BOM 込み" : "BOM なし") + "で 65536 バイトの正しい文書 + 末尾の空白 " + BitConverter.ToString(trailing);
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(new FileInfo(path).Length, Is.EqualTo(BrainLaunchSettings.MaxFileBytes + trailing.Length), "テスト側の組み立て");
            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid), where);
            Assert.That(settings, Is.Null, where + ": settings");
            AssertOneLine(problem, where);
        }

        [TestCase((string)null)]
        [TestCase("")]
        public void Load_ReturnsInvalid_ForNullOrEmptyPath(string path)
        {
            BrainLaunchSettings settings;
            string problem;

            var result = BrainLaunchSettings.Load(path, out settings, out problem);

            Assert.That(result, Is.EqualTo(BrainLaunchLoad.Invalid));
            Assert.That(settings, Is.Null, "settings");
            AssertOneLine(problem, "path = " + (path ?? "null"));
        }

        [TestCase(" ")]
        [TestCase("C:\\a|b\\brain.json")]
        [TestCase("C:\\a\u0000b\\brain.json")]
        [TestCase("\\\\?\\")]
        [TestCase("::")]
        public void Load_DoesNotThrow_AndReportsConsistently_ForOddPaths(string path)
        {
            BrainLaunchSettings settings = null;
            string problem = null;
            var result = BrainLaunchLoad.Valid;

            Assert.DoesNotThrow(() => result = BrainLaunchSettings.Load(path, out settings, out problem), S(path));

            Assert.That(result, Is.Not.EqualTo(BrainLaunchLoad.Valid), S(path));
            Assert.That(settings, Is.Null, S(path) + ": settings");
            if (result == BrainLaunchLoad.Missing)
            {
                Assert.That(problem, Is.Null, S(path) + ": Missing なのに problem がある");
            }
            else
            {
                AssertOneLine(problem, S(path));
            }
        }

        [Test]
        public void Load_DoesNotThrow_ForAVeryLongPath()
        {
            var path = Path.Combine(_folder, new string('a', 40000) + ".json");
            BrainLaunchSettings settings = null;
            string problem = null;
            var result = BrainLaunchLoad.Valid;

            Assert.DoesNotThrow(() => result = BrainLaunchSettings.Load(path, out settings, out problem));

            Assert.That(result, Is.Not.EqualTo(BrainLaunchLoad.Valid));
            Assert.That(settings, Is.Null, "settings");
            if (result == BrainLaunchLoad.Invalid)
            {
                AssertOneLine(problem, "とても長いパス");
            }
            else
            {
                Assert.That(problem, Is.Null, "Missing なのに problem がある");
            }
        }
    }
}
