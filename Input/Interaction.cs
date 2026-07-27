using AngleSharp.Dom;
using Sideload.Css;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sideload.Input
{
    /// <summary>
    /// Turns uGUI pointer events into CSS interaction states.
    ///
    /// Two constraints shape this. First, the GraphicRaycaster only ever hits a <see cref="Graphic"/>, and Sideload's
    /// boxes are raw CanvasRenderer meshes - so an interactive node gets a fully transparent Image purely as a hit
    /// target. Second, event handling goes through <see cref="EventTrigger"/> rather than a component implementing
    /// IPointerEnterHandler: implementing a Unity interface on a managed type is the same unreliable virtual-override
    /// path that ruled out a custom Graphic, whereas EventTrigger takes plain delegates.
    /// </summary>
    internal sealed class Interaction
    {
        private readonly Dictionary<IElement, StateFlags> _states = new Dictionary<IElement, StateFlags>();

        /// <summary>States pinned from outside - the Styles pane's `:hov` toggles. Kept apart from the pointer's own
        /// states so a render reset cannot silently drop them.</summary>
        private readonly Dictionary<IElement, StateFlags> _forced = new Dictionary<IElement, StateFlags>();
        private readonly Action<IElement> _onStateChanged;
        private readonly Action<IElement> _onClicked;

        internal Interaction(Action<IElement> onStateChanged, Action<IElement> onClicked)
        {
            _onStateChanged = onStateChanged;
            _onClicked = onClicked;
        }

        /// <summary>Current interaction state of an element - this is what the cascade asks for.</summary>
        /// <summary>Current interaction state of an element - this is what the cascade asks for. Forced states are
        /// merged in, so a state pinned from DevTools behaves exactly like one the pointer produced.</summary>
        internal StateFlags StateOf(IElement element)
        {
            if (element == null) return StateFlags.None;

            StateFlags state = _states.TryGetValue(element, out StateFlags natural) ? natural : StateFlags.None;
            return _forced.TryGetValue(element, out StateFlags forced) ? state | forced : state;
        }

        /// <summary>
        /// Pin interaction states on an element, from the Styles pane's `:hov` toggles. The pointer keeps working
        /// underneath - a forced state is added to whatever the pointer is doing, never a replacement for it - so
        /// releasing the toggle leaves the element in the state it would have been in anyway.
        /// </summary>
        internal void Force(IElement element, StateFlags flags)
        {
            if (element == null) return;

            if (flags == StateFlags.None) _forced.Remove(element);
            else _forced[element] = flags;

            _onStateChanged?.Invoke(element);
        }

        internal StateFlags ForcedOn(IElement element) =>
            element != null && _forced.TryGetValue(element, out StateFlags f) ? f : StateFlags.None;

        internal void Clear() => _states.Clear();

        /// <summary>
        /// Start a fresh render. Hover and press are properties of the pointer against GameObjects that are about to
        /// be destroyed, and no PointerExit ever arrives for a destroyed object - carrying those flags over would
        /// leave a button stuck in its hover style forever. `:disabled` is re-derived instead, because script may have
        /// changed the attribute since the last pass.
        /// </summary>
        internal void ResetForRender(IDocument document)
        {
            foreach (IElement element in new List<IElement>(_states.Keys))
                _states[element] &= ~(StateFlags.Hover | StateFlags.Active);

            foreach (IElement element in new List<IElement>(_states.Keys))
                _states[element] &= ~StateFlags.Disabled;

            SeedDisabled(document);
        }

        /// <summary>
        /// Mark every element carrying the `disabled` attribute BEFORE the first cascade runs. Hover and press states
        /// arrive later through the pointer, but `:disabled` is true from the start - discovering it only while wiring
        /// handlers would paint the first frame with the enabled style.
        /// </summary>
        internal void SeedDisabled(IDocument document)
        {
            if (document == null) return;
            foreach (IElement element in document.QuerySelectorAll("[disabled]"))
                _states[element] = StateOf(element) | StateFlags.Disabled;
        }

        /// <summary>
        /// Make one painted node interactive. Only elements that actually need it should get this: every hit target is
        /// an extra transparent quad, and a page where every box swallows the pointer cannot be scrolled sensibly.
        /// </summary>
        /// <summary>
        /// <paramref name="ownGameObject"/> puts the handlers on the element's OWN node instead of a child hit target.
        /// Required for anything that already carries a Selectable (an input field): the EventSystem executes an event
        /// on the FIRST GameObject in the chain that handles it, so an EventTrigger sitting on a child swallows
        /// PointerDown and the field below never receives the click - it looks focusable but cannot be typed into.
        /// On the same GameObject both components get the event.
        /// </summary>
        internal void Attach(RectTransform rect, IElement element, bool disabled, bool ownGameObject = false)
        {
            if (rect == null || element == null) return;

            if (disabled)
            {
                // A disabled element still needs its state so `:disabled` rules match, but must not react.
                Set(element, StateFlags.Disabled, on: true, notify: false);
                return;
            }

            GameObject handlerHost = rect.gameObject;

            if (!ownGameObject)
            {
                // The hit target MUST live on its own GameObject. A Graphic takes ownership of the CanvasRenderer it
                // sits on and pushes its own geometry into it, so adding the Image next to the box mesh silently
                // erased the box - the element rendered blank until the first hover triggered a repaint.
                RectTransform hitRect = Paint.UiFactory.Rect("hit", rect);
                Paint.UiFactory.Stretch(hitRect);

                var hit = hitRect.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                hit.raycastTarget = true;

                handlerHost = hitRect.gameObject;
            }

            var trigger = handlerHost.AddComponent<EventTrigger>();
            Add(trigger, EventTriggerType.PointerEnter, () => Set(element, StateFlags.Hover, true));
            Add(trigger, EventTriggerType.PointerExit, () =>
            {
                // Leaving the element also ends any press that started on it - otherwise :active would stick.
                Set(element, StateFlags.Hover, false);
                Set(element, StateFlags.Active, false);
            });
            Add(trigger, EventTriggerType.PointerDown, () => Set(element, StateFlags.Active, true));
            Add(trigger, EventTriggerType.PointerUp, () => Set(element, StateFlags.Active, false));

            // PointerClick rather than PointerUp: uGUI only raises it when press and release landed on the same
            // target, which is what "click" means everywhere else and what lets a player slide off a button to
            // cancel.
            Add(trigger, EventTriggerType.PointerClick, () => _onClicked?.Invoke(element));
        }

        private static void Add(EventTrigger trigger, EventTriggerType type, Action run)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback = new EventTrigger.TriggerEvent();
            entry.callback.AddListener((UnityEngine.Events.UnityAction<BaseEventData>)(_ =>
            {
                try { run(); }
                catch (Exception e) { Core.Log?.Warning("[Sideload] pointer handler failed: " + e.Message); }
            }));
            trigger.triggers.Add(entry);
        }

        private void Set(IElement element, StateFlags flag, bool on, bool notify = true)
        {
            StateFlags before = StateOf(element);
            StateFlags after = on ? before | flag : before & ~flag;
            if (after == before) return;

            _states[element] = after;
            if (notify) _onStateChanged?.Invoke(element);
        }
    }
}
