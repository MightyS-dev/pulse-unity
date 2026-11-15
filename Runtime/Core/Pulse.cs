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
        // Await this task, or ignore it. Never block on it with Wait() or Result on the Unity main thread: delivery
        // uses UnityWebRequest on the main thread, so a blocked main thread cannot finish the flush. If that happens
        // anyway, the SDK releases the wait after about five seconds without delivering.
        public static Task Flush(CancellationToken cancellationToken = default) => runtime.Flush(cancellationToken);
        public static bool OptOut { get => runtime.OptOut; set => runtime.OptOut = value; }

        internal static readonly TimeSpan FinalSaveTimeout = TimeSpan.FromSeconds(2);

        internal static void InstallPlatform(Func<string, PulseOptions, bool?, PulseClient> factory,
            bool debugBuild, Action<string> logger, Func<bool> storedOptOut = null)
        { PulseInfo.DebugBuild = debugBuild; runtime.InstallPlatform(factory, logger, storedOptOut); }
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
        private PulseClient client;
        private Task backgroundFlush;
        private string appKey, userHash, session = Guid.NewGuid().ToString("N");
        private PulseOptions options;
        private Action<string> logger;
        private DateTimeOffset lastActivity = DateTimeOffset.MinValue;
        private bool? optOut, storedOptOut;
        internal PulseRuntime(IClock clock) { this.clock = clock; }

        internal void InstallPlatform(Func<string, PulseOptions, bool?, PulseClient> platform, Action<string> log,
            Func<bool> storedPreference = null)
        {
            try { lock (gate) { factory = platform; logger = log; storedOptOutReader = storedPreference; Start(); } }
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
            while (beforeInitialize.Count > 0) client.Enqueue(beforeInitialize.Dequeue());
        }
        internal void Track(string type, double? value = null, IDictionary<string, object> props = null)
        {
            try
            {
                lock (gate)
                {
                    if (OptOut) return;
                    var now = clock.UtcNow;
                    if (now - lastActivity >= TimeSpan.FromMinutes(30)) session = Guid.NewGuid().ToString("N");
                    lastActivity = now;
                    var input = SignalJson.Create(type, value, props, now, session, userHash, Warn);
                    if (input == null) return;
                    if (client != null) client.Enqueue(input);
                    else { if (beforeInitialize.Count == 100) beforeInitialize.Dequeue(); beforeInitialize.Enqueue(input); }
                }
            }
            catch (Exception) { Warn("Signal capture failed."); }
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
                        if (value) { beforeInitialize.Clear(); session = Guid.NewGuid().ToString("N"); lastActivity = DateTimeOffset.MinValue; }
                        client?.SetOptOut(value);
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
            try { lock (gate) return client?.FlushAsync(cancellationToken) ?? Task.CompletedTask; }
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
            try { lock (gate) return client?.StopAsync() ?? Task.CompletedTask; }
            catch (Exception) { return Task.CompletedTask; }
        }
        private void Warn(string message)
        {
            try { if (options?.Debug ?? PulseInfo.DebugBuild) logger?.Invoke(message); } catch (Exception) { }
        }
    }
}
