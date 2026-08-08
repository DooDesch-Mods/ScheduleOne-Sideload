using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Input;
using Object = UnityEngine.Object;
using Il2CppBindingList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.UI.Input.InputPromptsBindingData>;
using Il2CppStringList = Il2CppSystem.Collections.Generic.List<string>;

namespace Sideload.Phone
{
    /// <summary>
    /// The one thing Sideload puts on screen for the player: a "Rotate Phone" line in the game's own key-hint strip,
    /// for as long as an app that can be turned is open.
    ///
    /// It goes there rather than into the app because the app's screen belongs to whoever wrote it, and because the
    /// game already teaches Tab and Escape in exactly that spot - a player who has read one has read the others.
    ///
    /// The line is a CLONE of one the game drew, so it matches the neighbouring lines without this file knowing a
    /// single font, colour or offset. <see cref="InputPromptsItemUI"/> draws the key badges from binding data, and
    /// <see cref="InputPromptsManager"/> resolves that data from the bound actions - so the badge shows whatever the
    /// player has rotate bound to, and keeps showing the right thing after a rebind.
    ///
    /// 0.4.6f11 replaced InputPromptsCanvas with InputPromptsManager + InputPromptsUI, and the strip's rows are
    /// InputPromptsItemUI now rather than InputPrompt. Both the row we copy and the container we copy it into are
    /// therefore found by looking at what the game currently has on screen, not by reaching for a named singleton
    /// field - which also means the line follows the game when it swaps the whole panel out.
    /// </summary>
    internal static class TurnPrompt
    {
        private const string RowName = "SideloadTurnPrompt";

        private static GameObject _row;
        private static RectTransform _hostModule;   // the module we attached to, so a swap is noticed
        private static bool _warned;

        /// <summary>Put the line up, or take it down. Called every frame; doing nothing is the common case, and it has
        /// to STAY cheap - the two lookups behind it walk the scene, so neither may run on an ordinary frame.</summary>
        internal static void Show(bool wanted)
        {
            if (!wanted && _row == null) return;   // nothing wanted, nothing up: the overwhelmingly common frame

            RectTransform module = CurrentModule();

            // The game swaps the whole panel out when the player's context changes, taking our line with it. Noticing
            // that the container is a different object is what gets it back.
            if (_row != null && (!wanted || module == null || module != _hostModule)) Remove();
            if (!wanted || module == null || _row != null) return;

            try { Build(module); }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] could not add the turn hint to the input prompts: " + e.Message);
                Remove();
            }
        }

        private static RectTransform _moduleCache;
        private static float _moduleCacheAt = float.NegativeInfinity;
        private const float ModuleRescanInterval = 0.25f;   // four scene scans a second, only while an app is open

        /// <summary>How many empty sweeps before the hint gives up. Reset by a scene change, same as the actions.</summary>
        private const int MaxModuleMisses = 8;

        private static int _moduleMisses;

        /// <summary>The row container the game is currently drawing into - i.e. the parent of whatever prompt rows are
        /// live. Derived from the rows themselves because the panel objects are pooled and their containers private.
        ///
        /// That derivation costs a scene scan, so the answer is cached: it is re-used for as long as the container is
        /// still alive and on screen, and otherwise re-derived at most four times a second. A per-frame scan here cost
        /// over 60% of the frame.</summary>
        private static RectTransform CurrentModule()
        {
            if (_moduleCache != null && _moduleCache.gameObject.activeInHierarchy) return _moduleCache;

            // The same give-up as the rotate actions, and for the same measured reason. A prompt strip that has no
            // line to derive the container from - because the player's context has none on screen, or because this
            // build draws them differently - would otherwise be re-derived four times a second for as long as a
            // turnable app is open. That is a FindObjectsOfType sweep of the whole scene, and it was the single
            // largest cost in the frame: 22 ms on average with spikes past 125 ms, which is a stutter the player
            // feels and, worse, a window in which a click's press and release land on different objects and uGUI
            // drops it. Without the hint the phone still turns; with the sweep, nothing else works properly.
            if (_moduleMisses >= MaxModuleMisses) return null;

            if (Time.unscaledTime - _moduleCacheAt < ModuleRescanInterval) return null;
            _moduleCacheAt = Time.unscaledTime;

            _moduleCache = null;
            try
            {
                foreach (InputPromptsItemUI row in Object.FindObjectsOfType<InputPromptsItemUI>())
                {
                    if (row == null || row.gameObject.name == RowName) continue;
                    if (!row.gameObject.activeInHierarchy) continue;
                    _moduleCache = row.transform.parent as RectTransform;
                    break;
                }
            }
            catch { }

            if (_moduleCache != null) { _moduleMisses = 0; return _moduleCache; }

            if (++_moduleMisses >= MaxModuleMisses)
            {
                Core.Log?.Warning("[Sideload] no input prompt strip to hang the turn hint on - the hint stays off. "
                    + "Searching further would cost a scene sweep four times a second.");
            }

            return null;
        }

        private static void Remove()
        {
            if (_row != null) Object.Destroy(_row);
            _row = null;
            _hostModule = null;
        }

        private static void Build(RectTransform module)
        {
            InputPromptsItemUI template = Template(module);
            if (template == null) { WarnOnce("the input prompt strip has no line to copy"); return; }

            if (!Singleton<InputPromptsManager>.InstanceExists) { WarnOnce("the input prompts manager is not up"); return; }
            InputPromptsManager manager = Singleton<InputPromptsManager>.Instance;

            InputActionReference left = RotateAction(true);
            InputActionReference right = RotateAction(false);

            // No reference, no hint. A line that names a key the player does not have is worse than no line at all.
            if (left == null && right == null) { WarnOnce("the rotate actions are not loaded"); return; }

            // Il2Cpp lists, not managed ones: Set hands these straight to the game.
            var bindings = new Il2CppBindingList();
            var displayStrings = new Il2CppStringList();
            foreach (InputActionReference reference in new[] { left, right })
            {
                if (reference == null) continue;
                try
                {
                    InputPromptsBindingData data = manager.GetBindingDataFromActionReference(reference);
                    if (data != null) bindings.Add(data);
                    if (reference.action != null && manager.TryGetActionBindingDisplayString(reference.action, out string display))
                        displayStrings.Add(display);
                }
                catch { /* one unresolvable binding should not cost the whole line */ }
            }
            if (bindings.Count == 0) { WarnOnce("the rotate actions have no resolvable binding"); return; }

            _row = Object.Instantiate(template.gameObject, module);
            _row.name = RowName;
            _row.transform.SetAsLastSibling();

            var item = _row.GetComponent<InputPromptsItemUI>();
            if (item == null) { WarnOnce("the copied line lost its prompt component"); Remove(); return; }

            // The game's own label for this exact action, on the one thing it already rotates, is "Rotate Conveyor"
            // (level1, level2). Following that pattern is why this says Rotate Phone rather than anything shorter.
            item.Set("Rotate Phone", Color.white, bindings, false, displayStrings);
            _row.SetActive(true);

            // Afterwards, because Set only fills the row's own contents - the row's place in the strip is ours to set.
            PlaceBelowTheRest(module, _row.GetComponent<RectTransform>());

            _hostModule = module;
        }

        /// <summary>
        /// Put the line under the ones already there. The module stacks nothing: every line is placed absolutely by
        /// hand, so a fresh clone lands exactly on top of the line it was copied from - which reads as one line of
        /// gibberish rather than two lines of help.
        ///
        /// The gap between the existing lines is the measurement worth taking: it is what the strip already uses, so
        /// ours sits at the same rhythm whatever the game's own spacing turns out to be.
        /// </summary>
        private static void PlaceBelowTheRest(RectTransform module, RectTransform row)
        {
            if (row == null) return;

            float lowest = float.MaxValue, secondLowest = float.MaxValue;

            for (int i = 0; i < module.childCount; i++)
            {
                Transform sibling = module.GetChild(i);
                if (sibling.gameObject == row.gameObject) continue;

                var rect = sibling.GetComponent<RectTransform>();
                if (rect == null) continue;

                float y = rect.anchoredPosition.y;
                if (y < lowest) { secondLowest = lowest; lowest = y; }
                else if (y < secondLowest) secondLowest = y;
            }

            if (lowest == float.MaxValue) return;   // nothing to sit under

            // Two lines give the real pitch; one leaves only the row's own height to go on.
            float pitch = secondLowest < float.MaxValue
                ? Math.Abs(secondLowest - lowest)
                : Math.Abs(row.rect.height);
            if (pitch < 1f) pitch = Math.Abs(row.rect.height) < 1f ? 30f : Math.Abs(row.rect.height);

            row.anchoredPosition = new Vector2(row.anchoredPosition.x, lowest - pitch);
        }

        /// <summary>Any line already in the strip - it carries the layout, the typeface and the shade we want.</summary>
        private static InputPromptsItemUI Template(RectTransform module)
        {
            foreach (InputPromptsItemUI candidate in module.GetComponentsInChildren<InputPromptsItemUI>(true))
                if (candidate.gameObject.name != RowName) return candidate;

            return null;
        }

        /// <summary>
        /// The InputActionReference asset for one action, found by name. Unity names these "&lt;map&gt;/&lt;action&gt;"
        /// - "Generic/RotateLeft" in this build - and they are loaded because the scene references them, so looking
        /// through what is already loaded beats constructing one and getting the map wrong.
        /// </summary>
        private static InputActionReference _leftAction, _rightAction;
        private static float _actionsResolvedAt = float.NegativeInfinity;
        private const float ActionRetryInterval = 2f;

        /// <summary>How many times the search may come back empty before it stops asking. See below.</summary>
        private const int MaxActionAttempts = 5;

        private static int _actionAttempts;

        /// <summary>The rotate-left / rotate-right action asset. Shared with <see cref="TurnInput"/> so the naming
        /// knowledge lives in one place.
        ///
        /// Resolved ONCE and kept: <see cref="FindAction"/> walks every loaded object, which is far too expensive for
        /// the per-frame caller. Until the assets turn up (the scene may not have loaded them yet) the search is
        /// retried, but only every couple of seconds.
        ///
        /// <para>And only a handful of times. A build where these assets are named differently, or absent, would
        /// otherwise retry for the whole session: two full <c>Resources.FindObjectsOfTypeAll</c> sweeps every two
        /// seconds, for as long as a turnable app is on screen. Measured at 137 ms per sweep pair on a real save -
        /// a hitch every two seconds, and a click whose press and release straddle one is dropped by uGUI. Giving up
        /// costs the key hint and the rotate keys; the player can still turn the phone from the app, and an app that
        /// asks can still call setOrientation. A missing convenience beats a stutter nobody can explain.</para>
        /// </summary>
        internal static InputActionReference RotateAction(bool left)
        {
            if (_leftAction == null && _rightAction == null
                && _actionAttempts < MaxActionAttempts
                && Time.unscaledTime - _actionsResolvedAt >= ActionRetryInterval)
            {
                _actionsResolvedAt = Time.unscaledTime;
                _actionAttempts++;

                _leftAction = FindAction("RotateLeft");
                _rightAction = FindAction("RotateRight");

                if (_leftAction == null && _rightAction == null && _actionAttempts >= MaxActionAttempts)
                {
                    Core.Log?.Warning("[Sideload] the rotate actions (RotateLeft/RotateRight) are not in this build - "
                        + "the phone can still be turned from an app, but the rotate keys and their key hint stay off. "
                        + "Searching further would cost a scene sweep every two seconds.");
                }
            }

            return left ? _leftAction : _rightAction;
        }

        /// <summary>
        /// Let the search run again, for a scene that may only now have loaded the input assets.
        ///
        /// Called on a scene change rather than on a timer: the give-up above is permanent by design, and a new scene
        /// is the one event that can honestly change the answer.
        /// </summary>
        internal static void RearmActionSearch()
        {
            _moduleMisses = 0;

            if (_leftAction != null || _rightAction != null) return;
            _actionAttempts = 0;
            _actionsResolvedAt = float.NegativeInfinity;
        }

        private static InputActionReference FindAction(string actionName)
        {
            string suffix = "/" + actionName;

            foreach (InputActionReference reference in Resources.FindObjectsOfTypeAll<InputActionReference>())
            {
                if (reference == null) continue;

                string name = reference.name ?? "";
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(actionName, StringComparison.OrdinalIgnoreCase))
                    return reference;
            }

            return null;
        }

        private static void WarnOnce(string reason)
        {
            if (_warned) return;
            _warned = true;
            Core.Log?.Warning($"[Sideload] no turn hint in the key strip - {reason}. Turning still works.");
        }
    }
}
