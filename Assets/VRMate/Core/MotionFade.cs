using System;

namespace VRMate
{
    /// <summary>
    /// Time and blend weight of the one motion that plays on top of the idle animation.
    /// A motion fades in over FadeIn and out over FadeOut at its end. Stopping, leaving idle, or asking
    /// for another motion lowers the weight from where it is over StopFade (never a jump); the newest
    /// request waits until the weight reaches 0 and then starts from time 0 and weight 0.
    /// Not thread-safe; call from one thread.
    /// </summary>
    public sealed class MotionFade
    {
        public const float FadeIn = 0.25f;
        public const float FadeOut = 0.3f;
        public const float StopFade = 0.15f;

        private bool _active;
        private bool _stopping;
        private float _length;
        private bool _hasWaiting;
        private long _waitingToken;
        private float _waitingLength;

        public bool IsActive
        {
            get { return _active; }
        }

        /// <summary>The motion being shown; kept after it ends.</summary>
        public long Token { get; private set; }

        /// <summary>Seconds into the clip, within 0 and the clip length.</summary>
        public float Time { get; private set; }

        /// <summary>0 to 1; 0 whenever IsActive is false.</summary>
        public float Weight { get; private set; }

        /// <summary>Ignored unless length is positive and finite. Never changes Weight by itself.</summary>
        public void Play(long token, float length)
        {
            if (!(length > 0f) || float.IsInfinity(length))
            {
                return;
            }

            if (!_active)
            {
                Start(token, length);
                return;
            }

            // Replacing the pose at full weight would snap the body; lower it first, then switch.
            _stopping = true;
            _hasWaiting = true;
            _waitingToken = token;
            _waitingLength = length;
        }

        /// <summary>Lowers the weight to 0 and drops a waiting request. Never changes Weight by itself.</summary>
        public void Stop()
        {
            _hasWaiting = false;
            if (_active)
            {
                _stopping = true;
            }
        }

        /// <summary>
        /// Moves time by delta seconds (negative or non-finite counts as 0). canPlay false means the
        /// avatar is not idle: the motion stops as if Stop were called.
        /// </summary>
        public void Advance(float delta, bool canPlay)
        {
            if (!_active)
            {
                return;
            }

            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                delta = 0f;
            }

            if (!canPlay)
            {
                Stop();
            }

            if (_stopping)
            {
                // The clip end does not end a stop: a waiting request must still start once the weight is 0.
                Time = Math.Min(Time + delta, _length);
                Weight = Math.Max(0f, Weight - delta / StopFade);
                if (Weight <= 0f)
                {
                    if (_hasWaiting)
                    {
                        // The new motion starts this frame from 0; the rest of delta belonged to the old one.
                        Start(_waitingToken, _waitingLength);
                    }
                    else
                    {
                        End();
                    }
                }

                return;
            }

            Time += delta;
            if (Time >= _length)
            {
                End();
                return;
            }

            Weight = Envelope(Time, _length);
        }

        private void Start(long token, float length)
        {
            _active = true;
            _stopping = false;
            _hasWaiting = false;
            _length = length;
            Token = token;
            Time = 0f;
            Weight = 0f;
        }

        private void End()
        {
            _active = false;
            _stopping = false;
            _hasWaiting = false;
            Time = Math.Min(Time, _length);
            Weight = 0f;
        }

        private static float Envelope(float time, float length)
        {
            var weight = Math.Min(1f, Math.Min(time / FadeIn, (length - time) / FadeOut));
            return Math.Max(0f, weight);
        }
    }
}
