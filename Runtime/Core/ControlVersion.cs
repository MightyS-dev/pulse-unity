using System;

namespace Mightys.Pulse
{
    // App Control version comparison. Spaces and a v or V prefix are removed, everything after the first + is dropped,
    // the part after the first - is a pre-release, the main part is dot separated numbers of any length (leading zeros
    // ignored, a missing part is 0) and any part that is not a number makes the version not comparable.
    // Pre-releases follow semver precedence. Numbers compare by digits, never through a fixed width integer.
    internal static class ControlVersion
    {
        /// <summary>-1, 0 or 1, or null when either version cannot be compared.</summary>
        internal static int? Compare(string a, string b)
        {
            var left = Parsed.From(a);
            var right = Parsed.From(b);
            if (left == null || right == null) return null;
            var length = Math.Max(left.Main.Length, right.Main.Length);
            for (var i = 0; i < length; i++)
            {
                var order = Number(i < left.Main.Length ? left.Main[i] : "0", i < right.Main.Length ? right.Main[i] : "0");
                if (order != 0) return order;
            }
            // A version without a pre-release is above the same version with one.
            if (left.Pre == null || right.Pre == null)
                return left.Pre == null && right.Pre == null ? 0 : left.Pre == null ? 1 : -1;
            var count = Math.Max(left.Pre.Length, right.Pre.Length);
            for (var i = 0; i < count; i++)
            {
                if (i >= left.Pre.Length) return -1;
                if (i >= right.Pre.Length) return 1;
                var x = left.Pre[i];
                var y = right.Pre[i];
                var xNumber = IsDigits(x);
                var yNumber = IsDigits(y);
                var order = xNumber && yNumber ? Number(x, y) : xNumber ? -1 : yNumber ? 1 : Math.Sign(string.CompareOrdinal(x, y));
                if (order != 0) return order;
            }
            return 0;
        }

        /// <summary>True when the version can be compared.</summary>
        internal static bool IsComparable(string version) => Parsed.From(version) != null;

        private sealed class Parsed
        {
            internal string[] Main, Pre;

            internal static Parsed From(string text)
            {
                if (text == null) return null;
                var value = text.Trim();
                if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V')) value = value.Substring(1);
                var plus = value.IndexOf('+');
                if (plus >= 0) value = value.Substring(0, plus);
                string[] pre = null;
                var dash = value.IndexOf('-');
                if (dash >= 0)
                {
                    pre = value.Substring(dash + 1).Split('.');
                    value = value.Substring(0, dash);
                    foreach (var part in pre) if (!IsIdentifier(part)) return null;
                }
                if (value.Length == 0) return null;
                var main = value.Split('.');
                foreach (var part in main) if (!IsDigits(part)) return null;
                return new Parsed { Main = main, Pre = pre };
            }
        }

        private static bool IsDigits(string part)
        {
            if (part.Length == 0) return false;
            foreach (var c in part) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsIdentifier(string part)
        {
            if (part.Length == 0) return false;
            foreach (var c in part)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-')) return false;
            return true;
        }

        // Numeric order of two digit strings of any length.
        private static int Number(string a, string b)
        {
            a = a.TrimStart('0');
            b = b.TrimStart('0');
            if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
            return Math.Sign(string.CompareOrdinal(a, b));
        }
    }

    // The public id of an app's rules: the first 32 hex characters (lower case) of SHA-256 over the UTF-8 bytes of the
    // app key exactly as it was given to the SDK.
    internal static class ControlIds
    {
        internal static string ForKey(string key) => SignalJson.Hash(key ?? "").Substring(0, 32);
        internal static string CacheName(string key) => "control-" + SignalJson.Hash(key ?? "").Substring(0, 16);
    }
}
