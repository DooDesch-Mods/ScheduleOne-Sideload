using AngleSharp.Dom;
using Sideload.Css;
using Sideload.Dom;
using Sideload.Input;
using Sideload.Layout;
using Sideload.Paint;
using UnityEngine;
using UnityEngine.UI;

namespace Sideload.Host
{
    /// <summary>
    /// The pointer held down, and the form controls that need it: pointer events with capture, dragging a range,
    /// and the popups of a select and a colour input.
    ///
    /// A press is followed by polling rather than by uGUI's drag events, and that is the design decision everything
    /// here rests on. A range changes its value on every frame of a drag, a changed value rebuilds the page, and a
    /// rebuild destroys every GameObject - including the one uGUI is delivering the drag to. Its PointerUp then never
    /// arrives. The press is therefore the view's own state: it starts on uGUI's PointerDown, follows
    /// <c>Input.mousePosition</c> each frame, and ends when the button is no longer held - whichever objects exist by
    /// then.
    /// </summary>
    public sealed partial class WebView
    {
        private enum FormDrag { None, Range, Sv, Hue }

        private bool _pressed;
        private int _pressButton;
        private bool _pressInPopup;
        private IElement _pressTarget;
        private Vector2 _pointerWas;

        /// <summary>The element a page captured with `setPointerCapture`, until release.</summary>
        private IElement _capture;

        private FormDrag _formDrag;
        private IElement _formDragElement;
        private string _rangeAtPress;

        private FormPopup _popup;
        private Dictionary<IElement, ComputedStyle> _popupStyles;
        private bool _popupScrollWanted;

        private ScriptedDrag _scripted;

        // ------------------------------------------------------------------------------------------- the press --

        /// <summary>
        /// The button went down on a wired element. `pointerdown` first, then the `mousedown` a browser sends for
        /// compatibility - which a page cancels by calling `preventDefault()` on the pointer event, as in a browser.
        /// A range starts following the pointer here; its value jumps to where it was pressed.
        /// </summary>
        private void BeginPress(IElement target, PointerSpot at, PointerSpot page, PointerRay ray, int button)
        {
            int buttons = ButtonBit(button) | (_interaction?.ButtonsHeld() ?? 0);

            // A second button pressed during a press is a change of buttons, not a new press: a browser reports it
            // on pointermove. The compatibility event still goes out.
            if (_pressed)
            {
                _script?.Dispatch(target, "mousedown", spot: at, button: button, clientX: page.OffsetX,
                                  clientY: page.OffsetY, buttons: buttons);
                return;
            }

            _pressed = true;
            _pressInPopup = false;
            _pressButton = button;
            _pressTarget = target;
            _pointerWas = ray.Valid ? ray.Screen : Vector2.zero;

            Script.JsEvent down = _script?.Dispatch(target, "pointerdown", spot: at, button: button,
                                                    clientX: page.OffsetX, clientY: page.OffsetY, buttons: buttons);
            bool cancelled = down != null && down.DefaultPrevented;
            if (!cancelled)
            {
                Script.JsEvent compat = _script?.Dispatch(target, "mousedown", spot: at, button: button,
                                                          clientX: page.OffsetX, clientY: page.OffsetY, buttons: buttons);
                cancelled = compat != null && compat.DefaultPrevented;
            }

            if (cancelled || button != 0 || target == null) return;
            if (ControlKinds.Of(target) != ControlKind.Range || target.HasAttribute("disabled")) return;

            _formDrag = FormDrag.Range;
            _formDragElement = target;
            _rangeAtPress = FormValues.RangeValue(target);
            if (_interaction != null) _interaction.Captured = true;
            MoveRange(ray);
        }

        /// <summary>
        /// The press ended - the button came up, wherever the pointer is and whatever was rebuilt in between.
        /// `pointerup` and `mouseup` go to the capturing element, or to whatever is under the pointer; a range that
        /// moved during the press raises its `change` here, once.
        /// </summary>
        private void EndPress(PointerRay ray)
        {
            if (!_pressed) return;
            _pressed = false;

            FormDrag drag = _formDrag;
            IElement dragged = _formDragElement;
            _formDrag = FormDrag.None;
            _formDragElement = null;
            if (_interaction != null) _interaction.Captured = false;

            if (_pressInPopup)
            {
                _pressInPopup = false;
                return;
            }

            IElement body = _document?.Body ?? _document?.DocumentElement;
            IElement target = _capture ?? DeepestUnder(body, ray) ?? _pressTarget;
            _pressTarget = null;

            if (target != null && _script != null)
            {
                PointerSpot at = SpotOn(target, ray);
                PointerSpot page = ray.Valid ? Interaction.SpotIn(_root, ray) : default;
                int buttons = _interaction?.ButtonsHeld() ?? 0;

                _script.Dispatch(target, "pointerup", spot: at, button: _pressButton, clientX: page.OffsetX,
                                 clientY: page.OffsetY, buttons: buttons);
                ReleaseCapture();
                _script.Dispatch(target, "mouseup", spot: at, button: _pressButton, clientX: page.OffsetX,
                                 clientY: page.OffsetY, buttons: buttons);
            }
            else ReleaseCapture();

            if (drag == FormDrag.Range && dragged != null)
            {
                string now = FormValues.RangeValue(dragged);
                if (!string.Equals(now, _rangeAtPress, StringComparison.Ordinal))
                    _script?.Dispatch(dragged, "change", value: now);
            }
        }

        /// <summary>
        /// One frame of a held press: end it when the button is up, otherwise follow the pointer - the range or the
        /// colour field being dragged, and `pointermove`/`mousemove` with `buttons` set to the capturing element or
        /// whatever is under the pointer. Outside a press the hover poll runs as before.
        /// </summary>
        private void TickPointer()
        {
            if (_scripted != null) AdvanceScriptedDrag();

            if (!_pressed)
            {
                _interaction?.TickMove();
                FinishScriptedDrag();
                return;
            }

            PointerRay ray = _interaction?.Now() ?? default;
            int held = _interaction?.ButtonsHeld() ?? 0;

            if ((held & ButtonBit(_pressButton)) == 0)
            {
                EndPress(ray);
                FinishScriptedDrag();
                return;
            }

            if (!ray.Valid || (ray.Screen - _pointerWas).sqrMagnitude < 0.01f) return;
            _pointerWas = ray.Screen;

            switch (_formDrag)
            {
                case FormDrag.Range: MoveRange(ray); break;
                case FormDrag.Sv:
                case FormDrag.Hue: MovePopupDrag(ray); break;
            }

            if (_pressInPopup || _script == null) return;

            IElement body = _document?.Body ?? _document?.DocumentElement;
            IElement target = _capture ?? DeepestUnder(body, ray);
            if (target == null) return;

            PointerSpot at = SpotOn(target, ray);
            PointerSpot page = Interaction.SpotIn(_root, ray);
            _script.Dispatch(target, "pointermove", spot: at, clientX: page.OffsetX, clientY: page.OffsetY, buttons: held);
            _script.Dispatch(target, "mousemove", spot: at, clientX: page.OffsetX, clientY: page.OffsetY, buttons: held);
        }

        /// <summary>
        /// `setPointerCapture` and `releasePointerCapture`. Only during a press, which is the only time a browser
        /// accepts one too; outside a press the call is ignored and says so in the log.
        /// </summary>
        private void OnPointerCapture(IElement element, bool on)
        {
            if (element == null) return;

            if (!on)
            {
                if (ReferenceEquals(_capture, element)) ReleaseCapture();
                return;
            }

            if (!_pressed || _pressInPopup)
            {
                Core.Log?.Msg($"[Sideload] {_appId}: setPointerCapture on <{element.LocalName}> ignored - no button is held.");
                return;
            }

            if (ReferenceEquals(_capture, element)) return;
            ReleaseCapture();

            _capture = element;
            if (_interaction != null) _interaction.Captured = true;
            _script?.Dispatch(element, "gotpointercapture");
        }

        private void ReleaseCapture()
        {
            IElement was = _capture;
            if (was == null) return;

            _capture = null;
            if (_interaction != null) _interaction.Captured = _formDrag != FormDrag.None;
            _script?.Dispatch(was, "lostpointercapture");
        }

        private IElement CapturedElement() => _capture;

        /// <summary>A reload starts a new page: no press, no capture, no popup carries over into it.</summary>
        private void ForgetPointerState()
        {
            _pressed = false;
            _pressInPopup = false;
            _pressTarget = null;
            _capture = null;
            _formDrag = FormDrag.None;
            _formDragElement = null;
            _popup = null;
            _popupStyles = null;
            if (_interaction != null) _interaction.Captured = false;
        }

        private static int ButtonBit(int button) => button switch { 1 => 4, 2 => 2, _ => 1 };

        /// <summary>The pointer in an element's own box, or nothing when the element is not on screen.</summary>
        private PointerSpot SpotOn(IElement element, PointerRay ray) =>
            ray.Valid && element != null && _painted != null
            && _painted.TryGetValue(element, out Painter.PaintedBox box) && box.Rect != null
                ? Interaction.SpotIn(box.Rect, ray)
                : default;

        // ------------------------------------------------------------------------------------------- range --

        /// <summary>
        /// Put a range's value where the pointer is, measured against the range as it was laid out: its content box
        /// and its own thumb, so the thumb's centre follows the pointer. `input` fires for each new value, none for a
        /// pointer that moved without leaving the step it was on.
        /// </summary>
        private void MoveRange(PointerRay ray)
        {
            IElement range = _formDragElement;
            if (range == null || _painted == null) return;
            if (!_painted.TryGetValue(range, out Painter.PaintedBox box) || box.Node == null || box.Rect == null)
            {
                Core.Log?.Warning($"[Sideload] {_appId}: the range being dragged is not on screen; the drag is ignored.");
                return;
            }

            PointerSpot spot = Interaction.SpotIn(box.Rect, ray);
            double fraction = FormParts.RangeFractionAt(box.Node, spot.OffsetX);
            string next = FormValues.Format(FormValues.ValueAt(FormValues.Range(range), fraction));
            if (string.Equals(next, FormValues.RangeValue(range), StringComparison.Ordinal)) return;

            range.SetAttribute("value", next);
            _script?.Dispatch(range, "input", value: next);
            QueueRebuild();
        }

        // ------------------------------------------------------------------------------------------- popups --

        /// <summary>A click landed on a select or a colour input: open its popup, unless the page cancelled the click
        /// or the control is disabled.</summary>
        private void OpenPopupFor(IElement target)
        {
            if (target == null || target.HasAttribute("disabled")) return;

            ControlKind kind = ControlKinds.Of(target);
            if (kind != ControlKind.Select && kind != ControlKind.Color) return;

            if (_popup != null) ClosePopup();

            _popup = kind == ControlKind.Select ? FormPopup.ForSelect(target) : FormPopup.ForColor(target);
            _popup.Anchor = ViewportBoxOf(target);
            _popupScrollWanted = true;
            QueueRebuild();

            Core.Log?.Msg($"[Sideload] {_appId}: opened the {(kind == ControlKind.Select ? "option list" : "colour picker")}"
                          + $" of <{target.LocalName}{(string.IsNullOrEmpty(target.Id) ? "" : "#" + target.Id)}>.");
        }

        /// <summary>
        /// Close the open popup. A colour picker reports `change` here when the colour moved off the one it opened
        /// with - the moment a browser's picker reports it too, since `input` already followed every drag.
        /// </summary>
        private void ClosePopup()
        {
            FormPopup popup = _popup;
            if (popup == null) return;

            _popup = null;
            _popupStyles = null;
            if (_pressInPopup) { _pressed = false; _pressInPopup = false; _formDrag = FormDrag.None; }
            if (_focused != null && popup.Owns(_focused)) _focused = null;

            if (popup.Kind == FormPopup.Kinds.Color)
            {
                string now = FormValues.ColorValue(popup.Owner);
                if (!string.Equals(now, popup.ValueAtOpen, StringComparison.Ordinal))
                    _script?.Dispatch(popup.Owner, "change", value: now);
            }

            QueueRebuild();
        }

        /// <summary>Choose an option from the open list: the select takes it, the list closes, and `input` then
        /// `change` fire when the choice is a different one. A disabled option does nothing, as in a browser.</summary>
        private void ChooseOption(int index)
        {
            FormPopup popup = _popup;
            if (popup == null || popup.Kind != FormPopup.Kinds.Select) return;

            List<IElement> options = FormValues.Options(popup.Owner);
            if (index < 0 || index >= options.Count || FormValues.IsDisabled(options[index])) return;

            bool changed = FormValues.SelectedIndex(popup.Owner) != index;
            FormValues.Select(popup.Owner, index);
            ClosePopup();
            if (!changed) return;

            string value = FormValues.SelectValue(popup.Owner);
            _script?.Dispatch(popup.Owner, "input", value: value);
            _script?.Dispatch(popup.Owner, "change", value: value);
        }

        /// <summary>
        /// A pointer event on a popup element. Never shown to the page: the popup is the engine's, and the page
        /// hears about what it did to the control - `input` and `change` - rather than how the pointer got there.
        /// </summary>
        private void PopupPointer(IElement element, string type, PointerRay ray, int button)
        {
            FormPopup popup = _popup;
            if (popup == null) return;

            IElement target = DeepestUnder(element, ray) ?? element;
            string part = FormPopup.PartOf(target);

            switch (type)
            {
                case "mousedown":
                    if (button != 0 || (part != "sv" && part != "hue")) return;
                    _pressed = true;
                    _pressInPopup = true;
                    _pressButton = 0;
                    _pointerWas = ray.Valid ? ray.Screen : Vector2.zero;
                    _formDrag = part == "sv" ? FormDrag.Sv : FormDrag.Hue;
                    _formDragElement = target;
                    if (_interaction != null) _interaction.Captured = true;
                    MovePopupDrag(ray);
                    return;

                case "mouseup":
                    EndPress(ray);
                    return;

                case "click":
                    if (part == "scrim") ClosePopup();
                    else if (part == "option") ChooseOption(popup.IndexOf(target));
                    return;
            }
        }

        /// <summary>Follow the pointer across the colour field or the hue bar. The cursor moves on every frame; the
        /// control's value and its `input` only when the colour itself changed.</summary>
        private void MovePopupDrag(PointerRay ray)
        {
            FormPopup popup = _popup;
            IElement part = _formDragElement;
            if (popup == null || part == null || _painted == null) return;
            if (!_painted.TryGetValue(part, out Painter.PaintedBox box) || box.Rect == null) return;

            PointerSpot spot = Interaction.SpotIn(box.Rect, ray);
            string hex = _formDrag == FormDrag.Sv ? popup.PickSv(spot.OffsetX, spot.OffsetY) : popup.PickHue(spot.OffsetX);
            QueueRebuild();

            if (string.Equals(hex, FormValues.ColorValue(popup.Owner), StringComparison.Ordinal)) return;
            popup.Owner.SetAttribute("value", hex);
            _script?.Dispatch(popup.Owner, "input", value: hex);
        }

        /// <summary>Typing in the picker's hex field. A complete code moves the picker and the control; half a code
        /// waits for the rest.</summary>
        private void PopupTyped(IElement field, string text)
        {
            FormPopup popup = _popup;
            if (popup == null) return;

            SetFocused(field);
            field.SetAttribute("value", text ?? "");

            string hex = popup.TypeHex(text);
            if (hex == null) return;

            QueueRebuild();
            if (string.Equals(hex, FormValues.ColorValue(popup.Owner), StringComparison.Ordinal)) return;
            popup.Owner.SetAttribute("value", hex);
            _script?.Dispatch(popup.Owner, "input", value: hex);
        }

        /// <summary>
        /// Arrow keys, Home, End and Enter while an option list is open. Read straight from the keyboard because no
        /// text field has the caret - the list is not something the player types into. Escape is not here: it
        /// arrives as `back`, and <see cref="DispatchBack"/> closes the popup before the page or the phone hears it.
        /// </summary>
        private void TickPopupKeys()
        {
            if (_popup == null || _popup.Kind != FormPopup.Kinds.Select) return;

            string key =
                UnityEngine.Input.GetKeyDown(KeyCode.DownArrow) ? "ArrowDown" :
                UnityEngine.Input.GetKeyDown(KeyCode.UpArrow) ? "ArrowUp" :
                UnityEngine.Input.GetKeyDown(KeyCode.Home) ? "Home" :
                UnityEngine.Input.GetKeyDown(KeyCode.End) ? "End" :
                UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.KeypadEnter) ? "Enter" :
                null;

            if (key != null) PopupKey(key);
        }

        /// <summary>One key in an open option list, by its DOM name. False when the key means nothing here.</summary>
        internal bool PopupKey(string key)
        {
            FormPopup popup = _popup;
            if (popup == null || popup.Kind != FormPopup.Kinds.Select) return false;

            int count = popup.Rows.Count;
            int at = popup.Highlight;
            int next;

            switch (key)
            {
                case "ArrowDown": next = at < 0 ? FormValues.NextEnabled(popup.Owner, -1, 1) : FormValues.NextEnabled(popup.Owner, at, 1); break;
                case "ArrowUp": next = at < 0 ? FormValues.NextEnabled(popup.Owner, count, -1) : FormValues.NextEnabled(popup.Owner, at, -1); break;
                case "Home": next = FormValues.NextEnabled(popup.Owner, -1, 1); break;
                case "End": next = FormValues.NextEnabled(popup.Owner, count, -1); break;
                case "Enter": ChooseOption(at); return true;
                case "Escape": ClosePopup(); return true;
                default: return false;
            }

            if (next < 0 || next >= count || next == at) return true;

            popup.SetHighlight(next);
            _popupScrollWanted = true;
            QueueRebuild();
            return true;
        }

        /// <summary>
        /// Lay the open popup out with the page, called between building the page's tree and laying it out. The
        /// popup closes instead when its control has left the page or been disabled - a list floating next to
        /// nothing would choose into a select nobody can see.
        /// </summary>
        private void AppendPopup(LayoutNode tree, Dictionary<IElement, ComputedStyle> styles, float cssW, float cssH)
        {
            FormPopup popup = _popup;
            if (popup == null || tree == null) return;

            IElement owner = popup.Owner;
            bool gone = _document == null || !_document.Contains(owner) || owner.HasAttribute("disabled")
                        || styles == null || !styles.ContainsKey(owner);
            if (gone)
            {
                Core.Log?.Msg($"[Sideload] {_appId}: closed the popup of <{owner.LocalName}> - the control is gone or disabled.");
                _popup = null;
                _popupStyles = null;
                return;
            }

            // Measured against the box on screen before this rebuild replaces it, which is where the player sees
            // the control - scroll offsets included, which the layout tree does not know about.
            float[] anchor = ViewportBoxOf(owner);
            if (anchor[2] > 0f || anchor[3] > 0f) popup.Anchor = anchor;

            popup.Place(cssW, cssH);
            LayoutNode node = popup.Build(_context, out _popupStyles);
            if (node != null) tree.Add(node);
        }

        /// <summary>Give the popup's elements their hit targets, after the page's own wiring.</summary>
        private void WirePopup()
        {
            FormPopup popup = _popup;
            if (popup == null || _painted == null) return;

            foreach (IElement element in popup.Interactive())
            {
                if (element == null || !_painted.TryGetValue(element, out Painter.PaintedBox box)) continue;

                _interaction.Attach(box.Rect, element, disabled: false,
                                    ownGameObject: ControlKinds.Of(element) == ControlKind.Text);
                _wired.Add(element);
            }
        }

        /// <summary>Bring the highlighted row into the list's view, after a rebuild has restored where it was
        /// scrolled to. Only when asked: on opening, and after an arrow key.</summary>
        private void ApplyPopupScroll()
        {
            if (!_popupScrollWanted || _popup == null || _popup.List == null || _painted == null) return;
            _popupScrollWanted = false;

            if (!_painted.TryGetValue(_popup.List, out Painter.PaintedBox box) || box.Rect == null) return;

            var scroll = box.Rect.GetComponentInChildren<ScrollRect>();
            if (scroll == null || scroll.content == null) return;   // the whole list fits

            RectTransform view = scroll.viewport != null ? scroll.viewport : box.Rect;
            float viewH = view.rect.height;
            float range = scroll.content.rect.height - viewH;
            if (range <= 0f) return;

            float rowTop = _popup.HighlightTop();
            if (rowTop < 0f) return;

            float top = (1f - scroll.verticalNormalizedPosition) * range;
            if (rowTop < top) top = rowTop;
            else if (rowTop + FormPopup.RowHeight > top + viewH) top = rowTop + FormPopup.RowHeight - viewH;

            scroll.verticalNormalizedPosition = 1f - Mathf.Clamp01(top / range);
        }

        /// <summary>
        /// An element's box on screen, in css pixels from the viewport's top left - read from the live RectTransform,
        /// so a box inside a scrolled list reports where it is drawn, not where the layout put it before the scroll.
        /// Falls back to the layout's answer for a box that is not painted.
        /// </summary>
        private float[] ViewportBoxOf(IElement element)
        {
            if (element == null || _painted == null || _root == null
                || !_painted.TryGetValue(element, out Painter.PaintedBox box) || box.Rect == null)
                return RectOf(element);

            Rect r = box.Rect.rect;
            Vector3 a = _root.InverseTransformPoint(box.Rect.TransformPoint(new Vector3(r.xMin, r.yMax, 0f)));
            Vector3 b = _root.InverseTransformPoint(box.Rect.TransformPoint(new Vector3(r.xMax, r.yMin, 0f)));

            Rect root = _root.rect;
            float x1 = a.x - root.xMin, x2 = b.x - root.xMin;
            float y1 = root.yMax - a.y, y2 = root.yMax - b.y;
            return new[] { Mathf.Min(x1, x2), Mathf.Min(y1, y2), Mathf.Abs(x2 - x1), Mathf.Abs(y2 - y1) };
        }

        // ------------------------------------------------------------------------------------------- probe --

        /// <summary>A drag driven by the debug probe: press at one point, move in steps, release at another.</summary>
        private sealed class ScriptedDrag
        {
            internal string Selector;
            internal Vector2 From, To;
            internal Camera Camera;
            internal int Steps;
            internal int Step;
            internal bool Released;
        }

        /// <summary>
        /// Debug probe: drag across an element, through the same press, poll and release the mouse goes through.
        /// The points are fractions of the element's box (0,0 top left, 1,1 bottom right). The selector is looked
        /// up in the page and then in the open popup, so a probe can drag the colour field too.
        ///
        /// One step per frame, because that is what the code under test expects: a rebuild between two moves is
        /// the case that broke uGUI's own drag, and a drag finished within one frame would never meet it.
        /// </summary>
        internal void DebugDrag(string selector, float fromX, float fromY, float toX, float toY, int steps)
        {
            IElement element = _document?.QuerySelector(selector) ?? _popup?.Document.QuerySelector(selector);
            if (element == null || _painted == null
                || !_painted.TryGetValue(element, out Painter.PaintedBox box) || box.Rect == null)
            {
                Core.Log?.Warning($"[Sideload/probe] drag: '{selector}' is not painted.");
                return;
            }

            if (_scripted != null) Core.Log?.Warning($"[Sideload/probe] drag on '{_scripted.Selector}' replaced by '{selector}'.");

            Canvas canvas = box.Rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Rect r = box.Rect.rect;
            Vector2 Screen(float fx, float fy) => RectTransformUtility.WorldToScreenPoint(camera,
                box.Rect.TransformPoint(new Vector3(r.xMin + fx * r.width, r.yMax - fy * r.height, 0f)));

            _scripted = new ScriptedDrag
            {
                Selector = selector,
                From = Screen(fromX, fromY),
                To = Screen(toX, toY),
                Camera = camera,
                Steps = Math.Max(1, steps),
            };
            Core.Log?.Msg($"[Sideload/probe] drag on '{selector}' from ({fromX:0.##},{fromY:0.##}) to ({toX:0.##},{toY:0.##}) in {_scripted.Steps} step(s).");
        }

        private void AdvanceScriptedDrag()
        {
            ScriptedDrag drag = _scripted;
            if (drag == null || drag.Released || _interaction == null) return;

            if (drag.Step == 0)
            {
                _interaction.DriveSynthetic(drag.From, drag.Camera, 1);
                PointerRay ray = _interaction.Now();
                drag.Step = 1;

                // The press lands on whatever is under the point - the popup first, it lies above the page.
                IElement hit = _popup != null ? DeepestUnder(_popup.Scrim, ray) : null;
                if (hit != null) { PopupPointer(hit, "mousedown", ray, 0); return; }

                IElement body = _document?.Body ?? _document?.DocumentElement;
                IElement target = DeepestUnder(body, ray);
                if (target == null)
                {
                    Core.Log?.Warning("[Sideload/probe] drag: nothing on the page under the start point.");
                    _interaction.EndSynthetic();
                    _scripted = null;
                    return;
                }

                BeginPress(target, SpotOn(target, ray), Interaction.SpotIn(_root, ray), ray, 0);
                return;
            }

            if (drag.Step <= drag.Steps)
            {
                float t = drag.Step / (float)drag.Steps;
                _interaction.DriveSynthetic(Vector2.Lerp(drag.From, drag.To, t), drag.Camera, 1);
                drag.Step++;
                return;
            }

            _interaction.DriveSynthetic(drag.To, drag.Camera, 0);
            drag.Released = true;
        }

        private void FinishScriptedDrag()
        {
            if (_scripted == null || !_scripted.Released) return;

            _interaction?.EndSynthetic();
            Core.Log?.Msg($"[Sideload/probe] drag on '{_scripted.Selector}' released.");
            _scripted = null;
        }
    }
}
