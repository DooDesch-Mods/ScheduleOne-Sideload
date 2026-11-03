#if DEBUG
using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: <c>sideloadesc</c> - press Escape through the Input System and say whether the app on screen
    /// closed.
    ///
    /// The press is a keyboard state event queued on the real keyboard device, so it reaches the game's
    /// <c>PrimaryExitAction</c> the way a physical key does, and <c>GameInput.HandleExitInputs</c> applies its own
    /// <c>IsTyping</c> gate to it. That gate is what a stuck typing flag breaks, which is why a direct call to the
    /// exit listeners would prove nothing. TextMeshPro reads keys from IMGUI events and does not see this press, so
    /// a focused field is not deactivated by it either: what is tested is the game's exit path alone.
    /// </summary>
    internal static class EscapeConsole
    {
        private const int HoldFrames = 2;
        private const int SettleFrames = 15;

        private static int _lastFrame = -1;

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
            if (!parts[0].Equals("sideloadesc", StringComparison.OrdinalIgnoreCase)) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one - see KeyConsole.
            if (Time.frameCount == _lastFrame) return true;
            _lastFrame = Time.frameCount;

            MelonCoroutines.Start(Press());
            return true;
        }

        private static IEnumerator Press()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                Core.Log?.Msg("sideloadesc: FAIL no keyboard device.");
                yield break;
            }

            Phone.PhoneAppHost before = ShowingApp();
            Core.Log?.Msg($"sideloadesc: app on screen {(before == null ? "none" : before.Id)}, "
                          + $"IsTyping {Typing()}, window focused {Application.isFocused}.");

            // An unfocused game window disables the keyboard and drops every event queued on it. Test slots run in
            // the background while the user works in another window, so the device is held enabled for the press.
            InputSettings.BackgroundBehavior behaviour = InputSystem.settings.backgroundBehavior;
            if (!Application.isFocused)
            {
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                if (!keyboard.enabled) InputSystem.EnableDevice(keyboard);
            }

            if (!Queue(keyboard, true)) { InputSystem.settings.backgroundBehavior = behaviour; yield break; }
            for (int i = 0; i < HoldFrames; i++) yield return null;
            Queue(keyboard, false);
            for (int i = 0; i < SettleFrames; i++) yield return null;

            InputSystem.settings.backgroundBehavior = behaviour;

            Phone.PhoneAppHost after = ShowingApp();
            if (before == null)
                Core.Log?.Msg($"sideloadesc: FAIL no app was on screen, IsTyping {Typing()}.");
            else if (after == null)
                Core.Log?.Msg($"sideloadesc: PASS closed '{before.Id}', IsTyping {Typing()}.");
            else
                Core.Log?.Msg($"sideloadesc: FAIL '{after.Id}' still on screen, IsTyping {Typing()}.");
        }

        /// <summary>Queue the keyboard's whole state with Escape down or up. False, logged, when the event could not
        /// be built.</summary>
        private static bool Queue(Keyboard keyboard, bool down)
        {
            try
            {
                Unity.Collections.NativeArray<byte> buffer = StateEvent.From(keyboard, out InputEventPtr eventPtr);
                try
                {
                    // Il2CppSystem.Single is the float's own layout with a read-only field, so it is reinterpreted
                    // rather than constructed; boxing it gives the Il2CppSystem.Object the control expects.
                    float raw = down ? 1f : 0f;
                    Il2CppSystem.Single value = System.Runtime.CompilerServices.Unsafe.As<float, Il2CppSystem.Single>(ref raw);
                    keyboard.escapeKey.WriteValueFromObjectIntoEvent(eventPtr, value.BoxIl2CppObject());
                    InputSystem.QueueEvent(eventPtr);
                }
                finally { buffer.Dispose(); }

                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Msg($"sideloadesc: FAIL could not queue the Escape {(down ? "press" : "release")}: {e.Message}");
                return false;
            }
        }

        private static Phone.PhoneAppHost ShowingApp()
        {
            foreach (Phone.PhoneAppHost host in Phone.HomeScreenPatch.Hosts)
                if (host.IsAlive && host.IsShowing) return host;

            return null;
        }

        private static string Typing()
        {
            try { return Il2CppScheduleOne.GameInput.IsTyping ? "true" : "false"; }
            catch (Exception e) { return "unreadable (" + e.Message + ")"; }
        }
    }
}
#endif
