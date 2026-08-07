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

        /// <summary>One declaration with its keycode already looked up. Resolving at publish time rather than per
        /// frame means a name the keycode table does not know is dropped once, loudly, instead of silently failing
        /// to match sixty times a second.</summary>
        internal readonly struct Bound
        {
            internal Bound(KeyDeclaration key, KeyCode code)
            {
                Key = key;
                Code = code;
            }

            internal KeyDeclaration Key { get; }

            internal KeyCode Code { get; }
        }

        /// <summary>
        /// Which field declared what, keyed by the field's instance id.
        ///
        /// Flat and global rather than per view because the question the caret guard asks arrives from a Harmony
        /// prefix that knows only the TMP_InputField it was called on. Each view withdraws its own ids before
        /// publishing new ones, so a rebuild replaces its entries instead of piling more on.
        /// </summary>
        private static readonly Dictionary<int, Bound[]> Declared = new Dictionary<int, Bound[]>();

        private KeyDeclaration _held;
        private KeyCode _heldCode;
        private bool _holding;
        private float _pressedAt;
        private float _nextAt;

        /// <summary>
        /// Record what a freshly painted field declared, with every keycode resolved. Called once per render.
        ///
        /// Returns the bound set so the caller can hand the same array straight to <see cref="Tick"/> instead of
        /// looking it up again every frame.
        /// </summary>
        internal static Bound[] Publish(int fieldId, KeyDeclarationSet keys)
        {
            if (keys == null || keys.Count == 0)
            {
                Declared.Remove(fieldId);
                return Array.Empty<Bound>();
            }

            var bound = new List<Bound>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                KeyDeclaration key = keys[i];
                if (!KeyCodes.TryResolve(key.Name, out KeyCode code))
                {
                    Core.Log?.Error($"[Sideload] key '{key.Name}' parsed but has no keycode - it will never fire.");
                    continue;
                }

                bound.Add(new Bound(key, code));
            }

            if (bound.Count == 0)
            {
                Declared.Remove(fieldId);
                return Array.Empty<Bound>();
            }

            Bound[] result = bound.ToArray();
            Declared[fieldId] = result;
            return result;
        }

        internal static void Withdraw(int fieldId) => Declared.Remove(fieldId);

        /// <summary>
        /// Asked by the caret guard: does the page own this key RIGHT NOW, so TextMeshPro must keep its hands off?
        ///
        /// Modifiers are part of the question, because for some keys the plain press and the modified press are
        /// different features that both have to work. `Ctrl+Backspace` deletes a word - TMP has no such thing and
        /// would delete one character - while a plain Backspace must stay TMP's, or the field stops erasing.
        ///
        /// Declaring a key with NO modifier claims it whenever no modifier is held, which leaves Shift+Up free to
        /// select text on a page that only asked for Up.
        /// </summary>
        internal static bool Suppresses(TMP_InputField field, string keyName)
        {
            if (field == null || !Declared.TryGetValue(field.GetInstanceID(), out Bound[] keys)) return false;

            for (int i = 0; i < keys.Length; i++)
            {
                if (!string.Equals(keys[i].Key.Name, keyName, StringComparison.Ordinal)) continue;
                if (ModifiersHeld(keys[i].Key)) return true;
            }

            return false;
        }

        /// <summary>
        /// One frame of keyboard for one focused field. Returns true when a key fired, with
        /// <paramref name="repeat"/> telling the page whether this is the player holding it down.
        ///
        /// At most one key per frame, which is what a keyboard can actually produce and what keeps a page from having
        /// to reason about two completions arriving together.
        /// </summary>
        internal bool Tick(TMP_InputField field, Bound[] keys, out KeyDeclaration fired, out bool repeat)
        {
            fired = default;
            repeat = false;

            // Not focused means not typing here: a page must not walk its list because the player pressed Up while
            // driving. isFocused rather than a remembered flag, because focus can move to another view's field
            // without this one hearing about it.
            if (field == null || keys == null || keys.Length == 0 || !field.isFocused)
            {
                _holding = false;
                return false;
            }

            // Unscaled, because a page is usable while the game is paused and a scaled clock would freeze the repeat
            // exactly when a menu-like app is most likely to be open.
            float now = Time.unscaledTime;

            for (int i = 0; i < keys.Length; i++)
            {
                Bound bound = keys[i];
                if (!UnityEngine.Input.GetKeyDown(bound.Code) || !ModifiersHeld(bound.Key)) continue;

                _held = bound.Key;
                _heldCode = bound.Code;
                _holding = true;
                _pressedAt = now;
                _nextAt = now + RepeatDelay;

                fired = bound.Key;
                return true;
            }

            if (!_holding) return false;

            // Released, or a modifier let go mid-hold - either way the gesture is over. Checking the modifiers here
            // too is what stops Ctrl+R firing on after Ctrl comes up while R is still down.
            if (!UnityEngine.Input.GetKey(_heldCode) || !ModifiersHeld(_held))
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

        /// <summary>A second physical key that means the same thing. Only the number pad has one worth honouring: a
        /// player pressing the big Enter and a player pressing the one by the numbers mean Enter.</summary>
        private static readonly Dictionary<string, KeyCode> Alternates =
            new Dictionary<string, KeyCode>(StringComparer.Ordinal) { ["Enter"] = KeyCode.KeypadEnter };

        internal static bool TryResolve(string name, out KeyCode code)
        {
            if (!string.IsNullOrEmpty(name) && Map.TryGetValue(name, out code)) return true;

            code = KeyCode.None;
            return false;
        }

        internal static bool TryResolveAlternate(string name, out KeyCode code)
        {
            if (!string.IsNullOrEmpty(name) && Alternates.TryGetValue(name, out code)) return true;

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

            // Global only - a page may not declare Enter, because a focused field already delivers it as its own
            // submit. See GlobalKey for why the same name means something different outside a page.
            map["Enter"] = KeyCode.Return;

            // The vocabulary is the authority on what an app may declare; this table only has to keep up. A name
            // added there and forgotten here would parse cleanly, publish cleanly and never fire, so it says so at
            // startup rather than costing an evening.
            foreach (string name in GlobalKey.Vocabulary)
                if (!map.ContainsKey(name))
                    Core.Log?.Error($"[Sideload] key '{name}' is declarable but has no keycode - it will never fire.");

            return map;
        }
    }
}
