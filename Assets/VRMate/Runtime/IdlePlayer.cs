using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace VRMate
{
    /// <summary>
    /// Plays the .vrma files in VRMate/Idle in place of upstream's idle on a VRM 1.0 avatar, and one of
    /// those in VRMate/Talk while the avatar talks (the next by name each time talking starts).
    /// IdleSequence decides which clips show, when and how much; this component reads the folders,
    /// gives each clip its motion name, keeps the clips loaded and copies their humanoid
    /// poses with HumanPoseHandler. The avatar's Animator is never touched: without a loadable idle clip,
    /// or outside idle, upstream's body shows as before. The folders are read again when the avatar
    /// changes and whenever IdleSequence asks (the order is about to wrap to the first clip).
    /// </summary>
    // After the Animator has written this frame's pose. Before MotionPlayer (-50): a tag's motion blends
    // from the pose written here, so it plays on top of the own idle.
    [DefaultExecutionOrder(-60)]
    [DisallowMultipleComponent]
    public class IdlePlayer : MonoBehaviour
    {
        private const string IdleLabel = "Idle";
        private const string TalkLabel = "Talk";
        private const int MaxShownNameLength = 80;

        // Both folders together never offer more candidates than this.
        private const int MaxFailedFiles = 2 * IdleFiles.MaxFiles;

        // Names quoted in the one warning about files past IdleFiles.MaxFiles.
        private const int MaxListedOverLimit = 5;

        // Every loaded clip, by its IdleSequence token. Only this component disposes them.
        private readonly Dictionary<long, VrmaClip> clips = new Dictionary<long, VrmaClip>();

        // The file each clip came from; an unchanged file is not imported again on the next reload.
        private readonly Dictionary<string, LoadedFile> files = new Dictionary<string, LoadedFile>(StringComparer.OrdinalIgnoreCase);

        // Files whose import failed, with the length and write time they had then. UniVRM leaves the Avatar
        // of a failed import behind, so the same version is not imported again; a replaced file is.
        // Kept across avatar changes: the file, not the avatar, decides whether the import fails.
        private readonly Dictionary<string, FileStamp> failed = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        // Left out of the latest lists: disposed once IdleSequence no longer uses them.
        private readonly HashSet<long> retiring = new HashSet<long>();

        // Reused by the reload in progress but not given to IdleSequence yet: must not be disposed meanwhile.
        private readonly HashSet<long> claimed = new HashSet<long>();
        private readonly List<long> disposable = new List<long>();

        // Only the few fixed kinds (idle state, avatar, reload, listing, over the limit): never runs out of keys.
        private readonly WarnOnce warnings = new WarnOnce();

        // Problems with the files present now, counted afresh on every reload (any number of bad files).
        private readonly FileProblemLog problems = new FileProblemLog();

        // Clips whose pose copy already failed; tied to the clip, so bounded by the loaded clips.
        private readonly HashSet<long> applyWarned = new HashSet<long>();
        private readonly AvatarWatch avatar = new AvatarWatch();
        private string idleFolder;
        private string talkFolder;
        private IdleSequence sequence;
        private long nextToken = 1;

        // Changes with the avatar (and on destroy); a reload that started for another value is dropped.
        private int generation;
        private bool reloading;
        private bool reloadWanted;

        private Animator avatarAnimator;
        private HumanPoseHandler avatarPose;
        private HumanPose targetPose;
        private HumanPose sourcePose;

        public static IdlePlayer Instance { get; private set; }

        /// <summary>
        /// The own idle is held at a boundary between clips, or there is no own idle to wait for (upstream's
        /// idle shows): a walk may start without cutting an idle clip short.
        /// </summary>
        public bool HeldAtBoundary
        {
            get { return sequence == null || !sequence.HasNormalClips || sequence.HeldAtBoundary; }
        }

        /// <summary>Cancel and disconnect: return to the normal order without waiting for the talk hold.</summary>
        public void EndTalking()
        {
            if (sequence != null)
            {
                sequence.EndTalking();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            var root = Path.Combine(Application.persistentDataPath, "VRMate");
            idleFolder = Path.Combine(root, IdleLabel);
            talkFolder = Path.Combine(root, TalkLabel);
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

            var speech = SpeechPlayer.Instance;
            sequence.SetTalking(speech != null && speech.IsTalking);

            // WalkPlayer runs after this component, so its request from the last frame applies here.
            var walk = WalkPlayer.Instance;
            sequence.SetHold(walk != null && walk.HoldIdle);
            sequence.Advance(Time.deltaTime, IsIdle());
            if (sequence.TakeReloadRequest())
            {
                RequestReload();
            }

            DisposeRetired();
            ApplyLayers();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            generation++;
            sequence = null;
            DisposeClips();
            DisposeAvatarPose();
        }

        private void ApplyLayers()
        {
            var count = sequence.LayerCount;
            for (var i = 0; i < count; i++)
            {
                var layer = sequence.GetLayer(i);
                VrmaClip clip;
                if (!(layer.Weight > 0f) || !clips.TryGetValue(layer.Token, out clip))
                {
                    continue;
                }

                try
                {
                    VrmaClipLoader.ApplyPose(avatarPose, clip, layer.Time, layer.Weight, ref targetPose, ref sourcePose);
                }
                catch (Exception e)
                {
                    // Skipping the rest of this frame leaves the body to the Animator until the next one.
                    WarnApply(clip, e);
                    return;
                }
            }
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

        private void SetAvatar(Animator found)
        {
            // The order and its clips start over with the new avatar; reloads still running are dropped.
            generation++;
            sequence = null;
            DisposeClips();
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

            sequence = new IdleSequence();
            RequestReload();
        }

        private void DisposeAvatarPose()
        {
            if (avatarPose != null)
            {
                avatarPose.Dispose();
                avatarPose = null;
            }
        }

        private void DisposeClips()
        {
            foreach (var clip in clips.Values)
            {
                clip.Dispose();
            }

            clips.Clear();
            files.Clear();
            retiring.Clear();
            claimed.Clear();
            applyWarned.Clear();
        }

        private void DisposeRetired()
        {
            if (retiring.Count == 0 || sequence == null)
            {
                return;
            }

            disposable.Clear();
            foreach (var token in retiring)
            {
                if (!claimed.Contains(token) && !sequence.IsInUse(token))
                {
                    disposable.Add(token);
                }
            }

            foreach (var token in disposable)
            {
                retiring.Remove(token);
                DisposeClip(token);
            }
        }

        private void DisposeClip(long token)
        {
            VrmaClip clip;
            if (!clips.TryGetValue(token, out clip))
            {
                return;
            }

            clips.Remove(token);
            applyWarned.Remove(token);
            clip.Dispose();

            string stale = null;
            foreach (var entry in files)
            {
                if (entry.Value.Token == token)
                {
                    stale = entry.Key;
                    break;
                }
            }

            if (stale != null)
            {
                files.Remove(stale);
            }
        }

        /// <summary>A request during a reload runs one more reload after it, so the newest files are always read.</summary>
        private void RequestReload()
        {
            reloadWanted = true;
            if (!reloading)
            {
                Reload();
            }
        }

        // async void: nothing awaits the reload; every failure is caught and reported here.
        private async void Reload()
        {
            reloading = true;
            try
            {
                while (reloadWanted)
                {
                    reloadWanted = false;
                    if (!await ReloadOnce(generation))
                    {
                        continue;
                    }

                    foreach (var line in problems.End())
                    {
                        Debug.LogWarning("[VRMate] " + line);
                    }
                }
            }
            catch (Exception e)
            {
                claimed.Clear();
                Warn("reload", "cannot read the idle motion folders: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                reloading = false;
            }
        }

        /// <summary>
        /// One pass over both folders. False when it was cut short (the avatar changed or this was
        /// destroyed): its problems were not all counted, so they must not replace the ones logged before.
        /// </summary>
        private async Task<bool> ReloadOnce(int pass)
        {
            problems.Begin();
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normal = await LoadFolder(idleFolder, IdleLabel, pass, candidates);
            if (normal == null)
            {
                return false;
            }

            var talk = await LoadFolder(talkFolder, TalkLabel, pass, candidates);
            if (talk == null)
            {
                return false;
            }

            ForgetFailuresExcept(candidates);
            UseClips(normal, talk);
            return true;
        }

        private bool IsCurrent(int pass)
        {
            return this != null && pass == generation && sequence != null;
        }

        /// <summary>
        /// The folder's clips in name order, loading one file at a time so that the main thread is never
        /// held for more than one import. Null when the avatar changed (or this was destroyed) meanwhile.
        /// </summary>
        private async Task<List<IdleClip>> LoadFolder(string folder, string label, int pass, HashSet<string> candidates)
        {
            var scan = await Task.Run(() => Scan(folder));
            if (!IsCurrent(pass))
            {
                claimed.Clear();
                return null;
            }

            Report(label, folder, scan);
            var result = new List<IdleClip>();
            foreach (var file in scan.Files)
            {
                candidates.Add(file.Path);
                string failure;
                if (FailedBefore(file, out failure))
                {
                    // Importing the same bytes again would only leak again. The problem is still present,
                    // so it is counted: otherwise it would look gone and might never be named.
                    problems.Add(LoadProblemKey(file), failure);
                    continue;
                }

                long token;
                if (TryReuse(file, out token))
                {
                    claimed.Add(token);
                    result.Add(new IdleClip(token, clips[token].Clip.length, file.Name));
                    continue;
                }

                var name = label + "/" + file.Name;
                var loaded = await VrmaClipLoader.LoadAsync(this, name, file.Path, "VRMate idle " + name);
                if (!IsCurrent(pass))
                {
                    if (loaded.Clip != null)
                    {
                        loaded.Clip.Dispose();
                    }

                    claimed.Clear();
                    return null;
                }

                if (loaded.Clip == null)
                {
                    var message = "cannot load idle motion '" + file.Name + "' from " + file.Path + ": " + loaded.Error;
                    RememberFailure(file, loaded.Imported, message);
                    problems.Add(LoadProblemKey(file), message);
                    continue;
                }

                failed.Remove(file.Path);
                token = nextToken++;
                loaded.Clip.Token = token;
                clips[token] = loaded.Clip;
                claimed.Add(token);
                if (file.HasStamp)
                {
                    files[file.Path] = new LoadedFile(token, file.Length, file.WriteTimeUtc);
                }
                else
                {
                    files.Remove(file.Path);
                }

                result.Add(new IdleClip(token, loaded.Clip.Clip.length, file.Name));
            }

            return result;
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
        /// A file that could not be stat'ed or read (missing, locked, too large, empty) fails before
        /// importing, leaks nothing, and is tried again on the next reload, as a lock may be gone by then.
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
                // Unreachable while each reload forgets files that are no longer candidates; the cap only
                // keeps a mistake there from growing the table without bound.
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

        /// <summary>The same length and write time as when it was loaded: the loaded clip is still the file.</summary>
        private bool TryReuse(ScannedFile file, out long token)
        {
            token = 0;
            LoadedFile loaded;
            if (!file.HasStamp || !files.TryGetValue(file.Path, out loaded))
            {
                return false;
            }

            if (loaded.Length != file.Length || loaded.WriteTimeUtc != file.WriteTimeUtc || !clips.ContainsKey(loaded.Token))
            {
                return false;
            }

            token = loaded.Token;
            return true;
        }

        private void UseClips(List<IdleClip> normal, List<IdleClip> talk)
        {
            sequence.SetClips(normal, talk);

            var given = new HashSet<long>();
            foreach (var clip in normal)
            {
                given.Add(clip.Token);
            }

            foreach (var clip in talk)
            {
                given.Add(clip.Token);
            }

            claimed.Clear();
            foreach (var token in clips.Keys)
            {
                if (given.Contains(token))
                {
                    retiring.Remove(token);
                }
                else
                {
                    retiring.Add(token);
                }
            }

            DisposeRetired();
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

        private void Report(string label, string folder, FolderScan scan)
        {
            if (scan.ListError != null)
            {
                Warn("list:" + folder, "cannot list the " + label + " folder " + folder + ": " + scan.ListError);
            }

            var overLimit = scan.Selection.OverLimit;
            if (overLimit.Count > 0)
            {
                // One line per folder: hundreds of extra files must not flood the log or use up the keys.
                var listed = new List<string>();
                for (var i = 0; i < overLimit.Count && i < MaxListedOverLimit; i++)
                {
                    listed.Add("'" + overLimit[i] + "'");
                }

                var more = overLimit.Count > listed.Count ? ", ..." : string.Empty;
                Warn("overlimit:" + folder, "ignored " + overLimit.Count + " files in " + folder + ": only the first " + IdleFiles.MaxFiles + " files in name order are played (ignored: " + string.Join(", ", listed.ToArray()) + more + ")");
            }

            foreach (var fileName in scan.Selection.Malformed)
            {
                problems.Add("malformed:" + folder + "|" + fileName, "ignored '" + Shown(fileName) + "' in " + folder + ": the name before .vrma must be 1 to " + CueNames.MaxMotionLength + " ASCII letters, digits, '_' or '-', and not a device name");
            }

            foreach (var fileName in scan.Selection.Duplicates)
            {
                problems.Add("duplicate:" + folder + "|" + fileName, "ignored '" + Shown(fileName) + "' in " + folder + ": another file differs from it only in letter case");
            }

            foreach (var name in scan.Unusable)
            {
                problems.Add("path:" + folder + "|" + name, "idle motion '" + name + "' has no usable path in " + folder);
            }
        }

        /// <summary>
        /// A file name made safe for one log line: control, line-break and direction characters are
        /// escaped and long names are cut, so a name cannot forge or hide other log lines.
        /// </summary>
        private static string Shown(string fileName)
        {
            var builder = new StringBuilder();
            foreach (var c in fileName)
            {
                if (builder.Length >= MaxShownNameLength)
                {
                    builder.Append("...");
                    break;
                }

                if (char.IsControl(c) || IsInvisibleFormat(c))
                {
                    builder.Append("\\u").Append(((int)c).ToString("X4"));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        /// <summary>Line and paragraph separators, zero-width and direction marks, bidi overrides and isolates, BOM.</summary>
        private static bool IsInvisibleFormat(char c)
        {
            int code = c;
            return (code >= 0x200B && code <= 0x200F) || code == 0x2028 || code == 0x2029 || (code >= 0x202A && code <= 0x202E) || (code >= 0x2066 && code <= 0x2069) || code == 0xFEFF;
        }

        private void Warn(string key, string message)
        {
            if (warnings.ShouldWarn(key))
            {
                Debug.LogWarning("[VRMate] " + message);
            }
        }

        /// <summary>Once while the clip stays loaded, however many frames it keeps failing.</summary>
        private void WarnApply(VrmaClip clip, Exception e)
        {
            if (applyWarned.Add(clip.Token))
            {
                Debug.LogWarning("[VRMate] cannot play idle motion '" + clip.Name + "': " + e.Message);
            }
        }

        private struct LoadedFile
        {
            public LoadedFile(long token, long length, DateTime writeTimeUtc)
            {
                Token = token;
                Length = length;
                WriteTimeUtc = writeTimeUtc;
            }

            public long Token { get; }

            public long Length { get; }

            public DateTime WriteTimeUtc { get; }
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

            /// <summary>False when the file could not be stat'ed; such a file is never taken from the cache.</summary>
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

            public IdleFileSelection Selection { get; set; }

            public string ListError { get; set; }
        }
    }
}
