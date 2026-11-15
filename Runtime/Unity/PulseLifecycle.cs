using System;
using System.IO;
using System.Threading;
using UnityEngine;

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
                Pulse.InstallPlatform((key, options, optOut) => new PulseClient(key, options, new SystemClock(),
                    new UnityTransport(context, Pulse.ReleaseFlushWaiters), storage, environment, log, initialOptOut: optOut),
                    UnityEngine.Debug.isDebugBuild, log, () => PulseClient.ReadStoredOptOut(storage));
                var host = new GameObject(PulseInfo.DisplayName) { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(host); host.AddComponent<PulseLifecycle>();
            }
            catch (Exception) { }
        }
        // One shared background flush with a deadline: repeated pause/focus events while offline do not pile up.
        private void OnApplicationPause(bool paused) { if (paused) _ = Pulse.FlushInBackground(); }
        private void OnApplicationFocus(bool focused) { if (!focused) _ = Pulse.FlushInBackground(); }
        // Bounded final save (at most 2 s). Sending is not attempted here because it would need the main thread;
        // the saved queue is sent on the next launch.
        private void OnApplicationQuit() { Pulse.Shutdown(); }
    }
}
