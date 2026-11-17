using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace Mightys.Pulse
{
    // Heatmaps in Unity: reads pointer releases (legacy Input touches and mouse, or the Input System through reflection when
    // that is the only backend), hands them to the core in logical points, and in debug builds takes one picture per screen.
    // Everything here runs on the main thread, never consumes or delays an input event, and never throws.
    internal sealed class UnityHeatmapHook
    {
        private const float ActiveCheckSeconds = 0.5f, ScreenCheckSeconds = 0.25f;
        private readonly MonoBehaviour host;
        private readonly TapDetector detector = new TapDetector();
        private readonly ShotScheduler scheduler = new ShotScheduler();
        private readonly InputSystemPointer inputSystem = new InputSystemPointer();
        private float nextActiveCheck, nextScreenCheck;
        private bool active;
        private Task<HeatmapShotTicket> pendingTicket;
        private bool capturing;

        internal UnityHeatmapHook(MonoBehaviour host) { this.host = host; }

        internal void Update()
        {
            try
            {
                var now = Time.unscaledTime;
                if (now >= nextActiveCheck) { nextActiveCheck = now + ActiveCheckSeconds; active = Pulse.HeatmapActive; }
                if (!active) { if (scheduler.Screen != null) scheduler.Observe(null, now); return; }
                ReadPointers();
                if (now >= nextScreenCheck) { nextScreenCheck = now + ScreenCheckSeconds; scheduler.Observe(Pulse.HeatmapScreen, now); }
                if (UnityEngine.Debug.isDebugBuild) StepShot(now);
            }
            catch (Exception) { }
        }

        // ---- pointers ----

#if ENABLE_LEGACY_INPUT_MANAGER
        private void ReadPointers()
        {
            var touchEnded = false;
            var touches = Input.touchCount;
            for (var i = 0; i < touches; i++)
            {
                var t = Input.GetTouch(i);
                var p = Local(t.position);
                switch (t.phase)
                {
                    case TouchPhase.Began: detector.Begin(t.fingerId, p.x, p.y); break;
                    case TouchPhase.Moved: case TouchPhase.Stationary: detector.Move(t.fingerId, p.x, p.y); break;
                    case TouchPhase.Canceled: detector.Cancel(t.fingerId); break;
                    case TouchPhase.Ended:
                        touchEnded = true;
                        if (detector.End(t.fingerId, p.x, p.y, out var x, out var y)) Report(x, y, t.position);
                        break;
                }
            }
            // Touches also arrive as simulated mouse events on touch screens; a release seen as a touch is never counted twice.
            if (Application.isMobilePlatform || touchEnded || touches > 0) return;
            var mouse = Local(Input.mousePosition);
            if (Input.GetMouseButtonDown(0)) detector.Begin(-1, mouse.x, mouse.y);
            else if (Input.GetMouseButton(0)) detector.Move(-1, mouse.x, mouse.y);
            if (Input.GetMouseButtonUp(0) && detector.End(-1, mouse.x, mouse.y, out var mx, out var my)) Report(mx, my, Input.mousePosition);
        }
#elif ENABLE_INPUT_SYSTEM
        // Only the Input System is active: the legacy Input class would throw. Read through reflection (the package does not
        // depend on it); the primary pointer covers mouse, pen and the first finger.
        private void ReadPointers()
        {
            if (!inputSystem.Available) return;
            if (!inputSystem.Read(out var position, out var pressed, out var released)) return;
            var p = Local(position);
            if (pressed) detector.Begin(-1, p.x, p.y);
            else if (!released) detector.Move(-1, p.x, p.y);
            if (released && detector.End(-1, p.x, p.y, out var x, out var y)) Report(x, y, position);
        }
#else
        private void ReadPointers() { }
#endif

        // Screen pixels (origin bottom left) to logical points from the top left.
        private static Vector2 Local(Vector2 pixels)
        {
            var dpi = Screen.dpi;
            return new Vector2((float)HeatmapUnits.Points(pixels.x, dpi), (float)HeatmapUnits.FromTop(pixels.y, Screen.height, dpi));
        }

        private static float WidthPoints => (float)HeatmapUnits.Points(Screen.width, Screen.dpi);
        private static float HeightPoints => (float)HeatmapUnits.Points(Screen.height, Screen.dpi);

        private void Report(double x, double y, Vector2 pixels)
        {
            if (SdkUiState.Swallows(pixels.x, pixels.y)) return;
            var keyboard = TouchScreenKeyboard.visible;
            bool ignored = false, input = false;
            if (!keyboard) UiProbe.Inspect(pixels, out ignored, out input);
            Pulse.HeatmapTap(x, y, WidthPoints, HeightPoints, keyboard, input, ignored);
        }

        // ---- debug picture ----

        private void StepShot(float now)
        {
            if (pendingTicket != null)
            {
                if (!pendingTicket.IsCompleted) return;
                var ticket = pendingTicket.IsFaulted ? null : pendingTicket.Result; pendingTicket = null;
                if (ticket != null && !capturing && !SdkUiState.Visible && string.Equals(ticket.Screen, scheduler.Screen, StringComparison.Ordinal))
                { capturing = true; host.StartCoroutine(Capture(ticket)); }
                return;
            }
            if (capturing || SdkUiState.Visible || !scheduler.Due(now) || TouchScreenKeyboard.visible) return;
            pendingTicket = Pulse.HeatmapShotTicketAsync(WidthPoints, HeightPoints);
        }

        private IEnumerator Capture(HeatmapShotTicket ticket)
        {
            yield return new WaitForEndOfFrame();
            Texture2D texture = null;
            try
            {
                if (TouchScreenKeyboard.visible || SdkUiState.Visible || !string.Equals(ticket.Screen, Pulse.HeatmapScreen, StringComparison.Ordinal)) yield break;
                texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null) yield break;
                HeatmapPictures.Fit(texture.width, texture.height, out var w, out var h);
                if (w != texture.width || h != texture.height) texture = Scaled(texture, w, h);
                var bytes = ImageConversion.EncodeToPNG(texture); var type = "image/png";
                if (bytes != null && bytes.Length > HeatmapShots.MaxBytes) { bytes = ImageConversion.EncodeToJPG(texture, 80); type = "image/jpeg"; }
                if (bytes != null && bytes.Length > 0 && bytes.Length <= HeatmapShots.MaxBytes) _ = Pulse.HeatmapScreenshot(ticket, bytes, type);
            }
            finally
            {
                capturing = false;
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private static Texture2D Scaled(Texture2D source, int width, int height)
        {
            var target = RenderTexture.GetTemporary(width, height, 0);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var small = new Texture2D(width, height, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, width, height), 0, 0); small.Apply();
                UnityEngine.Object.Destroy(source);
                return small;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }
    }

    // The UI event system lives in a package this one does not depend on, so it is used through reflection (cached once).
    // What it answers: is the tap on an object that carries PulseHeatmapIgnore (itself or a parent), and is it on, or is the
    // current selection, a text input field.
    internal static class UiProbe
    {
        private static bool resolved, available;
        private static PropertyInfo currentProperty, selectedProperty, positionProperty, objectProperty;
        private static MethodInfo raycastAll;
        private static ConstructorInfo pointerConstructor;
        private static Type resultListType;

        private static void Resolve()
        {
            resolved = true;
            try
            {
                var system = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
                var pointer = Type.GetType("UnityEngine.EventSystems.PointerEventData, UnityEngine.UI");
                var result = Type.GetType("UnityEngine.EventSystems.RaycastResult, UnityEngine.UI");
                if (system == null || pointer == null || result == null) return;
                currentProperty = system.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                selectedProperty = system.GetProperty("currentSelectedGameObject", BindingFlags.Public | BindingFlags.Instance);
                positionProperty = pointer.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                objectProperty = result.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                pointerConstructor = pointer.GetConstructor(new[] { system });
                resultListType = typeof(List<>).MakeGenericType(result);
                raycastAll = system.GetMethod("RaycastAll", new[] { pointer, resultListType });
                available = currentProperty != null && selectedProperty != null && positionProperty != null && objectProperty != null &&
                    pointerConstructor != null && raycastAll != null;
            }
            catch (Exception) { available = false; }
        }

        internal static void Inspect(Vector2 pixels, out bool ignored, out bool textInput)
        {
            ignored = false; textInput = false;
            try
            {
                if (!resolved) Resolve();
                if (!available) return;
                var system = currentProperty.GetValue(null);
                if (system == null) return;
                if (selectedProperty.GetValue(system) is GameObject selected && IsInputBelow(selected)) textInput = true;
                var data = pointerConstructor.Invoke(new[] { system });
                positionProperty.SetValue(data, pixels);
                var list = (IList)Activator.CreateInstance(resultListType);
                raycastAll.Invoke(system, new[] { data, list });
                // The list is sorted, the first entry is what the tap hit.
                if (list.Count == 0 || !(objectProperty.GetValue(list[0]) is GameObject hit)) return;
                for (var t = hit.transform; t != null; t = t.parent)
                {
                    if (t.GetComponent<PulseHeatmapIgnore>() != null) ignored = true;
                    if (HasInput(t.gameObject)) textInput = true;
                }
            }
            catch (Exception) { }
        }

        private static bool IsInputBelow(GameObject o)
        {
            for (var t = o.transform; t != null; t = t.parent) if (HasInput(t.gameObject)) return true;
            return false;
        }

        private static bool HasInput(GameObject o)
        {
            foreach (var component in o.GetComponents<Component>())
                if (component != null && TextInputTypes.IsTextInput(component.GetType())) return true;
            return false;
        }
    }

    // The Input System through reflection: Pointer.current (the last used mouse, pen or touch screen) with its press button and position.
    internal sealed class InputSystemPointer
    {
        private bool resolved, available;
        private PropertyInfo current, press, position, wasPressed, wasReleased;
        private MethodInfo readValue;

        internal bool Available { get { if (!resolved) Resolve(); return available; } }

        private void Resolve()
        {
            resolved = true;
            try
            {
                var pointer = Type.GetType("UnityEngine.InputSystem.Pointer, Unity.InputSystem");
                if (pointer == null) return;
                current = pointer.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                press = pointer.GetProperty("press", BindingFlags.Public | BindingFlags.Instance);
                position = pointer.GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                var button = press?.PropertyType; var vector = position?.PropertyType;
                wasPressed = button?.GetProperty("wasPressedThisFrame"); wasReleased = button?.GetProperty("wasReleasedThisFrame");
                readValue = vector?.GetMethod("ReadValue", Type.EmptyTypes);
                available = current != null && press != null && position != null && wasPressed != null && wasReleased != null && readValue != null;
            }
            catch (Exception) { available = false; }
        }

        internal bool Read(out Vector2 pixels, out bool pressed, out bool released)
        {
            pixels = default; pressed = released = false;
            try
            {
                var pointer = current.GetValue(null);
                if (pointer == null) return false;
                var button = press.GetValue(pointer);
                pressed = (bool)wasPressed.GetValue(button); released = (bool)wasReleased.GetValue(button);
                pixels = (Vector2)readValue.Invoke(position.GetValue(pointer), null);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
