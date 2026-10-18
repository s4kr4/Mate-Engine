using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace VRMate.Tests
{
    /// <summary>
    /// モーションのフォルダの直下の &lt;name&gt;.vrma だけを読むこと。ファイルは毎回 Path.GetTempPath() の下の新しいフォルダに作る。
    /// </summary>
    public class MotionFilesTests
    {
        private const string FolderPrefix = "vrmate-motion-tests-";

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

        private static string Trimmed(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private string WriteFile(string name, byte[] data)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        private static byte[] Bytes(int count)
        {
            return Enumerable.Range(0, count).Select(i => (byte)(i * 37 + 11)).ToArray();
        }

        // ---- 定数 ----

        [Test]
        public void MaxBytes_Is16MiB()
        {
            Assert.That(MotionFiles.MaxBytes, Is.EqualTo(16L * 1024 * 1024));
        }

        // ---- TryGetPath ----

        [TestCase("wave")]
        [TestCase("VRMA_02")]
        [TestCase("-")]
        [TestCase("--")]
        [TestCase("COM10")]
        [TestCase("CON_")]
        [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-")]
        public void TryGetPath_ReturnsVrmaDirectlyInsideFolder_WhenNameIsWellFormed(string name)
        {
            string path;
            var ok = MotionFiles.TryGetPath(_folder, name, out path);

            Assert.That(ok, Is.True, name);
            Assert.That(path, Is.Not.Null);
            Assert.That(Path.GetFileName(path), Is.EqualTo(name + ".vrma"));
            Assert.That(Path.GetFullPath(Path.GetDirectoryName(path)), Is.EqualTo(Trimmed(Path.GetFullPath(_folder))));
            Assert.That(Path.GetFullPath(path), Is.EqualTo(Path.Combine(Trimmed(Path.GetFullPath(_folder)), name + ".vrma")));
        }

        [Test]
        public void TryGetPath_KeepsPathDirectlyInsideFolder_WhenFolderEndsWithSeparatorOrUsesForwardSlashes()
        {
            var folders = new[]
            {
                _folder + Path.DirectorySeparatorChar,
                _folder + Path.AltDirectorySeparatorChar,
                _folder.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            };

            foreach (var folder in folders)
            {
                string path;
                var ok = MotionFiles.TryGetPath(folder, "wave", out path);

                Assert.That(ok, Is.True, folder);
                Assert.That(Path.GetFileName(path), Is.EqualTo("wave.vrma"), folder);
                Assert.That(Path.GetFullPath(Path.GetDirectoryName(path)), Is.EqualTo(Trimmed(Path.GetFullPath(_folder))), folder);
            }
        }

        [Test]
        public void TryGetPath_ReturnsFalseAndNullPath_WhenNameIsNotAWellFormedMotion()
        {
            var names = new[]
            {
                null,
                string.Empty,
                ".",
                "..",
                "../wave",
                "..\\wave",
                "../../Windows/win.ini",
                "sub/wave",
                "sub\\wave",
                "/wave",
                "\\wave",
                "C:wave",
                "C:\\Windows\\win",
                "\\\\server\\share\\wave",
                "wave.vrma",
                "wave.",
                "wave:stream",
                "wave*",
                "wave?",
                " wave",
                "wave ",
                "wave" + (char)0,
                "wave" + (char)0 + "/../x",
                "CON",
                "con",
                "Con",
                "NUL",
                "nul",
                "AUX",
                "PRN",
                "COM1",
                "com9",
                "LPT9",
                "lpt0",
                "ｗａｖｅ",
                "caf" + (char)0x00E9,
                new string('a', 65),
            };

            foreach (var name in names)
            {
                string path = "untouched";
                var ok = MotionFiles.TryGetPath(_folder, name, out path);

                var shown = name == null ? "null" : name.Replace(((char)0).ToString(), "\\0");
                Assert.That(ok, Is.False, shown);
                Assert.That(path, Is.Null, shown);
            }
        }

        [Test]
        public void TryGetPath_ReturnsFalse_WhenFolderIsNullEmptyOrRelative()
        {
            var folders = new[]
            {
                null,
                string.Empty,
                "Motions",
                "VRMate/Motions",
                "./Motions",
                "../Motions",
                ".",
                "..",
                "C:Motions",
                "\\Motions",
                "/Motions",
            };

            foreach (var folder in folders)
            {
                string path = "untouched";
                var ok = MotionFiles.TryGetPath(folder, "wave", out path);

                Assert.That(ok, Is.False, folder ?? "null");
                Assert.That(path, Is.Null, folder ?? "null");
            }
        }

        [Test]
        public void TryGetPath_NeverPointsOutsideFolder_ForEveryNameItAccepts()
        {
            // 受け入れた名前はどれも、フォルダの直下の <name>.vrma になる
            var random = new Random(20261017);
            var alphabet = "aZ09_-./\\:*?\"<>| " + (char)0 + (char)0x00E9 + (char)0xFF41;
            var accepted = 0;
            var full = Trimmed(Path.GetFullPath(_folder));

            for (var round = 0; round < 5000; round++)
            {
                var length = 1 + random.Next(8);
                var chars = new char[length];
                for (var i = 0; i < length; i++)
                {
                    chars[i] = alphabet[random.Next(alphabet.Length)];
                }

                var name = new string(chars);
                string path;
                if (!MotionFiles.TryGetPath(_folder, name, out path))
                {
                    continue;
                }

                accepted++;
                Assert.That(CueNames.IsMotion(name), Is.True, name);
                Assert.That(Path.GetFileName(path), Is.EqualTo(name + ".vrma"), name);
                Assert.That(Path.GetFullPath(Path.GetDirectoryName(path)), Is.EqualTo(full), name);
            }

            Assert.That(accepted, Is.GreaterThan(100), "受け入れた名前が少なすぎて確かめになっていない");
        }

        // ---- TryRead ----

        [TestCase(1)]
        [TestCase(9)]
        [TestCase(10)]
        public void TryRead_ReturnsExactBytes_WhenFileIsNotLargerThanMaxBytes(int size)
        {
            var data = Bytes(size);
            var path = WriteFile("wave.vrma", data);

            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(path, 10, out read, out error);

            Assert.That(ok, Is.True, error);
            Assert.That(read, Is.EqualTo(data));
        }

        [TestCase(11)]
        [TestCase(12)]
        [TestCase(5000)]
        public void TryRead_ReturnsFalse_WhenFileIsLargerThanMaxBytes(int size)
        {
            var path = WriteFile("wave.vrma", Bytes(size));

            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(path, 10, out read, out error);

            Assert.That(ok, Is.False);
            Assert.That(read, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryRead_ReturnsFalse_WhenFileIsEmpty()
        {
            var path = WriteFile("wave.vrma", new byte[0]);

            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(path, 10, out read, out error);

            Assert.That(ok, Is.False);
            Assert.That(read, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryRead_ReturnsFalse_WhenFileDoesNotExist()
        {
            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(Path.Combine(_folder, "missing.vrma"), 10, out read, out error);

            Assert.That(ok, Is.False);
            Assert.That(read, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryRead_ReturnsFalse_WhenParentFolderDoesNotExist()
        {
            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(Path.Combine(Path.Combine(_folder, "nope"), "wave.vrma"), 10, out read, out error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryRead_ReturnsFalse_WhenPathIsDirectory()
        {
            var directory = Path.Combine(_folder, "wave.vrma");
            Directory.CreateDirectory(directory);

            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(directory, 10, out read, out error);

            Assert.That(ok, Is.False);
            Assert.That(read, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void TryRead_ReturnsFalseWithoutThrowing_WhenFileIsLockedByAnotherHandle()
        {
            var path = WriteFile("wave.vrma", Bytes(5));

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] read = null;
                string error = null;
                var ok = true;
                Assert.DoesNotThrow(() => ok = MotionFiles.TryRead(path, 10, out read, out error));

                Assert.That(ok, Is.False);
                Assert.That(read, Is.Null);
                Assert.That(error, Is.Not.Null.And.Not.Empty);
            }
        }

        [Test]
        public void TryRead_ReturnsFalseWithoutThrowing_WhenPathIsNullEmptyOrMalformed()
        {
            var inside = _folder + Path.DirectorySeparatorChar;
            var paths = new[] { null, string.Empty, " ", inside + "wa" + (char)0 + "ve.vrma", inside + "wa|ve.vrma", inside + "wa\"ve.vrma", "\\\\?\\" };

            foreach (var path in paths)
            {
                byte[] read = null;
                string error = null;
                var ok = true;
                Assert.DoesNotThrow(() => ok = MotionFiles.TryRead(path, 10, out read, out error), path ?? "null");

                Assert.That(ok, Is.False, path ?? "null");
                Assert.That(read, Is.Null, path ?? "null");
                Assert.That(error, Is.Not.Null.And.Not.Empty, path ?? "null");
            }
        }

        [Test]
        public void TryRead_ReadsFileFoundByTryGetPath()
        {
            var data = Bytes(7);
            WriteFile("VRMA_02.vrma", data);

            string path;
            Assert.That(MotionFiles.TryGetPath(_folder, "VRMA_02", out path), Is.True);
            byte[] read;
            string error;
            var ok = MotionFiles.TryRead(path, MotionFiles.MaxBytes, out read, out error);

            Assert.That(ok, Is.True, error);
            Assert.That(read, Is.EqualTo(data));
        }
    }
}
