using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Kirurobo;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VRMate
{
    /// <summary>
    /// Walks a VRM 1.0 avatar left and right over the desktop with the first loadable .vrma in
    /// VRMate/Walk (in IdleFiles name order). WalkSequence decides when, which way and how far; this
    /// component gathers its input (the avatar's state, the body's rectangle and the pixels per metre
    /// from the camera, the monitors), moves the window sideways with UniWindowController and copies
    /// the walk pose, turned toward the way it goes, with HumanPoseHandler. The avatar's Animator is
    /// never touched (upstream's AvatarLocomotionController and its Locomotion Layer are left alone).
    /// The folder is read when the avatar changes (startup included); without it, or without a
    /// loadable clip, or on a VRM 0.x avatar, the avatar does not walk. In the editor the folder is still
    /// read, but the avatar never walks.
    /// </summary>
    // After IdlePlayer (-60) has written the own idle, before MotionPlayer (-50): a tag's motion plays on top of the walk.
    [DefaultExecutionOrder(-55)]
    [DisallowMultipleComponent]
    public class WalkPlayer : MonoBehaviour
    {
        private const string Label = "Walk";

        // The folder never offers more candidates than this.
        private const int MaxFailedFiles = IdleFiles.MaxFiles;

        // Only the few fixed kinds (idle state, avatar, reload, listing, no clip, apply): never runs out of keys.
        private readonly WarnOnce warnings = new WarnOnce();

        // Problems with the files present now, counted afresh on every reload.
        private readonly FileProblemLog problems = new FileProblemLog();

        // Files whose import failed, with the length and write time they had then. UniVRM leaves the Avatar
        // of a failed import behind, so the same version is not imported again on the next avatar change.
        private readonly Dictionary<string, FileStamp> failed = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        private readonly AvatarWatch avatar = new AvatarWatch();
        private readonly System.Random random = new System.Random();
        private readonly List<ScreenRect> monitors = new List<ScreenRect>();
        private readonly List<Renderer> renderers = new List<Renderer>();
        private readonly Vector3[] corners = new Vector3[8];

        private string folder;
        private WalkSequence sequence;
        private VrmaClip clip;

        // Set when copying the clip's pose failed: the avatar stops walking until the next avatar change.
        private bool clipBroken;

        // Changes with the avatar (and on destroy); a load that started for another value is dropped.
        private int generation;

        private UniWindowController controller;

        // The part of the sideways moves not yet given to the window, which may keep whole pixels only.
        private float carryX;

        private Animator avatarAnimator;
        private HumanPoseHandler avatarPose;
        private HumanPose targetPose;
        private HumanPose sourcePose;

        public static WalkPlayer Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            folder = Path.Combine(Application.persistentDataPath, "VRMate", Label);
        }

        private void LateUpdate()
        {
            if (avatar.Refresh())
            {
                SetAvatar(avatar.Animator);
            }

            if (sequence == null)
            {
                return;
            }

            if (controller == null)
            {
                controller = UniWindowController.current;
            }

            var canWalk = CanWalk();
            var speech = SpeechPlayer.Instance;
            var talking = speech != null && speech.IsTalking;
            float pixelsPerMeter;
            ScreenRect body;
            Measure(out pixelsPerMeter, out body);
            ReadMonitors();

            sequence.Advance(Time.deltaTime, new WalkInput(canWalk, talking, pixelsPerMeter, body, monitors));
            MoveWindow();
            ApplyPose();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            generation++;
            sequence = null;
            DisposeClip();
            DisposeAvatarPose();
        }

        private bool CanWalk()
        {
            // Not in the editor: there the Game view is not the whole window and its scale differs, so
            // Measure's body rectangle is wrong, and writing windowPosition would move the editor itself.
            if (Application.isEditor || avatarPose == null || clip == null || clipBroken || !IsIdle())
            {
                return false;
            }

            var motion = MotionPlayer.Instance;
            return (motion == null || !motion.IsPlaying) && !IsTyping();
        }

        private bool IsIdle()
        {
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

        /// <summary>An input field has the focus: the window must not move under the text being typed.</summary>
        private static bool IsTyping()
        {
            var events = EventSystem.current;
            if (events == null)
            {
                return false;
            }

            var selected = events.currentSelectedGameObject;
            return selected != null && (selected.GetComponent<InputField>() != null || selected.GetComponent<TMP_InputField>() != null);
        }

        /// <summary>
        /// The pixels one metre of the model takes and the body's rectangle, in UniWindowController's
        /// coordinates. Those have their origin at the bottom left and y going up, with windowPosition the
        /// window's bottom-left corner (UniWindowController turns the cursor into Unity's screen position by
        /// subtracting windowPosition), so a screen point from WorldToScreenPoint (bottom-left origin) is
        /// windowPosition plus the point, scaled from Unity's pixels to the window's. Anything that cannot be
        /// measured comes out NaN, which WalkSequence takes as "cannot walk".
        /// </summary>
        private void Measure(out float pixelsPerMeter, out ScreenRect body)
        {
            pixelsPerMeter = float.NaN;
            body = new ScreenRect(float.NaN, float.NaN, float.NaN, float.NaN);

            var view = Camera.main;
            if (view == null || controller == null || avatarAnimator == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return;
            }

            var client = controller.clientSize;
            var scaleX = client.x > 0f ? client.x / Screen.width : 1f;
            var scaleY = client.y > 0f ? client.y / Screen.height : 1f;

            var hips = avatarAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips != null)
            {
                var center = hips.position;
                var meter = Mathf.Abs(avatarAnimator.transform.lossyScale.x);
                var a = view.WorldToScreenPoint(center);
                var b = view.WorldToScreenPoint(center + view.transform.right * meter);
                if (a.z > 0f && b.z > 0f)
                {
                    pixelsPerMeter = Mathf.Abs(b.x - a.x) * scaleX;
                }
            }

            Bounds bounds;
            if (!TryGetBodyBounds(out bounds))
            {
                return;
            }

            var min = bounds.min;
            var max = bounds.max;
            corners[0] = new Vector3(min.x, min.y, min.z);
            corners[1] = new Vector3(max.x, min.y, min.z);
            corners[2] = new Vector3(min.x, max.y, min.z);
            corners[3] = new Vector3(max.x, max.y, min.z);
            corners[4] = new Vector3(min.x, min.y, max.z);
            corners[5] = new Vector3(max.x, min.y, max.z);
            corners[6] = new Vector3(min.x, max.y, max.z);
            corners[7] = new Vector3(max.x, max.y, max.z);

            var left = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            var bottom = float.PositiveInfinity;
            var top = float.NegativeInfinity;
            foreach (var corner in corners)
            {
                var point = view.WorldToScreenPoint(corner);
                if (!(point.z > 0f))
                {
                    // Behind the camera the projection flips; such a rectangle would be wrong, not just loose.
                    return;
                }

                left = Mathf.Min(left, point.x);
                right = Mathf.Max(right, point.x);
                bottom = Mathf.Min(bottom, point.y);
                top = Mathf.Max(top, point.y);
            }

            var window = controller.windowPosition;
            body = new ScreenRect(window.x + left * scaleX, window.y + bottom * scaleY, (right - left) * scaleX, (top - bottom) * scaleY);
        }

        /// <summary>
        /// The enabled mesh renderers of the avatar. Particles, trails and lines are left out: they can
        /// reach far from the body and would stop the walk short of the monitor's edge.
        /// </summary>
        private bool TryGetBodyBounds(out Bounds bounds)
        {
            bounds = default(Bounds);
            var found = false;
            avatarAnimator.GetComponentsInChildren(false, renderers);
            foreach (var part in renderers)
            {
                if (part == null || !part.enabled || !(part is SkinnedMeshRenderer || part is MeshRenderer))
                {
                    continue;
                }

                if (!found)
                {
                    bounds = part.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(part.bounds);
                }
            }

            renderers.Clear();
            return found;
        }

        /// <summary>Every frame: WalkSequence takes a new list only when a walk starts.</summary>
        private void ReadMonitors()
        {
            monitors.Clear();
            var count = UniWindowController.GetMonitorCount();
            for (var i = 0; i < count; i++)
            {
                // A monitor that cannot be read comes back as Rect.zero, which WalkSequence ignores.
                var rect = UniWindowController.GetMonitorRect(i);
                monitors.Add(new ScreenRect(rect.x, rect.y, rect.width, rect.height));
            }
        }

        /// <summary>
        /// Sideways only; y is written back as read. Whole pixels are moved and the rest is carried to the
        /// next frame, so the many small steps of the fade-in and fade-out are not lost to rounding.
        /// </summary>
        private void MoveWindow()
        {
            if (!sequence.IsWalking)
            {
                carryX = 0f;
                return;
            }

            if (sequence.StepX == 0f || controller == null)
            {
                return;
            }

            carryX += sequence.StepX;
            var whole = (float)Math.Truncate(carryX);
            if (whole == 0f)
            {
                return;
            }

            carryX -= whole;
            var position = controller.windowPosition;
            controller.windowPosition = new Vector2(position.x + whole, position.y);
        }

        private void ApplyPose()
        {
            var weight = sequence.Weight;
            if (!(weight > 0f) || clip == null || clipBroken || avatarPose == null)
            {
                return;
            }

            var length = clip.Clip.length;
            var time = sequence.Time % length;
            try
            {
                VrmaClipLoader.ApplyPose(avatarPose, clip, time, weight, Turn(), ref targetPose, ref sourcePose);
            }
            catch (Exception e)
            {
                // The body goes back to the idle below at once; walking stops from the next frame.
                clipBroken = true;
                Warn("apply", "cannot play walk motion '" + clip.Name + "': " + e.Message);
            }
        }

        /// <summary>
        /// About the avatar root's up axis, by TurnDegrees, so that the body's front faces the way it walks
        /// on the screen. The sign comes from where the camera's right lies in the root's space (with the
        /// camera in front of the avatar it is the root's -x), so a turned root or camera still faces right.
        /// HumanPose's body rotation and position are in the root's space.
        /// </summary>
        private Quaternion Turn()
        {
            var view = Camera.main;
            var side = 1f;
            if (view != null)
            {
                var right = avatarAnimator.transform.InverseTransformDirection(view.transform.right);
                side = right.x >= 0f ? 1f : -1f;
            }

            return Quaternion.AngleAxis(sequence.Direction * side * WalkSequence.TurnDegrees, Vector3.up);
        }

        private void SetAvatar(Animator found)
        {
            // A new avatar starts standing; a load still running for the previous one is dropped.
            generation++;
            sequence = null;
            carryX = 0f;
            DisposeClip();
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
                return;
            }

            sequence = new WalkSequence(random.NextDouble);
            Load(generation);
        }

        private void DisposeAvatarPose()
        {
            if (avatarPose != null)
            {
                avatarPose.Dispose();
                avatarPose = null;
            }
        }

        private void DisposeClip()
        {
            if (clip != null)
            {
                clip.Dispose();
                clip = null;
            }

            clipBroken = false;
        }

        private bool IsCurrent(int pass)
        {
            return this != null && pass == generation && sequence != null;
        }

        // async void: nothing awaits the load; every failure is caught and reported here.
        private async void Load(int pass)
        {
            try
            {
                var scan = await Task.Run(() => Scan(folder));
                if (!IsCurrent(pass))
                {
                    return;
                }

                if (scan.ListError != null)
                {
                    Warn("list", "cannot list the " + Label + " folder " + folder + ": " + scan.ListError);
                    return;
                }

                if (!scan.Exists)
                {
                    // No folder: walking is not wanted.
                    return;
                }

                problems.Begin();
                Report(scan);
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                VrmaClip first = null;
                foreach (var file in scan.Files)
                {
                    candidates.Add(file.Path);
                    if (first != null)
                    {
                        continue;
                    }

                    string failure;
                    if (FailedBefore(file, out failure))
                    {
                        // Importing the same bytes again would only leak again; the problem is still present.
                        problems.Add(LoadProblemKey(file), failure);
                        continue;
                    }

                    var name = Label + "/" + file.Name;
                    var loaded = await VrmaClipLoader.LoadAsync(this, name, file.Path, "VRMate walk " + name);
                    if (!IsCurrent(pass))
                    {
                        if (loaded.Clip != null)
                        {
                            loaded.Clip.Dispose();
                        }

                        return;
                    }

                    if (loaded.Clip == null)
                    {
                        var message = "cannot load walk motion '" + file.Name + "' from " + file.Path + ": " + loaded.Error;
                        RememberFailure(file, loaded.Imported, message);
                        problems.Add(LoadProblemKey(file), message);
                        continue;
                    }

                    failed.Remove(file.Path);
                    first = loaded.Clip;
                }

                ForgetFailuresExcept(candidates);
                foreach (var line in problems.End())
                {
                    Debug.LogWarning("[VRMate] " + line);
                }

                if (first == null)
                {
                    Warn("none", "no loadable walk motion in " + folder + "; the avatar does not walk");
                    return;
                }

                clip = first;
                clipBroken = false;
            }
            catch (Exception e)
            {
                Warn("load", "cannot read the walk motion folder: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string LoadProblemKey(ScannedFile file)
        {
            return "load:" + file.Path;
        }

        /// <summary>The import of this very version (same length and write time) failed before; message says how.</summary>
        private bool FailedBefore(ScannedFile file, out string message)
        {
            message = null;
            FileStamp stamp;
            if (!file.HasStamp || !failed.TryGetValue(file.Path, out stamp) || stamp.Length != file.Length || stamp.WriteTimeUtc != file.WriteTimeUtc)
            {
                return false;
            }

            message = stamp.Message;
            return true;
        }

        /// <summary>
        /// Only failures inside the import are remembered: they are the ones that leave objects behind.
        /// A file that could not be read fails before importing, leaks nothing, and is tried again.
        /// </summary>
        private void RememberFailure(ScannedFile file, bool imported, string message)
        {
            if (!imported || !file.HasStamp)
            {
                failed.Remove(file.Path);
                return;
            }

            if (failed.Count >= MaxFailedFiles && !failed.ContainsKey(file.Path))
            {
                // Unreachable while each load forgets files that are no longer candidates.
                return;
            }

            failed[file.Path] = new FileStamp(file.Length, file.WriteTimeUtc, message);
        }

        /// <summary>Removed or renamed files are no longer candidates; their failures need not be kept.</summary>
        private void ForgetFailuresExcept(HashSet<string> candidates)
        {
            if (failed.Count == 0)
            {
                return;
            }

            var gone = new List<string>();
            foreach (var path in failed.Keys)
            {
                if (!candidates.Contains(path))
                {
                    gone.Add(path);
                }
            }

            foreach (var path in gone)
            {
                failed.Remove(path);
            }
        }

        /// <summary>Thread pool: no Unity API. Lists the folder (files only, no subfolders) and stats the candidates.</summary>
        private static FolderScan Scan(string folder)
        {
            var scan = new FolderScan();
            var names = new List<string>();
            try
            {
                if (Directory.Exists(folder))
                {
                    scan.Exists = true;

                    // No search pattern: "*.vrma" would also match 8.3 short names whose long name has
                    // another extension. IdleFiles decides from the long names alone.
                    foreach (var entry in Directory.EnumerateFiles(folder))
                    {
                        names.Add(Path.GetFileName(entry));
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Removed while listing: the same as a missing folder.
                scan.Exists = false;
                names.Clear();
            }
            catch (Exception e)
            {
                names.Clear();
                scan.ListError = e.GetType().Name + ": " + e.Message;
            }

            scan.Selection = IdleFiles.Select(names);
            foreach (var name in scan.Selection.Names)
            {
                string path;
                if (!MotionFiles.TryGetPath(folder, name, out path))
                {
                    scan.Unusable.Add(name);
                    continue;
                }

                var file = new ScannedFile(name, path);
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        file.Stamp(info.Length, info.LastWriteTimeUtc);
                    }
                }
                catch (Exception)
                {
                    // Without a stamp the file is imported again; reading it reports any real problem.
                }

                scan.Files.Add(file);
            }

            return scan;
        }

        /// <summary>
        /// Names that are not motion names are only counted: written out they could forge or hide log
        /// lines. The other names are motion names (ASCII letters, digits, '_' and '-') and safe to show.
        /// </summary>
        private void Report(FolderScan scan)
        {
            var selection = scan.Selection;
            if (selection.Malformed.Count > 0)
            {
                problems.Add("malformed", "ignored " + selection.Malformed.Count + " files in " + folder + ": the name before .vrma must be 1 to " + CueNames.MaxMotionLength + " ASCII letters, digits, '_' or '-', and not a device name");
            }

            if (selection.Duplicates.Count > 0)
            {
                problems.Add("duplicate", "ignored " + selection.Duplicates.Count + " files in " + folder + " that differ from another only in letter case");
            }

            if (selection.OverLimit.Count > 0)
            {
                problems.Add("overlimit", "ignored " + selection.OverLimit.Count + " files in " + folder + ": only the first " + IdleFiles.MaxFiles + " files in name order are tried");
            }

            foreach (var name in scan.Unusable)
            {
                problems.Add("path:" + name, "walk motion '" + name + "' has no usable path in " + folder);
            }
        }

        private void Warn(string key, string message)
        {
            if (warnings.ShouldWarn(key))
            {
                Debug.LogWarning("[VRMate] " + message);
            }
        }

        private struct FileStamp
        {
            public FileStamp(long length, DateTime writeTimeUtc, string message)
            {
                Length = length;
                WriteTimeUtc = writeTimeUtc;
                Message = message;
            }

            public long Length { get; }

            public DateTime WriteTimeUtc { get; }

            /// <summary>The warning for the failure, repeated to FileProblemLog while the file is skipped.</summary>
            public string Message { get; }
        }

        private sealed class ScannedFile
        {
            public ScannedFile(string name, string path)
            {
                Name = name;
                Path = path;
            }

            public string Name { get; }

            public string Path { get; }

            /// <summary>False when the file could not be stat'ed; such a file is imported again.</summary>
            public bool HasStamp { get; private set; }

            public long Length { get; private set; }

            public DateTime WriteTimeUtc { get; private set; }

            public void Stamp(long length, DateTime writeTimeUtc)
            {
                HasStamp = true;
                Length = length;
                WriteTimeUtc = writeTimeUtc;
            }
        }

        private sealed class FolderScan
        {
            public readonly List<ScannedFile> Files = new List<ScannedFile>();
            public readonly List<string> Unusable = new List<string>();

            public bool Exists { get; set; }

            public IdleFileSelection Selection { get; set; }

            public string ListError { get; set; }
        }
    }
}
