using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mightys.Pulse
{
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
                var screens = new ScreenHookGate();
                Pulse.InstallPlatform((key, options, optOut) =>
                    {
                        screens.Initialized();
                        var usage = UnityUsage.Start(storage, installId, optOut);
                        return new PulseClient(key, options, new SystemClock(), new UnityTransport(context, Pulse.ReleaseFlushWaiters),
                            storage, environment, log, initialOptOut: optOut, usage: usage);
                    },
                    UnityEngine.Debug.isDebugBuild, log, () => PulseClient.ReadStoredOptOut(storage), installId.Hash, installId.Forget);
                var host = new GameObject(PulseInfo.DisplayName) { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(host);
                var lifecycle = host.AddComponent<PulseLifecycle>(); lifecycle.environment = environment; lifecycle.screens = screens;
            }
            catch (Exception) { }
        }

        private const float RefreshSeconds = 10f;
        private UnityEnvironmentInfo environment;
        private ScreenHookGate screens;
        private bool screenHook;
        private float nextRefresh;

        // 0.9.4 automatic screen views: once Pulse.Initialize has run, the scene hook is installed only when screen views
        // are enabled for this run (code option and cached portal settings, decided by the core). The scene active at that
        // moment counts as the first screen. Naming, trimming and repeats are the core's rules.
        private void InstallScreenHookWhenReady()
        {
            if (screens == null || !screens.TryDecide(() => Pulse.AutomaticScreenViewsEnabled)) return;
            SceneManager.activeSceneChanged += OnActiveSceneChanged; screenHook = true;
            Pulse.AutomaticScreen(SceneManager.GetActiveScene().name);
        }
        private static void OnActiveSceneChanged(Scene previous, Scene next) { Pulse.AutomaticScreen(next.name); }
        private void OnDestroy() { if (screenHook) SceneManager.activeSceneChanged -= OnActiveSceneChanged; }

        // Screen size, orientation, color scheme, connection and low power mode are read here on the main thread, so the
        // worker thread only copies the last snapshot.
        private void Update()
        {
            InstallScreenHookWhenReady();
            if (environment == null || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            environment.RefreshDynamic();
        }
        // One shared background flush with a deadline: repeated pause/focus events while offline do not pile up.
        private void OnApplicationPause(bool paused) { if (paused) _ = Pulse.FlushInBackground(); }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) { _ = Pulse.FlushInBackground(); return; }
            environment?.RefreshDynamic(); nextRefresh = Time.unscaledTime + RefreshSeconds;
        }
        // Bounded final save (at most 2 s). Sending is not attempted here because it would need the main thread;
        // the saved queue is sent on the next launch.
        private void OnApplicationQuit() { Pulse.Shutdown(); }
    }
}
