using System;
using System.Threading.Tasks;

namespace Mightys.Pulse
{
    /// <summary>App Control for the app: the current state of required updates, maintenance windows and notices set in the portal.
    /// The SDK checks by itself after <c>Initialize</c> and whenever the app comes to the foreground, and keeps working normally
    /// whenever a check fails. This type draws nothing: read <see cref="State"/>, listen to <see cref="Changed"/> and show your own screen.
    /// Every member is safe to call at any time (also before <c>Initialize</c>), never throws and never blocks.</summary>
    public sealed class PulseControl
    {
        private readonly object gate = new object();
        private ControlClient client;

        /// <summary>Opens an address for the user, for example in the system browser. Set by the platform; null means <see cref="OpenStore"/> opens nothing.</summary>
        internal Func<string, bool> Opener { get; set; }

        /// <summary>The current state. <see cref="ControlKind.Normal"/> until a check has found something to show.</summary>
        public ControlState State { get { try { return client?.State ?? ControlState.Normal; } catch (Exception) { return ControlState.Normal; } } }

        /// <summary>Raised when <see cref="State"/> changed, also when a scheduled maintenance window starts or ends. Handlers run on a thread pool
        /// thread: switch to your UI thread before touching the UI. An exception in a handler is ignored.</summary>
        public event EventHandler<ControlState> Changed;

        internal void Attach(ControlClient control)
        {
            lock (gate) client = control;
            control.Changed += state => { try { Changed?.Invoke(this, state); } catch (Exception) { } };
        }

        /// <summary>Checks the server now (for a "Try again" button). The returned task completes when the check ended; it never faults.
        /// With <see cref="ControlMode.Off"/> it completes at once and nothing is sent.</summary>
        public Task CheckNowAsync()
        {
            try { return client?.CheckNowAsync() ?? Task.CompletedTask; } catch (Exception) { return Task.CompletedTask; }
        }

        /// <summary>Opens the address behind the main button of the current state (the store page of the app) when the platform allows it.
        /// Pressing it for an update counts toward the safeguard that lets a user through after three returns on the same version.</summary>
        /// <returns>True when an address was handed to the platform.</returns>
        public bool OpenStore()
        {
            try
            {
                var address = client?.NoteStorePress();
                if (address == null) return false;
                return Opener?.Invoke(address) ?? false;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Dismisses what the user may dismiss: a suggestion (not shown again for 24 hours), a card, or the preview lock of a debug build.
        /// Does nothing for a lock in a release build.</summary>
        public void Dismiss() { try { client?.Dismiss(); } catch (Exception) { } }

        /// <summary>Tell the SDK the app came to the foreground. It checks again, at most once in 60 seconds.
        /// Reporting the foreground through the platform integration does this too.</summary>
        public void OnForeground() { try { client?.OnForeground(); } catch (Exception) { } }
    }
}
