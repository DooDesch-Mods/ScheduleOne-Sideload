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

        /// <summary>Every other pointer event, by name: mousedown, mouseup, mouseover, mouseout, dblclick,
        /// contextmenu. One callback rather than six, because the view does the same thing with all of them.</summary>
        private readonly Action<IElement, string, PointerSpot, PointerRay, int, int> _onPointer;
        /// <summary>A drag phase, reported to the page. True when the page called <c>preventDefault()</c> and the
        /// gesture must not also scroll whatever the element sits in.</summary>
        private readonly Func<IElement, string, PointerSpot, Vector2, bool> _onDragged;
        private readonly Func<IElement, float, bool> _onWheel;
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

        /// <summary>True once the page has claimed the gesture in progress with <c>preventDefault()</c>. Latches for
        /// the rest of the gesture: a pan that had to fight the scroll area for every frame would stutter.</summary>
        private bool _dragOwned;

        /// <summary>The element the pointer is over and the rectangle it was measured against, for the frame poll
        /// that raises `mousemove`. Null whenever the pointer is over nothing wired.</summary>
        private IElement _hovered;
        private RectTransform _hoveredRect;

        /// <summary>The camera the last pointer event came through, so a polled position can be converted the same
        /// way an event's would be. An overlay canvas reports none, which is a valid answer and has to be kept.</summary>
        private Camera _lastCamera;
        private bool _sawPointer;
        private Vector3 _lastMouse;

        internal Interaction(Action<IElement> onStateChanged,
                             Action<IElement, PointerSpot, PointerRay> onClicked,
                             Func<IElement, string, PointerSpot, Vector2, bool> onDragged = null,
                             Func<IElement, float, bool> onWheel = null,
                             Action<IElement, bool> onHover = null,
                             RectTransform pageRoot = null,
                             Action<IElement, string, PointerSpot, PointerRay, int, int> onPointer = null)
        {
            _onStateChanged = onStateChanged;
            _onClicked = onClicked;
            _onDragged = onDragged;
            _onWheel = onWheel;
            _onHover = onHover;
            _pageRoot = pageRoot;
            _onPointer = onPointer;
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
        /// Work out what the pointer is over NOW and hover it, after a render has replaced the boxes.
        ///
        /// <see cref="ResetForRender"/> has to drop hover, because a destroyed GameObject never sends its
        /// PointerExit and the style would stick forever. The cost of that was two things the player sees:
        ///
        /// A page that rebuilds on a timer - the self-test app redraws its clock once a second - dropped the hover
        /// every second and got it back a frame later when uGUI noticed the pointer over the new hit target, so
        /// the button flickered while the pointer stood still. And a REUSED control never got it back at all: a
        /// text field keeps its GameObject across the rebuild, the pointer never left it, so no enter is ever
        /// raised again and the field stayed unhovered until the player moved out and back in.
        ///
        /// Asking where the pointer is answers both, and it is the same question uGUI would answer a frame later.
        /// </summary>
        internal void RehoverFrom(IEnumerable<KeyValuePair<IElement, RectTransform>> boxes)
        {
            if (!WantsRehover || boxes == null) return;

            var ray = new PointerRay(UnityEngine.Input.mousePosition, _lastCamera);
            if (!ray.Valid) return;

            foreach (KeyValuePair<IElement, RectTransform> box in boxes)
            {
                if (box.Value == null || !Covers(box.Value, ray)) continue;

                // Notified, so the box repaints in this frame rather than waiting for the next pointer event. Set
                // skips both the write and the notification when the flag is already there, which it is for every
                // box the pointer has not moved off.
                Set(box.Key, StateFlags.Hover, true);
                _hovered = box.Key;
                _hoveredRect = box.Value;
            }
        }

        /// <summary>False until a pointer event has been seen, because the camera one arrived through is the only
        /// way to convert a screen position - and a null camera is a valid answer, so its absence cannot say.</summary>
        private bool WantsRehover => _sawPointer;

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
        /// Listening is passive: the gesture still reaches the scroll area the element sits in, and the page takes it
        /// by claiming it - <c>preventDefault()</c> on the event, or <paramref name="ownsGesture"/> for a box whose
        /// stylesheet said `touch-action: none` before anything was pressed.
        /// </summary>
        internal void Attach(RectTransform rect, IElement element, bool disabled, bool ownGameObject = false,
                             bool draggable = false, bool wheel = false, bool ownsGesture = false)
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

            // The element's OWN rect, not the hit target's - they are stretched to match, and the element's is the
            // one whose size a page reasons about.
            RectTransform measuredRect = rect;

            var trigger = handlerHost.AddComponent<EventTrigger>();
            AddWithData(trigger, EventTriggerType.PointerEnter, data =>
            {
                Set(element, StateFlags.Hover, true);
                Entered(element, measuredRect, data);

                // The page hears about it too. :hover alone can only repaint, so anything that has to APPEAR on
                // hover - a tooltip above all - was not buildable without this.
                _onHover?.Invoke(element, true);

                // The bubbling twin. A list that highlights whichever row the pointer is over listens once on the
                // list for `mouseover`; with only `mouseenter` it would need a listener per row.
                Pointer(element, "mouseover", measuredRect, data);
            });
            AddWithData(trigger, EventTriggerType.PointerExit, data =>
            {
                // Leaving the element also ends any press that started on it - otherwise :active would stick.
                Set(element, StateFlags.Hover, false);
                Set(element, StateFlags.Active, false);
                Left(element);
                _onHover?.Invoke(element, false);
                Pointer(element, "mouseout", measuredRect, data);
            });
            AddWithData(trigger, EventTriggerType.PointerDown, data =>
            {
                _dragged = false;
                Set(element, StateFlags.Active, true);
                Pointer(element, "mousedown", measuredRect, data);
            });
            AddWithData(trigger, EventTriggerType.PointerUp, data =>
            {
                Set(element, StateFlags.Active, false);
                Pointer(element, "mouseup", measuredRect, data);
            });

            // PointerClick rather than PointerUp: uGUI only raises it when press and release landed on the same
            // target, which is what "click" means everywhere else and what lets a player slide off a button to
            // cancel.
            //
            // This one keeps the event data instead of discarding it, so the page can be told WHERE it was clicked.
            // The rect measured against is the element's own, not the hit target's - they are stretched to match,
            // and the element's is the one whose size the page reasons about.
            AddWithData(trigger, EventTriggerType.PointerClick, data =>
            {
                // A pan that ends on the element it started on is still a click as far as uGUI is concerned. A page
                // that took the gesture gets the drag and not a click on top of it; one that let the drag scroll the
                // list underneath still gets its click, exactly as an element with no drag listener does.
                if (_dragged && _dragOwned) return;

                // The right button raises `contextmenu`, not `click` - which is what a browser does, and what a page
                // that wants its own menu on right-click has to be able to tell apart. Escape and right-click also
                // still raise `back` at the document; the two are different questions and a page may answer both.
                if (ButtonOf(data) == 2)
                {
                    Pointer(element, "contextmenu", measuredRect, data);
                    return;
                }

                // The screen point travels with the event, because the element that CAUGHT the click is not
                // necessarily the one that was clicked - see WebView.OnClicked.
                _onClicked?.Invoke(element, SpotIn(measuredRect, data), Ray(data));

                if (IsSecondClick(element)) Pointer(element, "dblclick", measuredRect, data, detail: 2);
            });

            ScrollRect around = PassScrollingThrough(trigger, handlerHost.transform,
                                                     forwardDrag: !draggable, forwardWheel: !wheel);

            if (draggable) AttachDragging(trigger, element, measuredRect, around, ownsGesture);

            // A wheel LISTENER reports the notch; it does not swallow it. Browsers made these listeners passive by
            // default for exactly this reason, and the page still gets its say through `preventDefault()`.
            //
            // Without this, one line of React took the whole page's scrolling away: `createRoot` registers a wheel
            // listener on the container as part of its event delegation, so the root box counted as "the page
            // handles the wheel" and every notch stopped there. Nothing on screen said so - the page simply did not
            // move, which reads as a renderer that crops rather than scrolls.
            if (wheel)
                AddWithData(trigger, EventTriggerType.Scroll, data =>
                {
                    if (Wheel(element, data) || around == null) return;

                    var pointer = data.TryCast<PointerEventData>();
                    if (pointer == null) return;
                    if (!SmoothScroll.Wheel(around, pointer)) around.OnScroll(pointer);
                });
        }

        /// <summary>
        /// Report the drag to the page, and hand it on to the scroll area unless the page claims it.
        ///
        /// The claim is <c>preventDefault()</c>, on any phase of the gesture, and it latches until the gesture ends.
        /// A listener that only watches is passive, which is what a browser does and what this engine already does
        /// with the wheel: registering a handler is not the same as taking the gesture. It used to be - wiring decided
        /// exclusivity at BUILD time - and the cost of that was not a nicety. React installs `dragstart`, `drag` and
        /// `dragend` on its root container as part of its event delegation, so mounting a React app made the root the
        /// owner of every drag on the page and nothing could be scrolled by hand any more.
        ///
        /// The movement is measured against the PAGE ROOT rather than the element itself, and that is the delicate
        /// part: an element that pans is an element that moves under the pointer, so measuring against it would fold
        /// this frame's movement back into the next frame's reading and the map would accelerate away. The root stands
        /// still, carries the canvas scale and the portrait rotation, and so gives a delta already in CSS pixels the
        /// right way up.
        /// </summary>
        private void AttachDragging(EventTrigger trigger, IElement element, RectTransform measured, ScrollRect around,
                                    bool owns)
        {
            AddWithData(trigger, EventTriggerType.BeginDrag, data =>
            {
                _dragged = true;
                _dragOwned = owns;
                if (!RootPoint(data, out Vector2 start)) return;

                _dragFrom = start;
                Claimed(element, "dragstart", measured, data, Vector2.zero);
                if (_dragOwned || around == null) return;

                var pointer = data.TryCast<PointerEventData>();
                if (pointer == null) return;

                // Taking hold of the content cancels whatever the wheel was still aiming at, or the list snaps back
                // to it the moment the drag ends.
                SmoothScroll.Release(around);
                around.OnBeginDrag(pointer);
            });

            AddWithData(trigger, EventTriggerType.Drag, data =>
            {
                _dragged = true;
                if (!RootPoint(data, out Vector2 now)) return;

                Vector2 delta = now - _dragFrom;
                _dragFrom = now;
                Claimed(element, "drag", measured, data, delta);
                if (_dragOwned || around == null) return;

                var pointer = data.TryCast<PointerEventData>();
                if (pointer != null) around.OnDrag(pointer);
            });

            AddWithData(trigger, EventTriggerType.EndDrag, data =>
            {
                Claimed(element, "dragend", measured, data, Vector2.zero);
                if (_dragOwned || around == null) return;

                var pointer = data.TryCast<PointerEventData>();
                if (pointer != null) around.OnEndDrag(pointer);
            });
        }

        /// <summary>Tell the page about one drag phase and remember whether it claimed the gesture.</summary>
        private void Claimed(IElement element, string type, RectTransform measured, BaseEventData data, Vector2 delta)
        {
            if (_onDragged != null && _onDragged(element, type, SpotIn(measured, data), delta)) _dragOwned = true;
        }

        /// <summary>
        /// One wheel notch, reported the way the DOM reports it: positive is scrolling AWAY from the reader, which is
        /// the opposite sign to Unity's. True when the page called <c>preventDefault()</c> and the notch must not
        /// also scroll whatever it is over.
        /// </summary>
        private bool Wheel(IElement element, BaseEventData data)
        {
            var pointer = data.TryCast<PointerEventData>();
            if (pointer == null) return false;

            return _onWheel != null && _onWheel(element, -pointer.scrollDelta.y);
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
        /// wired, because the hierarchy above a box does not change while the page stands. It is handed back so that
        /// an element with its own wheel listener can forward to the same one after the page has had its say.
        /// </summary>
        private static ScrollRect PassScrollingThrough(EventTrigger trigger, Transform host,
                                                       bool forwardDrag = true, bool forwardWheel = true)
        {
            ScrollRect scroll = host == null ? null : host.GetComponentInParent<ScrollRect>();
            if (scroll == null) return null;

            if (forwardWheel)
                AddWithData(trigger, EventTriggerType.Scroll, data =>
                {
                    var pointer = data.TryCast<PointerEventData>();
                    if (pointer == null) return;

                    // Eased when this area is smoothed; handed straight to uGUI when it is not, or when there is
                    // nothing to scroll and the walk would have no distance to cover.
                    if (!SmoothScroll.Wheel(scroll, pointer)) scroll.OnScroll(pointer);
                });

            if (!forwardDrag) return scroll;

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

            return scroll;
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

        private void Pointer(IElement element, string type, RectTransform measured, BaseEventData data, int detail = 1)
        {
            Remember(data);
            _onPointer?.Invoke(element, type, SpotIn(measured, data), Ray(data), ButtonOf(data), detail);
        }

        // ------------------------------------------------------------------ mousemove --

        /// <summary>
        /// Whether the page listens for `mousemove` anywhere. Set by the wiring pass, once per build.
        ///
        /// It is a switch rather than an always-on poll because that poll is the whole cost of the feature. uGUI has
        /// no pointer-move event to hang this on - <see cref="EventTriggerType"/> has PointerEnter, PointerExit and
        /// Drag and nothing between them - so the position has to be read per frame and compared. A page that never
        /// asks pays nothing; one that asks pays one vector comparison a frame.
        /// </summary>
        internal bool WantsMove { get; set; }

        /// <summary>
        /// Raise `mousemove` on whatever the pointer is over, when it has actually moved.
        ///
        /// Only the innermost WIRED element is known here; the view re-targets from there to the deepest painted box
        /// under the pointer, exactly as it does for a click. So a page that listens on its root still learns which
        /// row the pointer is over without every box needing a hit target of its own.
        /// </summary>
        internal void TickMove()
        {
            if (!WantsMove || _hovered == null || !_sawPointer) return;

            Vector3 now = UnityEngine.Input.mousePosition;
            if ((now - _lastMouse).sqrMagnitude < 0.01f) return;
            _lastMouse = now;

            var ray = new PointerRay(now, _lastCamera);
            _onPointer?.Invoke(_hovered, "mousemove", SpotIn(_hoveredRect, ray), ray, 0, 0);
        }

        /// <summary>The pointer entered a wired element. Innermost wins: uGUI raises PointerEnter on the child after
        /// the parent, so the last one in is the one the pointer is really over.</summary>
        private void Entered(IElement element, RectTransform measured, BaseEventData data)
        {
            Remember(data);
            _hovered = element;
            _hoveredRect = measured;
        }

        /// <summary>The pointer left a wired element. Only clears when it is still the one being tracked - leaving a
        /// parent after entering its child must not forget the child.</summary>
        private void Left(IElement element)
        {
            if (!ReferenceEquals(_hovered, element)) return;

            _hovered = null;
            _hoveredRect = null;
        }

        /// <summary>Keep the camera an event arrived through, so a position read outside an event can be converted the
        /// same way. Null is a valid camera here - a Screen Space Overlay canvas reports none - which is why the fact
        /// that an event was seen at all is tracked separately.</summary>
        private void Remember(BaseEventData data)
        {
            var pointer = data?.TryCast<PointerEventData>();
            if (pointer == null) return;

            _lastCamera = pointer.pressEventCamera;
            _lastMouse = pointer.position;
            _sawPointer = true;
        }

        /// <summary>0 left, 1 middle, 2 right - the numbering the DOM uses, which is not Unity's enum order.</summary>
        private static int ButtonOf(BaseEventData data)
        {
            var pointer = data?.TryCast<PointerEventData>();
            if (pointer == null) return 0;

            return pointer.button switch
            {
                PointerEventData.InputButton.Right => 2,
                PointerEventData.InputButton.Middle => 1,
                _ => 0,
            };
        }

        /// <summary>
        /// Whether this click is the second of a pair on the same element, within the window a double-click gets.
        ///
        /// Kept here rather than left to uGUI because `PointerEventData.clickCount` counts clicks on the GAME OBJECT,
        /// and this page rebuilds its objects on every change - so the count resets under any page that reacts to
        /// the first click, which is every page that would want a double.
        /// </summary>
        private bool IsSecondClick(IElement element)
        {
            float now = Time.unscaledTime;
            bool second = ReferenceEquals(element, _lastClicked) && now - _lastClickAt <= DoubleClickSeconds;

            _lastClicked = second ? null : element;   // a third click starts a new pair, it does not extend this one
            _lastClickAt = now;
            return second;
        }

        /// <summary>Windows' own default, and the one every browser on it follows.</summary>
        private const float DoubleClickSeconds = 0.5f;

        private IElement _lastClicked;
        private float _lastClickAt;

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
