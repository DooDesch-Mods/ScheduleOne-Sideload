#if DEBUG
using System.Collections;
using System.Collections.Generic;
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
        // Real time, not frames: the Input System can update slower than the game renders, and a press shorter than
        // one input update is merged with its release and never seen.
        private const float HoldSeconds = 0.1f;
        private const float GapSeconds = 0.25f;

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
            if (verb != "fix" && parts.Length > 2 && (!int.TryParse(parts[2], out count) || count < 1))
            {
                Core.Log?.Warning($"sideloadpad: '{parts[2]}' is not a press count - using 1.");
                count = 1;
            }

            try
            {
                switch (verb)
                {
                    case "open": OpenPhone(); break;
                    case "close":
                        Core.Log?.Msg(Phone.PhoneScreen.IsRaised
                            ? (Phone.PhoneScreen.Lower() ? "sideloadpad close: phone put away." : "sideloadpad close: the game refused to lower the phone.")
                            : "sideloadpad close: phone is not in hand.");
                        break;
                    case "dump": Dump(); break;
                    case "off": RemovePad(); break;
                    case "fix": Fix(parts.Length > 2 ? parts[2].ToLowerInvariant() : ""); break;
                    case "select": Select(count - 1); break;
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
            if (PlayerSingleton<GamePhone>.Instance == null) { Core.Log?.Warning("sideloadpad open: no phone (is a save loaded?)."); return; }
            if (Phone.PhoneScreen.IsRaised) { Core.Log?.Msg("sideloadpad open: phone already in hand."); return; }
            Core.Log?.Msg(Phone.PhoneScreen.Raise() ? "sideloadpad open: phone raised." : "sideloadpad open: the game refused to raise the phone.");
        }

        /// <summary>Select home icon <paramref name="index"/> (0-based) and put the grid back at the top.</summary>
        private static void Select(int index)
        {
            HomeScreen home = PlayerSingleton<HomeScreen>.Instance;
            RectTransform icons = home != null ? home.appIconContainer : null;
            if (icons == null || home.uiPanel == null) { Core.Log?.Warning("sideloadpad select: no home screen panel."); return; }
            if (index < 0 || index >= icons.childCount) { Core.Log?.Warning($"sideloadpad select: no icon #{index} ({icons.childCount} icons)."); return; }

            GamePhone phone = PlayerSingleton<GamePhone>.Instance;
            if (phone != null && phone.IsAnyAppOpen)
            {
                phone.RequestCloseApp();
                Core.Log?.Msg("sideloadpad select: closed the open app to reach the home screen.");
            }

            var selectable = icons.GetChild(index).GetComponent<Il2CppScheduleOne.UISelectable>();
            if (selectable == null) { Core.Log?.Warning($"sideloadpad select: icon #{index} is not selectable."); return; }

            ScrollRect scroll = icons.GetComponentInParent<ScrollRect>();
            if (scroll != null) scroll.verticalNormalizedPosition = 1f;
            // Navigation runs in the screen's current panel, which may be the hotbar after a press left the grid.
            if (home.uiPanel.ParentScreen != null)
                home.uiPanel.ParentScreen.SetCurrentSelectedPanel(home.uiPanel, selectable, false, true);
            else
                Core.Log?.Warning("sideloadpad select: home panel has no parent screen - selecting inside the panel only.");
            home.uiPanel.SelectSelectable(selectable, false);
            Core.Log?.Msg($"sideloadpad select: icon #{index} '{selectable.name}' selected, grid at the top.");
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
            // Test slots run unfocused, and the default background behaviour disables devices while the window has no
            // focus - a pad added then never delivers a press. Focus is ignored for as long as the pad exists.
            if (_savedBehaviour == null)
            {
                _savedBehaviour = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            }

            if (_pad == null || !_pad.added)
            {
                _pad = InputSystem.AddDevice<Gamepad>("SideloadVirtualPad");
                Core.Log?.Msg($"sideloadpad: virtual gamepad added ({_pad.name}).");
            }

            // With focus ignored, the desktop's own mouse and keyboard reach the test game too, and the game's
            // PlayerInput switches back to keyboard and mouse at every move. They stay off while the pad exists.
            MuteDesktopInput();
            if (!_pad.enabled)
            {
                InputSystem.EnableDevice(_pad);
                Core.Log?.Msg($"sideloadpad: virtual gamepad was disabled - enabled it (now {_pad.enabled}).");
            }
            return _pad;
        }

        private static InputSettings.BackgroundBehavior? _savedBehaviour;
        private static readonly List<InputDevice> _muted = new List<InputDevice>();

        private static void MuteDesktopInput()
        {
            foreach (InputDevice device in new InputDevice[] { Mouse.current, Keyboard.current })
            {
                if (device == null || !device.enabled || _muted.Contains(device)) continue;
                InputSystem.DisableDevice(device);
                _muted.Add(device);
                Core.Log?.Msg($"sideloadpad: {device.name} disabled while the virtual gamepad is in use.");
            }
        }

        private static void UnmuteDesktopInput()
        {
            foreach (InputDevice device in _muted)
            {
                if (device == null || !device.added) { Core.Log?.Warning("sideloadpad off: a disabled desktop device is gone, nothing to re-enable."); continue; }
                InputSystem.EnableDevice(device);
                Core.Log?.Msg($"sideloadpad off: {device.name} enabled again.");
            }
            _muted.Clear();
        }

        private static void RemovePad()
        {
            UnmuteDesktopInput();
            if (_savedBehaviour != null)
            {
                InputSystem.settings.backgroundBehavior = _savedBehaviour.Value;
                _savedBehaviour = null;
            }
            if (_pad == null || !_pad.added) { Core.Log?.Msg("sideloadpad off: no virtual gamepad to remove."); return; }
            InputSystem.RemoveDevice(_pad);
            _pad = null;
            Core.Log?.Msg("sideloadpad off: virtual gamepad removed.");
        }

        private static IEnumerator Press(string verb, int count)
        {
            Gamepad pad = Pad();

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
                for (float t = Time.unscaledTime + HoldSeconds; Time.unscaledTime < t;) yield return null;
                if (i == 0)
                    Core.Log?.Msg($"sideloadpad: holding {verb}: pressed={button.isPressed}, pad enabled={pad.enabled}, current pad={(Gamepad.current == pad)}, focused={Application.isFocused}, background={InputSystem.settings.backgroundBehavior}, update={InputSystem.settings.updateMode}, fps={(1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f)):0}.");
                Queue(pad, button, false);
                for (float t = Time.unscaledTime + GapSeconds; Time.unscaledTime < t;) yield return null;
            }

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
