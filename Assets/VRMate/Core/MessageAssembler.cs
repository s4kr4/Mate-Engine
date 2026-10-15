using System;
using System.IO;
using System.Text;

namespace VRMate
{
    public enum AssembleStatus
    {
        Pending,
        Text,
        Binary,
        TooLarge,
    }

    public struct AssembleResult
    {
        public AssembleResult(AssembleStatus status, string text)
        {
            Status = status;
            Text = text;
        }

        public AssembleStatus Status { get; }

        /// <summary>The decoded message when Status is Text; otherwise null.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// Joins WebSocket frames into whole messages with a size limit. Text is decoded only once the
    /// message is complete, so multi-byte characters split across frames survive.
    /// </summary>
    public sealed class MessageAssembler
    {
        public const int DefaultMaxBytes = 8 * 1024 * 1024;

        // Invalid UTF-8 becomes U+FFFD instead of throwing.
        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly int _maxBytes;
        private readonly MemoryStream _buffer = new MemoryStream();
        private long _total;
        private bool _inMessage;
        private bool _isText;

        public MessageAssembler(int maxBytes)
        {
            if (maxBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            _maxBytes = maxBytes;
        }

        public AssembleResult Append(byte[] buffer, int offset, int count, bool isText, bool endOfMessage)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || count > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (!_inMessage)
            {
                _inMessage = true;
                _isText = isText;
            }

            _total += count;
            if (_total > _maxBytes)
            {
                Reset();
                return new AssembleResult(AssembleStatus.TooLarge, null);
            }

            // Binary messages are counted against the limit but their content is not kept.
            if (_isText)
            {
                _buffer.Write(buffer, offset, count);
            }

            if (!endOfMessage)
            {
                return new AssembleResult(AssembleStatus.Pending, null);
            }

            AssembleResult result;
            if (_isText)
            {
                result = new AssembleResult(AssembleStatus.Text, Utf8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length));
            }
            else
            {
                result = new AssembleResult(AssembleStatus.Binary, null);
            }

            Reset();
            return result;
        }

        private void Reset()
        {
            _buffer.SetLength(0);
            _total = 0;
            _inMessage = false;
            _isText = false;
        }
    }
}
