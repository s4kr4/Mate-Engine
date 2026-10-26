using System;
using System.Text;

namespace VRMate
{
    /// <summary>
    /// Decides which lines of the brain's output go into brain.log, so that the file stays within a size
    /// limit however much the brain prints. Lines are passed through unchanged and in order until the next
    /// one would leave no room for LimitNote; then LimitNote is returned once instead and every later line
    /// is dropped. Sizes are counted in the encoding brain.log is written with (UTF-8 without a BOM, lone
    /// surrogates replaced), so the bytes returned never add up to more than the limit.
    /// Not thread-safe; the caller locks.
    /// </summary>
    public sealed class BrainLog
    {
        public const long MaxBytes = 8L * 1024 * 1024;

        public const string LimitNote = "brain.log reached its size limit; later output is dropped";

        /// <summary>The encoding brain.log is written with: no BOM, and lone surrogates become U+FFFD (3 bytes).</summary>
        public static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private static readonly string NoteLine = LimitNote + "\n";
        private static readonly long NoteBytes = Utf8.GetByteCount(NoteLine);

        private readonly long _maxBytes;
        private bool _limitReached;

        /// <summary>A log limited to MaxBytes.</summary>
        public BrainLog()
            : this(MaxBytes)
        {
        }

        /// <summary>Throws ArgumentOutOfRangeException when maxBytes cannot hold LimitNote and its newline.</summary>
        public BrainLog(long maxBytes)
        {
            if (maxBytes < NoteBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "must hold at least the limit note");
            }

            _maxBytes = maxBytes;
        }

        /// <summary>The UTF-8 size of all the text returned so far.</summary>
        public long WrittenBytes { get; private set; }

        /// <summary>
        /// Returns the text to write for one line of output (the line and "\n"), LimitNote and "\n" in its
        /// place when it does not fit, or null when nothing is written. A null line (the end of the output)
        /// returns null and changes nothing.
        /// </summary>
        public string Accept(string line)
        {
            if (line == null || _limitReached)
            {
                return null;
            }

            // Counted without building line + "\n" first, so a huge line that is dropped is not copied.
            // The newline is one byte and never pairs with a lone surrogate, so the counts simply add.
            var lineBytes = (long)Utf8.GetByteCount(line) + 1;
            if (WrittenBytes + lineBytes + NoteBytes > _maxBytes)
            {
                _limitReached = true;
                WrittenBytes += NoteBytes;
                return NoteLine;
            }

            WrittenBytes += lineBytes;
            return line + "\n";
        }
    }
}
