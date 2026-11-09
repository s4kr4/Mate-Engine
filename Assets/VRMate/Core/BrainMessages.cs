using System.Collections.Generic;

namespace VRMate
{
    public enum VisemeShape
    {
        Sil,
        Aa,
        Ih,
        Ou,
        Ee,
        Oh,
    }

    public struct VisemeKey
    {
        public VisemeKey(double time, VisemeShape shape)
        {
            Time = time;
            Shape = shape;
        }

        /// <summary>Seconds from the start of the WAV.</summary>
        public double Time { get; }

        public VisemeShape Shape { get; }
    }

    public abstract class BrainMessage
    {
    }

    public sealed class SayMessage : BrainMessage
    {
        private static readonly IReadOnlyList<string> NoProblems = new string[0];

        public SayMessage(
            long id,
            long turn,
            string text,
            bool hasAudio,
            string audioBase64,
            IReadOnlyList<VisemeKey> visemes,
            string expression = null,
            string motion = null,
            IReadOnlyList<string> problems = null)
        {
            Id = id;
            Turn = turn;
            Text = text;
            HasAudio = hasAudio;
            AudioBase64 = audioBase64;
            Visemes = visemes;
            Expression = expression;
            Motion = motion;
            Problems = problems == null || problems.Count == 0 ? NoProblems : new List<string>(problems).AsReadOnly();
        }

        public long Id { get; }

        public long Turn { get; }

        public string Text { get; }

        /// <summary>True when the message carried a non-null "audio" field, even if it was not a string.</summary>
        public bool HasAudio { get; }

        /// <summary>The "audio" string, or null when absent or not a string.</summary>
        public string AudioBase64 { get; }

        /// <summary>Validated keys, or null when absent or unusable.</summary>
        public IReadOnlyList<VisemeKey> Visemes { get; }

        /// <summary>A name accepted by CueNames.IsExpression, or null when absent or unusable.</summary>
        public string Expression { get; }

        /// <summary>A name accepted by CueNames.IsMotion, or null when absent or unusable.</summary>
        public string Motion { get; }

        /// <summary>
        /// Why an unusable expression or motion was dropped; the say itself still plays. Never null.
        /// Each reason is bounded in length, so it can be used as a warn-once key.
        /// </summary>
        public IReadOnlyList<string> Problems { get; }
    }

    public sealed class CancelMessage : BrainMessage
    {
    }

    public sealed class ExpressionMessage : BrainMessage
    {
        public ExpressionMessage(string name)
        {
            Name = name;
        }

        /// <summary>Accepted by CueNames.IsExpression; "neutral" gives the face back to upstream.</summary>
        public string Name { get; }
    }

    public sealed class MotionMessage : BrainMessage
    {
        public MotionMessage(string name)
        {
            Name = name;
        }

        /// <summary>Accepted by CueNames.IsMotion.</summary>
        public string Name { get; }
    }

    /// <summary>Where the brain is in a push-to-talk turn.</summary>
    public enum ListenState
    {
        Idle,
        Recording,
        Recognizing,
    }

    public sealed class ListenMessage : BrainMessage
    {
        public ListenMessage(ListenState state)
        {
            State = state;
        }

        public ListenState State { get; }
    }

    public sealed class HeardMessage : BrainMessage
    {
        public HeardMessage(string text)
        {
            Text = text;
        }

        /// <summary>
        /// A sentence the brain recognized and queued, as the decoded JSON string (possibly empty): an unpaired
        /// surrogate escaped as \uXXXX is read as U+FFFD, a surrogate pair is kept. It arrives before any say
        /// of the reply to it.
        /// </summary>
        public string Text { get; }
    }

    public sealed class IgnoredMessage : BrainMessage
    {
        public IgnoredMessage(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; }
    }

    public sealed class InvalidMessage : BrainMessage
    {
        public InvalidMessage(long? id, string reason, bool isCue = false)
        {
            Id = id;
            Reason = reason;
            IsCue = isCue;
        }

        /// <summary>The say id when it could be read, so that the brain can still be answered.</summary>
        public long? Id { get; }

        public string Reason { get; }

        /// <summary>
        /// True for an expression or motion message. Its Reason is then bounded in length and names
        /// the value, so it can be used as a warn-once key.
        /// </summary>
        public bool IsCue { get; }
    }
}
