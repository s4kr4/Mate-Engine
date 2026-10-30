namespace VRMate
{
    /// <summary>
    /// Holds the raw text of the current reply: every text appended for that reply since it started, in order.
    /// The bubble's displayed text is rewritten (Markdown turned into tags), so the next text is appended to this
    /// instead. Text is kept exactly as received (no trimming or normalization) and null counts as empty.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class ReplyText
    {
        private bool _hasReply;
        private ReplyId _reply;

        public string Text { get; private set; } = string.Empty;

        /// <summary>
        /// Appends text to the current reply. Returns true when it started over (no reply yet, or another reply),
        /// in which case Text is only this text.
        /// </summary>
        public bool Append(ReplyId reply, string text)
        {
            return Append(reply, text, true);
        }

        /// <summary>
        /// Appends text to the current reply. bubbleAlive: whether the current reply's bubble is still shown; when
        /// false, even the same reply starts over. Returns true when it started over (no reply yet, another reply,
        /// or the bubble is gone), in which case Text is only this text.
        /// </summary>
        public bool Append(ReplyId reply, string text, bool bubbleAlive)
        {
            text = text ?? string.Empty;
            if (bubbleAlive && _hasReply && reply == _reply)
            {
                Text += text;
                return false;
            }

            _hasReply = true;
            _reply = reply;
            Text = text;
            return true;
        }

        /// <summary>Forgets the current reply, so the next Append starts over.</summary>
        public void Clear()
        {
            _hasReply = false;
            _reply = default(ReplyId);
            Text = string.Empty;
        }
    }
}
