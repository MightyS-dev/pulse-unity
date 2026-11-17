using System;
using System.Collections.Generic;

namespace Mightys.Pulse
{
    // The built-in App Control screen, worked out without UnityEngine so the .NET tests cover it: what a state looks like as a screen,
    // the colours of both themes (contrast measured) and the canvas size the layout works in.

    internal enum ControlSurface { None, Lock, Dialog, Card }

    internal enum ControlAction { None, OpenUrl, CheckNow, Dismiss }

    // What the screen shows for one state. A banner and a test build strip can come on their own or next to a Lock, Dialog or Card.
    internal sealed class ControlPlan
    {
        internal ControlSurface Surface;
        internal ControlKind Kind;
        internal string Title = "", Body = "";
        internal string PrimaryText = "", SecondaryText = "";
        internal ControlAction Primary, Secondary;
        internal bool HasBanner, HasTestStrip;
        internal string BannerText = "", TestStripText = "";

        internal bool Visible => Surface != ControlSurface.None || HasBanner;
        /// <summary>True when the screen covers the app and takes the touches (everything except a lone banner).</summary>
        internal bool Modal => Surface != ControlSurface.None;
    }

    internal static class ControlOverlayRules
    {
        internal static readonly ControlPlan Nothing = new ControlPlan();

        internal static ControlPlan Plan(ControlState state)
        {
            if (state == null || state.Kind == ControlKind.Normal && state.Extras.Count == 0) return Nothing;
            var items = new List<ControlState> { state };
            items.AddRange(state.Extras);
            ControlState modal = null, banner = null;
            foreach (var item in items)
            {
                if (item.Kind == ControlKind.Normal) continue;
                if (item.Kind == ControlKind.MaintenanceUpcoming) { if (banner == null) banner = item; }
                else if (modal == null) modal = item;
            }
            var plan = new ControlPlan();
            if (banner != null) { plan.HasBanner = true; plan.BannerText = banner.Body ?? ""; }
            if (modal == null) { plan.Kind = ControlKind.MaintenanceUpcoming; return plan.HasBanner ? plan : Nothing; }
            plan.Kind = modal.Kind;
            plan.Title = modal.Title ?? ""; plan.Body = modal.Body ?? "";
            var hasUrl = !string.IsNullOrEmpty(modal.Url);
            var dismissText = modal.CanDismiss ? modal.DismissText : "";
            switch (modal.Kind)
            {
                case ControlKind.UpdateRequired:
                case ControlKind.VersionBlocked:
                    plan.Surface = ControlSurface.Lock;
                    if (hasUrl) { plan.Primary = ControlAction.OpenUrl; plan.PrimaryText = modal.ButtonText ?? ""; }
                    break;
                case ControlKind.Maintenance:
                    plan.Surface = ControlSurface.Lock;
                    plan.Primary = ControlAction.CheckNow; plan.PrimaryText = modal.ButtonText ?? "";
                    break;
                case ControlKind.UpdateSuggested:
                    plan.Surface = ControlSurface.Dialog;
                    if (hasUrl) { plan.Primary = ControlAction.OpenUrl; plan.PrimaryText = modal.ButtonText ?? ""; }
                    break;
                case ControlKind.Announcement:
                    plan.Surface = ControlSurface.Card;
                    if (hasUrl) { plan.Primary = ControlAction.OpenUrl; plan.PrimaryText = modal.ButtonText ?? ""; }
                    break;
                case ControlKind.WhatsNew:
                    plan.Surface = ControlSurface.Card;
                    break;
                default:
                    return plan.HasBanner ? plan : Nothing;
            }
            if (dismissText.Length > 0) { plan.Secondary = ControlAction.Dismiss; plan.SecondaryText = dismissText; }
            // With no link the only button is the one that dismisses: a card uses its own button text for it.
            if (plan.Primary == ControlAction.None && plan.Secondary == ControlAction.Dismiss)
            {
                var own = modal.Kind == ControlKind.Announcement || modal.Kind == ControlKind.WhatsNew ? modal.ButtonText : "";
                plan.Primary = ControlAction.Dismiss;
                plan.PrimaryText = string.IsNullOrEmpty(own) ? plan.SecondaryText : own;
                plan.Secondary = ControlAction.None; plan.SecondaryText = "";
            }
            else if (plan.Primary == ControlAction.None && modal.Kind == ControlKind.WhatsNew)
            {
                plan.Primary = ControlAction.Dismiss;
                plan.PrimaryText = string.IsNullOrEmpty(modal.ButtonText) ? "OK" : modal.ButtonText;
            }
            if (plan.Primary != ControlAction.None && plan.PrimaryText.Length == 0) plan.PrimaryText = plan.SecondaryText.Length > 0 ? plan.SecondaryText : "OK";
            if (modal.IsTestBuild && modal.TestBuildText.Length > 0) { plan.HasTestStrip = true; plan.TestStripText = modal.TestBuildText; }
            return plan;
        }

        // ---- canvas ----

        internal const float ReferenceWidth = 1080f, ReferenceHeight = 1920f, Match = 0.5f;

        /// <summary>The factor a canvas scaler with the reference size and match 0.5 applies to the screen.</summary>
        internal static float CanvasScale(float screenWidth, float screenHeight)
        {
            if (screenWidth < 1f || screenHeight < 1f) return 1f;
            var log = Math.Log(screenWidth / ReferenceWidth, 2) * (1 - Match) + Math.Log(screenHeight / ReferenceHeight, 2) * Match;
            return (float)Math.Pow(2, log);
        }

        internal static void CanvasSize(float screenWidth, float screenHeight, out float width, out float height)
        {
            var scale = CanvasScale(screenWidth, screenHeight);
            width = screenWidth / scale; height = screenHeight / scale;
        }

        /// <summary>Card width in canvas units for the free width of the safe area.</summary>
        internal static float CardWidth(float freeWidth, ControlSurface surface)
        {
            var limit = surface == ControlSurface.Lock ? 920f : 840f;
            return Math.Max(240f, Math.Min(freeWidth - 2 * Margin, limit));
        }

        internal const float Margin = 40f;
    }

    // Colours as 0xRRGGBB. Both themes keep text at least 4.5:1 against the surface it sits on (the tests measure it).
    internal sealed class ControlTheme
    {
        internal uint Card, Title, Body, Primary, PrimaryText, Secondary, SecondaryText, Strip, StripText, Banner, BannerText;
        internal const float DimAlpha = 0.85f, DialogDimAlpha = 0.6f;

        internal static readonly ControlTheme Dark = new ControlTheme
        {
            Card = 0x1F2127, Title = 0xF4F5F7, Body = 0xC4C8D0, Primary = 0x2F6BF0, PrimaryText = 0xFFFFFF, Secondary = 0x30333B, SecondaryText = 0xF4F5F7,
            Strip = 0xF5B301, StripText = 0x1A1A1A, Banner = 0x30333B, BannerText = 0xF4F5F7
        };

        internal static readonly ControlTheme Light = new ControlTheme
        {
            Card = 0xFFFFFF, Title = 0x111318, Body = 0x4A5160, Primary = 0x1D4ED8, PrimaryText = 0xFFFFFF, Secondary = 0xE6E8EC, SecondaryText = 0x111318,
            Strip = 0xF5B301, StripText = 0x1A1A1A, Banner = 0xFFFFFF, BannerText = 0x111318
        };

        /// <summary>The system theme where the platform says it, otherwise dark.</summary>
        internal static ControlTheme For(bool? systemDark) => systemDark == false ? Light : Dark;

        internal static double Contrast(uint a, uint b)
        {
            var la = Luminance(a); var lb = Luminance(b);
            var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static double Luminance(uint rgb)
        {
            double Channel(uint c) { var v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        }
    }
}
