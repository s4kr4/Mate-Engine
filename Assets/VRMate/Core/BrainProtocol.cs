using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRMate
{
    /// <summary>
    /// Reads the brain's messages and builds the avatar's replies (JSON text frames).
    /// </summary>
    public static class BrainProtocol
    {
        /// <summary>The brain closes the connection on messages larger than this.</summary>
        public const int MaxOutgoingBytes = 65536;

        // Deep nesting is never legitimate here; the reader refuses it instead of recursing.
        private const int MaxDepth = 64;
        private const int ReasonValueLength = 40;

        private static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
        };

        private static readonly Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Never throws and never returns null.</summary>
        public static BrainMessage Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new InvalidMessage(null, "empty message");
            }

            JToken root;
            try
            {
                root = ReadSingleValue(json);
            }
            catch (Exception e)
            {
                return new InvalidMessage(null, "broken JSON: " + e.GetType().Name);
            }

            if (root == null)
            {
                return new InvalidMessage(null, "broken JSON: content after the value");
            }

            try
            {
                return Interpret(root);
            }
            catch (Exception e)
            {
                return new InvalidMessage(null, "unreadable message: " + e.GetType().Name);
            }
        }

        public static string Started(long id)
        {
            return "{\"type\":\"started\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}";
        }

        /// <summary>The "error" key is present whenever error is not null, even when it is empty.</summary>
        public static string Played(long id, string error = null)
        {
            var builder = new StringBuilder("{\"type\":\"played\",\"id\":");
            builder.Append(id.ToString(CultureInfo.InvariantCulture));
            if (error != null)
            {
                builder.Append(",\"error\":").Append(JsonConvert.ToString(error));
            }

            return builder.Append('}').ToString();
        }

        /// <summary>False when text is null or the JSON would exceed MaxOutgoingBytes in UTF-8.</summary>
        public static bool TryBuildInput(string text, out string json)
        {
            json = null;
            if (text == null)
            {
                return false;
            }

            var built = "{\"type\":\"input\",\"text\":" + JsonConvert.ToString(text) + "}";
            if (Utf8.GetByteCount(built) > MaxOutgoingBytes)
            {
                return false;
            }

            json = built;
            return true;
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

        private static BrainMessage Interpret(JToken root)
        {
            var obj = root as JObject;
            if (obj == null)
            {
                return new InvalidMessage(null, "not a JSON object: " + root.Type);
            }

            JToken typeToken;
            if (!obj.TryGetValue("type", out typeToken) || typeToken.Type != JTokenType.String)
            {
                return new InvalidMessage(null, "missing or non-string type");
            }

            var type = (string)typeToken;
            switch (type)
            {
                case "say":
                    return InterpretSay(obj);
                case "cancel":
                    return new CancelMessage();
                case "expression":
                    return InterpretCueMessage(obj, CueKind.Expression);
                case "motion":
                    return InterpretCueMessage(obj, CueKind.Motion);
                default:
                    return new IgnoredMessage("unknown type '" + Shorten(type) + "'");
            }
        }

        private static BrainMessage InterpretSay(JObject obj)
        {
            long id;
            if (!TryGetLong(obj, "id", out id))
            {
                return new InvalidMessage(null, "say: id is missing or not a 64-bit integer");
            }

            long turn;
            if (!TryGetLong(obj, "turn", out turn))
            {
                return new InvalidMessage(id, "say: turn is missing or not a 64-bit integer");
            }

            JToken textToken;
            if (!obj.TryGetValue("text", out textToken) || textToken.Type != JTokenType.String)
            {
                return new InvalidMessage(id, "say: text is missing or not a string");
            }

            var hasAudio = false;
            string audio = null;
            JToken audioToken;
            if (obj.TryGetValue("audio", out audioToken) && audioToken.Type != JTokenType.Null)
            {
                // A non-string audio still counts as audio so that the say reports a playback error.
                hasAudio = true;
                if (audioToken.Type == JTokenType.String)
                {
                    audio = (string)audioToken;
                }
            }

            JToken visemesToken;
            obj.TryGetValue("visemes", out visemesToken);

            // An unusable cue is dropped on its own; the sentence is still spoken and shown.
            var problems = new List<string>(2);
            var expression = ReadSayCue(obj, CueKind.Expression, problems);
            var motion = ReadSayCue(obj, CueKind.Motion, problems);

            return new SayMessage(id, turn, (string)textToken, hasAudio, audio, ReadVisemes(visemesToken), expression, motion, problems);
        }

        private enum CueKind
        {
            Expression,
            Motion,
        }

        /// <summary>Absent and null both mean "no cue" in a say.</summary>
        private static string ReadSayCue(JObject obj, CueKind kind, List<string> problems)
        {
            var key = kind == CueKind.Expression ? "expression" : "motion";
            JToken token;
            if (!obj.TryGetValue(key, out token) || token.Type == JTokenType.Null)
            {
                return null;
            }

            string name;
            string problem;
            if (!TryReadCueName(token, kind, "say: " + key, out name, out problem))
            {
                problems.Add(problem);
                return null;
            }

            return name;
        }

        private static BrainMessage InterpretCueMessage(JObject obj, CueKind kind)
        {
            var where = kind == CueKind.Expression ? "expression: name" : "motion: name";
            JToken token;
            if (!obj.TryGetValue("name", out token) || token.Type == JTokenType.Null)
            {
                return new InvalidMessage(null, where + " is missing", isCue: true);
            }

            string name;
            string problem;
            if (!TryReadCueName(token, kind, where, out name, out problem))
            {
                return new InvalidMessage(null, problem, isCue: true);
            }

            return kind == CueKind.Expression ? (BrainMessage)new ExpressionMessage(name) : new MotionMessage(name);
        }

        /// <summary>
        /// The problem names the kind and holds at most ReasonValueLength characters of the value, and
        /// only the JSON type of a non-string (never its text), so its length is bounded whatever the input.
        /// </summary>
        private static bool TryReadCueName(JToken token, CueKind kind, string where, out string name, out string problem)
        {
            name = null;
            problem = null;
            if (token.Type != JTokenType.String)
            {
                problem = where + " is not a string (" + token.Type + ")";
                return false;
            }

            var value = (string)token;
            if (kind == CueKind.Expression ? !CueNames.IsExpression(value) : !CueNames.IsMotion(value))
            {
                var expected = kind == CueKind.Expression ? " is not a known expression '" : " is not a well-formed motion name '";
                problem = where + expected + Shorten(value) + "'";
                return false;
            }

            name = value;
            return true;
        }

        /// <summary>Returns null unless every key is well-formed and times strictly increase from a finite non-negative start.</summary>
        private static IReadOnlyList<VisemeKey> ReadVisemes(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count == 0)
            {
                return null;
            }

            var keys = new List<VisemeKey>(array.Count);
            foreach (var item in array)
            {
                var element = item as JObject;
                if (element == null)
                {
                    return null;
                }

                JToken timeToken;
                double time;
                if (!element.TryGetValue("t", out timeToken) || !TryGetFiniteDouble(timeToken, out time) || time < 0)
                {
                    return null;
                }

                if (keys.Count > 0 && !(time > keys[keys.Count - 1].Time))
                {
                    return null;
                }

                JToken shapeToken;
                if (!element.TryGetValue("v", out shapeToken) || shapeToken.Type != JTokenType.String)
                {
                    return null;
                }

                keys.Add(new VisemeKey(time, ToShape((string)shapeToken)));
            }

            return keys.AsReadOnly();
        }

        private static VisemeShape ToShape(string name)
        {
            switch (name)
            {
                case "aa":
                    return VisemeShape.Aa;
                case "ih":
                    return VisemeShape.Ih;
                case "ou":
                    return VisemeShape.Ou;
                case "ee":
                    return VisemeShape.Ee;
                case "oh":
                    return VisemeShape.Oh;
                default:
                    return VisemeShape.Sil;
            }
        }

        private static bool TryGetLong(JObject obj, string name, out long value)
        {
            value = 0;
            JToken token;
            if (!obj.TryGetValue(name, out token) || token.Type != JTokenType.Integer)
            {
                return false;
            }

            // Integers beyond the long range are read as BigInteger.
            var raw = ((JValue)token).Value;
            if (!(raw is long))
            {
                return false;
            }

            value = (long)raw;
            return true;
        }

        private static bool TryGetFiniteDouble(JToken token, out double value)
        {
            value = 0;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                return false;
            }

            var raw = ((JValue)token).Value;
            if (raw is double)
            {
                value = (double)raw;
            }
            else if (raw is long)
            {
                value = (long)raw;
            }
            else
            {
                var digits = Convert.ToString(raw, CultureInfo.InvariantCulture);
                if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return false;
                }
            }

            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string Shorten(string value)
        {
            return value.Length <= ReasonValueLength ? value : value.Substring(0, ReasonValueLength) + "...";
        }
    }
}
