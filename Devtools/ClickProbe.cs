using UnityEngine;
using UnityEngine.EventSystems;

namespace Sideload.Devtools
{
    /// <summary>
    /// Debug-only: clicks an element the way the player's mouse would, and says what actually happened.
    ///
    /// It does NOT call the handler directly. It builds a pointer event at the element's screen position, runs the
    /// EventSystem's real raycast, and executes the click on whatever came back on top. That distinction is the whole
    /// point: calling a handler proves the handler works, while raycasting proves the element has a hit target, that
    /// the target is where the layout says it is, and that nothing above it is swallowing the pointer - which is the
    /// class of bug that actually happens here.
    /// </summary>
    internal static class ClickProbe
    {
        /// <summary>Click at a screen position and report the chain. Returns the object that took the click.</summary>
        internal static GameObject ClickAt(Vector2 screenPosition, string what)
        {
            EventSystem system = EventSystem.current;
            if (system == null)
            {
                Core.Log?.Warning("[Sideload/probe] no EventSystem - nothing can be clicked.");
                return null;
            }

            var data = new PointerEventData(system) { position = screenPosition, button = PointerEventData.InputButton.Left };

            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            system.RaycastAll(data, hits);

            if (hits.Count == 0)
            {
                Core.Log?.Warning($"[Sideload/probe] {what}: raycast at {screenPosition} hit nothing.");
                return null;
            }

            RaycastResult top = hits[0];
            data.pointerCurrentRaycast = top;
            data.pointerPressRaycast = top;

            // Selection first, exactly where StandaloneInputModule does it - on the press, before the click handlers.
            //
            // Without this the probe could not click a text field: a TMP_InputField takes the caret by being
            // SELECTED, not by being clicked, so the field lit up its hover state and stayed dead. And the null case
            // carries as much as the hit one - clicking something that is not selectable deselects whatever was, and
            // that is the press that has to hand the keyboard back to a page's data-typing field.
            system.SetSelectedGameObject(ExecuteEvents.GetEventHandler<ISelectHandler>(top.gameObject), data);

            // The same three-step uGUI itself performs, so a handler that only listens for one of them still fires.
            ExecuteEvents.ExecuteHierarchy(top.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy(top.gameObject, data, ExecuteEvents.pointerUpHandler);
            GameObject handled = ExecuteEvents.ExecuteHierarchy(top.gameObject, data, ExecuteEvents.pointerClickHandler);

            Core.Log?.Msg($"[Sideload/probe] {what}: raycast hit '{Path(top.gameObject)}' " +
                          $"({hits.Count} candidate(s)), click handled by '{Path(handled)}'.");
            return handled;
        }

        private static string Path(GameObject go)
        {
            if (go == null) return "<none>";

            string path = go.name;
            Transform t = go.transform.parent;
            for (int depth = 0; t != null && depth < 4; depth++, t = t.parent) path = t.name + "/" + path;
            return path;
        }
    }
}
