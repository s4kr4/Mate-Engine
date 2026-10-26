using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRMate
{
    /// <summary>The outcome of reading brain.json.</summary>
    public enum BrainLaunchLoad
    {
        Missing,
        Invalid,
        Valid,
    }

    /// <summary>
    /// How to start the brain process, read from brain.json: a UTF-8 JSON object (a leading BOM is
    /// skipped) whose "command" and "workingDirectory" are absolute paths and whose optional "arguments"
    /// is a string kept exactly as written. Other keys are ignored. The file is written by hand, so every
    /// mistake in its shape is reported as a one-line problem that names the key but never quotes its
    /// value. Whether the paths exist is not checked here; starting the process finds that out.
    /// </summary>
    public sealed class BrainLaunchSettings
    {
        public const int MaxFileBytes = 65536;

        // Deep nesting is never legitimate here; the reader refuses it instead of recursing.
        private const int MaxDepth = 64;

        private const string CommandKey = "command";
        private const string ArgumentsKey = "arguments";
        private const string WorkingDirectoryKey = "workingDirectory";

        private static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
        };

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

        private BrainLaunchSettings(string command, string arguments, string workingDirectory)
        {
            Command = command;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
        }

        /// <summary>An absolute path, as written.</summary>
        public string Command { get; }

        /// <summary>As written; empty when the key is absent or null.</summary>
        public string Arguments { get; }

        /// <summary>An absolute path, as written.</summary>
        public string WorkingDirectory { get; }

        /// <summary>
        /// Missing (with a null problem) when neither a file nor a folder has that name; Invalid when the
        /// path is null or empty, names a folder, cannot be read, is larger than MaxFileBytes (only
        /// MaxFileBytes + 1 bytes are read to tell) or fails TryParse; otherwise Valid. Never throws.
        /// </summary>
        public static BrainLaunchLoad Load(string path, out BrainLaunchSettings settings, out string problem)
        {
            settings = null;
            problem = null;
            if (string.IsNullOrEmpty(path))
            {
                problem = "no path to brain.json was given";
                return BrainLaunchLoad.Invalid;
            }

            byte[] content;
            try
            {
                if (!File.Exists(path))
                {
                    if (Directory.Exists(path))
                    {
                        problem = "brain.json is a folder, not a file";
                        return BrainLaunchLoad.Invalid;
                    }

                    return BrainLaunchLoad.Missing;
                }

                content = ReadAtMost(path, MaxFileBytes + 1);
            }
            catch (Exception e)
            {
                // Only the type: the message of a file error can hold the path.
                problem = "brain.json could not be read (" + e.GetType().Name + ")";
                return BrainLaunchLoad.Invalid;
            }

            return TryParse(content, out settings, out problem) ? BrainLaunchLoad.Valid : BrainLaunchLoad.Invalid;
        }

        /// <summary>
        /// Checks the bytes of brain.json. On success the problem is null; on failure the settings are
        /// null and the problem is one line that names the key at fault, without its value. Never throws.
        /// </summary>
        public static bool TryParse(byte[] content, out BrainLaunchSettings settings, out string problem)
        {
            settings = null;
            problem = null;
            try
            {
                return TryParseCore(content, out settings, out problem);
            }
            catch (Exception e)
            {
                settings = null;
                problem = "brain.json could not be checked (" + e.GetType().Name + ")";
                return false;
            }
        }

        private static bool TryParseCore(byte[] content, out BrainLaunchSettings settings, out string problem)
        {
            settings = null;
            problem = null;
            if (content == null)
            {
                problem = "brain.json has no content";
                return false;
            }

            // The limit counts the BOM too, the same as the size of the file.
            if (content.Length > MaxFileBytes)
            {
                problem = "brain.json is larger than " + MaxFileBytes + " bytes";
                return false;
            }

            string text;
            if (!TryDecode(content, out text))
            {
                problem = "brain.json is not valid UTF-8";
                return false;
            }

            // JsonTextReader treats NUL as the end of its buffer and may skip it, so a NUL anywhere
            // (a stray byte or UTF-16 without a BOM) is refused here instead of being half read.
            if (text.IndexOf('\0') >= 0)
            {
                problem = "brain.json contains a NUL character";
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                problem = "brain.json is empty";
                return false;
            }

            JToken root;
            try
            {
                root = ReadSingleValue(text);
            }
            catch (Exception e)
            {
                // Only the type: the reader's message can quote the text around the error.
                problem = "brain.json is not valid JSON (" + e.GetType().Name + ")";
                return false;
            }

            if (root == null)
            {
                problem = "brain.json has content after the JSON object";
                return false;
            }

            var obj = root as JObject;
            if (obj == null)
            {
                problem = "brain.json is not a JSON object (" + root.Type + ")";
                return false;
            }

            string command;
            if (!TryReadPath(obj, CommandKey, out command, out problem))
            {
                return false;
            }

            string workingDirectory;
            if (!TryReadPath(obj, WorkingDirectoryKey, out workingDirectory, out problem))
            {
                return false;
            }

            string arguments;
            if (!TryReadArguments(obj, out arguments, out problem))
            {
                return false;
            }

            settings = new BrainLaunchSettings(command, arguments, workingDirectory);
            return true;
        }

        /// <summary>Reads up to limit bytes; a longer file is cut there, so its size shows as limit.</summary>
        private static byte[] ReadAtMost(string path, int limit)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[limit];
                var length = 0;
                while (length < limit)
                {
                    var read = stream.Read(buffer, length, limit - length);
                    if (read <= 0)
                    {
                        break;
                    }

                    length += read;
                }

                if (length == limit)
                {
                    return buffer;
                }

                var content = new byte[length];
                Buffer.BlockCopy(buffer, 0, content, 0, length);
                return content;
            }
        }

        private static bool TryDecode(byte[] content, out string text)
        {
            text = null;
            var start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            try
            {
                text = StrictUtf8.GetString(content, start, content.Length - start);
                return true;
            }
            catch (ArgumentException)
            {
                // DecoderFallbackException derives from ArgumentException.
                return false;
            }
        }

        /// <summary>Returns null when anything other than whitespace or comments follows the value.</summary>
        private static JToken ReadSingleValue(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                // Keep date-like strings as strings and read non-finite literals as doubles.
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = MaxDepth;

                var token = JToken.ReadFrom(reader, LoadSettings);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                    {
                        return null;
                    }
                }

                return token;
            }
        }

        private static bool TryReadPath(JObject obj, string key, out string path, out string problem)
        {
            path = null;
            problem = null;
            JToken token;
            if (!obj.TryGetValue(key, out token))
            {
                problem = KeyInFile(key) + " is missing";
                return false;
            }

            if (token.Type == JTokenType.Null)
            {
                problem = KeyInFile(key) + " is null";
                return false;
            }

            if (token.Type != JTokenType.String)
            {
                problem = KeyInFile(key) + " is not a string (" + token.Type + ")";
                return false;
            }

            var value = (string)token;
            if (string.IsNullOrWhiteSpace(value))
            {
                problem = KeyInFile(key) + " is empty";
                return false;
            }

            if (!IsAbsolutePath(value))
            {
                problem = KeyInFile(key) + " is not an absolute path";
                return false;
            }

            path = value;
            return true;
        }

        private static bool TryReadArguments(JObject obj, out string arguments, out string problem)
        {
            arguments = string.Empty;
            problem = null;
            JToken token;
            if (!obj.TryGetValue(ArgumentsKey, out token) || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.String)
            {
                problem = KeyInFile(ArgumentsKey) + " is not a string (" + token.Type + ")";
                return false;
            }

            arguments = (string)token;
            return true;
        }

        /// <summary>
        /// "X:\" or "X:/" with X in A-Z or a-z, or "\\server..." with a server name. The value is not
        /// trimmed: leading or trailing whitespace, an invalid path character or any control character
        /// makes it not absolute.
        /// </summary>
        private static bool IsAbsolutePath(string value)
        {
            if (value.Length < 3 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            {
                return false;
            }

            if (value.IndexOfAny(InvalidPathChars) >= 0)
            {
                return false;
            }

            foreach (var c in value)
            {
                // The set GetInvalidPathChars returns varies by runtime; control characters are never valid.
                if (c < 0x20)
                {
                    return false;
                }
            }

            if (IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
            {
                return true;
            }

            return value[0] == '\\' && value[1] == '\\' && value[2] != '\\' && value[2] != '/';
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        private static string KeyInFile(string key)
        {
            return "\"" + key + "\" in brain.json";
        }
    }
}
