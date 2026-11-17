using System;

namespace Mightys.Pulse
{
    // Heatmaps, Unity layer rules without UnityEngine types, so the .NET tests cover them.

    internal static class HeatmapUnits
    {
        // Logical points: pixels divided by dpi / 160 (density independent). Where the dpi is unknown the pixels are used as they are.
        internal static double Scale(double dpi) => dpi > 0 && !double.IsInfinity(dpi) ? dpi / 160.0 : 1.0;
        internal static double Points(double pixels, double dpi) => pixels / Scale(dpi);
        // Unity screen y runs from the bottom; heatmaps count from the top.
        internal static double FromTop(double pixelY, double heightPixels, double dpi) => (heightPixels - pixelY) / Scale(dpi);
    }

    // One tap: press and release of a single pointer that never moved beyond the slop. A second pointer spoils the gesture.
    internal sealed class TapDetector
    {
        internal const double Slop = 18;
        private int id; private double startX, startY; private bool active, spoiled;
        private int extra;

        internal void Begin(int pointerId, double x, double y)
        {
            if (active && pointerId != id) { spoiled = true; extra++; return; }
            id = pointerId; startX = x; startY = y; active = true; spoiled = false; extra = 0;
        }

        internal void Move(int pointerId, double x, double y)
        {
            if (active && pointerId == id && Distance(x, y) > Slop) spoiled = true;
        }

        internal void Cancel(int pointerId) { if (active && pointerId == id) Clear(); }

        internal bool End(int pointerId, double x, double y, out double endX, out double endY)
        {
            endX = x; endY = y;
            if (!active) return false;
            if (pointerId != id)
            {
                // A finger that is not the first one lifts: the gesture is spoiled; it ends when the first one lifts.
                if (extra > 0) extra--;
                return false;
            }
            var tap = !spoiled && Distance(x, y) <= Slop;
            Clear();
            return tap;
        }

        private double Distance(double x, double y) => Math.Sqrt((x - startX) * (x - startX) + (y - startY) * (y - startY));
        private void Clear() { active = false; spoiled = false; extra = 0; }
    }

    internal static class TextInputTypes
    {
        // The package does not depend on the UI or TextMeshPro packages: an input field is recognised by the type name
        // (InputField, TMP_InputField) on the type or any base type.
        internal static bool IsTextInput(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.Name == "InputField" || t.Name == "TMP_InputField") return true;
            return false;
        }
    }

    internal static class HeatmapPictures
    {
        internal const int MaxSide = 2048;
        // Longest side at most 2048, aspect kept.
        internal static void Fit(int width, int height, out int newWidth, out int newHeight)
        {
            var longest = Math.Max(width, height);
            if (longest <= MaxSide) { newWidth = width; newHeight = height; return; }
            var factor = (double)MaxSide / longest;
            newWidth = Math.Max(1, (int)Math.Round(width * factor)); newHeight = Math.Max(1, (int)Math.Round(height * factor));
        }
    }

    // The debug picture is taken about a second after a screen became visible, once per visit.
    internal sealed class ShotScheduler
    {
        internal const double WaitSeconds = 1.0;
        private string screen; private double since; private bool asked;

        internal void Observe(string name, double now)
        {
            if (string.Equals(name, screen, StringComparison.Ordinal)) return;
            screen = name; since = now; asked = false;
        }

        internal bool Due(double now)
        {
            if (screen == null || asked || now - since < WaitSeconds) return false;
            asked = true; return true;
        }

        internal string Screen => screen;
    }

    // The SDK's own App Control screen is never app usage: a tap on it is not a heatmap point and no debug picture is taken
    // while it is up. The overlay sets this when it shows or removes a screen; the heatmap hook reads it on the main thread.
    internal static class SdkUiState
    {
        private static volatile bool visible, modal;
        private static volatile Func<double, double, bool> covers;

        internal static bool Visible => visible;

        // Called by the overlay with whether it is up, whether it covers the whole app, and a test for a screen pixel (origin bottom left)
        // that is on one of its parts (the bar and the test strip let touches through to the app everywhere else).
        internal static void Set(bool isVisible, bool isModal, Func<double, double, bool> hit)
        {
            modal = isModal; covers = hit; visible = isVisible;
        }

        internal static void Clear() { visible = false; modal = false; covers = null; }

        // True when a tap at this screen pixel belongs to the SDK's screen.
        internal static bool Swallows(double pixelX, double pixelY)
        {
            if (!visible) return false;
            if (modal) return true;
            var hit = covers;
            return hit != null && hit(pixelX, pixelY);
        }
    }
}
