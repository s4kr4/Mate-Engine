using System;
using System.Linq;
using UniVRM10;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Plays the brain's says one at a time on its own AudioSource and moves the avatar's mouth.
    /// The ordering rules live in SpeechSequencer; this component only carries out its actions.
    /// </summary>
    // UniversalBlendshapes (order 0) writes every expression, aa/ih/ou/ee/oh included, in its
    // LateUpdate, and Vrm10Instance (order 11000) applies the weights in its LateUpdate. Running
    // between the two is the only way for the mouth weights written here to reach the model.
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class SpeechPlayer : MonoBehaviour
    {
        private const float AvatarSearchInterval = 0.5f;

        private static readonly int IsTalkingHash = Animator.StringToHash("isTalking");
        private static readonly ExpressionKey KeyAa = ExpressionKey.Aa;
        private static readonly ExpressionKey KeyIh = ExpressionKey.Ih;
        private static readonly ExpressionKey KeyOu = ExpressionKey.Ou;
        private static readonly ExpressionKey KeyEe = ExpressionKey.Ee;
        private static readonly ExpressionKey KeyOh = ExpressionKey.Oh;

        private readonly SpeechSequencer sequencer = new SpeechSequencer();
        private AudioSource audioSource;
        private AudioClip clip;
        private VisemeTimeline timeline;
        private long? playingId;
        private bool closeMouthPending;
        private bool talking;

        private Animator avatarAnimator;
        private Vrm10Instance avatarVrm;
        private bool avatarHasTalkingParameter;
        private float nextAvatarSearchTime;

        public static SpeechPlayer Instance { get; private set; }

        /// <summary>
        /// Raised on the main thread when a say starts (or is shown without audio): connection, turn, text.
        /// Turns restart when the brain reconnects, so one reply is the pair of connection and turn.
        /// </summary>
        public event Action<long, long, string> TextAppended;

        public void OnSay(SayMessage say)
        {
            sequencer.OnSay(say);
            RunActions();
        }

        public void OnCancel()
        {
            sequencer.OnCancel();
            RunActions();
        }

        public void OnDisconnected()
        {
            sequencer.OnDisconnected();
            RunActions();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.spatialBlend = 0f;
        }

        private void Update()
        {
            RefreshAvatar();

            if (playingId.HasValue && !audioSource.isPlaying)
            {
                var finished = playingId.Value;
                EndPlayback();
                sequencer.OnPlaybackFinished(finished);
                RunActions();
                if (!playingId.HasValue)
                {
                    SetTalking(false);
                }
            }
        }

        private void LateUpdate()
        {
            if (playingId.HasValue && timeline != null && clip != null)
            {
                var time = (double)audioSource.timeSamples / clip.frequency;
                if (!WriteMouth(timeline.Evaluate(time)))
                {
                    // Ending the say keeps a broken avatar from stalling the queue; Update reports it.
                    timeline = null;
                    audioSource.Stop();
                }
            }
            else if (closeMouthPending)
            {
                WriteMouth(MouthWeights.Closed);
            }

            // Other frames leave the mouth to UniversalBlendshapes.
            closeMouthPending = false;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            DestroyClip();
        }

        private void RunActions()
        {
            // Actions produced while running (a failed playback finishing at once) are run in order after.
            while (true)
            {
                var actions = sequencer.DrainActions();
                if (actions.Count == 0)
                {
                    return;
                }

                foreach (var action in actions)
                {
                    try
                    {
                        RunAction(action);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }

        private void RunAction(SpeechAction action)
        {
            var play = action as PlayAudio;
            if (play != null)
            {
                StartPlayback(play);
                return;
            }

            if (action is StopAudio)
            {
                audioSource.Stop();
                EndPlayback();
                SetTalking(false);
                return;
            }

            var append = action as AppendText;
            if (append != null)
            {
                var handler = TextAppended;
                if (handler != null)
                {
                    handler(append.Connection, append.Turn, append.Text);
                }

                return;
            }

            var send = action as SendMessage;
            if (send != null && BrainLink.Instance != null)
            {
                BrainLink.Instance.TrySend(send.Json);
            }
        }

        private void StartPlayback(PlayAudio play)
        {
            try
            {
                audioSource.Stop();
                DestroyClip();
                var audio = play.Audio;
                clip = AudioClip.Create("VRMate say " + play.Id, audio.FrameCount, audio.Channels, audio.SampleRate, false);
                clip.SetData(audio.Samples, 0);
                audioSource.clip = clip;
                audioSource.timeSamples = 0;
                audioSource.Play();
            }
            catch (Exception e)
            {
                Debug.LogError("[VRMate] cannot play say " + play.Id + ": " + e.Message);
                audioSource.Stop();
                DestroyClip();
                EndPlayback();
                sequencer.OnPlaybackFinished(play.Id);
                return;
            }

            playingId = play.Id;
            timeline = play.Timeline;
            SetTalking(true);
        }

        private void EndPlayback()
        {
            playingId = null;
            timeline = null;
            closeMouthPending = true;
        }

        private void DestroyClip()
        {
            if (clip == null)
            {
                return;
            }

            if (audioSource != null)
            {
                audioSource.clip = null;
            }

            Destroy(clip);
            clip = null;
        }

        /// <summary>False when the weights could not be written to a VRM 1.0 avatar that is present.</summary>
        private bool WriteMouth(MouthWeights weights)
        {
            if (avatarVrm == null)
            {
                // VRM 0.x avatars (or none at all) keep their mouth; the voice still plays.
                return true;
            }

            try
            {
                var expression = avatarVrm.Runtime.Expression;
                expression.SetWeight(KeyAa, weights.Aa);
                expression.SetWeight(KeyIh, weights.Ih);
                expression.SetWeight(KeyOu, weights.Ou);
                expression.SetWeight(KeyEe, weights.Ee);
                expression.SetWeight(KeyOh, weights.Oh);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRMate] cannot write mouth weights: " + e.Message);
                avatarVrm = null;
                return false;
            }
        }

        private void SetTalking(bool value)
        {
            talking = value;
            if (avatarAnimator == null || !avatarHasTalkingParameter)
            {
                return;
            }

            try
            {
                avatarAnimator.SetBool(IsTalkingHash, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRMate] cannot set isTalking: " + e.Message);
            }
        }

        private void RefreshAvatar()
        {
            if (avatarAnimator != null && avatarAnimator.gameObject.activeInHierarchy)
            {
                return;
            }

            // Searching the scene every frame is expensive; the avatar only changes on a model switch.
            if (Time.unscaledTime < nextAvatarSearchTime)
            {
                return;
            }

            nextAvatarSearchTime = Time.unscaledTime + AvatarSearchInterval;
            var found = FindAvatarAnimator();
            if (found == avatarAnimator)
            {
                return;
            }

            avatarAnimator = found;
            avatarVrm = found != null ? found.GetComponentInChildren<Vrm10Instance>() : null;
            avatarHasTalkingParameter = false;
            try
            {
                avatarHasTalkingParameter = found != null && found.parameters.Any(p => p.nameHash == IsTalkingHash && p.type == AnimatorControllerParameterType.Bool);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRMate] cannot read animator parameters: " + e.Message);
            }

            if (talking)
            {
                SetTalking(true);
            }
        }

        // Same search order as ChatBot.FindAvatarSmart.
        private static Animator FindAvatarAnimator()
        {
            Animator found = null;
            var loader = FindFirstObjectByType<VRMLoader>();
            if (loader != null)
            {
                var current = loader.GetCurrentModel();
                if (current != null)
                {
                    found = current.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
                }
            }

            if (found == null)
            {
                var modelParent = GameObject.Find("Model");
                if (modelParent != null)
                {
                    found = modelParent.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
                }
            }

            if (found == null)
            {
                var all = FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                found = all.FirstOrDefault(a => a && a.isActiveAndEnabled);
            }

            return found;
        }
    }
}
