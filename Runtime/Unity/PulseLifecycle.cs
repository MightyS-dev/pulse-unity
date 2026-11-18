using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mightys.Pulse
{
    // 0.9.7: wraps the logger's handler. Every exception goes to the original handler unchanged; ErrorHooks decides
    // whether it is also sent.
    internal sealed class PulseLogHandler : ILogHandler
    {
        internal readonly ILogHandler Inner;
        private readonly ErrorHooks hooks;
        internal PulseLogHandler(ILogHandler inner, ErrorHooks hooks) { Inner = inner; this.hooks = hooks; }
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) => Inner.LogFormat(logType, context, format, args);
        public void LogException(Exception exception, UnityEngine.Object context) => hooks.LogException(exception, () => Inner.LogException(exception, context));
    }

    internal sealed class PersistentDataPathStorage : FileStorage
    { internal PersistentDataPathStorage(string root) : base(Path.Combine(root, "mightys-pulse")) { } }

    internal sealed class PulseLifecycle : MonoBehaviour
    {
        // Also runs when entering play mode with domain reload disabled. Reset waits (bounded) for the previous
        // client's final save so the next client never loads a queue file that is still being written.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Pulse.Reset(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            try
            {
                var context = SynchronizationContext.Current;
                if (context == null) return;
                var environment = new UnityEnvironmentInfo();
                var storage = new PersistentDataPathStorage(Application.persistentDataPath);
                Action<string> log = message => context.Post(_ => UnityEngine.Debug.LogWarning("[Pulse] " + message), null);
                // Anonymous install id next to the offline queue, read or created now (unless opted out) so the first
                // signals carry its hash without waiting. Only its SHA-256 hash is sent; opt-out forgets it.
                var installId = new InstallId(() => storage);
                try { if (!PulseClient.ReadStoredOptOut(storage)) installId.Prime(); } catch (Exception) { }
                var screens = new ScreenHookGate(); var errors = new ScreenHookGate(); var automatic = new ScreenHookGate();
                Pulse.InstallPlatform((key, options, optOut) =>
                    {
                        screens.Initialized(); errors.Initialized(); automatic.Initialized();
                        ControlOverlay.Mode = options.Control;
                        var usage = UnityUsage.Start(storage, installId, optOut);
                        return new PulseClient(key, options, new SystemClock(), new UnityTransport(context, Pulse.ReleaseFlushWaiters),
                            storage, environment, log, initialOptOut: optOut, usage: usage);
                    },
                    UnityEngine.Debug.isDebugBuild, log, () => PulseClient.ReadStoredOptOut(storage), installId.Hash, installId.Forget);
                Pulse.Control.Opener = OpenAddress;
                var host = new GameObject(PulseInfo.DisplayName) { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(host);
                var lifecycle = host.AddComponent<PulseLifecycle>(); lifecycle.environment = environment; lifecycle.screens = screens; lifecycle.errorGate = errors; lifecycle.automaticGate = automatic;
            }
            catch (Exception) { }
        }

        // The store or announcement address goes to the system. Called on the main thread by the built-in screen's button.
        private static bool OpenAddress(string url)
        {
            try { Application.OpenURL(url); return true; } catch (Exception) { return false; }
        }

        private const float RefreshSeconds = 10f;
        private readonly ControlOverlay controlOverlay = new ControlOverlay();
        private UnityEnvironmentInfo environment;
        private ScreenHookGate screens;
        private bool screenHook;
        private float nextRefresh;

        // 0.9.4 automatic screen views: once Pulse.Initialize has run, the scene hook is installed only when screen views
        // are enabled for this run (code option and cached portal settings, set by the core). The scene active at that
        // moment counts as the first screen. Naming, trimming and repeats are the core's rules.
        private void InstallScreenHookWhenReady()
        {
            if (screens == null || !screens.TryDecide(() => Pulse.AutomaticScreenViewsEnabled)) return;
            SceneManager.activeSceneChanged += OnActiveSceneChanged; screenHook = true;
            Pulse.AutomaticScreen(SceneManager.GetActiveScene().name);
        }
        private static void OnActiveSceneChanged(Scene previous, Scene next) { Pulse.AutomaticScreen(next.name); }
        private void OnDestroy() { controlOverlay.Dispose(); if (screenHook) SceneManager.activeSceneChanged -= OnActiveSceneChanged; if (deepLinkHook) Application.deepLinkActivated -= OnDeepLink; RemoveErrorHooks(); }

        // 0.9.7 error capture: installed once after Pulse.Initialize, only when CaptureErrors is on (set by the core),
        // removed at shutdown and when the host goes away. See ErrorHooks for the rules.
        private ScreenHookGate errorGate;
        private ErrorHooks errorHooks;
        private PulseLogHandler logHandler;
        private void InstallErrorHooksWhenReady()
        {
            if (errorGate == null || !errorGate.TryDecide(() => Pulse.CaptureErrorsEnabled)) return;
            try
            {
                errorHooks = new ErrorHooks(Pulse.CaptureUnhandled, Pulse.CaptureLog); errorHooks.Activate();
                logHandler = new PulseLogHandler(UnityEngine.Debug.unityLogger.logHandler, errorHooks);
                UnityEngine.Debug.unityLogger.logHandler = logHandler;
                Application.logMessageReceived += OnLogMessage;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
            }
            catch (Exception) { RemoveErrorHooks(); }
        }
        private void RemoveErrorHooks()
        {
            if (errorHooks == null) return;
            try
            {
                errorHooks.Deactivate();
                // Restore only when nobody wrapped the handler after Pulse; otherwise the deactivated wrapper just forwards.
                if (logHandler != null && ReferenceEquals(UnityEngine.Debug.unityLogger.logHandler, logHandler)) UnityEngine.Debug.unityLogger.logHandler = logHandler.Inner;
                Application.logMessageReceived -= OnLogMessage;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }
            catch (Exception) { }
            errorHooks = null; logHandler = null;
        }
        private void OnLogMessage(string condition, string stackTrace, LogType type) => errorHooks?.LogMessage(condition, stackTrace, type == LogType.Exception);
        private void OnUnhandled(object sender, UnhandledExceptionEventArgs args) => errorHooks?.Unhandled(args.ExceptionObject, args.IsTerminating);
        private void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs args) => errorHooks?.Unobserved(args.Exception);

        // 0.9.7 AE1 automatic events (the core decides what is sent, the portal switches and the caps apply there):
        // - launch time: Time.realtimeSinceStartup at the first Update of this host, which exists before the first scene loads,
        //   so it is the time from engine start to the first frame (see LaunchTime; not in the Editor). Set before the first
        //   report so the cold start session_started carries it when no signal was sent earlier.
        // - foreground and background: pause (phones) or focus (desktop) events go through one ForegroundTracker, so one real
        //   change is one report; the first foreground after Initialize is the cold start.
        // - deep links: Application.deepLinkActivated, and Application.absoluteURL once after Initialize when the app was
        //   opened by a link (Android and iOS only; in a browser it is the page address, not a deep link).
        // - permissions: the notification status at start and on every return to the foreground, and on iOS the tracking
        //   (ATT) status; both are read without asking the user.
        private ScreenHookGate automaticGate;
        private readonly ForegroundTracker foreground = new ForegroundTracker();
        private bool launchMeasured, automaticReady, deepLinkHook;
        private int permissionPolls;
        private void StartAutomaticEventsWhenReady()
        {
            if (!launchMeasured)
            {
                launchMeasured = true;
                try { if (LaunchTime.FromFirstFrame(Time.realtimeSinceStartupAsDouble, Application.isEditor) is long ms) Pulse.SetLaunchMs(ms); } catch (Exception) { }
            }
            if (automaticReady || automaticGate == null || !automaticGate.TryDecide(() => true)) return;
            automaticReady = true;
            try
            {
                Application.deepLinkActivated += OnDeepLink; deepLinkHook = true;
                if (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer)
                    if (!string.IsNullOrEmpty(Application.absoluteURL)) Pulse.DeepLink(Application.absoluteURL);
            }
            catch (Exception) { }
            InFront(true);
        }
        private static void OnDeepLink(string url) { try { Pulse.DeepLink(url); } catch (Exception) { } }
        private void InFront(bool? inFront)
        {
            if (!automaticReady || !inFront.HasValue) return;
            try
            {
                var change = foreground.Set(inFront.Value, out var coldStart);
                if (change == ForegroundTracker.Change.Foreground) { Pulse.ReportForeground(coldStart); ReadPermissions(); }
                else if (change == ForegroundTracker.Change.Background) Pulse.ReportBackground();
            }
            catch (Exception) { }
        }
        private void ReadPermissions()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // NotificationManager.areNotificationsEnabled (API 24+), no androidx dependency.
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    if (version.GetStatic<int>("SDK_INT") >= 24)
                        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        using (var manager = activity.Call<AndroidJavaObject>("getSystemService", "notification"))
                            if (manager != null) Pulse.ReportPermission("notification", PermissionStatus.Notification(manager.Call<bool>("areNotificationsEnabled")));
#elif UNITY_IOS && !UNITY_EDITOR
                // The notification status arrives asynchronously; it is picked up by the next few Updates.
                MightysPulseRefreshNotificationStatus(); permissionPolls = 120;
                if (PermissionStatus.Tracking(MightysPulseTrackingStatus()) is string tracking) Pulse.ReportPermission("tracking", tracking);
#endif
            }
            catch (Exception) { }
        }
        private void PollPermissions()
        {
            if (permissionPolls <= 0) return;
            permissionPolls--;
#if UNITY_IOS && !UNITY_EDITOR
            try { if (PermissionStatus.Notification(MightysPulseNotificationStatus()) is string status) { Pulse.ReportPermission("notification", status); permissionPolls = 0; } }
            catch (Exception) { permissionPolls = 0; }
#endif
        }
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void MightysPulseRefreshNotificationStatus();
        [DllImport("__Internal")] private static extern int MightysPulseNotificationStatus();
        [DllImport("__Internal")] private static extern int MightysPulseTrackingStatus();
#endif

        // Screen size, orientation, color scheme, connection and low power mode are read here on the main thread, so the
        // worker thread only copies the last snapshot.
        // 0.9.7 heatmaps: pointer releases and the debug picture; the core decides whether anything is captured.
        private UnityHeatmapHook heatmap;
        private void Update()
        {
            (heatmap ?? (heatmap = new UnityHeatmapHook(this))).Update();
            controlOverlay.Tick();
            StartAutomaticEventsWhenReady();
            PollPermissions();
            InstallScreenHookWhenReady();
            InstallErrorHooksWhenReady();
            if (environment == null || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            environment.RefreshDynamic();
        }
        // Pause and resume move the app between front and back on every platform. Desktop players and the Editor also
        // follow focus; mobile focus changes for system dialogs and permission prompts are ignored. One tracker removes
        // repeated transitions. ReportBackground saves the session end, queues the signal and starts a bounded flush.
        // 0.9.7 App Control checks again when the app returns (at most once in 60 s, set by the core).
        private void OnApplicationPause(bool paused)
        {
            InFront(LifecycleState.FromPause(paused));
            if (!paused) Pulse.Control.OnForeground();
        }
        private void OnApplicationFocus(bool focused)
        {
            InFront(LifecycleState.FromFocus(focused, Application.isMobilePlatform, Application.isEditor));
            if (!focused) return;
            Pulse.Control.OnForeground();
            environment?.RefreshDynamic(); nextRefresh = Time.unscaledTime + RefreshSeconds;
        }
        // Bounded final save (at most 2 s). Sending is not attempted here because it would need the main thread;
        // the saved queue is sent on the next launch.
        private void OnApplicationQuit() { RemoveErrorHooks(); Pulse.Shutdown(); }
    }
}
