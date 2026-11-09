#if DEBUG
using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI.Phone;
using GamePhone = Il2CppScheduleOne.UI.Phone.Phone;
using InputSettings = UnityEngine.InputSystem.InputSettings;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: <c>sideloadpad</c> - drive the phone home screen with a virtual gamepad, for test slots that have
    /// no controller plugged in.
    ///
    /// <c>sideloadpad open</c> opens the phone. <c>sideloadpad down|up|left|right [n]</c> presses the d-pad n times
    /// (default 1). <c>sideloadpad dump</c> logs where the icon grid sits against the phone screen and names the
    /// selected icon. <c>sideloadpad off</c> removes the virtual device again.
    ///
    /// The presses are state events on a real Input System device, so PlayerInput switches its control scheme to
    /// Gamepad the way a physical controller does and the game's UI navigation reads them through its own actions.
    /// </summary>
    internal static class PadConsole
    {
        private const int HoldFrames = 3;
        private const int GapFrames = 12;

        private static int _lastFrame = -1;
        private static Gamepad _pad;

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;

            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            if (!parts[0].Equals("sideloadpad", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one - see KeyConsole.
            if (Time.frameCount == _lastFrame) return true;
            _lastFrame = Time.frameCount;

            string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "dump";
            int count = 1;
            if (parts.Length > 2 && (!int.TryParse(parts[2], out count) || count < 1))
            {
                Core.Log?.Warning($"sideloadpad: '{parts[2]}' is not a press count - using 1.");
                count = 1;
            }

            try
            {
                switch (verb)
                {
                    case "open": OpenPhone(); break;
                    case "dump": Dump(); break;
                    case "off": RemovePad(); break;
                    case "fix": Fix(parts.Length > 2 ? parts[2].ToLowerInvariant() : ""); break;
                    case "up":
                    case "down":
                    case "left":
                    case "right":
                    case "a":
                        MelonCoroutines.Start(Press(verb, count));
                        break;
                    default:
                        Core.Log?.Warning($"sideloadpad: unknown '{verb}'. Use open, dump, off, up, down, left, right, a.");
                        break;
                }
            }
            catch (Exception e) { Core.Log?.Error("sideloadpad failed: " + e); }

            return true;
        }

        private static void OpenPhone()
        {
            GamePhone phone = PlayerSingleton<GamePhone>.Instance;
            if (phone == null) { Core.Log?.Warning("sideloadpad open: no phone (is a save loaded?)."); return; }
            phone.SetIsOpen(true);
            Core.Log?.Msg("sideloadpad open: phone opened.");
        }

        private static void Fix(string mode)
        {
            HomeScreen home = PlayerSingleton<HomeScreen>.Instance;
            if (home == null) { Core.Log?.Warning("sideloadpad fix: no HomeScreen."); return; }

            if (mode == "off")
            {
                Phone.HomeGamepadScroll.Disabled = true;
                Phone.HomeGamepadScroll.Revert(home);
            }
            else if (mode == "on")
            {
                Phone.HomeGamepadScroll.Disabled = false;
                Phone.HomeGamepadScroll.Apply(home);
                Core.Log?.Msg("sideloadpad fix on: repairs applied.");
            }
            else Core.Log?.Msg("usage: sideloadpad fix on|off");
        }

        private static Gamepad Pad()
        {
            if (_pad != null && _pad.added) return _pad;
            _pad = InputSystem.AddDevice<Gamepad>("SideloadVirtualPad");
            Core.Log?.Msg($"sideloadpad: virtual gamepad added ({_pad.name}).");
            return _pad;
        }

        private static void RemovePad()
        {
            if (_pad == null || !_pad.added) { Core.Log?.Msg("sideloadpad off: no virtual gamepad to remove."); return; }
            InputSystem.RemoveDevice(_pad);
            _pad = null;
            Core.Log?.Msg("sideloadpad off: virtual gamepad removed.");
        }

        private static IEnumerator Press(string verb, int count)
        {
            Gamepad pad = Pad();

            // Test slots run in the background; an unfocused window drops events queued on devices.
            InputSettings.BackgroundBehavior behaviour = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;

            ButtonControl button = verb switch
            {
                "up" => pad.dpad.up,
                "down" => pad.dpad.down,
                "left" => pad.dpad.left,
                "right" => pad.dpad.right,
                _ => pad.buttonSouth,
            };

            for (int i = 0; i < count; i++)
            {
                if (!Queue(pad, button, true)) break;
                for (int f = 0; f < HoldFrames; f++) yield return null;
                Queue(pad, button, false);
                for (int f = 0; f < GapFrames; f++) yield return null;
            }

            InputSystem.settings.backgroundBehavior = behaviour;
            Core.Log?.Msg($"sideloadpad: pressed {verb} x{count}, input device {Il2CppScheduleOne.GameInput.CurrentInputDevice}.");
            Dump();
        }

        private static bool Queue(Gamepad pad, ButtonControl button, bool down)
        {
            try
            {
                Unity.Collections.NativeArray<byte> buffer = StateEvent.From(pad, out InputEventPtr eventPtr);
                try
                {
                    float raw = down ? 1f : 0f;
                    Il2CppSystem.Single value = System.Runtime.CompilerServices.Unsafe.As<float, Il2CppSystem.Single>(ref raw);
                    button.WriteValueFromObjectIntoEvent(eventPtr, value.BoxIl2CppObject());
                    InputSystem.QueueEvent(eventPtr);
                }
                finally { buffer.Dispose(); }
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error($"sideloadpad: could not queue {button?.name} {(down ? "press" : "release")}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Log the grid against the screen it lives on: the scroll viewport (when a scroll mod made one), the icon
        /// container, the selected icon, and whether the grid's or the icon's rect leaves the visible screen.
        /// </summary>
        internal static void Dump()
        {
            HomeScreen home = PlayerSingleton<HomeScreen>.Instance;
            if (home == null) { Core.Log?.Warning("sideloadpad dump: no HomeScreen."); return; }

            RectTransform icons = home.appIconContainer;
            if (icons == null) { Core.Log?.Warning("sideloadpad dump: HomeScreen has no icon container."); return; }

            RectTransform screen = home.GetComponent<RectTransform>();
            ScrollRect scroll = icons.GetComponentInParent<ScrollRect>();
            RectTransform viewport = scroll != null ? scroll.viewport : null;
            RectTransform frame = viewport != null ? viewport : screen;

            Rect frameRect = Local(frame, frame);
            Rect gridRect = Local(icons, frame);
            Core.Log?.Msg($"sideloadpad dump: icons {icons.childCount}, parent '{icons.parent?.name}', "
                          + $"scroll {(scroll != null ? scroll.name : "none")}, frame '{frame.name}' {Fmt(frameRect)}, "
                          + $"grid {Fmt(gridRect)}, grid localPos {icons.localPosition}, "
                          + $"normPos {(scroll != null ? scroll.verticalNormalizedPosition.ToString("0.00") : "-")}, "
                          + $"input {Il2CppScheduleOne.GameInput.CurrentInputDevice}.");

            GameObject selected = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (selected == null) { Core.Log?.Msg("sideloadpad dump: nothing selected."); return; }

            var rect = selected.GetComponent<RectTransform>();
            if (rect == null || !rect.IsChildOf(icons))
            {
                Core.Log?.Msg($"sideloadpad dump: selected '{selected.name}' is not a home icon.");
                return;
            }

            Rect iconRect = Local(rect, frame);
            bool inside = iconRect.yMin >= frameRect.yMin - 1f && iconRect.yMax <= frameRect.yMax + 1f;
            MaskableGraphic outline = rect.Find("Outline")?.GetComponent<MaskableGraphic>();
            string frameText = outline == null ? "no Outline"
                : $"Outline maskable={outline.maskable} alpha={outline.canvasRenderer.GetAlpha() * outline.color.a:0.00}";
            bool leaks = !inside && outline != null && !outline.maskable && outline.canvasRenderer.GetAlpha() * outline.color.a > 0.01f;
            Core.Log?.Msg($"sideloadpad dump: selected icon '{selected.name}' (#{rect.GetSiblingIndex()}) {Fmt(iconRect)} "
                          + (inside ? "inside the frame" : "OUTSIDE the frame") + $", {frameText}"
                          + (leaks ? " - its frame draws outside the phone screen." : "."));
        }

        private static Rect Local(RectTransform target, RectTransform space)
        {
            var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            target.GetWorldCorners(corners);
            Vector3 a = space.InverseTransformPoint(corners[0]);
            Vector3 b = space.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private static string Fmt(Rect r) => $"y {r.yMin:0}..{r.yMax:0} x {r.xMin:0}..{r.xMax:0}";
    }
}
#endif
