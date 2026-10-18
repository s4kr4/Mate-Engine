using System;
using UniVRM10;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Shows the brain's expression on a VRM 1.0 avatar by overwriting the five emotion presets that
    /// upstream wrote this frame. The blending lives in ExpressionBlend; this component only reads and
    /// writes the weights. Blink and mouth are left to the model's override settings; the model's
    /// overrideMouth is passed to the blend so that it can lower mouth-blocking emotions while the voice
    /// plays. VRM 0.x avatars keep upstream's face.
    /// </summary>
    // UniversalBlendshapes (order 0) writes happy, angry, sad and relaxed in its LateUpdate, and
    // Vrm10Instance (order 11000) applies the weights in its LateUpdate. Running in between (after
    // SpeechPlayer at 100) makes the weights written here the ones the model shows.
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public class ExpressionPlayer : MonoBehaviour
    {
        // Same order as ExpressionBlend.Emotions.
        private static readonly ExpressionKey[] Keys =
        {
            ExpressionKey.Happy,
            ExpressionKey.Angry,
            ExpressionKey.Sad,
            ExpressionKey.Relaxed,
            ExpressionKey.Surprised,
        };

        private readonly ExpressionBlend blend = new ExpressionBlend();
        private readonly float[] upstream = new float[ExpressionBlend.Count];
        private readonly float[] output = new float[ExpressionBlend.Count];
        private readonly AvatarWatch avatar = new AvatarWatch();
        private Vrm10Instance avatarVrm;
        private bool wasActive;
        private bool mouthOverridesWarned;

        public static ExpressionPlayer Instance { get; private set; }

        /// <summary>One of the five emotions, or "neutral" to give the face back to upstream.</summary>
        public void Set(string name)
        {
            // A say sets its expression right after its audio starts, before this frame's LateUpdate;
            // without this the first say of a turn would place the weights as if nothing were speaking.
            UpdateSpeaking();
            blend.Set(name);
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

        private void LateUpdate()
        {
            if (avatar.Refresh())
            {
                var found = avatar.Animator;
                avatarVrm = found != null ? found.GetComponentInChildren<Vrm10Instance>() : null;
                blend.SetMouthOverrides(ReadMouthOverrides(avatarVrm));
            }

            UpdateSpeaking();
            blend.Advance(Time.deltaTime);
            var active = blend.IsActive;

            // One more write on the frame the blend ends puts surprised back to 0: upstream never writes it.
            if (active || wasActive)
            {
                WriteWeights();
            }

            wasActive = active;
        }

        /// <summary>Emotions that hold back the mouth are lowered while the voice plays, or the lip sync stops.</summary>
        private void UpdateSpeaking()
        {
            var speech = SpeechPlayer.Instance;
            blend.SetSpeaking(speech != null && speech.IsTalking);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// The model's overrideMouth for the five emotions, in ExpressionBlend.Emotions order. An emotion
        /// the model lacks is None; null (all None) when there is no VRM 1.0 model or it cannot be read.
        /// </summary>
        private MouthOverride[] ReadMouthOverrides(Vrm10Instance vrm)
        {
            if (vrm == null)
            {
                return null;
            }

            try
            {
                if (vrm.Vrm == null || vrm.Vrm.Expression == null)
                {
                    WarnMouthOverrides("the model has no expression settings");
                    return null;
                }

                var expressions = vrm.Vrm.Expression;
                return new[]
                {
                    ToMouthOverride(expressions.Happy),
                    ToMouthOverride(expressions.Angry),
                    ToMouthOverride(expressions.Sad),
                    ToMouthOverride(expressions.Relaxed),
                    ToMouthOverride(expressions.Surprised),
                };
            }
            catch (Exception e)
            {
                WarnMouthOverrides(e.Message);
                return null;
            }
        }

        private void WarnMouthOverrides(string reason)
        {
            if (!mouthOverridesWarned)
            {
                // Without them nothing is lowered while speaking, so the mouth may stay shut under some emotions.
                mouthOverridesWarned = true;
                Debug.LogWarning("[VRMate] cannot read the avatar's mouth overrides: " + reason);
            }
        }

        private static MouthOverride ToMouthOverride(VRM10Expression expression)
        {
            if (expression == null)
            {
                return MouthOverride.None;
            }

            switch (expression.OverrideMouth)
            {
                case UniGLTF.Extensions.VRMC_vrm.ExpressionOverrideType.blend:
                    return MouthOverride.Blend;
                case UniGLTF.Extensions.VRMC_vrm.ExpressionOverrideType.block:
                    return MouthOverride.Block;
                default:
                    return MouthOverride.None;
            }
        }

        private void WriteWeights()
        {
            if (avatarVrm == null)
            {
                return;
            }

            try
            {
                var expression = avatarVrm.Runtime.Expression;
                for (var i = 0; i < Keys.Length; i++)
                {
                    upstream[i] = expression.GetWeight(Keys[i]);
                }

                blend.Evaluate(upstream, output);
                for (var i = 0; i < Keys.Length; i++)
                {
                    expression.SetWeight(Keys[i], output[i]);
                }
            }
            catch (Exception e)
            {
                // Giving up on this avatar keeps a broken model from logging every frame.
                Debug.LogWarning("[VRMate] cannot write expression weights: " + e.Message);
                avatarVrm = null;
            }
        }
    }
}
