using AngleSharp.Dom;

namespace Sideload.Dom
{
    /// <summary>What an <c>&lt;input&gt;</c> actually is. A browser reads `type`; this engine used to ignore it.</summary>
    internal enum ControlKind
    {
        /// <summary>Not a form control at all.</summary>
        None,

        /// <summary>A field the player types into: text, search, password, email, url, tel, number, textarea.</summary>
        Text,

        /// <summary>A checkbox or a radio - a box that is on or off and has no text of its own.</summary>
        Toggle,

        /// <summary>`type="button"`, `submit` or `reset`: a button whose label is its `value`.</summary>
        Button,

        /// <summary>`type="hidden"` - data the page carries, never a box.</summary>
        Hidden,
    }

    /// <summary>
    /// Reads an input's `type`, once, in one place.
    ///
    /// Everything used to be a text field. A page with a checkbox got an empty single-line input where the box
    /// should be - present, focusable, typeable and completely wrong - and there was no way to tell from the
    /// screen that the type had been ignored rather than unsupported.
    ///
    /// Unity-free on purpose: the layout has to know a checkbox is a small square before anything is painted, and
    /// the painter has to know it is not a text field. One answer, read by both.
    /// </summary>
    internal static class ControlKinds
    {
        internal static ControlKind Of(IElement element)
        {
            if (element == null) return ControlKind.None;

            if (string.Equals(element.LocalName, "textarea", StringComparison.OrdinalIgnoreCase))
                return ControlKind.Text;

            if (!string.Equals(element.LocalName, "input", StringComparison.OrdinalIgnoreCase))
                return ControlKind.None;

            // An absent or unknown type is a text field, which is what HTML says and what every browser does with
            // `<input type="colour">`.
            switch ((element.GetAttribute("type") ?? "text").Trim().ToLowerInvariant())
            {
                case "checkbox":
                case "radio": return ControlKind.Toggle;

                case "button":
                case "submit":
                case "reset": return ControlKind.Button;

                case "hidden": return ControlKind.Hidden;

                default: return ControlKind.Text;
            }
        }

        /// <summary>Whether a toggle is on. The attribute, not a property - a rebuild has to be able to read it back.</summary>
        internal static bool IsChecked(IElement element) => element != null && element.HasAttribute("checked");

        /// <summary>
        /// The toggle a click on this element activates, or null.
        ///
        /// Usually the element itself. The other case is a LABEL, and it is not an edge case: wrapping a checkbox
        /// and its text in a `&lt;label&gt;` is the ordinary way to write one, and it is what makes the words
        /// clickable as well as the box. Clicking the text used to do nothing at all, which reads as a control that
        /// only works if you hit thirteen pixels exactly.
        ///
        /// Both spellings, because both are in use: `&lt;label for="id"&gt;` names its control, and a label with the
        /// control inside it labels that one.
        /// </summary>
        internal static IElement ActivatedBy(IElement clicked)
        {
            if (clicked == null) return null;
            if (Of(clicked) == ControlKind.Toggle) return clicked;

            IElement label = clicked.Closest("label");
            if (label == null) return null;

            string forId = label.GetAttribute("for");
            if (!string.IsNullOrEmpty(forId))
            {
                IElement named = label.Owner?.GetElementById(forId);
                return named != null && Of(named) == ControlKind.Toggle ? named : null;
            }

            foreach (IElement inside in label.QuerySelectorAll("input"))
                if (Of(inside) == ControlKind.Toggle) return inside;

            return null;
        }

        /// <summary>
        /// Which toggles are on right now, so a cancelled activation can be put back.
        ///
        /// A whole radio GROUP, not one element: turning a radio on turned another one off, and undoing half of
        /// that would leave the page with two selected radios or none.
        /// </summary>
        internal static List<IElement> Snapshot(IElement toggle)
        {
            var on = new List<IElement>();
            if (toggle == null) return on;

            foreach (IElement other in Group(toggle))
                if (IsChecked(other)) on.Add(other);

            return on;
        }

        /// <summary>Put a snapshot back, exactly.</summary>
        internal static void Restore(IElement toggle, List<IElement> wasOn)
        {
            if (toggle == null || wasOn == null) return;

            foreach (IElement other in Group(toggle))
            {
                if (wasOn.Contains(other)) other.SetAttribute("checked", "");
                else other.RemoveAttribute("checked");
            }
        }

        /// <summary>Everything one activation can change: a radio's whole group, or just the checkbox itself.</summary>
        private static IEnumerable<IElement> Group(IElement toggle)
        {
            string name = toggle.GetAttribute("name");
            bool radio = string.Equals(toggle.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase);

            if (!radio || string.IsNullOrEmpty(name)) return new[] { toggle };

            IParentNode scope = toggle.Closest("form") ?? (IParentNode)toggle.Owner;
            if (scope == null) return new[] { toggle };

            var group = new List<IElement>();
            foreach (IElement other in scope.QuerySelectorAll("input[type=radio]"))
                if (string.Equals(other.GetAttribute("name"), name, StringComparison.Ordinal)) group.Add(other);

            return group;
        }

        /// <summary>Flip a toggle and say what it became. A radio also turns off the others in its group.</summary>
        internal static bool Toggle(IElement element)
        {
            if (element == null) return false;

            bool radio = string.Equals(element.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase);
            bool now = radio || !IsChecked(element);

            if (radio) ClearGroup(element);

            if (now) element.SetAttribute("checked", "");
            else element.RemoveAttribute("checked");

            return now;
        }

        /// <summary>
        /// Turn off every other radio with the same name.
        ///
        /// Scoped to the form when there is one and to the document when there is not, which is what HTML says -
        /// and the reason two unrelated groups on one page do not fight each other as long as they are named
        /// differently.
        /// </summary>
        private static void ClearGroup(IElement radio)
        {
            string name = radio.GetAttribute("name");
            if (string.IsNullOrEmpty(name)) return;

            IParentNode scope = radio.Closest("form") ?? (IParentNode)radio.Owner;
            if (scope == null) return;

            foreach (IElement other in scope.QuerySelectorAll("input[type=radio]"))
            {
                if (ReferenceEquals(other, radio)) continue;
                if (!string.Equals(other.GetAttribute("name"), name, StringComparison.Ordinal)) continue;
                other.RemoveAttribute("checked");
            }
        }
    }
}
