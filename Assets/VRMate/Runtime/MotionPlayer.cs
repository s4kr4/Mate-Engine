using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniGLTF;
using UniVRM10;
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
        private const string IdleState = "Idle";

        // Names that open the same file share one entry; otherwise every letter case would load it again.
        private readonly Dictionary<string, LoadedMotion> loaded = new Dictionary<string, LoadedMotion>(MotionFiles.NameComparer);
        private readonly List<LoadedMotion> motions = new List<LoadedMotion>();
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

            LoadedMotion motion;
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

        private void ApplyPose(LoadedMotion motion, float time, float weight)
        {
            motion.Clip.SampleAnimation(motion.Root, Mathf.Min(time, motion.Clip.length));
            motion.Pose.GetHumanPose(ref sourcePose);
            avatarPose.GetHumanPose(ref targetPose);

            var muscles = targetPose.muscles;
            var source = sourcePose.muscles;
            var count = Math.Min(muscles.Length, source.Length);
            for (var i = 0; i < count; i++)
            {
                muscles[i] = Mathf.Lerp(muscles[i], source[i], weight);
            }

            targetPose.muscles = muscles;
            targetPose.bodyRotation = Quaternion.Slerp(targetPose.bodyRotation, sourcePose.bodyRotation, weight);
            targetPose.bodyPosition = Vector3.Lerp(targetPose.bodyPosition, sourcePose.bodyPosition, weight);
            avatarPose.SetHumanPose(ref targetPose);
        }

        private bool CanPlay()
        {
            if (avatarPose == null || avatarAnimator == null || !avatarAnimator.isActiveAndEnabled)
            {
                return false;
            }

            try
            {
                if (avatarAnimator.runtimeAnimatorController == null || avatarAnimator.layerCount == 0)
                {
                    return false;
                }

                // Idle only: dragging, sitting, dancing and sleeping keep the body to upstream.
                if (!avatarAnimator.GetCurrentAnimatorStateInfo(0).IsName(IdleState))
                {
                    return false;
                }

                return !avatarAnimator.IsInTransition(0) || avatarAnimator.GetNextAnimatorStateInfo(0).IsName(IdleState);
            }
            catch (Exception e)
            {
                Warn("idle", "cannot read the avatar's animator state: " + e.Message);
                return false;
            }
        }

        private void SetAvatar(Animator found)
        {
            // The playing motion belonged to the previous avatar's pose.
            fade = new MotionFade();
            requested = null;
            DisposeAvatarPose();
            avatarAnimator = null;

            if (found == null || found.avatar == null || !found.avatar.isHuman || found.GetComponentInChildren<Vrm10Instance>() == null)
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
            LoadedMotion motion = null;
            string error = null;
            try
            {
                // Reading up to 16 MiB stays off the main thread so a say that starts now does not stutter.
                var read = await Task.Run(() => ReadFile(path));
                if (this == null)
                {
                    return;
                }

                if (read.Data == null)
                {
                    error = read.Error;
                }
                else
                {
                    motion = await Import(name, path, read.Data);
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
                        error = "no humanoid animation clip";
                    }
                }
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

        private static ReadResult ReadFile(string path)
        {
            byte[] data;
            string error;
            return MotionFiles.TryRead(path, MotionFiles.MaxBytes, out data, out error) ? new ReadResult(data, null) : new ReadResult(null, error);
        }

        /// <summary>Main thread only. Null (with nothing left behind) when the file has no usable humanoid clip.</summary>
        private async Task<LoadedMotion> Import(string name, string path, byte[] bytes)
        {
            RuntimeGltfInstance instance;
            using (var data = new GlbBinaryParser(bytes, path).Parse())
            using (var importer = new Importer(data))
            {
                try
                {
                    // The synchronous Load() held the main thread for about 220 ms in the trial.
                    instance = await importer.LoadAsync(new RuntimeOnlyAwaitCaller());
                }
                catch (Exception)
                {
                    // A failed load can leave its half-built hierarchy in the scene, and a broken file is
                    // read again on every request; destroying the root removes its GameObjects. Objects
                    // made before ownership passes to the instance can still remain: the Avatar that
                    // OnLoadHierarchy creates is left behind when a later step (an expression channel) fails.
                    if (importer.PartialRoot != null)
                    {
                        Destroy(importer.PartialRoot);
                    }

                    throw;
                }
            }

            if (instance == null)
            {
                return null;
            }

            try
            {
                var root = instance.Root;
                root.name = "VRMate motion " + name;
                root.transform.SetParent(transform, false);

                var animationInstance = root.GetComponent<Vrm10AnimationInstance>();
                if (animationInstance != null && animationInstance.BoxMan != null)
                {
                    animationInstance.ShowBoxMan(false);
                }

                AnimationClip clip = null;
                var animation = root.GetComponent<Animation>();
                if (animation != null)
                {
                    clip = animation.clip != null ? animation.clip : animation.Cast<AnimationState>().Select(s => s.clip).FirstOrDefault(c => c != null);
                    animation.Stop();
                    animation.enabled = false;
                }

                // Only its Avatar is used; an enabled Animator would also be found as "the avatar".
                var animator = root.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.enabled = false;
                }

                if (clip == null || !(clip.length > 0f) || float.IsInfinity(clip.length) || animator == null || animator.avatar == null || !animator.avatar.isHuman)
                {
                    instance.Dispose();
                    return null;
                }

                return new LoadedMotion(name, instance, clip, new HumanPoseHandler(animator.avatar, root.transform));
            }
            catch (Exception)
            {
                instance.Dispose();
                throw;
            }
        }

        private void Warn(string key, string message)
        {
            if (warnings.ShouldWarn(key))
            {
                Debug.LogWarning("[VRMate] " + message);
            }
        }

        /// <summary>Exposes the root the importer creates, which is otherwise only reachable once loading succeeds.</summary>
        private sealed class Importer : VrmAnimationImporter
        {
            public Importer(GltfData data)
                : base(data)
            {
            }

            public GameObject PartialRoot
            {
                get { return Root; }
            }
        }

        private struct ReadResult
        {
            public ReadResult(byte[] data, string error)
            {
                Data = data;
                Error = error;
            }

            public byte[] Data { get; }

            public string Error { get; }
        }

        private sealed class LoadedMotion
        {
            private readonly RuntimeGltfInstance instance;

            public LoadedMotion(string name, RuntimeGltfInstance instance, AnimationClip clip, HumanPoseHandler pose)
            {
                Name = name;
                this.instance = instance;
                Root = instance.Root;
                Clip = clip;
                Pose = pose;
            }

            public string Name { get; }

            public GameObject Root { get; }

            public AnimationClip Clip { get; }

            public HumanPoseHandler Pose { get; private set; }

            /// <summary>Index in MotionPlayer.motions; used as the MotionFade token.</summary>
            public int Token { get; set; }

            public void Dispose()
            {
                if (Pose != null)
                {
                    Pose.Dispose();
                    Pose = null;
                }

                if (instance != null)
                {
                    instance.Dispose();
                }
            }
        }
    }
}
