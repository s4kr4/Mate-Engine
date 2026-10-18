using System;
using System.Collections.Generic;
using System.IO;

namespace VRMate
{
    /// <summary>
    /// Finds and reads motion files. Only "&lt;name&gt;.vrma" directly inside the motion folder is ever
    /// opened, and reading stops as soon as more than the limit has arrived. Never throws.
    /// </summary>
    public static class MotionFiles
    {
        public const string Extension = ".vrma";

        /// <summary>Larger files are not read (design: 16 MiB per .vrma).</summary>
        public const long MaxBytes = 16L * 1024 * 1024;

        /// <summary>
        /// Compares motion names the way the file system compares their files: Windows opens
        /// "VRMA_02.vrma" and "vrma_02.vrma" alike, so a cache keyed by name must treat them as one.
        /// Ordinal, never culture-aware: a culture comparer keeps the culture it was created under, and
        /// under tr-TR "i" and "I" are different letters. Motion names are ASCII (CueNames.IsMotion),
        /// where OrdinalIgnoreCase folds exactly A-Z to a-z.
        /// </summary>
        public static readonly IEqualityComparer<string> NameComparer = StringComparer.OrdinalIgnoreCase;

        private const int ChunkBytes = 81920;

        /// <summary>
        /// False (and path null) unless the folder is a fully qualified path and name is a well-formed
        /// motion name (CueNames.IsMotion).
        /// </summary>
        public static bool TryGetPath(string folder, string name, out string path)
        {
            path = null;
            if (!CueNames.IsMotion(name) || !IsFullyQualified(folder))
            {
                return false;
            }

            try
            {
                path = Path.Combine(Path.GetFullPath(folder), name + Extension);
                return true;
            }
            catch (Exception)
            {
                // Characters the platform refuses in the folder (the name is ASCII-only and cannot cause this).
                path = null;
                return false;
            }
        }

        /// <summary>
        /// Reads the whole file when it holds 1 to maxBytes bytes. Any failure (missing, a directory,
        /// locked, malformed path, too large, empty) returns false with a short reason in error.
        /// </summary>
        public static bool TryRead(string path, long maxBytes, out byte[] data, out string error)
        {
            data = null;
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "no path";
                return false;
            }

            if (maxBytes <= 0)
            {
                error = "nothing may be read";
                return false;
            }

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    long declared;
                    try
                    {
                        declared = stream.Length;
                    }
                    catch (NotSupportedException)
                    {
                        declared = -1;
                    }

                    if (declared > maxBytes)
                    {
                        error = "larger than " + maxBytes + " bytes";
                        return false;
                    }

                    // The length can change while reading (the file is being written), so the limit is
                    // enforced on the bytes that actually arrive, not on the length seen above.
                    using (var buffer = new MemoryStream(declared > 0 && declared <= int.MaxValue ? (int)declared : 0))
                    {
                        var chunk = new byte[ChunkBytes];
                        long total = 0;
                        while (true)
                        {
                            var wanted = (int)Math.Min(chunk.Length, maxBytes - total + 1);
                            var read = stream.Read(chunk, 0, wanted);
                            if (read <= 0)
                            {
                                break;
                            }

                            total += read;
                            if (total > maxBytes)
                            {
                                error = "larger than " + maxBytes + " bytes";
                                return false;
                            }

                            buffer.Write(chunk, 0, read);
                        }

                        if (total == 0)
                        {
                            error = "empty file";
                            return false;
                        }

                        data = buffer.ToArray();
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                data = null;
                error = "cannot read: " + e.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// "X:\" or "X:/" with an ASCII drive letter, or a UNC path ("\\server\share"). Path.IsPathRooted
        /// also accepts "C:Motions" (relative to the drive's current folder) and "\Motions" (relative to
        /// the current drive), and Path.IsPathFullyQualified is not available on every Unity profile.
        /// Device paths ("\\?\", "\\.\") are refused.
        /// </summary>
        private static bool IsFullyQualified(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder.Length < 3)
            {
                return false;
            }

            var first = folder[0];
            var drive = (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z');
            if (drive && folder[1] == ':' && IsSeparator(folder[2]))
            {
                return true;
            }

            if (IsSeparator(folder[0]) && IsSeparator(folder[1]))
            {
                var server = folder[2];
                return !IsSeparator(server) && server != '?' && server != '.';
            }

            return false;
        }

        private static bool IsSeparator(char c)
        {
            return c == '\\' || c == '/';
        }
    }
}
