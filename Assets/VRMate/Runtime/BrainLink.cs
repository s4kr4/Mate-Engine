using System;
using UnityEngine;

namespace VRMate
{
    public enum InputSendResult
    {
        Sent,
        NotConnected,
        TooLong,
    }

    /// <summary>
    /// Owns the connection to the brain. Socket work happens on background threads inside LinkClient;
    /// this component drains its events on the main thread and routes them to SpeechPlayer
    /// (expressions and motions included, so that cancel and disconnect undo them in order).
    /// </summary>
    [DisallowMultipleComponent]
    public class BrainLink : MonoBehaviour, IVoiceInputSource
    {
        public string url = "ws://127.0.0.1:48021";

        // Bounds the main-thread work per frame; each say may carry a megabyte of base64 audio.
        private const int MaxEventsPerFrame = 16;
        private const int MaxLoggedChars = 200;

        private readonly WarnOnce cueWarnings = new WarnOnce();
        private LinkClient client;

        public static BrainLink Instance { get; private set; }

        /// <summary>
        /// Raised on the main thread with a sentence the brain heard, in the order the brain sent it, so it
        /// comes before the says of its reply (which SpeechPlayer shows only when they start).
        /// </summary>
        public event Action<string> Heard;

        /// <summary>Raised on the main thread when the brain sends a listen state.</summary>
        public event Action<ListenState> ListenChanged;

        /// <summary>Raised on the main thread after the connection to the brain is lost.</summary>
        public event Action Disconnected;

        public bool IsConnected
        {
            get { return client != null && client.IsConnected; }
        }

        public bool TrySend(string json)
        {
            return client != null && client.TrySend(json);
        }

        public InputSendResult SendInput(string text)
        {
            if (!IsConnected)
            {
                return InputSendResult.NotConnected;
            }

            string json;
            if (!BrainProtocol.TryBuildInput(text, out json))
            {
                return InputSendResult.TooLong;
            }

            return TrySend(json) ? InputSendResult.Sent : InputSendResult.NotConnected;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
        }

        private void Start()
        {
            if (Instance != this)
            {
                return;
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "ws" || !uri.IsLoopback)
            {
                // The brain only listens on this machine; never send anything elsewhere.
                Debug.LogError("[VRMate] brain url must be ws:// on the loopback address: " + url);
                return;
            }

            client = new LinkClient(uri, () => new ClientWebSocketAdapter(), LinkOptions.Default);
            client.Start();
        }

        private void Update()
        {
            if (client == null)
            {
                return;
            }

            LinkEvent linkEvent;
            for (var i = 0; i < MaxEventsPerFrame && client.TryDequeue(out linkEvent); i++)
            {
                try
                {
                    Dispatch(linkEvent);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private void Dispatch(LinkEvent linkEvent)
        {
            switch (linkEvent.Kind)
            {
                case LinkEventKind.Connected:
                    Debug.Log("[VRMate] connected to the brain");
                    break;
                case LinkEventKind.Disconnected:
                    Debug.Log("[VRMate] disconnected from the brain");

                    // Raise never throws, so the speech player below is always reset.
                    Raise(Disconnected);
                    if (SpeechPlayer.Instance != null)
                    {
                        SpeechPlayer.Instance.OnDisconnected();
                    }

                    break;
                case LinkEventKind.Message:
                    HandleMessage(linkEvent.Text);
                    break;
            }
        }

        private void HandleMessage(string text)
        {
            var message = BrainProtocol.Parse(text);

            var say = message as SayMessage;
            if (say != null)
            {
                foreach (var problem in say.Problems)
                {
                    LogCueWarning("[VRMate] dropped a cue from say " + say.Id + " (" + problem + ")", problem);
                }

                if (SpeechPlayer.Instance != null)
                {
                    SpeechPlayer.Instance.OnSay(say);
                }
                else
                {
                    TrySend(BrainProtocol.Played(say.Id, "avatar: no speech player"));
                }

                return;
            }

            if (message is CancelMessage)
            {
                if (SpeechPlayer.Instance != null)
                {
                    SpeechPlayer.Instance.OnCancel();
                }

                return;
            }

            var expression = message as ExpressionMessage;
            if (expression != null)
            {
                if (SpeechPlayer.Instance != null)
                {
                    SpeechPlayer.Instance.OnExpression(expression.Name);
                }

                return;
            }

            var motion = message as MotionMessage;
            if (motion != null)
            {
                if (SpeechPlayer.Instance != null)
                {
                    SpeechPlayer.Instance.OnMotion(motion.Name);
                }

                return;
            }

            var listen = message as ListenMessage;
            if (listen != null)
            {
                Raise(ListenChanged, listen.State);
                return;
            }

            var heard = message as HeardMessage;
            if (heard != null)
            {
                Raise(Heard, heard.Text);
                return;
            }

            var invalid = message as InvalidMessage;
            if (invalid != null && invalid.IsCue)
            {
                // The brain may repeat a bad name in every reply; the reason names it, so warn once per reason.
                LogCueWarning("[VRMate] invalid message (" + invalid.Reason + ")", invalid.Reason);
                return;
            }

            if (invalid != null)
            {
                Debug.LogWarning("[VRMate] invalid message (" + invalid.Reason + "): " + Shorten(text));
                if (invalid.Id.HasValue)
                {
                    // The brain waits for a reply to every say id; answer so it does not stall.
                    TrySend(BrainProtocol.Played(invalid.Id.Value, invalid.Reason));
                }

                return;
            }

            var ignored = message as IgnoredMessage;
            Debug.LogWarning("[VRMate] ignored message (" + (ignored != null ? ignored.Reason : "unknown") + "): " + Shorten(text));
        }

        // A multicast delegate stops at the first handler that throws; call each one on its own so that
        // a failing subscriber neither hides the event from the others nor stops the rest of the frame.
        private static void Raise(Action handler)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var each in handler.GetInvocationList())
            {
                try
                {
                    ((Action)each)();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        private static void Raise<T>(Action<T> handler, T value)
        {
            if (handler == null)
            {
                return;
            }

            foreach (var each in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T>)each)(value);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>Logs only the first time key is seen.</summary>
        private void LogCueWarning(string message, string key)
        {
            if (cueWarnings.ShouldWarn(key))
            {
                Debug.LogWarning(message);
            }
        }

        private static string Shorten(string text)
        {
            if (text == null)
            {
                return "null";
            }

            return text.Length <= MaxLoggedChars ? text : text.Substring(0, MaxLoggedChars) + "... (" + text.Length + " chars)";
        }

        private void OnApplicationQuit()
        {
            DisposeClient();
        }

        private void OnDestroy()
        {
            DisposeClient();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void DisposeClient()
        {
            if (client != null)
            {
                client.Dispose();
                client = null;
            }
        }
    }
}
