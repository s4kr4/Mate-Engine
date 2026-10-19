using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>
    /// Decides which files in an idle folder are read. Only "&lt;name&gt;.vrma" with a well-formed motion
    /// name (CueNames.IsMotion) is a candidate; names that open the same file count once; at most
    /// MaxFiles are taken in NameOrder. Pure: the folder is listed by the caller. Never throws.
    /// </summary>
    public static class IdleFiles
    {
        public const int MaxFiles = 32;

        /// <summary>
        /// The playing order. The same comparison as MotionFiles.NameComparer, so that two names are
        /// equal here exactly when they open the same file. Ordinal, never culture-aware: under tr-TR a
        /// culture comparer would order "i" and "I" apart.
        /// </summary>
        public static readonly IComparer<string> NameOrder = StringComparer.OrdinalIgnoreCase;

        private const string Extension = MotionFiles.Extension;

        /// <summary>
        /// File names as Path.GetFileName gives them (null, empty and other extensions are skipped
        /// silently). Null reads as no files.
        /// </summary>
        public static IdleFileSelection Select(IEnumerable<string> fileNames)
        {
            var malformed = new List<string>();
            var candidates = new List<Candidate>();
            if (fileNames != null)
            {
                foreach (var fileName in fileNames)
                {
                    if (string.IsNullOrEmpty(fileName) || !HasExtension(fileName))
                    {
                        continue;
                    }

                    var name = fileName.Substring(0, fileName.Length - Extension.Length);
                    if (CueNames.IsMotion(name))
                    {
                        candidates.Add(new Candidate(name, fileName));
                    }
                    else
                    {
                        malformed.Add(fileName);
                    }
                }
            }

            // Sorting the spellings ordinally first makes the kept one independent of the listing order.
            candidates.Sort(CompareSpelling);
            var seen = new HashSet<string>(MotionFiles.NameComparer);
            var accepted = new List<string>();
            var duplicates = new List<string>();
            foreach (var candidate in candidates)
            {
                if (seen.Add(candidate.Name))
                {
                    accepted.Add(candidate.Name);
                }
                else
                {
                    duplicates.Add(candidate.FileName);
                }
            }

            accepted.Sort(NameOrder);
            var count = Math.Min(MaxFiles, accepted.Count);
            return new IdleFileSelection(
                accepted.GetRange(0, count).ToArray(),
                malformed.ToArray(),
                duplicates.ToArray(),
                accepted.GetRange(count, accepted.Count - count).ToArray());
        }

        /// <summary>
        /// ".vrma" in any letter case of ASCII letters only: Windows opens ".VRMA" as the same file, and
        /// folding no other letters keeps look-alikes (full-width, other scripts) out whatever the runtime's
        /// case tables say.
        /// </summary>
        private static bool HasExtension(string fileName)
        {
            if (fileName.Length < Extension.Length)
            {
                return false;
            }

            var start = fileName.Length - Extension.Length;
            for (var i = 0; i < Extension.Length; i++)
            {
                var c = fileName[start + i];
                if (c >= 'A' && c <= 'Z')
                {
                    c = (char)(c + ('a' - 'A'));
                }

                if (c != Extension[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareSpelling(Candidate left, Candidate right)
        {
            var byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.FileName, right.FileName);
        }

        private struct Candidate
        {
            public Candidate(string name, string fileName)
            {
                Name = name;
                FileName = fileName;
            }

            public string Name { get; }

            public string FileName { get; }
        }
    }

    /// <summary>The result of IdleFiles.Select. Every list is non-null.</summary>
    public sealed class IdleFileSelection
    {
        internal IdleFileSelection(IReadOnlyList<string> names, IReadOnlyList<string> malformed, IReadOnlyList<string> duplicates, IReadOnlyList<string> overLimit)
        {
            Names = names;
            Malformed = malformed;
            Duplicates = duplicates;
            OverLimit = overLimit;
        }

        /// <summary>Motion names to read (no extension, spelled as given), in NameOrder; at most MaxFiles.</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>File names (as given) ending in .vrma whose name is not a well-formed motion name.</summary>
        public IReadOnlyList<string> Malformed { get; }

        /// <summary>File names (as given) whose name opens the same file as a kept name with an ordinally smaller spelling.</summary>
        public IReadOnlyList<string> Duplicates { get; }

        /// <summary>Well-formed, distinct names past the first MaxFiles in NameOrder, in NameOrder.</summary>
        public IReadOnlyList<string> OverLimit { get; }
    }
}
