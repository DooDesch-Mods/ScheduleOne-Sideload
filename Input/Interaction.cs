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
        private readonly Action<IElement, PointerSpot, PointerRay> _onClicked;
        private readonly Action<IElement, string, PointerSpot, Vector2> _onDragged;
        private readonly Action<IElement, float> _onWheel;
        private readonly Action<IElement, bool> _onHover;

        /// <summary>The page root, used as the fixed frame a drag is measured against - see <see cref="RootPoint"/>.</summary>
        private readonly RectTransform _pageRoot;

        /// <summary>Where the pointer was at the previous drag event, in page-root CSS pixels.</summary>
        private Vector2 _dragFrom;

        /// <summary>
        /// Whether a drag has happened since the last press.
        ///
        /// uGUI raises PointerClick after a drag as long as press and release landed on the same object, so without
        /// this a page that pans by dragging would also receive a click at the end of every pan - and a map that
        /// recentres on click would jump the moment the player let go.
        /// </summary>
        private bool _dragged;

        internal Interaction(Action<IElement> onStateChanged,
                             Action<IElement, PointerSpot, PointerRay> onClicked,
                             Action<IElement, string, PointerSpot, Vector2> onDragged = null,
                             Action<IElement, float> onWheel = null,
                             Action<IElement, bool> onHover = null,
                             RectTransform pageRoot = null)
        {
            _onStateChanged = onStateChanged;
            _onClicked = onClicked;
            _onDragged = onDragged;
            _onWheel = onWheel;
            _onHover = onHover;
            _pageRoot = pageRoot;
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
        /// <summary>
        /// <paramref name="draggable"/> and <paramref name="wheel"/> say the page's script listens for those events.
        /// Both are opt-in per element because both take the gesture AWAY from the scroll area the element sits in:
        /// a list row that swallowed the drag could no longer be scrolled past.
        /// </summary>
        internal void Attach(RectTransform rect, IElement element, bool disabled, bool ownGameObject = false,
                             bool draggable = false, bool wheel = false)
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
                RectTransform hitRect = Paint.UiFactory.Rect(Paint.Painter.HitTargetName, rect);
                Paint.UiFactory.Stretch(hitRect);

                // BEHIND the element's own content, not on top of it. uGUI resolves a raycast to the front-most
                // Graphic, and front-most here means last in the hierarchy - so a hit target appended after the
                // children covers them and eats their clicks. That is fine for a button, whose icon and label
                // raycast to nothing anyway, and fatal for anything interactive nested inside something else
                // interactive: a dialog on a scrim, where every button lives inside a box that also takes clicks.
                // As the first child it stays above the element's own painted box and below everything in it.
                hitRect.SetAsFirstSibling();

                var hit = hitRect.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                hit.raycastTarget = true;

                handlerHost = hitRect.gameObject;
            }

            var trigger = handlerHost.AddComponent<EventTrigger>();
            Add(trigger, EventTriggerType.PointerEnter, () =>
            {
                Set(element, StateFlags.Hover, true);
                // The page hears about it too. :hover alone can only repaint, so anything that has to APPEAR on
                // hover - a tooltip above all - was not buildable without this.
                _onHover?.Invoke(element, true);
            });
            Add(trigger, EventTriggerType.PointerExit, () =>
            {
                // Leaving the element also ends any press that started on it - otherwise :active would stick.
                Set(element, StateFlags.Hover, false);
                Set(element, StateFlags.Active, false);
                _onHover?.Invoke(element, false);
            });
            Add(trigger, EventTriggerType.PointerDown, () =>
            {
                _dragged = false;
                Set(element, StateFlags.Active, true);
            });
            Add(trigger, EventTriggerType.PointerUp, () => Set(element, StateFlags.Active, false));

            // PointerClick rather than PointerUp: uGUI only raises it when press and release landed on the same
            // target, which is what "click" means everywhere else and what lets a player slide off a button to
            // cancel.
            //
            // This one keeps the event data instead of discarding it, so the page can be told WHERE it was clicked.
            // The rect measured against is the element's own, not the hit target's - they are stretched to match,
            // and the element's is the one whose size the page reasons about.
            RectTransform measured = rect;
            AddWithData(trigger, EventTriggerType.PointerClick, data =>
            {
                // A pan that ends on the element it started on is still a click as far as uGUI is concerned. The page
                // asked to handle the drag itself, so it gets the drag and not a click on top of it.
                if (draggable && _dragged) return;

                // The screen point travels with the event, because the element that CAUGHT the click is not
                // necessarily the one that was clicked - see WebView.OnClicked.
                _onClicked?.Invoke(element, SpotIn(measured, data), Ray(data));
            });

            if (draggable) AttachDragging(trigger, element, measured);
            if (wheel) AddWithData(trigger, EventTriggerType.Scroll, data => Wheel(element, data));

            PassScrollingThrough(trigger, handlerHost.transform, forwardDrag: !draggable, forwardWheel: !wheel);
        }

        /// <summary>
        /// Report the drag to the page instead of handing it to a scroll area.
        ///
        /// The movement is measured against the PAGE ROOT rather than the element itself, and that is the whole
        /// difficulty here: an element that pans is an element that moves under the pointer, so measuring against it
        /// would fold this frame's movement back into the next frame's reading and the map would accelerate away.
        /// The root stands still, carries the canvas scale and the portrait rotation, and so gives a delta already in
        /// CSS pixels the right way up.
        /// </summary>
        private void AttachDragging(EventTrigger trigger, IElement element, RectTransform measured)
        {
            AddWithData(trigger, EventTriggerType.BeginDrag, data =>
            {
                _dragged = true;
                if (!RootPoint(data, out Vector2 start)) return;

                _dragFrom = start;
                _onDragged?.Invoke(element, "dragstart", SpotIn(measured, data), Vector2.zero);
            });

            AddWithData(trigger, EventTriggerType.Drag, data =>
            {
                _dragged = true;
                if (!RootPoint(data, out Vector2 now)) return;

                Vector2 delta = now - _dragFrom;
                _dragFrom = now;
                _onDragged?.Invoke(element, "drag", SpotIn(measured, data), delta);
            });

            AddWithData(trigger, EventTriggerType.EndDrag,
                        data => _onDragged?.Invoke(element, "dragend", SpotIn(measured, data), Vector2.zero));
        }

        /// <summary>
        /// One wheel notch, reported the way the DOM reports it: positive is scrolling AWAY from the reader, which is
        /// the opposite sign to Unity's.
        /// </summary>
        private void Wheel(IElement element, BaseEventData data)
        {
            var pointer = data.TryCast<PointerEventData>();
            if (pointer == null) return;

            _onWheel?.Invoke(element, -pointer.scrollDelta.y);
        }

        /// <summary>
        /// A pointer position in page-root CSS pixels, measured from the top-left with Y pointing down.
        ///
        /// Same conversion as <see cref="SpotIn"/> and for the same reasons: TryCast because a managed `as` yields
        /// null for a perfectly good PointerEventData, the event's own camera because an overlay canvas reports none,
        /// and no scale division because the root carries the scale as a localScale.
        /// </summary>
        private bool RootPoint(BaseEventData data, out Vector2 point)
        {
            point = default;
            if (_pageRoot == null) return false;

            var pointer = data.TryCast<PointerEventData>();
            if (pointer == null) return false;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _pageRoot, pointer.position, pointer.pressEventCamera, out Vector2 local))
                return false;

            Rect r = _pageRoot.rect;
            point = new Vector2(local.x - r.xMin, r.yMax - local.y);
            return true;
        }

        /// <summary>
        /// Hand the wheel and the drag to the scroll area this element sits in.
        ///
        /// EventTrigger implements EVERY pointer interface, including IScrollHandler and IDragHandler - so uGUI
        /// stops looking as soon as it finds one, and the ScrollRect above never hears about it. The effect was that
        /// any list whose rows could be clicked could not be scrolled at all, which is most lists.
        ///
        /// Forwarding is what bubbling would have done anyway. The ScrollRect is found once, when the element is
        /// wired, because the hierarchy above a box does not change while the page stands.
        /// </summary>
        private static void PassScrollingThrough(EventTrigger trigger, Transform host,
                                                 bool forwardDrag = true, bool forwardWheel = true)
        {
            ScrollRect scroll = host == null ? null : host.GetComponentInParent<ScrollRect>();
            if (scroll == null) return;

            if (forwardWheel)
                AddWithData(trigger, EventTriggerType.Scroll, data =>
                {
                    var pointer = data.TryCast<PointerEventData>();
                    if (pointer == null) return;

                    // Eased when this area is smoothed; handed straight to uGUI when it is not, or when there is
                    // nothing to scroll and the walk would have no distance to cover.
                    if (!SmoothScroll.Wheel(scroll, pointer)) scroll.OnScroll(pointer);
                });

            if (!forwardDrag) return;

            AddWithData(trigger, EventTriggerType.BeginDrag, data =>
            {
                var pointer = data.TryCast<PointerEventData>();
                if (pointer == null) return;

                // Taking hold of the content cancels whatever the wheel was still aiming at, or the list snaps back
                // to it the moment the drag ends.
                SmoothScroll.Release(scroll);
                scroll.OnBeginDrag(pointer);
            });

            AddWithData(trigger, EventTriggerType.Drag, data =>
            {
                var pointer = data.TryCast<PointerEventData>();
                if (pointer != null) scroll.OnDrag(pointer);
            });

            AddWithData(trigger, EventTriggerType.EndDrag, data =>
            {
                var pointer = data.TryCast<PointerEventData>();
                if (pointer != null) scroll.OnEndDrag(pointer);
            });
        }

        /// <summary>
        /// Turn a pointer event into a position inside an element, in CSS pixels from its top-left.
        ///
        /// Three things are easy to get wrong here.
        ///
        /// The cast must be TryCast: under Il2CppInterop a managed `as` tests the WRAPPER type and yields null for a
        /// perfectly good PointerEventData.
        ///
        /// The camera must come from the event's own pressEventCamera - a Screen Space Overlay canvas reports none,
        /// and passing the main camera instead skews every result.
        ///
        /// And the result needs NO scale conversion, which is the counter-intuitive part. The page's root carries
        /// the scale as a localScale while every box beneath it is sized in CSS pixels (UiFactory.PlaceFromTopLeft
        /// takes the layout's own numbers), so a point in a box's local space is already in CSS pixels. Dividing by
        /// the scale a second time would report a click about 1.6x too close to the corner. The rotation of a
        /// portrait panel needs no handling either - the inverse transform has already undone it.
        ///
        /// What does need converting is the origin: uGUI measures from the rect's PIVOT with Y pointing up, CSS from
        /// the top-left with Y pointing down.
        /// </summary>
        private static PointerSpot SpotIn(RectTransform rect, BaseEventData data)
        {
            if (rect == null || data == null) return default;

            var pointer = data.TryCast<PointerEventData>();
            if (pointer == null) return default;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    rect, pointer.position, pointer.pressEventCamera, out Vector2 local))
                return default;

            Rect r = rect.rect;
            return new PointerSpot(local.x - r.xMin, r.yMax - local.y, r.width, r.height);
        }

        /// <summary>
        /// Where the pointer was, in screen coordinates, with the camera the event came through.
        ///
        /// Kept raw rather than converted, because the caller has to ask a DIFFERENT rectangle whether it contains
        /// this point - and the only reliable way to answer that under a scaled, rotated, scrolled hierarchy is to
        /// hand the screen point and the camera to <see cref="RectTransformUtility"/> once per rectangle.
        /// </summary>
        private static PointerRay Ray(BaseEventData data)
        {
            var pointer = data?.TryCast<PointerEventData>();
            return pointer == null ? default : new PointerRay(pointer.position, pointer.pressEventCamera);
        }

        /// <summary>Whether a rectangle covers the point the pointer was at.</summary>
        internal static bool Covers(RectTransform rect, PointerRay ray) =>
            rect != null && ray.Valid
            && RectTransformUtility.RectangleContainsScreenPoint(rect, ray.Screen, ray.Camera);

        /// <summary>The same conversion <see cref="SpotIn(RectTransform, BaseEventData)"/> does, for a point that is
        /// no longer carried by an event - the re-targeted element needs its own offsets, not the ones measured
        /// against whatever box happened to catch the click.</summary>
        internal static PointerSpot SpotIn(RectTransform rect, PointerRay ray)
        {
            if (rect == null || !ray.Valid) return default;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, ray.Screen, ray.Camera, out Vector2 local))
                return default;

            Rect r = rect.rect;
            return new PointerSpot(local.x - r.xMin, r.yMax - local.y, r.width, r.height);
        }

        private static void Add(EventTrigger trigger, EventTriggerType type, Action run) =>
            AddWithData(trigger, type, _ => run());

        private static void AddWithData(EventTrigger trigger, EventTriggerType type, Action<BaseEventData> run)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback = new EventTrigger.TriggerEvent();
            entry.callback.AddListener((UnityEngine.Events.UnityAction<BaseEventData>)(data =>
            {
                try { run(data); }
                catch (Exception e) { Core.Log?.Warning("pointer handler failed: " + e.Message); }
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
