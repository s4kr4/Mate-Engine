using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using LLMUnity;
using UnityEngine.UI;
using System.Collections;
using System.Linq;
using VRMate;

namespace LLMUnitySamples
{
    public class ChatBot : MonoBehaviour
    {
        [Header("Containers")]
        public Transform chatContainer;         
        public Transform inputContainer;        

        [Header("Colors & Font")]
        public Color playerColor = new Color32(81, 164, 81, 255);
        public Color aiColor = new Color32(29, 29, 73, 255);
        public Color fontColor = Color.white;
        public Font font;
        public int fontSize = 16;

        [Header("Bubble Layout")]
        public int bubbleWidth = 600;
        public float textPadding = 10f;
        public float bubbleSpacing = 10f;
        public float bottomPadding = 10f;       
        public Sprite sprite;
        public Sprite roundedSprite16;
        public Sprite roundedSprite32;
        public Sprite roundedSprite64;

        [Header("LLM")]
        public LLMCharacter llmCharacter;

        [Header("Input Settings")]
        public string inputPlaceholder = "Message me";

        [Header("Streaming Audio")]
        public AudioSource streamAudioSource;

        [Header("Bubble Materials")]
        public Material playerMaterial;         
        public Material aiMaterial;             
        [Header("Text Materials")]
        public Material playerTextMaterial;      
        public Material aiTextMaterial;        
        [Header("Scroll")]
        public ScrollRect scrollRect;           
        public bool autoScrollOnNewMessage = true;     
        public bool respectUserScroll = true;            

        [Header("History")]
        [Min(0)] public int maxMessages = 100;           
        public bool trimOnlyWhenAtBottom = true;       
        public bool enableOffscreenTrim = false;        

        [Header("Font Colors (per side)")]
        public Color playerFontColor = Color.white;
        public Color aiFontColor = Color.white;

        [Header("Rounded Sprite Radius")]
        [Range(0, 64)]
        public int cornerRadius = 16; 
        private bool layoutDirty;

        private InputBubble inputBubble;
        private List<Bubble> chatBubbles = new List<Bubble>();
        private bool blockInput = true;
        private BubbleUI playerUI, aiUI;
        private bool warmUpDone = false;
        private int lastBubbleOutsideFOV = -1;

        private Animator avatarAnimator;
        private Animator lastAvatarAnimator;

        // VRMate: replies come from the brain through SpeechPlayer instead of LLMUnity.
        private const string notConnectedPlaceholder = "頭脳につながっていません";
        private SpeechPlayer speechPlayer;
        // VRMate: the Markdown converter rewrites the bubble's text, so replies are written from the raw text kept in Core.
        private ReplyBubbleWriter<Bubble> aiReplyWriter;
        private bool? placeholderConnected;


        void Start()
        {
            avatarAnimator = GetComponent<Animator>();

            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (cornerRadius <= 16) sprite = roundedSprite16;
            else if (cornerRadius <= 32) sprite = roundedSprite32;
            else sprite = roundedSprite64;

            playerUI = new BubbleUI
            {
                sprite = sprite,
                font = font,
                fontSize = fontSize,
                fontColor = playerFontColor,
                bubbleColor = playerColor,
                bottomPosition = 0,
                leftPosition = 0,
                textPadding = textPadding,
                bubbleOffset = bubbleSpacing,
                bubbleWidth = bubbleWidth,
                bubbleHeight = -1
            };

            aiUI = new BubbleUI
            {
                sprite = sprite,
                font = font,
                fontSize = fontSize,
                fontColor = aiFontColor,
                bubbleColor = aiColor,
                bottomPosition = 0,
                leftPosition = 1,
                textPadding = textPadding,
                bubbleOffset = bubbleSpacing,
                bubbleWidth = bubbleWidth,
                bubbleHeight = -1
            };

            Transform inputParent = inputContainer != null ? inputContainer : chatContainer;

            inputBubble = new InputBubble(inputParent, playerUI, "InputBubble", "Loading...", 4);
            inputBubble.AddSubmitListener(onInputFieldSubmit);
            inputBubble.AddValueChangedListener(onValueChanged);
            inputBubble.setInteractable(false);

            FindAvatarSmart();

            // VRMate: no LLMUnity warm-up or history; input is open as soon as the panel exists.
            aiReplyWriter = new ReplyBubbleWriter<Bubble>(new AiBubbleView(this));
            SubscribeSpeechPlayer();
            warmUpDone = true;
            UpdateConnectionPlaceholder();
            AllowInput();
        }

        void SubscribeSpeechPlayer()
        {
            if (speechPlayer != null || SpeechPlayer.Instance == null) return;
            speechPlayer = SpeechPlayer.Instance;
            speechPlayer.TextAppended += OnSpeechTextAppended;
        }

        void OnDestroy()
        {
            if (speechPlayer != null) speechPlayer.TextAppended -= OnSpeechTextAppended;
            speechPlayer = null;
        }

        void OnSpeechTextAppended(long connection, long turn, string text)
        {
            if (inputBubble == null) return;

            // One AI bubble per reply; the writer starts a new one when the bubble was trimmed.
            // VRMate: a restarted brain counts turns from the start again, so a reply is the connection and turn together.
            aiReplyWriter.Append(connection, turn, text);
        }

        // VRMate: lets the Core writer add, rewrite and check AI bubbles.
        private sealed class AiBubbleView : IReplyBubbleView<Bubble>
        {
            private readonly ChatBot owner;

            public AiBubbleView(ChatBot owner) { this.owner = owner; }

            public Bubble Add(string text) { return owner.AddBubble(text, false); }

            public void Set(Bubble bubble, string text) { bubble.SetText(text); owner.layoutDirty = true; }

            public bool IsShown(Bubble bubble) { return owner.chatBubbles.Contains(bubble); }
        }

        void UpdateConnectionPlaceholder()
        {
            bool connected = BrainLink.Instance != null && BrainLink.Instance.IsConnected;
            if (placeholderConnected == connected) return;
            placeholderConnected = connected;
            inputBubble.SetPlaceHolderText(connected ? inputPlaceholder : notConnectedPlaceholder);
        }

        void FindAvatarSmart()
        {
            Animator found = null;
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var current = loader.GetCurrentModel();
                if (current != null) found = current.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
            {
                var modelParent = GameObject.Find("Model");
                if (modelParent != null) found = modelParent.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
            }
            if (found == null)
            {
                var all = GameObject.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                found = all.FirstOrDefault(a => a && a.isActiveAndEnabled);
            }
            if (found != avatarAnimator)
            {
                avatarAnimator = found;
                lastAvatarAnimator = avatarAnimator;
            }
        }

        void RefreshAvatarIfChanged()
        {
            if (avatarAnimator == null || lastAvatarAnimator == null || avatarAnimator != lastAvatarAnimator)
            {
                FindAvatarSmart();
            }
        }


        private void MarkLayoutDirty()
        {
            layoutDirty = true;
        }

        void OnDisable()
        {
            if (streamAudioSource != null && streamAudioSource.isPlaying)
            {
                streamAudioSource.Stop();
                streamAudioSource.volume = 1f;
            }
        }

        Bubble AddBubble(string message, bool isPlayerMessage)
        {
            Bubble bubble = new Bubble(chatContainer, isPlayerMessage ? playerUI : aiUI, isPlayerMessage ? "PlayerBubble" : "AIBubble", message);
            chatBubbles.Add(bubble);
            bubble.OnResize(MarkLayoutDirty);

            var image = bubble.GetRectTransform().GetComponentInChildren<Image>(true);
            if (image != null)
            {
                image.material = isPlayerMessage ? playerMaterial : aiMaterial;
            }
            var text = bubble.GetRectTransform().GetComponentInChildren<Text>(true);
            if (text != null)
            {
                Material m = isPlayerMessage ? playerTextMaterial : aiTextMaterial;
                if (m != null)
                {
                    text.material = m;
                }
            }

            // VRMate: replies may arrive while the panel is closed, when coroutines cannot start.
            if (isActiveAndEnabled && autoScrollOnNewMessage && (!respectUserScroll || IsAtBottom()))
            {
                StartCoroutine(ScrollToBottomNextFrame());
            }

            TrimHistoryIfNeeded();

            return bubble;
        }

        void TrimHistoryIfNeeded()
        {
            if (maxMessages <= 0) return;

            if (chatBubbles.Count > maxMessages)
            {
                if (!trimOnlyWhenAtBottom || IsAtBottom())
                {
                    int removeCount = chatBubbles.Count - maxMessages;
                    for (int i = 0; i < removeCount; i++)
                    {
                        chatBubbles[i].Destroy();
                    }
                    chatBubbles.RemoveRange(0, removeCount);
                    UpdateBubblePositions();
                }
            }
        }

        bool IsAtBottom(float tolerance = 0.01f)
        {
            if (scrollRect == null) return true; 
            return scrollRect.verticalNormalizedPosition <= tolerance;
        }

        void ShowLoadedMessages()
        {
            int start = 1;
            int total = llmCharacter.chat.Count;
            if (maxMessages > 0)
                start = Mathf.Max(1, total - maxMessages);

            for (int i = start; i < total; i++)
            {
                AddBubble(llmCharacter.chat[i].content, i % 2 == 1);
            }
            StartCoroutine(ScrollToBottomNextFrame());
        }

        void onInputFieldSubmit(string newText)
        {
            inputBubble.ActivateInputField();
            if (blockInput || newText.Trim() == "" || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                StartCoroutine(BlockInteraction());
                return;
            }
            string message = inputBubble.GetText().Replace("\v", "\n");

            // VRMate: the brain handles interruptions, so input stays open while it is replying.
            var result = BrainLink.Instance != null ? BrainLink.Instance.SendInput(message) : InputSendResult.NotConnected;
            if (result != InputSendResult.Sent)
            {
                if (result == InputSendResult.TooLong) Debug.LogWarning("[VRMate] input is too long to send");
                UpdateConnectionPlaceholder();
                StartCoroutine(BlockInteraction());
                return;
            }

            AddBubble(message, true);
            inputBubble.SetText("");
        }

        private IEnumerator FadeOutStreamAudio(float duration = 0.5f)
        {
            float startVolume = streamAudioSource.volume;

            while (streamAudioSource.volume > 0f)
            {
                streamAudioSource.volume -= startVolume * Time.deltaTime / duration;
                yield return null;
            }

            streamAudioSource.Stop();
            streamAudioSource.volume = startVolume; 
        }

        public void WarmUpCallback()
        {
            warmUpDone = true;
            inputBubble.SetPlaceHolderText(inputPlaceholder);
            AllowInput();
        }

        public void AllowInput()
        {
            blockInput = false;
            inputBubble.ReActivateInputField();
        }

        public void CancelRequests()
        {
            AllowInput();
        }

        IEnumerator<string> BlockInteraction()
        {
            inputBubble.setInteractable(false);
            yield return null;
            inputBubble.setInteractable(true);
            inputBubble.MoveTextEnd();
        }

        void onValueChanged(string newText)
        {
            if (Input.GetKey(KeyCode.Return))
            {
                if (inputBubble.GetText().Trim() == "")
                    inputBubble.SetText("");
            }
        }

        public void UpdateBubblePositions()
        {
            float y = bottomPadding;
            for (int i = chatBubbles.Count - 1; i >= 0; i--)
            {
                Bubble bubble = chatBubbles[i];
                RectTransform childRect = bubble.GetRectTransform();
                childRect.anchoredPosition = new Vector2(childRect.anchoredPosition.x, y);

                if (enableOffscreenTrim)
                {
                    float containerHeight = chatContainer.GetComponent<RectTransform>().rect.height;
                    if (y > containerHeight && lastBubbleOutsideFOV == -1)
                    {
                        lastBubbleOutsideFOV = i;
                    }
                }

                y += bubble.GetSize().y + bubbleSpacing;
            }
            var contentRect = chatContainer.GetComponent<RectTransform>();
            contentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, y + bottomPadding);
        }

        void Update()
        {
            RefreshAvatarIfChanged();
            SubscribeSpeechPlayer();
            UpdateConnectionPlaceholder();

            if (!inputBubble.inputFocused() && warmUpDone)
            {
                inputBubble.ActivateInputField();
                StartCoroutine(BlockInteraction());
            }

            if (enableOffscreenTrim && lastBubbleOutsideFOV != -1)
            {
                for (int i = 0; i <= lastBubbleOutsideFOV; i++)
                {
                    chatBubbles[i].Destroy();
                }
                chatBubbles.RemoveRange(0, lastBubbleOutsideFOV + 1);
                lastBubbleOutsideFOV = -1;
                UpdateBubblePositions();
            }
        }

        public void ExitGame()
        {
            Debug.Log("Exit button clicked");
            Application.Quit();
        }

        IEnumerator ScrollToBottomNextFrame()
        {
            yield return null;
            Canvas.ForceUpdateCanvases();
            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 0f; 
        }

        bool onValidateWarning = true;
        void OnValidate()
        {
            if (cornerRadius <= 16) sprite = roundedSprite16;
            else if (cornerRadius <= 32) sprite = roundedSprite32;
            else sprite = roundedSprite64;

            if (onValidateWarning && llmCharacter != null && !llmCharacter.remote && llmCharacter.llm != null && llmCharacter.llm.model == "")
            {
                Debug.LogWarning($"Please select a model in the {llmCharacter.llm.gameObject.name} GameObject!");
                onValidateWarning = false;
            }
        }

        void LateUpdate()
        {
            if (!layoutDirty) return;
            layoutDirty = false;

            UpdateBubblePositions();
            if (autoScrollOnNewMessage && (!respectUserScroll || IsAtBottom()))
            {
                if (scrollRect != null) scrollRect.verticalNormalizedPosition = 0f;
            }
        }

    }
}