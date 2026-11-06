#if DEBUG
using System.Collections.Generic;
using AngleSharp.Dom;
using Sideload.Input;
using Sideload.Paint;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Sideload.Host
{
    /// <summary>
    /// Debug probe: a mouse gesture spread over frames, the way a player's hand makes it.
    ///
    /// The click probe (<see cref="Devtools.ClickProbe"/>) sends down, up and click in one call on one GameObject, so
    /// it can never meet what happens between them. A real click is a pointer that rests on the element for a while,
    /// a press, a few frames, and a release - and anything the page rebuilds in between replaces the GameObjects the
    /// press landed on. This probe walks those frames through the real EventSystem raycast and decides the click with
    /// the rule uGUI's input module uses: the release only clicks when the object that took the press is still the
    /// click handler under the pointer. It reports what uGUI delivered; the page's own reaction is for the caller to
    /// read.
    /// </summary>
    public sealed partial class WebView
    {
        private sealed class Gesture
        {
            internal string Selector;
            internal Vector2 Point;
            internal Camera Camera;
            internal int HoverFrames, HoldFrames;
            internal int Frame;
            internal PointerEventData Data;
            internal readonly List<GameObject> Hovered = new List<GameObject>();
            internal GameObject PressedOn;
            internal int RendersAtStart, EntersAtStart, RendersAtPress, EntersAtPress;
            internal bool UpSent, ClickSent;
        }

        private Gesture _gesture;

        /// <summary>`mouseenter` dispatches since the view was built, for the gesture probe.</summary>
        private int _enterDispatches;

        /// <summary>
        /// Rest on the element's centre for <paramref name="hoverFrames"/> frames, press, hold for
        /// <paramref name="holdFrames"/> frames, release. One line in the log at the end says what uGUI delivered.
        /// </summary>
        internal void DebugGesture(string selector, int hoverFrames, int holdFrames)
        {
            IElement element = _document?.QuerySelector(selector);
            if (element == null || _painted == null
                || !_painted.TryGetValue(element, out Painter.PaintedBox box) || box.Rect == null)
            {
                Core.Log?.Warning($"[Sideload/probe] gesture: '{selector}' is not painted.");
                return;
            }

            EventSystem system = EventSystem.current;
            if (system == null)
            {
                Core.Log?.Warning("[Sideload/probe] gesture: no EventSystem.");
                return;
            }

            if (_gesture != null) Core.Log?.Warning($"[Sideload/probe] gesture on '{_gesture.Selector}' replaced by '{selector}'.");

            Canvas canvas = box.Rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector3 world = box.Rect.TransformPoint(box.Rect.rect.center);

            _gesture = new Gesture
            {
                Selector = selector,
                Point = RectTransformUtility.WorldToScreenPoint(camera, world),
                Camera = camera,
                HoverFrames = Math.Max(1, hoverFrames),
                HoldFrames = Math.Max(1, holdFrames),
                Data = new PointerEventData(system) { button = PointerEventData.InputButton.Left },
                RendersAtStart = _renders,
                EntersAtStart = _enterDispatches,
            };
            _gesture.Data.position = _gesture.Point;
            Core.Log?.Msg($"[Sideload/probe] gesture on '{selector}': hover {_gesture.HoverFrames} frame(s), hold {_gesture.HoldFrames} frame(s).");
        }

        /// <summary>One frame of the gesture. Runs before the frame's rebuild, as the input module does.</summary>
        private void AdvanceGesture()
        {
            Gesture g = _gesture;
            if (g == null || _interaction == null) return;

            EventSystem system = EventSystem.current;
            if (system == null)
            {
                Core.Log?.Warning("[Sideload/probe] gesture: the EventSystem went away.");
                _interaction.EndSynthetic();
                _gesture = null;
                return;
            }

            int pressAt = g.HoverFrames;
            int releaseAt = g.HoverFrames + g.HoldFrames;
            bool held = g.Frame >= pressAt && g.Frame < releaseAt;
            _interaction.DriveSynthetic(g.Point, g.Camera, held ? 1 : 0);

            GameObject over = RaycastTop(system, g.Data);
            MoveOver(g, over);

            if (g.Frame == pressAt) Press(g, over);
            else if (g.Frame == releaseAt) Release(g, over);
            else if (g.Frame > releaseAt + 2) Finish(g);

            g.Frame++;
        }

        private static GameObject RaycastTop(EventSystem system, PointerEventData data)
        {
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            system.RaycastAll(data, hits);
            if (hits.Count == 0) return null;

            data.pointerCurrentRaycast = hits[0];
            return hits[0].gameObject;
        }

        /// <summary>
        /// Enter and exit the way the input module raises them: a destroyed object counts as gone, so a rebuild under
        /// a resting pointer enters the new boxes from the top down without leaving anything first.
        /// </summary>
        private static void MoveOver(Gesture g, GameObject over)
        {
            GameObject was = g.Data.pointerEnter;
            if (was != null && was == over) return;

            var chain = new List<GameObject>();
            for (Transform t = over != null ? over.transform : null; t != null; t = t.parent) chain.Add(t.gameObject);

            for (int i = g.Hovered.Count - 1; i >= 0; i--)
            {
                GameObject h = g.Hovered[i];
                if (h != null && Holds(chain, h)) continue;
                if (h != null) ExecuteEvents.Execute(h, g.Data, ExecuteEvents.pointerExitHandler);
                g.Hovered.RemoveAt(i);
            }

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (Holds(g.Hovered, chain[i])) continue;
                ExecuteEvents.Execute(chain[i], g.Data, ExecuteEvents.pointerEnterHandler);
                g.Hovered.Add(chain[i]);
            }
            g.Data.pointerEnter = over;
        }

        /// <summary>By the native object: two interop wrappers of one GameObject are different managed objects.</summary>
        private static bool Holds(List<GameObject> list, GameObject go)
        {
            foreach (GameObject x in list)
                if (x != null && go != null && x.Pointer == go.Pointer) return true;
            return false;
        }

        private void Press(Gesture g, GameObject over)
        {
            g.RendersAtPress = _renders;
            g.EntersAtPress = _enterDispatches;
            if (over == null) { Core.Log?.Warning($"[Sideload/probe] gesture on '{g.Selector}': nothing under the pointer at the press."); return; }

            g.Data.pointerPressRaycast = g.Data.pointerCurrentRaycast;
            GameObject pressed = ExecuteEvents.ExecuteHierarchy(over, g.Data, ExecuteEvents.pointerDownHandler)
                                 ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(over);
            g.Data.pointerPress = pressed;
            g.Data.rawPointerPress = over;
            g.Data.eligibleForClick = true;
            g.PressedOn = pressed;
        }

        private void Release(Gesture g, GameObject over)
        {
            GameObject pressed = g.Data.pointerPress;
            if (pressed != null)
            {
                ExecuteEvents.Execute(pressed, g.Data, ExecuteEvents.pointerUpHandler);
                g.UpSent = true;
            }

            GameObject clickHandler = over != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(over) : null;
            if (pressed != null && pressed == clickHandler && g.Data.eligibleForClick)
            {
                ExecuteEvents.Execute(pressed, g.Data, ExecuteEvents.pointerClickHandler);
                g.ClickSent = true;
            }

            g.Data.eligibleForClick = false;
            g.Data.pointerPress = null;
            g.Data.rawPointerPress = null;

            Core.Log?.Msg($"[Sideload/probe] gesture on '{g.Selector}' released: renders {g.RendersAtPress - g.RendersAtStart} while hovering, "
                          + $"{_renders - g.RendersAtPress} while held; mouseenter {g.EntersAtPress - g.EntersAtStart} while hovering, "
                          + $"{_enterDispatches - g.EntersAtPress} while held; pressed object {(ReferenceEquals(g.PressedOn, null) ? "none" : pressed == null ? "destroyed" : "alive")} "
                          + $"at release; uGUI up {(g.UpSent ? "sent" : "lost")}, uGUI click {(g.ClickSent ? "sent" : "lost")}.");
        }

        private void Finish(Gesture g)
        {
            for (int i = g.Hovered.Count - 1; i >= 0; i--)
                if (g.Hovered[i] != null) ExecuteEvents.Execute(g.Hovered[i], g.Data, ExecuteEvents.pointerExitHandler);
            g.Hovered.Clear();
            _interaction.EndSynthetic();
            Core.Log?.Msg($"[Sideload/probe] gesture on '{g.Selector}' done.");
            _gesture = null;
        }
    }
}
#endif
