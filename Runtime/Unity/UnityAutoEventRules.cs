namespace Mightys.Pulse
{
    internal static class LifecycleState
    {
        internal static bool FromPause(bool paused) => !paused;

        // Mobile focus can change for a keyboard or permission prompt without the app going to the background.
        internal static bool? FromFocus(bool focused, bool mobile, bool editor) =>
            !mobile || editor ? focused : (bool?)null;
    }

    // 0.9.7 AE1, Unity layer rules without UnityEngine (tested in Tests/Platform).
    //
    // Foreground and background: Unity raises OnApplicationFocus and OnApplicationPause for the same change, in an order
    // that differs by platform (and focus also changes for system dialogs). Both feed one state here, so one real change
    // is one report: the first foreground of the process is the cold start, later ones are warm.
    internal sealed class ForegroundTracker
    {
        private bool? foreground;
        private bool coldReported;

        internal enum Change { None, Foreground, Background }

        // The app is in front (true) or not (false). Returns the report to make; ColdStart tells whether it is the first foreground.
        internal Change Set(bool inFront, out bool coldStart)
        {
            coldStart = false;
            if (foreground == inFront) return Change.None;
            // Before the first foreground there is nothing to end, so an early "not in front" only records the state.
            var first = foreground == null;
            foreground = inFront;
            if (!inFront) return first ? Change.None : Change.Background;
            coldStart = !coldReported; coldReported = true;
            return Change.Foreground;
        }
    }

    // Permission statuses read from the OS, mapped to the wire values of notificationPermission and trackingPermission.
    // Reading never asks the user; an unknown value maps to null and nothing is reported.
    internal static class PermissionStatus
    {
        // iOS UNAuthorizationStatus: 0 notDetermined, 1 denied, 2 authorized, 3 provisional, 4 ephemeral.
        internal static string Notification(int iosStatus)
        {
            switch (iosStatus)
            {
                case 0: return "notDetermined";
                case 1: return "denied";
                case 2: return "granted";
                case 3: return "provisional";
                case 4: return "ephemeral";
                default: return null;
            }
        }

        // Android NotificationManager.areNotificationsEnabled: granted or denied (Android has no "not asked yet" state to read).
        internal static string Notification(bool enabled) => enabled ? "granted" : "denied";

        // iOS ATTrackingManagerAuthorizationStatus: 0 notDetermined, 1 restricted, 2 denied, 3 authorized. Status only;
        // the advertising identifier is never read.
        internal static string Tracking(int iosStatus)
        {
            switch (iosStatus)
            {
                case 0: return "notDetermined";
                case 1: return "restricted";
                case 2: return "denied";
                case 3: return "authorized";
                default: return null;
            }
        }
    }

    internal static class LaunchTime
    {
        // Milliseconds from engine start to the first frame, from Time.realtimeSinceStartup at the first Update of the Pulse
        // host (created before the first scene loads, so its first Update runs in the first frame). Null when it is not
        // a usable launch time: in the Editor the clock counts from the Editor's own start, and a negative or longer than
        // ten minute value is not a launch.
        internal static long? FromFirstFrame(double secondsSinceStartup, bool editor)
        {
            if (editor || double.IsNaN(secondsSinceStartup) || secondsSinceStartup < 0 || secondsSinceStartup > 600) return null;
            return (long)System.Math.Round(secondsSinceStartup * 1000);
        }
    }
}
