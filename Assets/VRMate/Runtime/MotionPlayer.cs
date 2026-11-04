using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Plays a .vrma from the motion folder once on a VRM 1.0 avatar while it is idle, then gives the
    /// body back to the Animator. The VRMA's humanoid pose is copied onto the avatar with
    /// HumanPoseHandler and blended by MotionFade's weight; the avatar's Animator is never touched.
    /// Files load asynchronously and stay cached by name; failed loads are not cached (the next
    /// request reads again) but warn once per name.
    /// </summary>
    // After the Animator has written this frame's pose, before AvatarMouseTracking (LateUpdate,
    // order 0) adds the head and spine offsets on top of whatever pose the bones hold.
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public class MotionPlayer : MonoBehaviour
    {
        // Names that open the same file share one entry; otherwise every letter case would load it again.
        private readonly Dictionary<string, VrmaClip> loaded = new Dictionary<string, VrmaClip>(MotionFiles.NameComparer);

        // Index is the MotionFade token.
        private readonly List<VrmaClip> motions = new List<VrmaClip>();
        private readonly HashSet<string> loading = new HashSet<string>(MotionFiles.NameComparer);
        private readonly WarnOnce warnings = new WarnOnce();
        private readonly AvatarWatch avatar = new AvatarWatch();
        private MotionFade fade = new MotionFade();
        private string folder;

        // The newest Play whose file was still loading; any later Play or Stop replaces it.
        private string requested;

        private Animator avatarAnimator;
        private HumanPoseHandler avatarPose;
        private HumanPose targetPose;
        private HumanPose sourcePose;

        public static MotionPlayer Instance { get; private set; }

        /// <summary>A motion is showing or fading out; WalkPlayer neither starts nor keeps walking meanwhile.</summary>
        public bool IsPlaying
        {
            get { return fade.IsActive; }
        }

        /// <summary>
        /// Plays the motion if the avatar is idle now; a motion that is still loading plays when it is
        /// loaded, if it is still the newest request and the avatar is idle then.
        /// </summary>
        public void Play(string name)
        {
            requested = null;
            string path;
            if (!MotionFiles.TryGetPath(folder, name, out path))
            {
                Warn("path:" + name, "motion '" + name + "' has no usable path in " + folder);
                return;
            }

            if (avatarPose == null)
            {
                // No VRM 1.0 humanoid avatar (VRM 0.x is out of scope): nothing to play on.
                return;
            }

            VrmaClip motion;
            if (loaded.TryGetValue(name, out motion))
            {
                if (CanPlay())
                {
                    fade.Play(motion.Token, motion.Clip.length);
                }

                return;
            }

            requested = name;
            if (loading.Add(name))
            {
                Load(name, path);
            }
        }

        /// <summary>Fades the motion out and forgets a request that is still loading.</summary>
        public void Stop()
        {
            requested = null;
            fade.Stop();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            folder = Path.Combine(Application.persistentDataPath, "VRMate", "Motions");
        }

        private void LateUpdate()
        {
            if (avatar.Refresh())
            {
                SetAvatar(avatar.Animator);
            }

            if (!fade.IsActive)
            {
                return;
            }

            fade.Advance(Time.deltaTime, CanPlay());
            if (!fade.IsActive || fade.Weight <= 0f || avatarPose == null)
            {
                return;
            }

            var motion = motions[(int)fade.Token];
            try
            {
                ApplyPose(motion, fade.Time, fade.Weight);
            }
            catch (Exception e)
            {
                // Ending this playback keeps the avatar moving under its Animator.
                fade = new MotionFade();
                Warn("apply:" + motion.Name, "cannot play motion '" + motion.Name + "': " + e.Message);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            DisposeAvatarPose();
            foreach (var motion in motions)
            {
                motion.Dispose();
            }

            motions.Clear();
            loaded.Clear();
        }

        private void ApplyPose(VrmaClip motion, float time, float weight)
        {
            VrmaClipLoader.ApplyPose(avatarPose, motion, time, weight, ref targetPose, ref sourcePose);
        }

        private bool CanPlay()
        {
            if (avatarPose == null)
            {
                return false;
            }

            string error;
            if (AvatarState.IsIdle(avatarAnimator, out error))
            {
                return true;
            }

            if (error != null)
            {
                Warn("idle", "cannot read the avatar's animator state: " + error);
            }

            return false;
        }

        private void SetAvatar(Animator found)
        {
            // The playing motion belonged to the previous avatar's pose.
            fade = new MotionFade();
            requested = null;
            DisposeAvatarPose();
            avatarAnimator = null;

            if (!AvatarState.IsVrm10Humanoid(found))
            {
                return;
            }

            try
            {
                avatarPose = new HumanPoseHandler(found.avatar, found.transform);
                avatarAnimator = found;
            }
            catch (Exception e)
            {
                DisposeAvatarPose();
                Warn("avatar", "cannot read the avatar's humanoid pose: " + e.Message);
            }
        }

        private void DisposeAvatarPose()
        {
            if (avatarPose != null)
            {
                avatarPose.Dispose();
                avatarPose = null;
            }
        }

        // async void: nothing awaits the load; every failure is caught and reported here.
        private async void Load(string name, string path)
        {
            VrmaClip motion = null;
            string error = null;
            try
            {
                var result = await VrmaClipLoader.LoadAsync(this, name, path, "VRMate motion " + name);
                motion = result.Clip;
                error = result.Error;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                loading.Remove(name);
            }

            if (this == null)
            {
                if (motion != null)
                {
                    motion.Dispose();
                }

                return;
            }

            if (motion == null)
            {
                if (IsRequested(name))
                {
                    requested = null;
                }

                Warn("load:" + name, "cannot load motion '" + name + "' from " + path + ": " + error);
                return;
            }

            motion.Token = motions.Count;
            motions.Add(motion);
            loaded[name] = motion;

            if (IsRequested(name))
            {
                requested = null;
                if (CanPlay())
                {
                    fade.Play(motion.Token, motion.Clip.length);
                }
            }
        }

        /// <summary>A load for "VRMA_02" also answers a newer request written "vrma_02": it is the same file.</summary>
        private bool IsRequested(string name)
        {
            return requested != null && MotionFiles.NameComparer.Equals(requested, name);
        }

        private void Warn(string key, string message)
        {
            if (warnings.ShouldWarn(key))
            {
                Debug.LogWarning("[VRMate] " + message);
            }
        }
    }
}
