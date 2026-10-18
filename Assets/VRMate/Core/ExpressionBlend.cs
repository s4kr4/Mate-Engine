using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>
    /// Blends the brain's expression over upstream's emotion weights (happy, angry, sad, relaxed,
    /// surprised, in that order). Two values move at most 1 / FadeSeconds per second each: the amount
    /// the brain's weights take effect (0 = upstream only), and the brain's own five weights (toward the
    /// set emotion at 1 and the others at 0). Evaluate is lerp(upstream, brain, amount), so switching
    /// emotions or returning to upstream never jumps.
    /// While the avatar speaks, emotions that hold back the mouth (MouthOverride) aim lower: Blend at
    /// most SpeakingWeight, Block at 0, so the lip sync stays visible. Speaking lasts SpeakingHoldSeconds
    /// of Advance past SetSpeaking(false), so the face does not pump between sentences. Upstream's own
    /// weights are never lowered. Not thread-safe; call from one thread.
    /// </summary>
    public sealed class ExpressionBlend
    {
        public const int Count = 5;
        public const float FadeSeconds = 0.25f;

        /// <summary>The highest weight a Blend emotion keeps while speaking (the mouth then moves at half).</summary>
        public const float SpeakingWeight = 0.5f;

        /// <summary>Longer than the usual gap between two says, which arrive one after another.</summary>
        public const float SpeakingHoldSeconds = 0.6f;

        /// <summary>The order of the weight arrays given to and filled by Evaluate.</summary>
        public static readonly IReadOnlyList<string> Emotions = Array.AsReadOnly(new[] { "happy", "angry", "sad", "relaxed", "surprised" });

        private const int Surprised = 4;
        private const int None = -1;

        private readonly float[] _weights = new float[Count];
        private readonly MouthOverride[] _overrides = new MouthOverride[Count];
        private int _target = None;
        private float _amount;
        private bool _speakingNow;

        // Advance time since speaking last turned false; infinite until speaking has ever been true.
        private float _sinceSpeaking = float.PositiveInfinity;

        /// <summary>True while the brain's weights have any effect or an emotion is set.</summary>
        public bool IsActive
        {
            get { return _target != None || _amount > 0f; }
        }

        /// <summary>One of the five emotions, or "neutral" to give the face back to upstream. Other names are ignored.</summary>
        public void Set(string name)
        {
            if (name == CueNames.Neutral)
            {
                _target = None;
                return;
            }

            var index = IndexOf(name);
            if (index == None)
            {
                return;
            }

            _target = index;
            if (_amount <= 0f)
            {
                // Nothing of the brain's weights shows yet, so they can start at the target and the fade is a plain blend.
                for (var i = 0; i < Count; i++)
                {
                    _weights[i] = TargetWeight(i);
                }
            }
        }

        /// <summary>
        /// The mouth override of each emotion, in Emotions order. Null or shorter than Count means None
        /// for all; entries past Count are ignored and undefined values read as None. The brain's
        /// weights follow the new setting at the usual rate.
        /// </summary>
        public void SetMouthOverrides(MouthOverride[] overrides)
        {
            var usable = overrides != null && overrides.Length >= Count;
            for (var i = 0; i < Count; i++)
            {
                _overrides[i] = usable ? Sanitize(overrides[i]) : MouthOverride.None;
            }
        }

        /// <summary>Whether the mouth is moving now. The brain's weights follow at the usual rate.</summary>
        public void SetSpeaking(bool speaking)
        {
            if (_speakingNow && !speaking)
            {
                _sinceSpeaking = 0f;
            }

            _speakingNow = speaking;
        }

        /// <summary>Negative or non-finite delta counts as 0 (and does not count toward the speaking hold).</summary>
        public void Advance(float delta)
        {
            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                return;
            }

            if (!_speakingNow)
            {
                _sinceSpeaking += delta;
            }

            var step = delta / FadeSeconds;
            _amount = MoveTowards(_amount, _target != None ? 1f : 0f, step);
            if (_target != None)
            {
                for (var i = 0; i < Count; i++)
                {
                    _weights[i] = MoveTowards(_weights[i], TargetWeight(i), step);
                }
            }
        }

        /// <summary>
        /// Writes the weights to show into output. upstream is what the upstream code wrote this frame;
        /// surprised is read as 0 because upstream never writes it (the value there is the one written
        /// here last frame). NaN reads as 0 and other values are clamped to 0..1. Does nothing when
        /// either array is null or shorter than Count.
        /// </summary>
        public void Evaluate(float[] upstream, float[] output)
        {
            if (upstream == null || output == null || upstream.Length < Count || output.Length < Count)
            {
                return;
            }

            for (var i = 0; i < Count; i++)
            {
                var below = i == Surprised ? 0f : Sanitize(upstream[i]);
                output[i] = below + (_weights[i] - below) * _amount;
            }
        }

        private bool IsSpeaking
        {
            get { return _speakingNow || _sinceSpeaking < SpeakingHoldSeconds; }
        }

        /// <summary>Where the brain's weight i heads while an emotion is set.</summary>
        private float TargetWeight(int i)
        {
            var weight = i == _target ? 1f : 0f;
            if (!IsSpeaking)
            {
                return weight;
            }

            switch (_overrides[i])
            {
                case MouthOverride.Blend:
                    return Math.Min(weight, SpeakingWeight);
                case MouthOverride.Block:
                    return 0f;
                default:
                    return weight;
            }
        }

        private static MouthOverride Sanitize(MouthOverride value)
        {
            switch (value)
            {
                case MouthOverride.Blend:
                case MouthOverride.Block:
                    return value;
                default:
                    return MouthOverride.None;
            }
        }

        private static int IndexOf(string name)
        {
            for (var i = 0; i < Count; i++)
            {
                if (string.Equals(Emotions[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return None;
        }

        private static float Sanitize(float value)
        {
            if (float.IsNaN(value))
            {
                return 0f;
            }

            return Math.Max(0f, Math.Min(1f, value));
        }

        private static float MoveTowards(float current, float target, float step)
        {
            if (current < target)
            {
                return Math.Min(target, current + step);
            }

            return Math.Max(target, current - step);
        }
    }
}
