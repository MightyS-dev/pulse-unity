using System;
using System.Collections.Generic;
using System.Text;

namespace Mightys.Pulse
{
    /// <summary>What App Control currently asks the app to show.</summary>
    public enum ControlKind
    {
        /// <summary>Nothing to show. This is also the state whenever the rules cannot be read.</summary>
        Normal,
        /// <summary>A newer version exists. The user can continue.</summary>
        UpdateSuggested,
        /// <summary>The installed version is below the required minimum.</summary>
        UpdateRequired,
        /// <summary>This exact version is blocked.</summary>
        VersionBlocked,
        /// <summary>The app is in a maintenance window.</summary>
        Maintenance,
        /// <summary>A maintenance window starts soon. A banner, never a lock.</summary>
        MaintenanceUpcoming,
        /// <summary>An announcement card.</summary>
        Announcement,
        /// <summary>A card with what is new in the version the app was just updated to.</summary>
        WhatsNew
    }

    /// <summary>How the SDK treats App Control. Where the package has no built-in screen (plain .NET, or an engine without its
    /// optional UI module) <see cref="Automatic"/> acts as <see cref="Manual"/>: the SDK checks and keeps the state and the app
    /// draws any screen itself. Where the package draws a screen, <see cref="Automatic"/> shows it and <see cref="Manual"/> does not.</summary>
    public enum ControlMode
    {
        /// <summary>Check and keep the state (default). Shows the built-in screen where the platform has one.</summary>
        Automatic,
        /// <summary>Check and keep the state. Never shows a built-in screen; the app draws its own.</summary>
        Manual,
        /// <summary>No request is made and the state is always <see cref="ControlKind.Normal"/>.</summary>
        Off
    }

    /// <summary>The state of App Control at one moment. Immutable. Texts are ready to show: language chosen, custom texts applied,
    /// name and time markers filled.</summary>
    public sealed class ControlState
    {
        private static readonly IReadOnlyList<ControlState> EmptyExtras = new ControlState[0];

        /// <summary>The state with nothing to show.</summary>
        public static readonly ControlState Normal = new ControlState(ControlKind.Normal);

        internal ControlState(ControlKind kind) { Kind = kind; Extras = EmptyExtras; }

        /// <summary>What to show. When a lock or an update prompt applies it is that; otherwise a card; otherwise the maintenance banner.</summary>
        public ControlKind Kind { get; internal set; }
        /// <summary>Title text, or an empty string when the kind has none.</summary>
        public string Title { get; internal set; } = "";
        /// <summary>Body text.</summary>
        public string Body { get; internal set; } = "";
        /// <summary>Text for the main button, or an empty string.</summary>
        public string ButtonText { get; internal set; } = "";
        /// <summary>Where the main button leads (store page or announcement link), or null. Only https and http addresses are given.</summary>
        public string Url { get; internal set; }
        /// <summary>Start of the maintenance window, for the maintenance kinds.</summary>
        public DateTimeOffset? StartsAt { get; internal set; }
        /// <summary>End of the maintenance window, for the maintenance kinds.</summary>
        public DateTimeOffset? EndsAt { get; internal set; }
        /// <summary>The version this state is about (the newest known one, the blocked one or the what's new one), or null.</summary>
        public string Version { get; internal set; }
        /// <summary>True when the user may go on using the app. False only for the locking kinds in a release build.</summary>
        public bool AllowContinue { get; internal set; } = true;
        /// <summary>True in a debug build that would otherwise be locked: the rules are shown as a preview and the app can continue.</summary>
        public bool IsTestBuild { get; internal set; }
        /// <summary>Text for a "not now" or "continue" button when <see cref="CanDismiss"/> applies, otherwise an empty string.</summary>
        public string DismissText { get; internal set; } = "";
        /// <summary>The preview banner text when <see cref="IsTestBuild"/> is true, otherwise an empty string.</summary>
        public string TestBuildText { get; internal set; } = "";
        /// <summary>A card or banner that applies at the same time as <see cref="Kind"/>, for example a maintenance banner next to an announcement.</summary>
        public IReadOnlyList<ControlState> Extras { get; internal set; }

        /// <summary>True when the kind covers the screen and the user cannot go on.</summary>
        public bool IsLocking => !AllowContinue && (Kind == ControlKind.UpdateRequired || Kind == ControlKind.VersionBlocked || Kind == ControlKind.Maintenance);

        /// <summary>True when <c>Pulse.Control.Dismiss()</c> does something for this state.</summary>
        public bool CanDismiss => DismissText.Length > 0;

        // A string that is equal exactly when two states would look the same, used to decide whether anything changed.
        internal string Signature()
        {
            var b = new StringBuilder();
            Append(b, this);
            foreach (var extra in Extras) { b.Append('|'); Append(b, extra); }
            return b.ToString();
        }

        private static void Append(StringBuilder b, ControlState s)
        {
            b.Append((int)s.Kind).Append('\u001f').Append(s.Title).Append('\u001f').Append(s.Body).Append('\u001f').Append(s.ButtonText)
                .Append('\u001f').Append(s.Url).Append('\u001f').Append(s.StartsAt?.UtcTicks).Append('\u001f').Append(s.EndsAt?.UtcTicks)
                .Append('\u001f').Append(s.Version).Append('\u001f').Append(s.AllowContinue ? '1' : '0').Append(s.IsTestBuild ? '1' : '0')
                .Append('\u001f').Append(s.DismissText).Append('\u001f').Append(s.TestBuildText);
        }
    }

    internal static class ControlKinds
    {
        /// <summary>The name used by the rules and the shared tables, for example updateRequired.</summary>
        internal static string Wire(ControlKind kind)
        {
            var name = kind.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
