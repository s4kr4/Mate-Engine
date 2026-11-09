namespace VRMate
{
    /// <summary>
    /// Decides the input field's placeholder from the connection and the last listen state the brain sent.
    /// The brain does not send listen idle when it exits, so a disconnect returns to idle; after
    /// reconnecting, the connected text shows until the new brain sends a listen.
    /// </summary>
    public sealed class InputPlaceholder
    {
        public const string NotConnectedText = "頭脳につながっていません";

        // U+2026 (one character), not three dots.
        public const string RecordingText = "聞いています…";
        public const string RecognizingText = "聞き取っています…";

        private readonly string _connectedText;
        private ListenState _state = ListenState.Idle;

        /// <param name="connectedText">Shown as is while connected and idle; null shows as empty.</param>
        public InputPlaceholder(string connectedText)
        {
            _connectedText = connectedText ?? string.Empty;
        }

        public void OnListen(ListenState state)
        {
            _state = state;
        }

        public void OnDisconnected()
        {
            _state = ListenState.Idle;
        }

        /// <summary>Never null.</summary>
        public string Text(bool connected)
        {
            if (!connected)
            {
                return NotConnectedText;
            }

            switch (_state)
            {
                case ListenState.Recording:
                    return RecordingText;
                case ListenState.Recognizing:
                    return RecognizingText;
                default:
                    return _connectedText;
            }
        }
    }
}
