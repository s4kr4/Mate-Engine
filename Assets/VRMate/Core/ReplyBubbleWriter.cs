using System;

namespace VRMate
{
    /// <summary>
    /// The bubbles a reply is written into. TBubble is whatever the view uses for one bubble.
    /// </summary>
    public interface IReplyBubbleView<TBubble> where TBubble : class
    {
        /// <summary>Shows a new bubble with text and returns it.</summary>
        TBubble Add(string text);

        /// <summary>Replaces the whole text of a bubble Add returned.</summary>
        void Set(TBubble bubble, string text);

        /// <summary>Whether a bubble Add returned is still shown (not trimmed away).</summary>
        bool IsShown(TBubble bubble);
    }

    /// <summary>
    /// Writes replies into bubbles, one bubble per reply. The bubble's displayed text is rewritten (Markdown turned
    /// into tags), so each bubble is always given the reply's raw text as received, never its displayed text.
    /// A new bubble is started for another reply or when the current bubble is no longer shown.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class ReplyBubbleWriter<TBubble> where TBubble : class
    {
        private readonly IReplyBubbleView<TBubble> _view;
        private readonly ReplyText _replyText = new ReplyText();
        private TBubble _bubble;

        public ReplyBubbleWriter(IReplyBubbleView<TBubble> view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <summary>
        /// Appends text to the reply identified by connection and turn. Null or empty text changes nothing.
        /// </summary>
        public void Append(long connection, long turn, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            bool bubbleAlive = _bubble != null && _view.IsShown(_bubble);
            if (_replyText.Append(new ReplyId(connection, turn), text, bubbleAlive))
            {
                _bubble = _view.Add(_replyText.Text);
            }
            else
            {
                _view.Set(_bubble, _replyText.Text);
            }
        }
    }
}
