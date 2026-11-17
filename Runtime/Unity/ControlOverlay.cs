#if PULSE_UGUI
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Mightys.Pulse
{
    // Draws the built-in App Control screen from the state of Pulse.Control. Driven by the lifecycle component once per frame, on
    // the main thread. Nothing exists while the state is Normal: the canvas is created for a state to show and removed when it ends.
    // It never changes Time.timeScale or the audio, and it never closes the app. The cursor is shown while a covering screen is up and
    // put back as it was afterwards.
    internal sealed class ControlOverlay
    {
        // Set by the platform when the client is created: only Automatic draws anything.
        internal static volatile ControlMode Mode = ControlMode.Automatic;
        // A state shown regardless of the server, for design checks (see PulseControlPreview).
        internal static volatile ControlState Preview;
        internal static volatile bool PreviewDark = true;
        internal static volatile bool PreviewTheme;

        private ControlOverlayView view;
        private ControlState shown = ControlState.Normal;
        private ControlPlan plan = ControlOverlayRules.Nothing;
        private GameObject ownedEventSystem;
        private Task checking;
        private bool cursorSaved, previousVisible;
        private CursorLockMode previousLock;
        private int lastWidth, lastHeight;
        private Rect lastSafe;
        private int settleFrames;

        /// <summary>Called every frame. Never throws.</summary>
        internal void Tick()
        {
            try
            {
                var state = StateNow();
                if (!ReferenceEquals(state, shown)) { shown = state; Rebuild(); return; }
                if (view == null) return;
                if (Screen.width != lastWidth || Screen.height != lastHeight || Screen.safeArea != lastSafe || settleFrames > 0)
                {
                    if (settleFrames > 0) settleFrames--;
                    Remember(); view.Relayout();
                }
                if (plan.Modal) EnsureEventSystem();
                if (checking != null && checking.IsCompleted) { checking = null; view.SetPrimaryEnabled(true); }
            }
            catch (Exception) { }
        }

        internal void Dispose() { try { Close(); } catch (Exception) { } }

        private static ControlState StateNow()
        {
            var preview = Preview;
            if (preview != null) return preview;
            return Mode == ControlMode.Automatic ? Pulse.Control.State : ControlState.Normal;
        }

        private void Rebuild()
        {
            var next = ControlOverlayRules.Plan(shown);
            var wasModal = plan.Modal;
            plan = next;
            if (!next.Visible) { Close(); return; }
            if (view == null)
            {
                view = new ControlOverlayView();
                view.OnPrimary = Primary; view.OnSecondary = Secondary;
            }
            var dark = Preview != null && PreviewTheme ? PreviewDark : SystemDark();
            view.Show(next, ControlTheme.For(dark));
            SdkUiState.Set(true, next.Modal, view.Covers);
            view.SetPrimaryEnabled(checking == null);
            Remember(); settleFrames = 2;
            if (next.Modal && !wasModal) { SaveCursor(); EnsureEventSystem(); }
            else if (!next.Modal && wasModal) { RestoreCursor(); RemoveEventSystem(); }
        }

        private static bool? SystemDark()
        {
            try { return UnityEnvironmentInfo.SystemDark(); } catch (Exception) { return null; }
        }

        private void Remember() { lastWidth = Screen.width; lastHeight = Screen.height; lastSafe = Screen.safeArea; }

        private void Close()
        {
            plan = ControlOverlayRules.Nothing; shown = ControlState.Normal; checking = null;
            SdkUiState.Clear();
            if (view != null) { view.Destroy(); view = null; }
            RestoreCursor(); RemoveEventSystem();
        }

        // ---- buttons ----

        private void Primary() { Act(plan.Primary); }
        private void Secondary() { Act(plan.Secondary); }

        private void Act(ControlAction action)
        {
            switch (action)
            {
                case ControlAction.OpenUrl:
                    if (Preview != null) { if (!string.IsNullOrEmpty(shown.Url)) Application.OpenURL(shown.Url); }
                    else Pulse.Control.OpenStore();
                    break;
                case ControlAction.CheckNow:
                    if (checking != null) return;
                    if (Preview != null) return;
                    checking = Pulse.Control.CheckNowAsync();
                    view?.SetPrimaryEnabled(false);
                    break;
                case ControlAction.Dismiss:
                    if (Preview != null) Preview = null; else Pulse.Control.Dismiss();
                    break;
            }
        }

        // ---- cursor ----

        private void SaveCursor()
        {
            if (cursorSaved) return;
            previousVisible = Cursor.visible; previousLock = Cursor.lockState; cursorSaved = true;
            Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        }

        private void RestoreCursor()
        {
            if (!cursorSaved) return;
            cursorSaved = false;
            Cursor.lockState = previousLock; Cursor.visible = previousVisible;
        }

        // ---- event system ----

        // Buttons need an event system. When the scene has none one is created for as long as a covering screen is up.
        private void EnsureEventSystem()
        {
            if (EventSystem.current != null || ownedEventSystem != null) return;
            var go = new GameObject("MightyS Pulse EventSystem") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            AddInputModule(go);
            ownedEventSystem = go;
        }

        private static void AddInputModule(GameObject go)
        {
#if ENABLE_INPUT_SYSTEM
            // The new Input System is looked up by name: this package has no hard dependency on it.
            var type = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (type != null)
            {
                var module = go.AddComponent(type);
                try { type.GetMethod("AssignDefaultActions")?.Invoke(module, null); } catch (Exception) { }
                return;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER || !ENABLE_INPUT_SYSTEM
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        private void RemoveEventSystem()
        {
            if (ownedEventSystem == null) return;
            try { UnityEngine.Object.Destroy(ownedEventSystem); } catch (Exception) { }
            ownedEventSystem = null;
        }
    }
}
#else
using System;
using UnityEngine;

namespace Mightys.Pulse
{
    // uGUI (com.unity.ugui) is not in this project, so there is no built-in App Control screen: Automatic behaves like Manual.
    // Pulse.Control.State and Changed still work. One debug line says so the first time a screen would have been shown.
    internal sealed class ControlOverlay
    {
        internal static volatile ControlMode Mode = ControlMode.Automatic;
        internal static volatile ControlState Preview;
        internal static volatile bool PreviewDark = true;
        internal static volatile bool PreviewTheme;
        private static bool noted;

        internal void Tick()
        {
            try
            {
                if (noted) return;
                var state = Preview ?? (Mode == ControlMode.Automatic ? Pulse.Control.State : ControlState.Normal);
                if (state == null || state.Kind == ControlKind.Normal) return;
                noted = true;
                if (Debug.isDebugBuild) Debug.Log("[Pulse] The built-in App Control screen needs the uGUI package (com.unity.ugui). It is not installed, so no screen is shown; read Pulse.Control.State and draw your own.");
            }
            catch (Exception) { }
        }

        internal void Dispose() { }
    }
}
#endif
