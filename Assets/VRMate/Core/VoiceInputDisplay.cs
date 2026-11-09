using System;

namespace VRMate
{
    /// <summary>
    /// Where voice input notices come from. BrainLink raises these on the main thread.
    /// </summary>
    public interface IVoiceInputSource
    {
        event Action<string> Heard;
        event Action<ListenState> ListenChanged;
        event Action Disconnected;
    }

    /// <summary>
    /// The chat panel parts that show voice input.
    /// </summary>
    public interface IVoiceInputView
    {
        /// <summary>Shows text as the player's bubble, the same as a typed message.</summary>
        void AddPlayerBubble(string text);

        /// <summary>Replaces the input field's placeholder.</summary>
        void SetPlaceholder(string text);
    }

    /// <summary>
    /// Shows voice input from one source: each heard sentence becomes one player bubble as is, and the input field's
    /// placeholder follows the connection and the last listen state. Attach and Tick are meant to be called every frame;
    /// the source is subscribed once, and the placeholder is written only when its text changes.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class VoiceInputDisplay
    {
        private readonly IVoiceInputView _view;
        private readonly InputPlaceholder _placeholder;

        // Kept so that the exact delegates subscribed can be removed again.
        private readonly Action<string> _onHeard;
        private readonly Action<ListenState> _onListen;
        private readonly Action _onDisconnected;

        private IVoiceInputSource _source;
        private bool _hasWritten;
        private string _writtenText;

        /// <param name="connectedText">The placeholder while connected and idle; null shows as empty.</param>
        public VoiceInputDisplay(string connectedText, IVoiceInputView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _placeholder = new InputPlaceholder(connectedText);
            _onHeard = OnHeard;
            _onListen = _placeholder.OnListen;
            _onDisconnected = _placeholder.OnDisconnected;
        }

        /// <summary>
        /// Subscribes to source. Null keeps the current subscription and the same source changes nothing.
        /// Another source replaces the current one and returns listen to idle, since the state came from the old one.
        /// </summary>
        public void Attach(IVoiceInputSource source)
        {
            if (source == null || source == _source)
            {
                return;
            }

            Detach();
            _placeholder.OnDisconnected();
            source.Heard += _onHeard;
            source.ListenChanged += _onListen;
            source.Disconnected += _onDisconnected;
            _source = source;
        }

        /// <summary>Unsubscribes from the current source, if any.</summary>
        public void Detach()
        {
            if (_source == null)
            {
                return;
            }

            _source.Heard -= _onHeard;
            _source.ListenChanged -= _onListen;
            _source.Disconnected -= _onDisconnected;
            _source = null;
        }

        /// <summary>
        /// Writes the placeholder for connected if its text differs from the last one written; the first call always writes.
        /// The text, not the state, is compared: different states can give the same text.
        /// </summary>
        public void Tick(bool connected)
        {
            var text = _placeholder.Text(connected);
            if (_hasWritten && text == _writtenText)
            {
                return;
            }

            // Recorded before writing, as ChatBot did, so a view that throws is not retried every frame.
            _hasWritten = true;
            _writtenText = text;
            _view.SetPlaceholder(text);
        }

        private void OnHeard(string text)
        {
            _view.AddPlayerBubble(text);
        }
    }
}
