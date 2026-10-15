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
        public SayMessage(long id, long turn, string text, bool hasAudio, string audioBase64, IReadOnlyList<VisemeKey> visemes)
        {
            Id = id;
            Turn = turn;
            Text = text;
            HasAudio = hasAudio;
            AudioBase64 = audioBase64;
            Visemes = visemes;
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
    }

    public sealed class CancelMessage : BrainMessage
    {
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
        public InvalidMessage(long? id, string reason)
        {
            Id = id;
            Reason = reason;
        }

        /// <summary>The say id when it could be read, so that the brain can still be answered.</summary>
        public long? Id { get; }

        public string Reason { get; }
    }
}
