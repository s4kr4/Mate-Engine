using System;
using System.Linq;
using System.Threading.Tasks;
using UniGLTF;
using UniVRM10;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Loads a .vrma into a hidden humanoid whose clip can be sampled, and copies a sampled pose onto
    /// the avatar. Shared by MotionPlayer (tag motions) and IdlePlayer (own idle).
    /// </summary>
    public static class VrmaClipLoader
    {
        /// <summary>
        /// Reads the file off the main thread, then imports it on the main thread under owner's
        /// transform. Never throws: on failure Clip is null and Error says why. When owner is destroyed
        /// meanwhile, both are null and nothing is left behind.
        /// </summary>
        public static async Task<VrmaLoadResult> LoadAsync(MonoBehaviour owner, string name, string path, string rootName)
        {
            var imported = false;
            try
            {
                // Reading up to 16 MiB stays off the main thread so a say that starts now does not stutter.
                var read = await Task.Run(() => ReadFile(path));
                if (owner == null)
                {
                    return new VrmaLoadResult(null, null, false);
                }

                if (read.Data == null)
                {
                    return new VrmaLoadResult(null, read.Error, false);
                }

                imported = true;
                var clip = await Import(owner.transform, name, path, read.Data, rootName);
                if (owner == null)
                {
                    if (clip != null)
                    {
                        clip.Dispose();
                    }

                    return new VrmaLoadResult(null, null, true);
                }

                return clip == null ? new VrmaLoadResult(null, "no humanoid animation clip", true) : new VrmaLoadResult(clip, null, true);
            }
            catch (Exception e)
            {
                return new VrmaLoadResult(null, e.GetType().Name + ": " + e.Message, imported);
            }
        }

        /// <summary>
        /// Poses the clip's humanoid at time and blends the avatar's humanoid pose toward it by weight.
        /// targetPose and sourcePose are buffers kept by the caller so that no muscle arrays are allocated
        /// every frame.
        /// </summary>
        public static void ApplyPose(HumanPoseHandler avatarPose, VrmaClip motion, float time, float weight, ref HumanPose targetPose, ref HumanPose sourcePose)
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

        private static ReadResult ReadFile(string path)
        {
            byte[] data;
            string error;
            return MotionFiles.TryRead(path, MotionFiles.MaxBytes, out data, out error) ? new ReadResult(data, null) : new ReadResult(null, error);
        }

        /// <summary>Main thread only. Null (with nothing left behind) when the file has no usable humanoid clip.</summary>
        private static async Task<VrmaClip> Import(Transform parent, string name, string path, byte[] bytes, string rootName)
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
                        UnityEngine.Object.Destroy(importer.PartialRoot);
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
                root.name = rootName;
                root.transform.SetParent(parent, false);

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

                return new VrmaClip(name, instance, clip, new HumanPoseHandler(animator.avatar, root.transform));
            }
            catch (Exception)
            {
                instance.Dispose();
                throw;
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
    }

    /// <summary>The outcome of VrmaClipLoader.LoadAsync: a clip, or why there is none.</summary>
    public struct VrmaLoadResult
    {
        public VrmaLoadResult(VrmaClip clip, string error, bool imported)
        {
            Clip = clip;
            Error = error;
            Imported = imported;
        }

        public VrmaClip Clip { get; }

        public string Error { get; }

        /// <summary>
        /// The bytes were read and handed to the importer. A failure after that can leave UniVRM objects
        /// behind (the Avatar), so loading the same bytes again leaks again; a failure before it leaves nothing.
        /// </summary>
        public bool Imported { get; }
    }

    /// <summary>A loaded .vrma: its hidden humanoid, its clip and a pose handler for that humanoid.</summary>
    public sealed class VrmaClip
    {
        private readonly RuntimeGltfInstance instance;

        public VrmaClip(string name, RuntimeGltfInstance instance, AnimationClip clip, HumanPoseHandler pose)
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

        /// <summary>Set by the owner: the token it gives MotionFade or IdleSequence for this clip.</summary>
        public long Token { get; set; }

        public bool IsDisposed { get; private set; }

        /// <summary>Destroys the hidden humanoid. Further calls do nothing.</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
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
