using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Input;
using Object = UnityEngine.Object;

namespace Sideload.Phone
{
    /// <summary>
    /// The one thing Sideload puts on screen for the player: a "Rotate Phone" line in the game's own key-hint strip,
    /// for as long as an app that can be turned is open.
    ///
    /// It goes there rather than into the app because the app's screen belongs to whoever wrote it, and because the
    /// game already teaches Tab and Escape in exactly that spot - a player who has read one has read the others.
    ///
    /// The line is a CLONE of one the game drew. <see cref="InputPrompt"/> resolves its own key badges from the bound
    /// actions (<c>GetBindingDisplayString</c>), so handing it the rotate actions is enough: it draws whatever the
    /// player has those bound to, keeps drawing the right thing after a rebind, and matches the neighbouring lines
    /// without this file knowing a single font, colour or offset.
    /// </summary>
    internal static class TurnPrompt
    {
        private const string RowName = "SideloadTurnPrompt";

        private static GameObject _row;
        private static RectTransform _hostModule;   // the module we attached to, so a swap is noticed
        private static bool _warned;

        /// <summary>Put the line up, or take it down. Called every frame; doing nothing is the common case.</summary>
        internal static void Show(bool wanted)
        {
            RectTransform module = CurrentModule();

            // The game replaces the whole module when the player's context changes (InputPromptsCanvas.LoadModule),
            // taking our line with it. Noticing that the module is a different object is what gets it back.
            if (_row != null && (!wanted || module == null || module != _hostModule)) Remove();
            if (!wanted || module == null || _row != null) return;

            try { Build(module); }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] could not add the turn hint to the input prompts: " + e.Message);
                Remove();
            }
        }

        private static RectTransform CurrentModule()
        {
            if (!InputPromptsCanvas.InstanceExists) return null;

            InputPromptsCanvas canvas = InputPromptsCanvas.Instance;
            return canvas.currentModule != null ? canvas.currentModule : canvas.InputPromptsContainer;
        }

        private static void Remove()
        {
            if (_row != null) Object.Destroy(_row);
            _row = null;
            _hostModule = null;
        }

        private static void Build(RectTransform module)
        {
            InputPrompt template = Template(module);
            if (template == null) { WarnOnce("the input prompt strip has no line to copy"); return; }

            InputActionReference left = FindAction("RotateLeft");
            InputActionReference right = FindAction("RotateRight");

            // No reference, no hint. A line that names a key the player does not have is worse than no line at all.
            if (left == null && right == null) { WarnOnce("the rotate actions are not loaded"); return; }

            _row = Object.Instantiate(template.gameObject, module);
            _row.name = RowName;
            _row.transform.SetAsLastSibling();

            var prompt = _row.GetComponent<InputPrompt>();
            if (prompt == null) { WarnOnce("the copied line lost its InputPrompt component"); Remove(); return; }

            prompt.Actions.Clear();
            if (left != null) prompt.Actions.Add(left);
            if (right != null) prompt.Actions.Add(right);

            // The game's own label for this exact action, on the one thing it already rotates, is "Rotate Conveyor"
            // (level1, level2). Following that pattern is why this says Rotate Phone rather than anything shorter.
            prompt.Label = "Rotate Phone";

            // RefreshPromptImages is private and only runs from OnEnable, so this is how it is made to run again with
            // the actions it has just been given.
            _row.SetActive(false);
            _row.SetActive(true);

            // Afterwards, because the refresh moves the badges and the label INSIDE the row and would otherwise
            // undo nothing - but the row's own place in the strip is ours to set.
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
        private static InputPrompt Template(RectTransform module)
        {
            foreach (InputPrompt candidate in module.GetComponentsInChildren<InputPrompt>(true))
                if (candidate.gameObject.name != RowName) return candidate;

            return null;
        }

        /// <summary>
        /// The InputActionReference asset for one action, found by name. Unity names these "&lt;map&gt;/&lt;action&gt;"
        /// - "Generic/RotateLeft" in this build - and they are loaded because the scene references them, so looking
        /// through what is already loaded beats constructing one and getting the map wrong.
        /// </summary>
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
