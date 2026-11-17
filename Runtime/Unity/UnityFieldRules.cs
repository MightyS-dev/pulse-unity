using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mightys.Pulse
{
    // Pure mapping from raw Unity and OS values to the 0.9.3 system field formats. No UnityEngine types here, so the rules
    // are covered by the .NET tests. Every function returns null when the platform gave nothing usable: the key is then
    // left out, never invented.
    internal static class UnityFieldRules
    {
        internal static string Bool(bool value) => value ? "true" : "false";

        internal static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

        // Unity DeviceType names: Handheld, Console, Desktop, Unknown. A handheld with a diagonal of 7 inches or more is a
        // tablet (the largest phones are below 7, the smallest iPad mini is 7.9). Without a usable diagonal a handheld is
        // reported as a phone only on a phone OS.
        internal static string DeviceType(string unityDeviceType, string osName, double diagonalInches, bool androidTv)
        {
            if (androidTv || osName == "tvOS") return "tv";
            switch (unityDeviceType)
            {
                case "Handheld":
                    if (diagonalInches >= 7) return "tablet";
                    if (diagonalInches > 0) return "phone";
                    return osName == "iOS" || osName == "Android" ? "phone" : null;
                case "Desktop": return "desktop";
                default: return null;
            }
        }

        internal static double Diagonal(int widthPx, int heightPx, float dpi)
        {
            if (widthPx <= 0 || heightPx <= 0 || dpi <= 0 || float.IsNaN(dpi) || float.IsInfinity(dpi)) return 0;
            return Math.Sqrt((double)widthPx * widthPx + (double)heightPx * heightPx) / dpi;
        }

        // System.Runtime.InteropServices.Architecture names.
        internal static string Architecture(string processArchitecture)
        {
            switch (processArchitecture)
            {
                case "X64": return "x86_64";
                case "X86": return "x86";
                case "Arm64": return "arm64";
                case "Arm": return "armv7";
                case null: case "": return null;
                default: return processArchitecture.ToLowerInvariant();
            }
        }

        // Exactly "Unity <version>" with one space; a version with spaces is not a version.
        internal static string RuntimeVersion(string unityVersion)
        {
            if (string.IsNullOrEmpty(unityVersion)) return null;
            foreach (var c in unityVersion) if (char.IsWhiteSpace(c)) return null;
            return "Unity " + unityVersion;
        }

        // Android installer package (Application.installerName) or, on iOS, whether the receipt is a sandbox receipt.
        internal static string InstallSource(string osName, string installerName, bool debugBuild, bool? sandboxReceipt)
        {
            if (debugBuild) return "debug";
            if (osName == "Android")
            {
                switch (installerName)
                {
                    case null: case "": return "sideload";
                    case "com.android.vending": return "playstore";
                    case "com.sec.android.app.samsungapps": return "galaxystore";
                    case "com.amazon.venezia": return "amazon";
                    default: return "other";
                }
            }
            if (osName == "iOS" && sandboxReceipt.HasValue) return sandboxReceipt.Value ? "testflight" : "appstore";
            return null;
        }

        internal static bool IsAndroidEmulator(string fingerprint, string hardware, string product)
        {
            fingerprint = fingerprint ?? ""; hardware = hardware ?? ""; product = product ?? "";
            return fingerprint.StartsWith("generic", StringComparison.Ordinal) || fingerprint.Contains("emulator") ||
                hardware == "goldfish" || hardware == "ranchu" ||
                product.StartsWith("sdk", StringComparison.Ordinal) || product.Contains("_sdk") || product.Contains("emulator");
        }

        internal static string Orientation(int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            return width == height ? "square" : height > width ? "portrait" : "landscape";
        }

        // Unity NetworkReachability names. On a phone the local network is Wi-Fi; on a desktop it can be a cable, so it is "other".
        internal static string Connection(string reachability, bool mobile)
        {
            switch (reachability)
            {
                case "NotReachable": return "none";
                case "ReachableViaCarrierDataNetwork": return "cellular";
                case "ReachableViaLocalAreaNetwork": return mobile ? "wifi" : "other";
                default: return null;
            }
        }

        // Only IANA ids are sent. Windows ids such as "Central Europe Standard Time" are left out (the offset still goes).
        internal static string TimeZoneId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return null;
            foreach (var c in id) if (char.IsWhiteSpace(c)) return null;
            if (id == "UTC" || id == "Etc/UTC") return id;
            var slash = id.IndexOf('/');
            return slash > 0 && slash < id.Length - 1 ? id : null;
        }

        internal static string UtcOffsetMinutes(TimeSpan offset) => Int((long)Math.Round(offset.TotalMinutes));

        // Logical size: native pixels divided by the scale, at most 3 decimals.
        internal static string Logical(int pixels, double scale)
        {
            if (pixels <= 0 || scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return null;
            return (pixels / scale).ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal static string Decimal(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return null;
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // Language part of a culture name ("de-AT" -> "de"); the invariant culture gives nothing.
        internal static string LanguageOf(string cultureName)
        {
            if (string.IsNullOrEmpty(cultureName)) return null;
            var dash = cultureName.IndexOf('-');
            var language = (dash < 0 ? cultureName : cultureName.Substring(0, dash)).ToLowerInvariant();
            return language.Length >= 2 && language.Length <= 3 && IsLetters(language) ? language : null;
        }

        // Region part of a culture name ("de-AT" -> "AT", "zh-Hans-CN" -> "CN").
        internal static string RegionOf(string cultureName)
        {
            if (string.IsNullOrEmpty(cultureName)) return null;
            var parts = cultureName.Split('-');
            for (var i = parts.Length - 1; i >= 1; i--)
                if (parts[i].Length == 2 && IsLetters(parts[i])) return parts[i].ToUpperInvariant();
            return null;
        }

        private static bool IsLetters(string text)
        {
            foreach (var c in text) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
            return true;
        }

        // UnityEngine.SystemLanguage names to ISO 639-1. Unknown and the Chinese umbrella value map to the base code only.
        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Afrikaans"] = "af", ["Arabic"] = "ar", ["Basque"] = "eu", ["Belarusian"] = "be", ["Bulgarian"] = "bg",
            ["Catalan"] = "ca", ["Chinese"] = "zh", ["ChineseSimplified"] = "zh", ["ChineseTraditional"] = "zh",
            ["Czech"] = "cs", ["Danish"] = "da", ["Dutch"] = "nl", ["English"] = "en", ["Estonian"] = "et",
            ["Faroese"] = "fo", ["Finnish"] = "fi", ["French"] = "fr", ["German"] = "de", ["Greek"] = "el",
            ["Hebrew"] = "he", ["Hungarian"] = "hu", ["Icelandic"] = "is", ["Indonesian"] = "id", ["Italian"] = "it",
            ["Japanese"] = "ja", ["Korean"] = "ko", ["Latvian"] = "lv", ["Lithuanian"] = "lt", ["Norwegian"] = "no",
            ["Polish"] = "pl", ["Portuguese"] = "pt", ["Romanian"] = "ro", ["Russian"] = "ru", ["SerboCroatian"] = "sh",
            ["Slovak"] = "sk", ["Slovenian"] = "sl", ["Spanish"] = "es", ["Swedish"] = "sv", ["Thai"] = "th",
            ["Turkish"] = "tr", ["Ukrainian"] = "uk", ["Vietnamese"] = "vi", ["Hindi"] = "hi",
        };

        // UnityEngine.SystemLanguage names to the codes of the 12 built-in screen languages. A language without built-in texts gives "",
        // so the screens then fall back to the culture and finally English. Unity players often report an invariant culture, which is why
        // the screens ask the system language first.
        private static readonly Dictionary<string, string> ScreenLanguages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["English"] = "en", ["German"] = "de", ["French"] = "fr", ["Spanish"] = "es", ["Italian"] = "it", ["Portuguese"] = "pt-BR",
            ["Japanese"] = "ja", ["Korean"] = "ko", ["Chinese"] = "zh-Hans", ["ChineseSimplified"] = "zh-Hans", ["Turkish"] = "tr",
            ["Russian"] = "ru", ["SerboCroatian"] = "sr-Latn",
        };

        internal static string ScreenLanguage(string unitySystemLanguage) =>
            unitySystemLanguage != null && ScreenLanguages.TryGetValue(unitySystemLanguage, out var code) ? code : "";

        internal static string SystemLanguage(string unitySystemLanguage) =>
            unitySystemLanguage != null && Languages.TryGetValue(unitySystemLanguage, out var code) ? code : null;
    }
}
