using System;
using System.Collections.Generic;

namespace VRMate
{
    public abstract class SpeechAction
    {
    }

    public sealed class PlayAudio : SpeechAction
    {
        public PlayAudio(long id, PcmAudio audio, VisemeTimeline timeline)
        {
            Id = id;
            Audio = audio;
            Timeline = timeline;
        }

        public long Id { get; }

        public PcmAudio Audio { get; }

        /// <summary>Null when the say had no usable visemes.</summary>
        public VisemeTimeline Timeline { get; }
    }

    public sealed class StopAudio : SpeechAction
    {
    }

    public sealed class AppendText : SpeechAction
    {
        public AppendText(long connection, long turn, string text)
        {
            Connection = connection;
            Turn = turn;
            Text = text;
        }

        /// <summary>Changes on every disconnect; a turn only identifies a reply within one connection.</summary>
        public long Connection { get; }

        public long Turn { get; }

        public string Text { get; }
    }

    public sealed class SendMessage : SpeechAction
    {
        public SendMessage(string json)
        {
            Json = json;
        }

        public string Json { get; }
    }

    /// <summary>
    /// Orders says: at most one plays at a time, the rest wait in arrival order up to maxPending.
    /// Every say that is not cancelled gets exactly one "played"; "started" comes after its text is
    /// shown and only for says that actually start. Cancel and disconnect drop everything silently.
    /// Disconnect also moves to a new connection number: a restarted brain counts turns again from
    /// the start, so a reply is identified by connection and turn together, never by turn alone.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class SpeechSequencer
    {
        public const int DefaultMaxPending = 4;

        private readonly int _maxPending;
        private readonly Queue<SayMessage> _pending = new Queue<SayMessage>();
        private readonly List<SpeechAction> _actions = new List<SpeechAction>();
        private long? _playingId;
        private long _connection;

        public SpeechSequencer(int maxPending = DefaultMaxPending)
        {
            _maxPending = Math.Max(0, maxPending);
        }

        public bool IsPlaying
        {
            get { return _playingId.HasValue; }
        }

        public void OnSay(SayMessage say)
        {
            if (say == null)
            {
                throw new ArgumentNullException(nameof(say));
            }

            if (_playingId.HasValue)
            {
                if (_pending.Count >= _maxPending)
                {
                    _actions.Add(new SendMessage(BrainProtocol.Played(say.Id, "avatar: too many says waiting")));
                    return;
                }

                _pending.Enqueue(say);
                return;
            }

            Begin(say);
            RunPending();
        }

        /// <summary>Ignored unless id is the say that is playing now.</summary>
        public void OnPlaybackFinished(long id)
        {
            if (_playingId != id)
            {
                return;
            }

            _playingId = null;
            _actions.Add(new SendMessage(BrainProtocol.Played(id)));
            RunPending();
        }

        public void OnCancel()
        {
            StopAll();
        }

        public void OnDisconnected()
        {
            StopAll();
            _connection++;
        }

        public IReadOnlyList<SpeechAction> DrainActions()
        {
            var drained = _actions.ToArray();
            _actions.Clear();
            return drained;
        }

        private void StopAll()
        {
            _pending.Clear();
            if (_playingId.HasValue)
            {
                _playingId = null;
                _actions.Add(new StopAudio());
            }
        }

        private void RunPending()
        {
            while (!_playingId.HasValue && _pending.Count > 0)
            {
                Begin(_pending.Dequeue());
            }
        }

        private void Begin(SayMessage say)
        {
            if (!say.HasAudio)
            {
                _actions.Add(new AppendText(_connection, say.Turn, say.Text));
                _actions.Add(new SendMessage(BrainProtocol.Started(say.Id)));
                _actions.Add(new SendMessage(BrainProtocol.Played(say.Id)));
                return;
            }

            PcmAudio audio = null;
            string error;
            if (say.AudioBase64 == null)
            {
                error = "audio: not a string";
            }
            else
            {
                WavDecoder.TryDecodeBase64(say.AudioBase64, out audio, out error);
            }

            if (audio == null)
            {
                // The text is still shown so the speech bubble does not lose a sentence.
                _actions.Add(new AppendText(_connection, say.Turn, say.Text));
                _actions.Add(new SendMessage(BrainProtocol.Played(say.Id, string.IsNullOrEmpty(error) ? "audio: unplayable" : error)));
                return;
            }

            var timeline = say.Visemes != null ? new VisemeTimeline(say.Visemes) : null;
            _playingId = say.Id;
            _actions.Add(new PlayAudio(say.Id, audio, timeline));
            _actions.Add(new AppendText(_connection, say.Turn, say.Text));
            _actions.Add(new SendMessage(BrainProtocol.Started(say.Id)));
        }
    }
}
