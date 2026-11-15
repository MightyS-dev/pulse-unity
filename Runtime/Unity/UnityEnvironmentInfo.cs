using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Mightys.Pulse
{
    // Capture once on the Unity main thread. The worker only reads ordinary managed strings.
    internal sealed class UnityEnvironmentInfo : IEnvironmentInfo
    {
        public string Platform { get; }
        public string OsName { get; }
        public string OsVersion { get; }
        public string AppVersion { get; }
        public string AppBuildNumber { get; }
        public string DeviceModel { get; }
        public string Locale { get; }
        public string BundleId { get; }
        public string SigningCertSha256 { get; }

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
            SigningCertSha256 = AndroidCertificate();
        }

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
