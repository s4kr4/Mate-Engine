namespace VRMate
{
    /// <summary>
    /// The names the brain may use for expressions and motions. Both checks are ordinal and never
    /// normalize: a name that only resembles an accepted one (other case, full-width, padded) is refused.
    /// </summary>
    public static class CueNames
    {
        public const string Neutral = "neutral";

        /// <summary>Motion names are file names; longer ones are never produced by the brain's settings.</summary>
        public const int MaxMotionLength = 64;

        /// <summary>The five VRM 1.0 emotion presets and "neutral" (meaning: give the face back to upstream).</summary>
        public static bool IsExpression(string name)
        {
            switch (name)
            {
                case "happy":
                case "angry":
                case "sad":
                case "relaxed":
                case "surprised":
                case Neutral:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 1 to 64 of ASCII letters, digits, '_' and '-', and not a Windows device name.
        /// The name becomes "&lt;name&gt;.vrma" directly inside the motion folder, so the allowed set is a
        /// whitelist: no separator, dot, colon, or non-ASCII letter (char.IsLetterOrDigit would accept
        /// full-width and other scripts) can reach the path.
        /// </summary>
        public static bool IsMotion(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxMotionLength)
            {
                return false;
            }

            foreach (var c in name)
            {
                var allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return !IsReservedDeviceName(name);
        }

        // "NUL.vrma" opens the NUL device on Windows whatever the folder, so these stems are refused
        // in every letter case. Only exact stems are reserved; "COM10" and "CONX" are ordinary files.
        private static bool IsReservedDeviceName(string name)
        {
            if (name.Length == 3)
            {
                return EqualsAscii(name, "CON") || EqualsAscii(name, "PRN") || EqualsAscii(name, "AUX") || EqualsAscii(name, "NUL");
            }

            if (name.Length == 4 && name[3] >= '0' && name[3] <= '9')
            {
                var stem = name.Substring(0, 3);
                return EqualsAscii(stem, "COM") || EqualsAscii(stem, "LPT");
            }

            return false;
        }

        /// <summary>Case-insensitive for ASCII letters only, so that culture rules (Turkish i) never apply.</summary>
        private static bool EqualsAscii(string value, string upper)
        {
            if (value.Length != upper.Length)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c >= 'a' && c <= 'z')
                {
                    c = (char)(c - ('a' - 'A'));
                }

                if (c != upper[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
