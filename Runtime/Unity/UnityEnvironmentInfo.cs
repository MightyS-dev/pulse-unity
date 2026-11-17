using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Mightys.Pulse
{
    // Capture once on the Unity main thread. The worker only reads ordinary managed strings.
    // Static extra fields are read once at start. Values that change while the game runs (screen size, orientation, color
    // scheme, connection, low power mode) are read on the main thread by RefreshDynamic, which the lifecycle component calls
    // at start, on focus and every few seconds; DynamicFields only hands out the last snapshot, so it never touches Unity
    // APIs from the worker thread. Anything the platform cannot give is left out.
    internal sealed class UnityEnvironmentInfo : IEnvironmentInfo, IScreenLanguage, IExtraSystemFields
    {
        private readonly Dictionary<string, string> staticFields = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly bool mobile;
        private readonly double scale;

        public IReadOnlyDictionary<string, string> StaticFields => staticFields;

        public IReadOnlyDictionary<string, string> DynamicFields()
        {
            var current = Volatile.Read(ref snapshot);
            var fields = new Dictionary<string, string>(current, StringComparer.Ordinal);
            // Time zone and offset can change (travel, daylight saving); TimeZoneInfo is safe on any thread.
            try
            {
                var zone = TimeZoneInfo.Local;
                Put(fields, "timeZone", UnityFieldRules.TimeZoneId(zone.Id));
                Put(fields, "utcOffsetMinutes", UnityFieldRules.UtcOffsetMinutes(zone.GetUtcOffset(DateTime.UtcNow)));
            }
            catch (Exception) { }
            return fields;
        }

        public string Platform { get; }
        public string OsName { get; }
        public string OsVersion { get; }
        public string AppVersion { get; }
        public string AppBuildNumber { get; }
        public string DeviceModel { get; }
        public string Locale { get; }
        public string BundleId { get; }
        public string SigningCertSha256 { get; }
        // Application.systemLanguage mapped to a built-in screen language; the culture can be invariant on mobile players.
        public string ScreenLanguage { get; }

        internal UnityEnvironmentInfo()
        {
            Platform = Application.isMobilePlatform ? "mobile" : Application.platform == RuntimePlatform.WebGLPlayer ? "web" : "desktop";
            switch (Application.platform)
            {
                case RuntimePlatform.Android: OsName = "Android"; break;
                case RuntimePlatform.IPhonePlayer: OsName = "iOS"; break;
                case RuntimePlatform.OSXEditor: case RuntimePlatform.OSXPlayer: OsName = "macOS"; break;
                case RuntimePlatform.WindowsEditor: case RuntimePlatform.WindowsPlayer: OsName = "Windows"; break;
                case RuntimePlatform.LinuxEditor: case RuntimePlatform.LinuxPlayer: OsName = "Linux"; break;
                default: OsName = ""; break;
            }
            // Only the dotted version number is sent. On Android SystemInfo.operatingSystem also carries the firmware
            // build id, for example "Android OS 14 / API-34 (UP1A.231005.007/10754064)", which is never sent.
            OsVersion = Application.platform == RuntimePlatform.WebGLPlayer ? "" : SignalJson.OsVersionNumber(SystemInfo.operatingSystem);
            AppVersion = Application.version;
            AppBuildNumber = BuildNumber();
            DeviceModel = SystemInfo.deviceModel;
            Locale = CultureInfo.CurrentCulture.Name;
            BundleId = Application.identifier;
            try { ScreenLanguage = UnityFieldRules.ScreenLanguage(Application.systemLanguage.ToString()); } catch (Exception) { ScreenLanguage = ""; }
            SigningCertSha256 = AndroidCertificate();
            mobile = Application.isMobilePlatform;
            scale = Safe(ScreenScale);
            Safe(ReadStatic);
            RefreshDynamic();
        }

        private static void Put(Dictionary<string, string> fields, string key, string value)
        { if (!string.IsNullOrEmpty(value)) fields[key] = value; }

        private static T Safe<T>(Func<T> read) { try { return read(); } catch (Exception) { return default; } }
        private static void Safe(Action read) { try { read(); } catch (Exception) { } }

        private void ReadStatic()
        {
            var f = staticFields;
            var debug = UnityEngine.Debug.isDebugBuild;
            var android = Application.platform == RuntimePlatform.Android;
            var ios = Application.platform == RuntimePlatform.IPhonePlayer;
            Safe(() => Put(f, "deviceType", UnityFieldRules.DeviceType(SystemInfo.deviceType.ToString(), OsName,
                UnityFieldRules.Diagonal(Screen.width, Screen.height, Screen.dpi), AndroidTv())));
            if (ios || Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
                Put(f, "deviceBrand", "Apple");
            else if (android) Safe(() => Put(f, "deviceBrand", AndroidBuild("MANUFACTURER")));
            Safe(() => Put(f, "architecture", UnityFieldRules.Architecture(RuntimeInformation.ProcessArchitecture.ToString())));
            if (android) Safe(() => Put(f, "osApiLevel", AndroidApiLevel() > 0 ? UnityFieldRules.Int(AndroidApiLevel()) : null));
            Put(f, "runtimeVersion", UnityFieldRules.RuntimeVersion(Application.unityVersion));
#if ENABLE_IL2CPP
            Put(f, "scriptingBackend", "il2cpp");
#elif ENABLE_MONO
            Put(f, "scriptingBackend", "mono");
#endif
            Safe(() => Put(f, "appName", Application.productName));
            Safe(() => Put(f, "installSource", UnityFieldRules.InstallSource(OsName, android ? Application.installerName : null, debug, SandboxReceipt())));
            if (ios) Safe(() => Put(f, "isSimulator", UnityFieldRules.Bool(IosSimulator())));
            else if (android) Safe(() => Put(f, "isSimulator", UnityFieldRules.Bool(UnityFieldRules.IsAndroidEmulator(
                AndroidBuild("FINGERPRINT"), AndroidBuild("HARDWARE"), AndroidBuild("PRODUCT")))));
            Put(f, "isDebugBuild", UnityFieldRules.Bool(debug));
            Put(f, "screenScale", UnityFieldRules.Decimal(scale));
            Safe(() => Put(f, "screenDpi", Screen.dpi > 0 ? UnityFieldRules.Int((long)Math.Round(Screen.dpi)) : null));
            Safe(() => Put(f, "screenCount", Display.displays != null && Display.displays.Length > 0 ? UnityFieldRules.Int(Display.displays.Length) : null));
            Safe(() => Put(f, "language", UnityFieldRules.LanguageOf(CultureInfo.CurrentUICulture.Name)));
            Safe(() => Put(f, "preferredLanguage", UnityFieldRules.SystemLanguage(Application.systemLanguage.ToString())));
            Safe(() => Put(f, "region", UnityFieldRules.RegionOf(CultureInfo.CurrentCulture.Name)));
        }

        /// <summary>Reads the values that can change. Main thread only.</summary>
        internal void RefreshDynamic()
        {
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            Safe(() =>
            {
                // Mobile: the game's screen in its current orientation. Desktop: the main display, not the window.
                int w = mobile ? Screen.width : Screen.currentResolution.width, h = mobile ? Screen.height : Screen.currentResolution.height;
                if (w > 0 && h > 0)
                {
                    Put(f, "screenWidthPx", UnityFieldRules.Int(w)); Put(f, "screenHeightPx", UnityFieldRules.Int(h));
                    Put(f, "screenWidth", UnityFieldRules.Logical(w, scale)); Put(f, "screenHeight", UnityFieldRules.Logical(h, scale));
                }
                if (mobile) Put(f, "orientation", UnityFieldRules.Orientation(Screen.width, Screen.height));
            });
            Safe(() => Put(f, "connectionType", UnityFieldRules.Connection(Application.internetReachability.ToString(), mobile)));
            Safe(() => { var dark = DarkMode(); if (dark.HasValue) Put(f, "colorScheme", dark.Value ? "dark" : "light"); });
            Safe(() => { var low = LowPower(); if (low.HasValue) Put(f, "lowPowerMode", UnityFieldRules.Bool(low.Value)); });
            Volatile.Write(ref snapshot, f);
        }

        // Pixels per logical point: Android DisplayMetrics.density, iOS UIScreen.scale. Other platforms do not say.
        private static double ScreenScale()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var metrics = AndroidMetrics()) return metrics.Get<float>("density");
#elif UNITY_IOS && !UNITY_EDITOR
            return MightysPulseScreenScale();
#else
            return 0;
#endif
        }

        private static bool? SandboxReceipt()
        {
#if UNITY_IOS && !UNITY_EDITOR
            var value = MightysPulseSandboxReceipt();
            return value < 0 ? (bool?)null : value == 1;
#else
            return null;
#endif
        }

        private static bool IosSimulator()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return MightysPulseIsSimulator() == 1;
#else
            return false;
#endif
        }

        /// <summary>The system theme where the platform says it, otherwise null.</summary>
        internal static bool? SystemDark() => DarkMode();

        private static bool? DarkMode()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var activity = AndroidActivity())
            using (var resources = activity.Call<AndroidJavaObject>("getResources"))
            using (var configuration = resources.Call<AndroidJavaObject>("getConfiguration"))
            {
                var night = configuration.Get<int>("uiMode") & 0x30; // UI_MODE_NIGHT_MASK
                return night == 0x20 ? true : night == 0x10 ? false : (bool?)null;
            }
#elif UNITY_IOS && !UNITY_EDITOR
            var value = MightysPulseDarkMode();
            return value < 0 ? (bool?)null : value == 1;
#else
            return null;
#endif
        }

        private static bool? LowPower()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var activity = AndroidActivity())
            using (var power = activity.Call<AndroidJavaObject>("getSystemService", "power"))
                return power == null ? (bool?)null : power.Call<bool>("isPowerSaveMode");
#elif UNITY_IOS && !UNITY_EDITOR
            var value = MightysPulseLowPower();
            return value < 0 ? (bool?)null : value == 1;
#else
            return null;
#endif
        }

        private static bool AndroidTv()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var activity = AndroidActivity())
            using (var resources = activity.Call<AndroidJavaObject>("getResources"))
            using (var configuration = resources.Call<AndroidJavaObject>("getConfiguration"))
                return (configuration.Get<int>("uiMode") & 0x0f) == 4; // UI_MODE_TYPE_TELEVISION
#else
            return false;
#endif
        }

        private static string AndroidBuild(string field)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var build = new AndroidJavaClass("android.os.Build")) return build.GetStatic<string>(field);
#else
            return null;
#endif
        }

        private static int AndroidApiLevel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var version = new AndroidJavaClass("android.os.Build$VERSION")) return version.GetStatic<int>("SDK_INT");
#else
            return 0;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject AndroidActivity()
        {
            using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                return unity.GetStatic<AndroidJavaObject>("currentActivity");
        }

        private static AndroidJavaObject AndroidMetrics()
        {
            using (var activity = AndroidActivity())
            using (var resources = activity.Call<AndroidJavaObject>("getResources"))
                return resources.Call<AndroidJavaObject>("getDisplayMetrics");
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern double MightysPulseScreenScale();
        [DllImport("__Internal")] private static extern int MightysPulseSandboxReceipt();
        [DllImport("__Internal")] private static extern int MightysPulseIsSimulator();
        [DllImport("__Internal")] private static extern int MightysPulseDarkMode();
        [DllImport("__Internal")] private static extern int MightysPulseLowPower();
#endif

        private static string BuildNumber()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var manager = activity.Call<AndroidJavaObject>("getPackageManager"))
                using (var info = manager.Call<AndroidJavaObject>("getPackageInfo", Application.identifier, 0))
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    return (version.GetStatic<int>("SDK_INT") >= 28 ? info.Call<long>("getLongVersionCode") :
                        info.Get<int>("versionCode")).ToString(CultureInfo.InvariantCulture);
#elif UNITY_IOS && !UNITY_EDITOR
                return Marshal.PtrToStringAnsi(MightysPulseBuildNumber()) ?? "";
#else
                return "";
#endif
            }
            catch (Exception) { return ""; }
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern IntPtr MightysPulseBuildNumber();
#endif

        private static string AndroidCertificate()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var manager = activity.Call<AndroidJavaObject>("getPackageManager"))
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    var modern = version.GetStatic<int>("SDK_INT") >= 28;
                    using (var info = manager.Call<AndroidJavaObject>("getPackageInfo", Application.identifier, modern ? 0x08000000 : 0x40))
                    {
                        AndroidJavaObject[] signatures;
                        if (modern)
                        {
                            using (var signing = info.Get<AndroidJavaObject>("signingInfo"))
                                signatures = signing.Call<AndroidJavaObject[]>("getApkContentsSigners");
                        }
                        else signatures = info.Get<AndroidJavaObject[]>("signatures");
                        if (signatures == null || signatures.Length == 0) return "";
                        try
                        {
                            var bytes = signatures[0].Call<byte[]>("toByteArray");
                            using (var sha = SHA256.Create())
                            {
                                var b = new StringBuilder(64);
                                foreach (var item in sha.ComputeHash(bytes)) b.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                                return b.ToString();
                            }
                        }
                        finally { foreach (var signature in signatures) signature?.Dispose(); }
                    }
                }
            }
            catch (Exception) { return ""; }
#else
            return "";
#endif
        }
    }
}
