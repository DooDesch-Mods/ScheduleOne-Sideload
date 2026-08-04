using System.Collections.Generic;
using Il2CppTMPro;
using Sideload.Model;
using UnityEngine;

namespace Sideload.Input
{
    /// <summary>
    /// The keyboard an app asked for.
    ///
    /// Sideload has always delivered exactly one key - Enter, out of the field's own submit - because that is the one
    /// every form depends on and it arrives without reading the keyboard at all. A terminal, an editor or a list that
    /// walks with the arrows needs more, and the honest way to give it more is to let the page say which keys it
    /// wants:
    ///
    /// <code>&lt;input data-keys="Tab ArrowUp ArrowDown Ctrl+R"&gt;</code>
    ///
    /// Only declared keys are read, only declared keys are dispatched, and only declared keys are taken away from the
    /// field. A page that declares nothing behaves exactly as it did before this existed, which is the whole point of
    /// the declaration: the alternative - forwarding every keystroke - makes every page pay for the feature and lets
    /// a careless one swallow keys the player bound to something else.
    ///
    /// <para><b>A declared key is always swallowed.</b> <c>preventDefault()</c> is on the event for symmetry with
    /// <c>back</c>, but it cannot un-move a caret: the field processes keys from the EventSystem, and nothing
    /// guarantees this runs first. So the decision is made where it can be made reliably - at declaration time. A
    /// page that wants the caret to keep moving simply does not declare the arrows.</para>
    ///
    /// <para>Reading the keyboard through the legacy <c>Input</c> class rather than the input system the game
    /// otherwise uses is deliberate: these are raw keyboard keys, not rebindable game actions, and there is no action
    /// asset to read for "Tab". The dev overlay already reads F9 the same way, so the legacy path is known to work in
    /// this build.</para>
    /// </summary>
    internal sealed class Keys
    {
        // The curve is lifted from the console autocomplete mod, where it was tuned against a real suggestion list
        // rather than guessed: long enough that a tap never repeats, short enough that holding walks the list, and a
        // second gear so a hundred-item list stays reachable without the key feeling stuck.
        private const float RepeatDelay = 0.35f;
        private const float RepeatInterval = 0.06f;
        private const float SecondGearAfter = 1.2f;
        private const float SecondGearInterval = 0.03f;

        /// <summary>
        /// Which field declared what, keyed by the field's instance id.
        ///
        /// Flat and global rather than per view because the question the caret guard asks arrives from a Harmony
        /// prefix that knows only the TMP_InputField it was called on. Each view withdraws its own ids before
        /// publishing new ones, so a rebuild replaces its entries instead of piling more on.
        /// </summary>
        private static readonly Dictionary<int, KeyDeclarationSet> Declared = new Dictionary<int, KeyDeclarationSet>();

        private KeyDeclaration _held;
        private bool _holding;
        private float _pressedAt;
        private float _nextAt;

        /// <summary>Record what a freshly painted field declared. Called once per render, not per frame.</summary>
        internal static void Publish(int fieldId, KeyDeclarationSet keys)
        {
            if (keys == null || keys.Count == 0) Declared.Remove(fieldId);
            else Declared[fieldId] = keys;
        }

        internal static void Withdraw(int fieldId) => Declared.Remove(fieldId);

        /// <summary>
        /// Asked by the caret guard: does the page own this key, so TextMeshPro must keep its hands off?
        ///
        /// Modifiers are deliberately not compared. TMP moves the caret on Up whether or not Ctrl is down, so a page
        /// that declared <c>Ctrl+ArrowUp</c> and nothing else would still watch its caret jump on the plain key. One
        /// declaration for a key claims that key.
        /// </summary>
        internal static bool Suppresses(TMP_InputField field, string keyName)
        {
            if (field == null) return false;

            return Declared.TryGetValue(field.GetInstanceID(), out KeyDeclarationSet keys) && keys.Uses(keyName);
        }

        /// <summary>
        /// One frame of keyboard for one focused field. Returns true when a key fired, with
        /// <paramref name="repeat"/> telling the page whether this is the player holding it down.
        ///
        /// At most one key per frame, which is what a keyboard can actually produce and what keeps a page from having
        /// to reason about two completions arriving together.
        /// </summary>
        internal bool Tick(TMP_InputField field, KeyDeclarationSet keys, out KeyDeclaration fired, out bool repeat)
        {
            fired = default;
            repeat = false;

            // Not focused means not typing here: a page must not walk its list because the player pressed Up while
            // driving. isFocused rather than a remembered flag, because focus can move to another view's field
            // without this one hearing about it.
            if (field == null || keys == null || keys.Count == 0 || !field.isFocused)
            {
                _holding = false;
                return false;
            }

            // Unscaled, because a page is usable while the game is paused and a scaled clock would freeze the repeat
            // exactly when a menu-like app is most likely to be open.
            float now = Time.unscaledTime;

            for (int i = 0; i < keys.Count; i++)
            {
                KeyDeclaration key = keys[i];
                if (!KeyCodes.TryResolve(key.Name, out KeyCode code)) continue;
                if (!UnityEngine.Input.GetKeyDown(code) || !ModifiersHeld(key)) continue;

                _held = key;
                _holding = true;
                _pressedAt = now;
                _nextAt = now + RepeatDelay;

                fired = key;
                return true;
            }

            if (!_holding) return false;

            // Released, or a modifier let go mid-hold - either way the gesture is over. Checking the modifiers here
            // too is what stops Ctrl+R firing on after Ctrl comes up while R is still down.
            if (!KeyCodes.TryResolve(_held.Name, out KeyCode heldCode)
                || !UnityEngine.Input.GetKey(heldCode) || !ModifiersHeld(_held))
            {
                _holding = false;
                return false;
            }

            if (now < _nextAt) return false;

            _nextAt = now + (now - _pressedAt >= SecondGearAfter ? SecondGearInterval : RepeatInterval);

            fired = _held;
            repeat = true;
            return true;
        }

        private static bool ModifiersHeld(KeyDeclaration key) =>
            key.Ctrl == Held(KeyCode.LeftControl, KeyCode.RightControl)
            && key.Shift == Held(KeyCode.LeftShift, KeyCode.RightShift)
            && key.Alt == Held(KeyCode.LeftAlt, KeyCode.RightAlt);

        private static bool Held(KeyCode a, KeyCode b) =>
            UnityEngine.Input.GetKey(a) || UnityEngine.Input.GetKey(b);
    }

    /// <summary>
    /// Canonical key name to Unity keycode.
    ///
    /// Built by walking <see cref="KeyDeclarationSet.Vocabulary"/> rather than by writing the list out a second time:
    /// a name added to the vocabulary and forgotten here would parse cleanly, publish cleanly and then never fire,
    /// which is the kind of bug that takes an evening. This way it says so in the log at startup instead.
    /// </summary>
    internal static class KeyCodes
    {
        private static readonly Dictionary<string, KeyCode> Map = Build();

        internal static bool TryResolve(string name, out KeyCode code)
        {
            if (!string.IsNullOrEmpty(name) && Map.TryGetValue(name, out code)) return true;

            code = KeyCode.None;
            return false;
        }

        private static Dictionary<string, KeyCode> Build()
        {
            var map = new Dictionary<string, KeyCode>(StringComparer.Ordinal);

            for (char c = 'a'; c <= 'z'; c++) map[c.ToString()] = KeyCode.A + (c - 'a');
            for (char c = '0'; c <= '9'; c++) map[c.ToString()] = KeyCode.Alpha0 + (c - '0');

            for (int i = 1; i <= 12; i++) map["F" + i] = KeyCode.F1 + (i - 1);

            map["Tab"] = KeyCode.Tab;
            map["Backspace"] = KeyCode.Backspace;
            map["Delete"] = KeyCode.Delete;
            map["Insert"] = KeyCode.Insert;
            map["Home"] = KeyCode.Home;
            map["End"] = KeyCode.End;
            map["PageUp"] = KeyCode.PageUp;
            map["PageDown"] = KeyCode.PageDown;
            map["ArrowUp"] = KeyCode.UpArrow;
            map["ArrowDown"] = KeyCode.DownArrow;
            map["ArrowLeft"] = KeyCode.LeftArrow;
            map["ArrowRight"] = KeyCode.RightArrow;
            map["Space"] = KeyCode.Space;

            foreach (string name in KeyDeclarationSet.Vocabulary)
                if (!map.ContainsKey(name))
                    Core.Log?.Error($"[Sideload] key '{name}' is declarable but has no keycode - it will never fire.");

            return map;
        }
    }
}
