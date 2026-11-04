using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>What WalkSequence needs to know this frame. Rectangles are in the window system's coordinates.</summary>
    public readonly struct WalkInput
    {
        public WalkInput(
            bool canWalk,
            bool talking,
            float pixelsPerMeter,
            ScreenRect body,
            IReadOnlyList<ScreenRect> monitors,
            bool idleAtBoundary,
            float clipLength,
            float startTime,
            IReadOnlyList<float> restTimes)
        {
            CanWalk = canWalk;
            Talking = talking;
            PixelsPerMeter = pixelsPerMeter;
            Body = body;
            Monitors = monitors;
            IdleAtBoundary = idleAtBoundary;
            ClipLength = clipLength;
            StartTime = startTime;
            RestTimes = restTimes;
        }

        /// <summary>VRM 1.0, idle, no tag motion, no input field focused and a walk clip loaded.</summary>
        public bool CanWalk { get; }

        public bool Talking { get; }

        /// <summary>How many pixels one metre of the model takes on the screen.</summary>
        public float PixelsPerMeter { get; }

        /// <summary>The avatar's body on the screen, with the window where it is now. Only X and Width are used for the edges.</summary>
        public ScreenRect Body { get; }

        /// <summary>May be null.</summary>
        public IReadOnlyList<ScreenRect> Monitors { get; }

        /// <summary>The own idle is held where it would move on to its next clip; always true without own idle clips.</summary>
        public bool IdleAtBoundary { get; }

        /// <summary>The walk clip's length in seconds.</summary>
        public float ClipLength { get; }

        /// <summary>Clip seconds to start the walk at, in [0, ClipLength).</summary>
        public float StartTime { get; }

        /// <summary>
        /// Clip seconds where the pose is close to the idle's (WalkRestTimes), in any order; values outside
        /// [0, ClipLength) are not used. May be null. Without a usable one, or with ClipLength or StartTime
        /// out of range, the walk starts at 0 and slows down by the distance left alone.
        /// </summary>
        public IReadOnlyList<float> RestTimes { get; }
    }

    /// <summary>
    /// When the avatar walks, which way, and how far the window moves this frame. Once the avatar has
    /// been able to walk without a break for a random wait, it asks the idle to hold (HoldIdle) and
    /// starts on the first frame the idle is at a boundary. It walks a random distance left or right at
    /// a random speed scale k: the clip plays k seconds a second, and the window moves sideways at
    /// FootSpeed (m/s, the speed the walk clip's feet move at at k = 1) times k times the pixels per
    /// metre times sin(TurnDegrees), times the walk weight, so the feet do not slide. The clip starts at
    /// StartTime; the slow-down starts when the clip passes a rest time from which the next one is out
    /// of reach, or, failing that, when only the stopping distance is left. The walk ends early when
    /// the body's edge reaches the edge of the monitor it started on, and the next walk then goes the
    /// other way. When walking stops being possible the window stops at once and the weight falls to 0
    /// over FadeOut. The window never moves vertically. The random source is drawn in a fixed order:
    /// the wait whenever a wait starts, then at the start of a walk the distance, (only when both ways
    /// are open) the direction and the speed scale. Not thread-safe; call from one thread.
    /// </summary>
    public sealed class WalkSequence
    {
        public const float MinWaitSeconds = 15f;
        public const float MaxWaitSeconds = 30f;

        /// <summary>Pixels the window moves in one walk.</summary>
        public const float MinDistance = 400f;
        public const float MaxDistance = 1000f;

        /// <summary>Metres per second the planted foot moves back relative to the hips in the walk clip.</summary>
        public const float FootSpeed = 0.5f;

        /// <summary>Each walk plays the clip, and moves the window, this many times as fast (drawn per walk).</summary>
        public const float MinSpeedScale = 0.5f;
        public const float MaxSpeedScale = 0.75f;

        /// <summary>The body turns this far toward the way it walks.</summary>
        public const float TurnDegrees = 90f;

        public const float FadeIn = 0.25f;
        public const float FadeOut = 0.3f;

        /// <summary>The same pause after talking as the idle's, so a sentence break does not start a walk.</summary>
        public const float TalkHoldSeconds = IdleSequence.TalkHoldSeconds;

        /// <summary>A longer frame (a hitch, a breakpoint) must not move the window by more than one such frame would.</summary>
        public const float MaxDeltaSeconds = 1f / 15f;

        private readonly Func<double> _random;

        private float _hold;

        // The wait drawn when the current run of walkable frames began, and how much of it has passed.
        private bool _waiting;
        private double _waitSeconds;
        private double _waited;

        private ScreenRect _monitor;
        private double _distance;
        private double _walked;
        private double _scale = MinSpeedScale;
        private bool _ending;
        private bool _endingAtEdge;

        // This walk's clip length and rest times (sorted, within [0, length)); empty when the walk
        // started at 0 because the clip input was not usable.
        private readonly List<double> _restTimes = new List<double>();
        private double _clipLength;

        // The last walk ended at the monitor's edge: the next one goes the other way.
        private bool _stoppedAtEdge;

        public WalkSequence(Func<double> random)
        {
            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            _random = random;
            Direction = 1;
        }

        /// <summary>0 to 1: how much of the walk pose shows.</summary>
        public float Weight { get; private set; }

        /// <summary>-1 left, +1 right. Kept after a walk, so the pose fades out facing the same way.</summary>
        public int Direction { get; private set; }

        /// <summary>
        /// Clip seconds: from the start time, k a second, and running on while the pose fades out. Not
        /// wrapped to the clip's length (the caller does that).
        /// </summary>
        public float Time { get; private set; }

        /// <summary>Pixels to move the window sideways this frame (signed).</summary>
        public float StepX { get; private set; }

        /// <summary>Walking, its start and its slowing down at the end included; a fade after a break is not.</summary>
        public bool IsWalking { get; private set; }

        /// <summary>
        /// The idle should not move on to its next clip: the wait is over and the walk waits for the idle's
        /// boundary, or the walk pose shows.
        /// </summary>
        public bool HoldIdle
        {
            get { return Weight > 0f || IsWalking || (_waiting && _waited >= _waitSeconds); }
        }

        /// <summary>Moves time by delta seconds (negative or non-finite counts as 0; at most MaxDeltaSeconds).</summary>
        public void Advance(float delta, WalkInput input)
        {
            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                delta = 0f;
            }

            delta = Math.Min(delta, MaxDeltaSeconds);
            StepX = 0f;

            // Counted whatever else holds: a sentence that ended while the avatar was dragged is over.
            _hold = input.Talking ? TalkHoldSeconds : Math.Max(0f, _hold - delta);
            var ready = _hold <= 0f && CanWalk(input);

            if (IsWalking)
            {
                if (ready)
                {
                    Walk(delta, input, false);
                }
                else
                {
                    // Breaking off is not reaching the edge: the next walk picks its way afresh.
                    IsWalking = false;
                    _stoppedAtEdge = false;
                    FadeAway(delta);
                }

                return;
            }

            if (Weight > 0f)
            {
                // The pose returns first; the wait counts only from weight 0.
                FadeAway(delta);
                return;
            }

            if (!ready)
            {
                _waiting = false;
                return;
            }

            if (!_waiting)
            {
                _waiting = true;
                _waitSeconds = MinWaitSeconds + Draw() * (MaxWaitSeconds - MinWaitSeconds);
                _waited = 0.0;
            }

            _waited += delta;

            // Past the wait the walk holds the idle until it reaches a boundary, so no idle clip is cut short.
            if (_waited >= _waitSeconds && input.IdleAtBoundary)
            {
                _waiting = false;
                if (TryStart(input))
                {
                    // The fade-in starts with the frame the walk starts in.
                    Walk(delta, input, true);
                }
            }
        }

        private void FadeAway(float delta)
        {
            Time = (float)(Time + _scale * delta);
            Weight = Math.Max(0f, Weight - delta / FadeOut);
        }

        /// <summary>
        /// One frame of the walk. On the first frame the walk does not end even at weight 0 (a walk that
        /// must slow down at once): the frame it starts in always reads as walking.
        /// </summary>
        private void Walk(float delta, WalkInput input, bool first)
        {
            var previousTime = Time;
            Time = (float)(Time + _scale * delta);
            var gap = Gap(input.Body, _monitor, Direction);
            var left = _distance - _walked;
            var remaining = Math.Min(left, gap);
            var fullSpeed = FullSpeed(input.PixelsPerMeter);
            var speed = fullSpeed * _scale;

            // Starting the slow-down half its length before the end lands the stop near it: the weight falls linearly over FadeOut.
            var stopping = speed * FadeOut / 2.0;
            if (!_ending && (remaining <= stopping || RestTimeEnds(previousTime, Time, fullSpeed, stopping, remaining)))
            {
                _ending = true;
                _endingAtEdge = gap < left;
            }

            Weight = _ending ? Math.Max(0f, Weight - delta / FadeOut) : Math.Min(1f, Weight + delta / FadeIn);

            // Never past what is left, so the body's edge never crosses the monitor's.
            var step = Math.Min(speed * Weight * delta, Math.Max(0.0, remaining));
            StepX = (float)(Direction * step);
            _walked += Math.Abs((double)StepX);

            if (_ending && Weight <= 0f && !first)
            {
                IsWalking = false;
                _stoppedAtEdge = _endingAtEdge;
            }
        }

        /// <summary>
        /// The clip passed a rest time this frame, and from there the next rest time is out of reach:
        /// going on to it and then stopping would take more than is left. A clip second moves the
        /// window by the full speed at k = 1 whatever k is, since k scales both.
        /// </summary>
        private bool RestTimeEnds(double previousTime, double time, double fullSpeed, double stopping, double remaining)
        {
            if (_restTimes.Count == 0 || !(time > previousTime))
            {
                return false;
            }

            var passed = LastRestTimeAtOrBefore(time);
            if (!(passed > previousTime))
            {
                return false;
            }

            return (NextRestTimeAfter(passed) - passed) * fullSpeed + stopping > remaining;
        }

        /// <summary>The latest of the rest times repeated every clip length that is at most time.</summary>
        private double LastRestTimeAtOrBefore(double time)
        {
            // From one period above floor(time / length) down: rounding may put time in either neighbour.
            var period = Math.Floor(time / _clipLength);
            for (var p = period + 1.0; p >= period - 1.0; p--)
            {
                var origin = p * _clipLength;
                for (var i = _restTimes.Count - 1; i >= 0; i--)
                {
                    if (origin + _restTimes[i] <= time)
                    {
                        return origin + _restTimes[i];
                    }
                }
            }

            return double.NegativeInfinity;
        }

        /// <summary>The earliest of the rest times repeated every clip length that is greater than time.</summary>
        private double NextRestTimeAfter(double time)
        {
            var period = Math.Floor(time / _clipLength);
            for (var p = period - 1.0; p <= period + 1.0; p++)
            {
                var origin = p * _clipLength;
                for (var i = 0; i < _restTimes.Count; i++)
                {
                    if (origin + _restTimes[i] > time)
                    {
                        return origin + _restTimes[i];
                    }
                }
            }

            return double.PositiveInfinity;
        }

        /// <summary>False when the body is as wide as the monitor or no way has room: the avatar waits again.</summary>
        private bool TryStart(WalkInput input)
        {
            var monitor = StartMonitor(input.Body, input.Monitors);
            var distance = MinDistance + Draw() * (MaxDistance - MinDistance);
            if (input.Body.Width >= monitor.Width)
            {
                return false;
            }

            int direction;
            if (!TryChooseDirection(input.Body, monitor, distance, out direction))
            {
                return false;
            }

            IsWalking = true;
            Direction = direction;
            Weight = 0f;
            _scale = MinSpeedScale + Draw() * (MaxSpeedScale - MinSpeedScale);
            Time = TakeClipInput(input) ? input.StartTime : 0f;
            _monitor = monitor;
            _distance = distance;
            _walked = 0.0;
            _ending = false;
            _endingAtEdge = false;
            _stoppedAtEdge = false;
            return true;
        }

        /// <summary>
        /// Keeps this walk's clip length and rest times. False (and no rest times) when the length is not
        /// positive and finite, the start time is not in [0, length), or no rest time is.
        /// </summary>
        private bool TakeClipInput(WalkInput input)
        {
            _restTimes.Clear();
            var length = input.ClipLength;
            var start = input.StartTime;
            if (!IsPositiveFinite(length) || !(start >= 0f && start < length) || input.RestTimes == null)
            {
                return false;
            }

            for (var i = 0; i < input.RestTimes.Count; i++)
            {
                var rest = input.RestTimes[i];
                if (rest >= 0f && rest < length)
                {
                    _restTimes.Add(rest);
                }
            }

            _restTimes.Sort();
            _clipLength = length;
            return _restTimes.Count > 0;
        }

        private bool TryChooseDirection(ScreenRect body, ScreenRect monitor, double distance, out int direction)
        {
            var leftGap = Gap(body, monitor, -1);
            var rightGap = Gap(body, monitor, 1);

            if (_stoppedAtEdge)
            {
                var back = -Direction;
                if ((back < 0 ? leftGap : rightGap) > 0.0)
                {
                    direction = back;
                    return true;
                }
            }

            var leftOpen = leftGap >= distance;
            var rightOpen = rightGap >= distance;
            if (leftOpen && rightOpen)
            {
                direction = Draw() < 0.5 ? -1 : 1;
                return true;
            }

            if (leftOpen || rightOpen)
            {
                direction = leftOpen ? -1 : 1;
                return true;
            }

            direction = leftGap > rightGap ? -1 : 1;
            return (direction < 0 ? leftGap : rightGap) > 0.0;
        }

        /// <summary>[0, 1]; NaN reads as 0.</summary>
        private double Draw()
        {
            var r = _random();
            if (double.IsNaN(r) || r < 0.0)
            {
                return 0.0;
            }

            return r > 1.0 ? 1.0 : r;
        }

        /// <summary>Pixels per second at full weight.</summary>
        private static double FullSpeed(float pixelsPerMeter)
        {
            return (double)FootSpeed * pixelsPerMeter * Math.Sin((double)TurnDegrees * Math.PI / 180.0);
        }

        /// <summary>Room between the body's edge and the monitor's edge on that side; negative when the body is already past it.</summary>
        private static double Gap(ScreenRect body, ScreenRect monitor, int direction)
        {
            return direction < 0
                ? (double)body.X - monitor.X
                : ((double)monitor.X + monitor.Width) - ((double)body.X + body.Width);
        }

        private static bool CanWalk(WalkInput input)
        {
            return input.CanWalk
                && IsPositiveFinite(input.PixelsPerMeter)
                && IsFinite(input.Body.X) && IsFinite(input.Body.Y) && IsFinite(input.Body.Width) && IsFinite(input.Body.Height)
                && input.Body.Width > 0f
                && HasValidMonitor(input.Monitors);
        }

        private static bool HasValidMonitor(IReadOnlyList<ScreenRect> monitors)
        {
            if (monitors == null)
            {
                return false;
            }

            for (var i = 0; i < monitors.Count; i++)
            {
                if (IsValid(monitors[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The valid monitor holding the body's center (edges included, the lower index first); else the
        /// one nearest to it (the lower index on a tie), as WindowPlacement.TryRestore picks.
        /// monitors must hold a valid one.
        /// </summary>
        private static ScreenRect StartMonitor(ScreenRect body, IReadOnlyList<ScreenRect> monitors)
        {
            var centerX = body.X + body.Width / 2.0;
            var centerY = body.Y + body.Height / 2.0;
            var hasNearest = false;
            var nearest = default(ScreenRect);
            var nearestDistance = 0.0;
            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                if (!IsValid(monitor))
                {
                    continue;
                }

                var dx = Outside(centerX, monitor.X, (double)monitor.X + monitor.Width);
                var dy = Outside(centerY, monitor.Y, (double)monitor.Y + monitor.Height);
                if (dx == 0.0 && dy == 0.0)
                {
                    return monitor;
                }

                // Squared distances compare the same as distances and keep ties exact.
                var distance = dx * dx + dy * dy;
                if (!hasNearest || distance < nearestDistance)
                {
                    hasNearest = true;
                    nearest = monitor;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        private static double Outside(double center, double min, double max)
        {
            if (center < min)
            {
                return min - center;
            }

            return center > max ? center - max : 0.0;
        }

        private static bool IsValid(ScreenRect monitor)
        {
            return IsFinite(monitor.X) && IsFinite(monitor.Y) && IsPositiveFinite(monitor.Width) && IsPositiveFinite(monitor.Height);
        }

        private static bool IsPositiveFinite(float value)
        {
            // NaN fails the comparison.
            return value > 0f && !float.IsPositiveInfinity(value);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
