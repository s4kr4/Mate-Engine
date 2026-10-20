using System;
using System.Collections.Generic;

namespace VRMate
{
    /// <summary>A loaded idle clip: the caller's token for it, its length in seconds and its motion name.</summary>
    public struct IdleClip
    {
        public IdleClip(long token, float length)
            : this(token, length, null)
        {
        }

        public IdleClip(long token, float length, string name)
        {
            Token = token;
            Length = length;
            Name = name;
        }

        public long Token { get; }

        public float Length { get; }

        /// <summary>May be null. Only talk clips' names are used: they choose the clip each time talking starts.</summary>
        public string Name { get; }
    }

    /// <summary>One clip to copy onto the avatar this frame: pose = lerp(pose, clip at Time, Weight).</summary>
    public struct IdleLayer
    {
        internal IdleLayer(long token, float time, float weight)
        {
            Token = token;
            Time = time;
            Weight = weight;
        }

        public long Token { get; }

        /// <summary>Seconds into the clip, within 0 and its length.</summary>
        public float Time { get; }

        /// <summary>0 to 1.</summary>
        public float Weight { get; }
    }

    /// <summary>
    /// Which idle clips show, at which time and weight. The normal clips play in list order, each to its
    /// end, cross-fading into the next CrossFadeSeconds before the end; the order wraps to the first.
    /// While talking (SetTalking true, and TalkHoldSeconds after it turns false) a talk clip loops
    /// instead: each time talking starts, the one whose name comes next in IdleFiles.NameOrder after the
    /// one that started last time, wrapping to the smallest name. The name is remembered rather than the
    /// token or the position, since a reload can give new tokens (a changed file) and add or remove
    /// clips around it. Without talk clips the current normal clip repeats. Layers are applied in index
    /// order over upstream's pose, so their weights are chosen to give the shares: upstream 1 - o, one
    /// layer o, and during a cross-fade the outgoing layer o(1 - c) and the incoming layer o * c, where
    /// o = (being idle) * (own idle shown) and c is the cross-fade progress. Every share moves at a
    /// bounded rate, so the pose never jumps. A new clip list waits for the order to wrap, so a clip is
    /// never cut short. Not thread-safe; call from one thread.
    /// </summary>
    public sealed class IdleSequence
    {
        /// <summary>Between clips, from talking back to the normal order, and between own and upstream idle.</summary>
        public const float CrossFadeSeconds = 1f;

        /// <summary>Into the talk clip: speech starts at once, so the body should settle sooner.</summary>
        public const float TalkFadeSeconds = 0.5f;

        /// <summary>Longer than the usual pause between two sentences, so the body does not swing between them.</summary>
        public const float TalkHoldSeconds = 1.5f;

        /// <summary>Leaving idle (MotionFade.StopFade): dragging or sitting should take the body at once.</summary>
        public const float LeaveIdleSeconds = 0.15f;

        /// <summary>Returning to idle (MotionFade.FadeIn).</summary>
        public const float EnterIdleSeconds = 0.25f;

        /// <summary>
        /// MateEngine never runs below 15 fps; a longer frame (a hitch, a breakpoint) must not move the
        /// pose by more than one such frame would.
        /// </summary>
        public const float MaxDeltaSeconds = 1f / 15f;

        /// <summary>
        /// Without normal clips the order never wraps, so it would never ask for a reload; asking this
        /// often instead picks up files put back (or unlocked) without restarting.
        /// </summary>
        public const float EmptyReloadSeconds = 5f;

        private static readonly IdleClip[] NoClips = new IdleClip[0];

        private IdleClip[] _normal = NoClips;
        private IdleClip[] _talk = NoClips;
        private bool _hasPending;
        private IdleClip[] _pendingNormal = NoClips;
        private IdleClip[] _pendingTalk = NoClips;

        // How idle the avatar is (p) and how much of the own idle shows over upstream's (s).
        private float _idle;
        private float _own;

        private bool _talkingNow;
        private float _hold;

        // The shown clip (the outgoing one during a cross-fade) and the incoming one.
        private Playing _current;
        private Playing _incoming;
        private float _mix;
        private float _mixRate;
        private bool _reversing;

        // The normal clip the order is at: talking keeps it, and the return goes to the one after it.
        private int _position;

        // _position before the return from talking started; a return taken back restores it.
        private int _positionBeforeReturn;

        private bool _reloadRequested;

        // The name of the talk clip the last start of talking chose; kept across reloads. A separate flag,
        // since the name itself may be null.
        private bool _talkStarted;
        private string _lastTalkName;

        // Advance time since the normal list in use became empty (from creation, or from the wrap that
        // emptied it); counted only while it is empty.
        private float _emptyTime;

        public int LayerCount
        {
            get
            {
                if (_current == null || !(Overall > 0f))
                {
                    return 0;
                }

                return _incoming == null ? 1 : 2;
            }
        }

        private float Overall
        {
            get { return _idle * _own; }
        }

        private bool Mixing
        {
            get { return _incoming != null; }
        }

        /// <summary>
        /// Clips whose length is not positive and finite are dropped; null is empty. Used at once when no
        /// normal clips are in use; otherwise both lists wait until the order next wraps to the first clip
        /// (a later call replaces a waiting one).
        /// </summary>
        public void SetClips(IReadOnlyList<IdleClip> normal, IReadOnlyList<IdleClip> talk)
        {
            var validNormal = Valid(normal);
            var validTalk = Valid(talk);
            if (_normal.Length > 0)
            {
                _hasPending = true;
                _pendingNormal = validNormal;
                _pendingTalk = validTalk;
                return;
            }

            _hasPending = false;
            _pendingNormal = NoClips;
            _pendingTalk = NoClips;
            _normal = validNormal;
            _talk = validTalk;
            if (_normal.Length == 0)
            {
                return;
            }

            _position = 0;
            _positionBeforeReturn = 0;
            RequestReloadIfLast(0);
            var first = Playing.Normal(_normal, 0);
            if (_current == null)
            {
                _current = first;
                _own = 0f;
                return;
            }

            // Own idle is still fading out to upstream: blend from what shows instead of cutting to the new clip.
            StartMix(first, CrossFadeSeconds);
        }

        /// <summary>Takes effect at the next Advance.</summary>
        public void SetTalking(bool talking)
        {
            if (_talkingNow && !talking)
            {
                _hold = TalkHoldSeconds;
            }

            _talkingNow = talking;
        }

        /// <summary>Cancel and disconnect: talking ends without the hold. A later SetTalking(true) talks again.</summary>
        public void EndTalking()
        {
            _talkingNow = false;
            _hold = 0f;
        }

        /// <summary>
        /// Moves time by delta seconds (negative or non-finite counts as 0; at most MaxDeltaSeconds).
        /// idle false fades the own idle out over LeaveIdleSeconds; once it is gone the clips stop where
        /// they are and continue from there when idle returns.
        /// </summary>
        public void Advance(float delta, bool idle)
        {
            if (!(delta > 0f) || float.IsInfinity(delta))
            {
                delta = 0f;
            }

            delta = Math.Min(delta, MaxDeltaSeconds);

            // The hold counts even while the clock is stopped: a sentence that ended while dragging is over.
            if (!_talkingNow && _hold > 0f)
            {
                _hold = Math.Max(0f, _hold - delta);
            }

            var talking = _talkingNow || _hold > 0f;
            var running = idle || _idle > 0f;
            _idle = idle ? MoveTowards(_idle, 1f, delta / EnterIdleSeconds) : MoveTowards(_idle, 0f, delta / LeaveIdleSeconds);

            // Without own normal clips upstream's idle shows, and a talk clip alone would swing between the two.
            var toTalk = talking && _normal.Length > 0 && _talk.Length > 0;
            Steer(toTalk);
            if (running)
            {
                Tick(delta);
            }

            Settle(toTalk, talking);
            CountEmptyTime(delta);
        }

        /// <summary>0 &lt;= index &lt; LayerCount. Layer 0 is the outgoing clip during a cross-fade.</summary>
        public IdleLayer GetLayer(int index)
        {
            if (index < 0 || index >= LayerCount)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            var overall = Overall;
            if (_incoming == null)
            {
                return new IdleLayer(_current.Token, _current.Time, Clamp01(overall));
            }

            var incoming = overall * _mix;
            if (index == 1)
            {
                return new IdleLayer(_incoming.Token, _incoming.Time, Clamp01(incoming));
            }

            // The incoming layer is applied after this one and scales it by (1 - incoming).
            var rest = 1f - incoming;
            var weight = rest > 0f ? overall * (1f - _mix) / rest : 0f;
            return new IdleLayer(_current.Token, _current.Time, Clamp01(weight));
        }

        /// <summary>
        /// True while the token can still show: it is in a layer (even at weight 0), in the lists in use,
        /// or in the lists waiting for the order to wrap. The caller may free a clip once this is false
        /// and it has not given the token again.
        /// </summary>
        public bool IsInUse(long token)
        {
            if ((_current != null && _current.Token == token) || (_incoming != null && _incoming.Token == token))
            {
                return true;
            }

            return Contains(_normal, token) || Contains(_talk, token) || (_hasPending && (Contains(_pendingNormal, token) || Contains(_pendingTalk, token)));
        }

        /// <summary>True once after the order moves to the last normal clip: the caller reads the folders again.</summary>
        public bool TakeReloadRequest()
        {
            var requested = _reloadRequested;
            _reloadRequested = false;
            return requested;
        }

        /// <summary>
        /// Points a cross-fade at where it should go now. Going back to the clip that is fading out runs
        /// the fade backwards; any other change finishes the fade (at the new speed) before moving on.
        /// </summary>
        private void Steer(bool toTalk)
        {
            if (!Mixing)
            {
                return;
            }

            if (!toTalk)
            {
                if (_reversing)
                {
                    // Talking ended again while the return was taken back: the incoming clip is the one to go to.
                    _reversing = false;
                    _position = _incoming.Index;
                }

                _mixRate = 1f / CrossFadeSeconds;
                return;
            }

            if (_current.IsTalk && !_incoming.IsTalk)
            {
                if (!_reversing)
                {
                    _reversing = true;
                    _position = _positionBeforeReturn;
                }

                _mixRate = 1f / TalkFadeSeconds;
                return;
            }

            // The talk clip looping into itself keeps its pace; anything else hurries toward the talk clip.
            _mixRate = _current.IsTalk && _incoming.IsTalk ? 1f / CrossFadeSeconds : 1f / TalkFadeSeconds;
        }

        /// <summary>Counts whether idle or not: files can be put back while the avatar is dragged or sitting.</summary>
        private void CountEmptyTime(float delta)
        {
            if (_normal.Length > 0)
            {
                return;
            }

            _emptyTime += delta;
            if (_emptyTime >= EmptyReloadSeconds)
            {
                _emptyTime -= EmptyReloadSeconds;
                _reloadRequested = true;
            }
        }

        private void Tick(float delta)
        {
            if (_current != null)
            {
                _current.Advance(delta);
            }

            if (_incoming != null)
            {
                _incoming.Advance(delta);
                _mix = _reversing ? Math.Max(0f, _mix - delta * _mixRate) : Math.Min(1f, _mix + delta * _mixRate);
            }

            _own = MoveTowards(_own, _normal.Length > 0 ? 1f : 0f, delta / CrossFadeSeconds);
        }

        private void Settle(bool toTalk, bool talking)
        {
            if (Mixing)
            {
                if (_reversing && _mix <= 0f)
                {
                    _incoming = null;
                    _reversing = false;
                }
                else if (!_reversing && _mix >= 1f)
                {
                    _current = _incoming;
                    _incoming = null;
                }
            }

            if (_normal.Length == 0)
            {
                // The last clip plays on while the own idle fades out; then upstream's idle shows alone.
                if (_own <= 0f)
                {
                    _current = null;
                    _incoming = null;
                }

                return;
            }

            if (_current == null || Mixing)
            {
                return;
            }

            if (toTalk)
            {
                if (!_current.IsTalk)
                {
                    StartMix(Playing.Talk(StartingTalkClip()), TalkFadeSeconds);
                }
                else if (_current.AtCrossFadeStart)
                {
                    // Still the same talk: the clip chosen when it started loops into itself.
                    StartMix(_current.Again(), CrossFadeSeconds);
                }

                return;
            }

            if (_current.IsTalk)
            {
                StartNext();
                return;
            }

            if (!_current.AtCrossFadeStart)
            {
                return;
            }

            if (talking)
            {
                // No talk clips: hold the order where it is by repeating the clip. Only a one-clip list asks
                // for a reload here, as it does every time its clip starts.
                if (_normal.Length == 1)
                {
                    _reloadRequested = true;
                }

                StartMix(Playing.Normal(_normal, _position), CrossFadeSeconds);
                return;
            }

            StartNext();
        }

        /// <summary>Moves to the normal clip after _position; past the last one, the waiting lists take over.</summary>
        private void StartNext()
        {
            var before = _position;
            var next = _position + 1;
            if (next >= _normal.Length)
            {
                if (_hasPending)
                {
                    _normal = _pendingNormal;
                    _talk = _pendingTalk;
                    _hasPending = false;
                    _pendingNormal = NoClips;
                    _pendingTalk = NoClips;
                }

                if (_normal.Length == 0)
                {
                    _emptyTime = 0f;
                    return;
                }

                next = 0;
                before = _normal.Length - 1;
            }

            _positionBeforeReturn = before;
            _position = next;
            RequestReloadIfLast(next);
            StartMix(Playing.Normal(_normal, next), CrossFadeSeconds);
        }

        /// <summary>
        /// The talk clip whose name is the smallest of those greater than the last start's name; without
        /// one (or before the first start), the smallest name. Of equal names the earlier clip. Compared
        /// rather than found by position, so the order the caller lists the clips in does not matter.
        /// Remembers the chosen name. _talk must not be empty.
        /// </summary>
        private IdleClip StartingTalkClip()
        {
            var smallest = 0;
            var next = -1;
            for (var i = 0; i < _talk.Length; i++)
            {
                var name = _talk[i].Name;
                if (IdleFiles.NameOrder.Compare(name, _talk[smallest].Name) < 0)
                {
                    smallest = i;
                }

                if (_talkStarted && IdleFiles.NameOrder.Compare(name, _lastTalkName) > 0 && (next < 0 || IdleFiles.NameOrder.Compare(name, _talk[next].Name) < 0))
                {
                    next = i;
                }
            }

            var chosen = _talk[next >= 0 ? next : smallest];
            _talkStarted = true;
            _lastTalkName = chosen.Name;
            return chosen;
        }

        private void StartMix(Playing incoming, float seconds)
        {
            _incoming = incoming;
            _mix = 0f;
            _mixRate = 1f / seconds;
            _reversing = false;
        }

        private void RequestReloadIfLast(int index)
        {
            if (index == _normal.Length - 1)
            {
                _reloadRequested = true;
            }
        }

        private static IdleClip[] Valid(IReadOnlyList<IdleClip> clips)
        {
            if (clips == null)
            {
                return NoClips;
            }

            var valid = new List<IdleClip>(clips.Count);
            for (var i = 0; i < clips.Count; i++)
            {
                var length = clips[i].Length;
                if (length > 0f && !float.IsInfinity(length))
                {
                    valid.Add(clips[i]);
                }
            }

            return valid.ToArray();
        }

        private static bool Contains(IdleClip[] clips, long token)
        {
            for (var i = 0; i < clips.Length; i++)
            {
                if (clips[i].Token == token)
                {
                    return true;
                }
            }

            return false;
        }

        private static float MoveTowards(float value, float target, float step)
        {
            return value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
        }

        private static float Clamp01(float value)
        {
            return value > 1f ? 1f : (value > 0f ? value : 0f);
        }

        private sealed class Playing
        {
            private Playing(long token, float length, int index)
            {
                Token = token;
                Length = length;
                Index = index;
            }

            public long Token { get; }

            public float Length { get; }

            /// <summary>Position in the normal list; -1 for a talk clip.</summary>
            public int Index { get; }

            public bool IsTalk
            {
                get { return Index < 0; }
            }

            public float Time { get; private set; }

            /// <summary>The next clip starts CrossFadeSeconds before the end, so the fade ends with this clip.</summary>
            public bool AtCrossFadeStart
            {
                get { return Time >= Math.Max(0f, Length - CrossFadeSeconds); }
            }

            public static Playing Normal(IdleClip[] clips, int index)
            {
                return new Playing(clips[index].Token, clips[index].Length, index);
            }

            public static Playing Talk(IdleClip clip)
            {
                return new Playing(clip.Token, clip.Length, -1);
            }

            /// <summary>The same clip from its start.</summary>
            public Playing Again()
            {
                return new Playing(Token, Length, Index);
            }

            /// <summary>Stops at the end: the last pose holds while the next clip fades in.</summary>
            public void Advance(float delta)
            {
                Time = Math.Min(Time + delta, Length);
            }
        }
    }
}
