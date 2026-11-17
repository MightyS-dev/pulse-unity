using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    public static class Pulse
    {
        private static PulseRuntime runtime = new PulseRuntime(new SystemClock());
        public static void Initialize(string appKey, PulseOptions options = null) => runtime.Initialize(appKey, options);
        public static void Track(string type, double? value = null, IDictionary<string, object> props = null) => runtime.Track(type, value, props);
        public static void SetUser(string stableUserId) => runtime.SetUser(stableUserId);
        /// <summary>Records a screen view (<c>pulse.screen_view</c>). Names are trimmed to 128 characters; empty names and repeats of the current screen are ignored.</summary>
        public static void Screen(string name) => runtime.Screen(name, false);
        // Entry point for platform hooks: does nothing when AutomaticScreenViews is false.
        internal static void AutomaticScreen(string name) => runtime.Screen(name, true);
        // Await this task, or ignore it. Never block on it with Wait() or Result on the Unity main thread: delivery
        // uses UnityWebRequest on the main thread, so a blocked main thread cannot finish the flush. If that happens
        // anyway, the SDK releases the wait after about five seconds without delivering.
        public static Task Flush(CancellationToken cancellationToken = default) => runtime.Flush(cancellationToken);
        // Platform hooks ask this before installing a screen hook. False when the code option or the cached portal settings switch screen views off.
        internal static bool AutomaticScreenViewsEnabled => runtime.AutomaticScreenViewsEnabled;
        /// <summary>Sends a handled error as <c>pulse.error</c> (kind handled): type, filtered message and stack frames. Props are optional.</summary>
        public static void TrackError(Exception exception, IDictionary<string, object> props = null) => runtime.TrackError(exception, props);
        // Entry point for platform hooks (kind crash or error). See PulseRuntime.CaptureUnhandled.
        internal static void CaptureUnhandled(Exception exception, string kind) => runtime.CaptureUnhandled(exception, kind);
        // Fallback for log callbacks that only give strings (for example a game engine's log message event). See PulseRuntime.CaptureLog.
        internal static void CaptureLog(string condition, string stackTrace, string kind, IReadOnlyList<string> skipLeadingFramePrefixes = null) =>
            runtime.CaptureLog(condition, stackTrace, kind, skipLeadingFramePrefixes);
        // Platform hooks ask this before installing an error hook. False when the CaptureErrors option is off.
        internal static bool CaptureErrorsEnabled => runtime.CaptureErrorsEnabled;
        /// <summary>Reports a link that opened the app as <c>pulse.deep_link</c>: scheme, host and path (numeric and uuid-like segments become <c>:id</c>), plus the three utm_ campaign tags. The query, the fragment and every other parameter are never sent. Call after <c>Initialize</c>.</summary>
        public static void DeepLink(string url) => runtime.DeepLink(url);
        // Entry points for platform hooks. See PulseRuntime.
        internal static void ReportForeground(bool coldStart) => runtime.ReportForeground(coldStart);
        internal static void ReportBackground() => runtime.ReportBackground();
        internal static void ReportPermission(string name, string status) => runtime.ReportPermission(name, status);
        internal static void SetLaunchMs(long milliseconds) => runtime.SetLaunchMs(milliseconds);
        // Heatmaps: entry points for platform hooks. A tap in logical points; the flags are the privacy rules the platform can see.
        internal static void HeatmapTap(double x, double y, double width, double height, bool keyboardVisible, bool inputTarget, bool ignored) =>
            runtime.HeatmapTap(x, y, width, height, keyboardVisible, inputTarget, ignored);
        // True while taps are being captured (portal switch, options, opt-out all allow it). Platform hooks may skip their work otherwise.
        internal static bool HeatmapActive => runtime.HeatmapActive;
        // The screen name taps are filed under, or null when none is known.
        internal static string HeatmapScreen => runtime.HeatmapScreen;
        // Whether a debug screenshot of the current screen is wanted (reads a small file: call off the main thread).
        internal static Task<HeatmapShotTicket> HeatmapShotTicketAsync(double width, double height) => runtime.HeatmapShotTicketAsync(width, height);
        internal static Task<bool> HeatmapScreenshot(HeatmapShotTicket ticket, byte[] bytes, string contentType) => runtime.HeatmapScreenshot(ticket, bytes, contentType);
        /// <summary>App Control: required updates and maintenance windows set in the portal. See <see cref="PulseControl"/>.</summary>
        public static PulseControl Control => runtime.Control;
        public static bool OptOut { get => runtime.OptOut; set => runtime.OptOut = value; }

        internal static readonly TimeSpan FinalSaveTimeout = TimeSpan.FromSeconds(2);

        internal static void InstallPlatform(Func<string, PulseOptions, bool?, PulseClient> factory,
            bool debugBuild, Action<string> logger, Func<bool> storedOptOut = null, Func<string> installHash = null, Action forgetInstall = null)
        { PulseInfo.DebugBuild = debugBuild; runtime.InstallPlatform(factory, logger, storedOptOut, installHash, forgetInstall); }
        internal static Task FlushInBackground() => runtime.FlushInBackground(TimeSpan.FromMinutes(1));
        internal static void ReleaseFlushWaiters() => runtime.ReleaseFlushWaiters();
        // Bounded final save for application quit. Saving runs on the thread pool and never needs the main thread.
        internal static void Shutdown() => Wait(runtime.StopAsync(), FinalSaveTimeout);
        // With domain reload disabled the old client must finish its final save before a new client loads the queue.
        internal static void Reset()
        {
            var stopping = runtime.StopAsync();
            Wait(stopping, TimeSpan.FromSeconds(5));
            runtime = new PulseRuntime(new SystemClock());
        }
        private static void Wait(Task task, TimeSpan timeout) { try { task.Wait(timeout); } catch (Exception) { } }
    }

    // The facade also owns the small pre-initialization queue, with capture-time sessions and user hashes.
    internal sealed class PulseRuntime
    {
        private readonly object gate = new object();
        private readonly Queue<SignalInput> beforeInitialize = new Queue<SignalInput>();
        private readonly IClock clock;
        private Func<string, PulseOptions, bool?, PulseClient> factory;
        private Func<bool> storedOptOutReader;
        private Func<string> installIdHash;
        private Action forgetInstallId;
        private PulseClient client;
        private Task backgroundFlush;
        private string appKey, userHash, session = Guid.NewGuid().ToString("N");
        private PulseOptions options;
        private Action<string> logger;
        private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);
        private DateTimeOffset lastActivity = DateTimeOffset.MinValue;
        private long? launchMs;
        private bool? optOut, storedOptOut;
        private readonly HeatmapCapture heatmap;
        /// <summary>App Control. Exists before Initialize so handlers can subscribe early; it attaches to the client when one starts.</summary>
        internal readonly PulseControl Control = new PulseControl();
        internal PulseRuntime(IClock clock)
        {
            this.clock = clock; createdAt = clock.UtcNow;
            heatmap = new HeatmapCapture(clock, HeatmapEnabled, SessionAt, EmitHeatmap);
        }

        // ---- heatmaps ----
        private bool HeatmapEnabled()
        {
            try { lock (gate) return client != null && !OptOut && client.Policy.HeatmapAllowed; } catch (Exception) { return false; }
        }
        // The session a signal captured now would belong to; "pending" stands for a session that does not exist yet.
        private string SessionAt(DateTimeOffset now) { lock (gate) return now - lastActivity >= SessionTimeout ? HeatmapCapture.PendingSession : session; }
        private void EmitHeatmap(string json)
        {
            lock (gate) { Record("pulse.heatmap", null, null, null, false, json); heatmap.AdoptSession(session); }
        }
        internal void HeatmapTap(double x, double y, double width, double height, bool keyboardVisible, bool inputTarget, bool ignored)
        {
            try { heatmap.OnTap(x, y, width, height, keyboardVisible, inputTarget, ignored); } catch (Exception) { Warn("Heatmap capture failed."); }
        }
        internal bool HeatmapActive { get { try { return HeatmapEnabled() && heatmap.Screen != null; } catch (Exception) { return false; } } }
        internal string HeatmapScreen { get { try { return heatmap.Screen; } catch (Exception) { return null; } } }
        internal void HeatmapFlush() { try { heatmap.Flush(); } catch (Exception) { Warn("Heatmap flush failed."); } }
        internal HeatmapShotTicket HeatmapShotTicket(double width, double height)
        {
            try { return HeatmapShotTicketAsync(width, height).GetAwaiter().GetResult(); } catch (Exception) { return null; }
        }
        internal Task<HeatmapShotTicket> HeatmapShotTicketAsync(double width, double height)
        {
            try
            {
                PulseClient current;
                lock (gate) current = client;
                var name = heatmap.Screen;
                if (current == null || name == null || !HeatmapEnabled()) return Task.FromResult<HeatmapShotTicket>(null);
                return current.Shots.TicketAsync(name, width, height);
            }
            catch (Exception) { return Task.FromResult<HeatmapShotTicket>(null); }
        }
        internal Task<bool> HeatmapScreenshot(HeatmapShotTicket ticket, byte[] bytes, string contentType)
        {
            try
            {
                PulseClient current;
                lock (gate) current = client;
                if (current == null || ticket == null || !HeatmapEnabled()) return Task.FromResult(false);
                return current.Shots.UploadAsync(ticket, bytes, contentType);
            }
            catch (Exception) { return Task.FromResult(false); }
        }

        internal void InstallPlatform(Func<string, PulseOptions, bool?, PulseClient> platform, Action<string> log,
            Func<bool> storedPreference = null, Func<string> installHash = null, Action forgetInstall = null)
        {
            try { lock (gate) { factory = platform; logger = log; storedOptOutReader = storedPreference;
                installIdHash = installHash; forgetInstallId = forgetInstall; Start(); } }
            catch (Exception) { lock (gate) { if (client == null) appKey = null; } Warn("Platform initialization failed."); }
        }
        internal void Initialize(string key, PulseOptions requested = null)
        {
            try
            {
                lock (gate)
                {
                    if (client != null || appKey != null) return;
                    if (string.IsNullOrWhiteSpace(key) || key.IndexOf('\r') >= 0 || key.IndexOf('\n') >= 0)
                    { Warn("Invalid application key."); return; }
                    options = (requested ?? new PulseOptions()).Snapshot();
                    appKey = key; Start();
                }
            }
            catch (Exception) { lock (gate) { if (client == null) appKey = null; } Warn("Initialization failed."); }
        }
        private void Start()
        {
            if (client != null || appKey == null || factory == null) return;
            client = factory(appKey, options, optOut);
            if (launchMs.HasValue) client.SetLaunchMs(launchMs.Value);
            client.ConfigRefreshed = () => { if (!HeatmapEnabled()) heatmap.Drop(); };
            if (lastScreen != null && client.Policy.Allows("pulse.screen_view")) heatmap.SetScreen(lastScreen);
            client.StartConfigFetch();
            Control.Attach(client.Control);
            client.Control.Start();
            while (beforeInitialize.Count > 0)
            {
                var queued = beforeInitialize.Dequeue();
                if (queued.Anonymous) queued.UserHash = AnonymousHash();
                client.Enqueue(queued);
            }
        }
        // Without setUser, signals carry the hash of the anonymous install id, once the platform can provide it.
        private string AnonymousHash()
        {
            try { return client == null ? null : installIdHash?.Invoke(); }
            catch (Exception) { return null; }
        }
        internal void Track(string type, double? value = null, IDictionary<string, object> props = null) =>
            Record(type, value, props, null, false);

        // The one capture path. Errors take the same session, user and queue rules as any signal, plus the per-type rate limit.
        private void Record(string type, double? value, IDictionary<string, object> props, ErrorEvent error, bool crash, string heatmapJson = null)
        {
            try
            {
                lock (gate)
                {
                    if (OptOut) return;
                    var now = clock.UtcNow;
                    // A signal that is dropped as invalid neither starts a session nor keeps the current one alive.
                    var candidate = now - lastActivity >= SessionTimeout ? Guid.NewGuid().ToString("N") : session;
                    var input = SignalJson.Create(type, value, props, now, candidate, userHash ?? AnonymousHash(), Warn);
                    if (input == null) return;
                    if (error != null)
                    {
                        if (!ErrorAllowed(candidate, error.Type)) return;
                        input.Error = error;
                    }
                    session = candidate; lastActivity = now;
                    input.Anonymous = userHash == null;
                    input.HeatmapJson = heatmapJson;
                    if (client != null) { if (crash) client.EnqueueCrash(input); else client.Enqueue(input); }
                    else { if (beforeInitialize.Count == 100) beforeInitialize.Dequeue(); beforeInitialize.Enqueue(input); }
                }
            }
            catch (Exception) { Warn("Signal capture failed."); }
        }

        // At most 10 error signals per session and error type; the counts live in memory only.
        internal const int MaxErrorsPerType = 10;
        private readonly Dictionary<string, int> errorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private string errorCountsSession;
        private bool ErrorAllowed(string sessionId, string errorType)
        {
            if (errorCountsSession != sessionId) { errorCounts.Clear(); errorCountsSession = sessionId; }
            errorCounts.TryGetValue(errorType, out var count);
            if (count >= MaxErrorsPerType) return false;
            errorCounts[errorType] = count + 1;
            return true;
        }

        [ThreadStatic] private static bool capturing;

        /// <summary>A handled error from the developer (kind handled).</summary>
        internal void TrackError(Exception exception, IDictionary<string, object> props)
        {
            if (exception == null || capturing) return;
            capturing = true;
            try { Record(ErrorKinds.SignalType, null, props, ErrorReport.FromException(exception, ErrorKinds.Handled), false); }
            catch (Exception) { }
            finally { capturing = false; }
        }

        /// <summary>Entry point of the platform hooks. Kind is <c>crash</c> (the process is ending: saved to disk at once, sent at the next
        /// launch) or <c>error</c> (the app goes on). Never throws, ignores errors raised inside itself, and does nothing when
        /// <c>CaptureErrors</c> is false.</summary>
        internal void CaptureUnhandled(Exception exception, string kind)
        {
            if (exception == null || capturing) return;
            capturing = true;
            try
            {
                lock (gate) { if (options != null && !options.CaptureErrors) return; }
                var crash = kind == ErrorKinds.Crash;
                Record(ErrorKinds.SignalType, null, null, ErrorReport.FromException(exception, crash ? ErrorKinds.Crash : ErrorKinds.Error), crash);
            }
            catch (Exception) { }
            finally { capturing = false; }
        }

        /// <summary>Like <see cref="CaptureUnhandled"/> for hooks that only get strings: <c>condition</c> (for example
        /// "NullReferenceException: message") and <c>stackTrace</c> in a game engine's format. Empty condition is ignored. Same rules otherwise. Leading frames whose type starts with one of
        /// <c>skipLeadingFramePrefixes</c> (none by default) are dropped.</summary>
        internal void CaptureLog(string condition, string stackTrace, string kind, IReadOnlyList<string> skipLeadingFramePrefixes = null)
        {
            if (string.IsNullOrWhiteSpace(condition) || capturing) return;
            capturing = true;
            try
            {
                lock (gate) { if (options != null && !options.CaptureErrors) return; }
                var crash = kind == ErrorKinds.Crash;
                Record(ErrorKinds.SignalType, null, null, ErrorReport.FromLog(condition, stackTrace, crash ? ErrorKinds.Crash : ErrorKinds.Error, skipLeadingFramePrefixes), crash);
            }
            catch (Exception) { }
            finally { capturing = false; }
        }

        /// <summary>Whether a platform integration should install its error hooks: the option is on.</summary>
        internal bool CaptureErrorsEnabled
        {
            get { try { lock (gate) return options?.CaptureErrors ?? true; } catch (Exception) { return false; } }
        }
        internal const int MaxScreenName = 128;
        private string lastScreen;
        internal void Screen(string name, bool automatic)
        {
            try
            {
                lock (gate)
                {
                    if (automatic && options != null && !options.AutomaticScreenViews) return;
                    if (automatic && client != null && !client.Policy.AutomaticScreenHook) return;
                    if (OptOut) return;
                    var screen = name?.Trim();
                    if (string.IsNullOrEmpty(screen)) return;
                    if (screen.Length > MaxScreenName) screen = screen.Substring(0, MaxScreenName).Trim();
                    if (screen.Length == 0 || string.Equals(screen, lastScreen, StringComparison.Ordinal)) return;
                    var props = new Dictionary<string, object> { ["screen"] = screen };
                    if (lastScreen != null)
                    {
                        props["previousScreen"] = lastScreen;
                        props["previousScreenSeconds"] = ScreenSeconds(clock.UtcNow);
                    }
                    // The old screen's points are sent first, so the heatmap precedes the new screen view in time.
                    heatmap.SetScreen(client != null && client.Policy.Allows("pulse.screen_view") ? screen : null);
                    lastScreen = screen; screenAccumulated = TimeSpan.Zero; screenSince = clock.UtcNow;
                    Track("pulse.screen_view", null, props);
                }
            }
            catch (Exception) { Warn("Screen capture failed."); }
        }
        // Visible time of the current screen: time in the foreground only. Without foreground reports the app counts as always visible.
        private TimeSpan screenAccumulated;
        private DateTimeOffset screenSince;
        private int foregroundState; // 0 unknown (treated as visible), 1 foreground, 2 background
        private DateTimeOffset createdAt;
        private long ScreenSeconds(DateTimeOffset now)
        {
            var total = screenAccumulated + (foregroundState == 2 ? TimeSpan.Zero : now - screenSince);
            return Math.Max(0, (long)total.TotalSeconds);
        }

        /// <summary>The app came to the foreground (a real transition; repeats are ignored). Counts it, restarts the screen timer and sends
        /// <c>pulse.app_opened</c> (which the portal must have switched on). Needs a started client, so call after <c>Initialize</c>.</summary>
        internal void ReportForeground(bool coldStart)
        {
            // App Control checks again on every foreground, also when analytics is off.
            try { Control.OnForeground(); } catch (Exception) { }
            try
            {
                lock (gate)
                {
                    if (OptOut || client == null || foregroundState == 1) return;
                    var now = clock.UtcNow;
                    if (foregroundState == 2) screenSince = now;
                    foregroundState = 1;
                    client.Auto.NoteForeground(now);
                    Track(RemoteConfig.AppOpenedType, null, new Dictionary<string, object> { ["coldStart"] = coldStart });
                }
            }
            catch (Exception) { Warn("Foreground report failed."); }
        }

        /// <summary>The app went to the background (a real transition; repeats are ignored). Stops the screen timer and sends
        /// <c>pulse.app_backgrounded</c> with the seconds of the foreground period.</summary>
        internal void ReportBackground()
        {
            try
            {
                lock (gate)
                {
                    if (OptOut || client == null) return;
                    heatmap.Flush();
                    if (foregroundState == 2) return;
                    var now = clock.UtcNow;
                    if (lastScreen != null) screenAccumulated += now - screenSince;
                    foregroundState = 2;
                    var seconds = client.Auto.CurrentForegroundSeconds(now, createdAt);
                    // A background report never opens a session by itself: after the session timeout of silence the signal is a rule drop
                    // (not a quota drop, so it counts toward no cap and not as droppedAutomatic). The foreground counters still update.
                    if (lastActivity == DateTimeOffset.MinValue || now - lastActivity < SessionTimeout)
                        Track(RemoteConfig.AppBackgroundedType, null, new Dictionary<string, object> { ["foregroundSeconds"] = seconds });
                    client.Auto.NoteBackground(seconds);
                }
            }
            catch (Exception) { Warn("Background report failed."); }
        }

        /// <summary>Reports a permission status (read only; the SDK never asks for a permission). <c>name</c> is notification or tracking;
        /// status uses the wire values. A status that differs from the stored one sends <c>pulse.permission_changed</c> once; the
        /// first status ever seen is stored without a signal. Needs a started client.</summary>
        internal void ReportPermission(string name, string status)
        {
            try
            {
                lock (gate)
                {
                    if (OptOut || client == null) return;
                    var key = name?.Trim().ToLowerInvariant();
                    key = key == "notification" || key == "notifications" || key == "notificationpermission" ? "notificationPermission" :
                        key == "tracking" || key == "trackingpermission" ? "trackingPermission" : null;
                    var clean = key == null ? null : SystemFields.Clean(key, status);
                    if (clean == null) return;
                    var before = client.Auto.NotePermission(key, clean);
                    if (before != null)
                        Track(RemoteConfig.PermissionChangedType, null, new Dictionary<string, object>
                        { ["permission"] = key.Substring(0, key.Length - "Permission".Length), ["from"] = before, ["to"] = clean });
                }
            }
            catch (Exception) { Warn("Permission report failed."); }
        }

        /// <summary>See <see cref="Pulse.DeepLink"/>.</summary>
        internal void DeepLink(string url)
        {
            try
            {
                var props = DeepLinkProps.From(url);
                if (props != null) Track(RemoteConfig.DeepLinkType, null, props);
            }
            catch (Exception) { Warn("Deep link report failed."); }
        }

        /// <summary>Time from process start to the first frame, in milliseconds, for the cold start session_started. Optional.</summary>
        internal void SetLaunchMs(long milliseconds)
        {
            try { lock (gate) { launchMs = milliseconds; client?.SetLaunchMs(milliseconds); } }
            catch (Exception) { }
        }

        /// <summary>Whether a platform integration should capture screens in this run: the code option and the cached portal settings agree.</summary>
        internal bool AutomaticScreenViewsEnabled
        {
            get { try { lock (gate) return client != null ? client.Policy.AutomaticScreenHook : options?.AutomaticScreenViews ?? true; } catch (Exception) { return false; } }
        }
        internal void SetUser(string value)
        {
            try
            {
                lock (gate)
                {
                    if (OptOut) return;
                    userHash = null;
                    if (!string.IsNullOrEmpty(value)) userHash = SignalJson.Hash(value);
                }
            }
            catch (Exception) { Warn("User hashing failed."); }
        }
        // Before a client exists, the getter reports the saved preference (read once through the platform), so a
        // settings screen never shows false for a saved opt-out and writes it back.
        internal bool OptOut
        {
            get { try { lock (gate) return client?.IsOptOut ?? optOut ?? StoredOptOut(); } catch (Exception) { return true; } }
            set
            {
                try
                {
                    lock (gate)
                    {
                        optOut = value; userHash = null;
                        if (value) { heatmap.Drop(); heatmap.SetScreen(null); beforeInitialize.Clear(); session = Guid.NewGuid().ToString("N"); lastActivity = DateTimeOffset.MinValue; lastScreen = null; foregroundState = 0; screenAccumulated = TimeSpan.Zero; }
                        client?.SetOptOut(value);
                        if (value) { try { forgetInstallId?.Invoke(); } catch (Exception) { } }
                    }
                }
                catch (Exception) { Warn("Privacy preference update failed."); }
            }
        }
        private bool StoredOptOut()
        {
            if (storedOptOut.HasValue || storedOptOutReader == null) return storedOptOut ?? false;
            try { storedOptOut = storedOptOutReader(); return storedOptOut.Value; }
            catch (Exception) { return true; }
        }
        internal Task Flush(CancellationToken cancellationToken = default)
        {
            try { lock (gate) { heatmap.Flush(); return client?.FlushAsync(cancellationToken) ?? Task.CompletedTask; } }
            catch (Exception) { Warn("Flush failed."); return Task.CompletedTask; }
        }
        // Pause and focus loss reuse one outstanding flush, so repeated events while offline never pile up waiters.
        internal Task FlushInBackground(TimeSpan deadline)
        {
            try
            {
                lock (gate)
                {
                    if (client == null) return Task.CompletedTask;
                    heatmap.Flush();
                    if (backgroundFlush != null && !backgroundFlush.IsCompleted) return backgroundFlush;
                    var cancellation = new CancellationTokenSource(deadline);
                    backgroundFlush = client.FlushAsync(cancellation.Token).ContinueWith(_ => cancellation.Dispose(),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return backgroundFlush;
                }
            }
            catch (Exception) { Warn("Flush failed."); return Task.CompletedTask; }
        }
        internal void ReleaseFlushWaiters() { try { PulseClient current; lock (gate) current = client; current?.ReleaseWaiters(); } catch (Exception) { } }
        internal Task StopAsync()
        {
            try { lock (gate) { heatmap.Flush(); return client?.StopAsync() ?? Task.CompletedTask; } }
            catch (Exception) { return Task.CompletedTask; }
        }
        private void Warn(string message)
        {
            try { if (options?.Debug ?? PulseInfo.DebugBuild) logger?.Invoke(message); } catch (Exception) { }
        }
    }
}
