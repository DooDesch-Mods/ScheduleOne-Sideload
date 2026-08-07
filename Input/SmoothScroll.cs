using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sideload.Input
{
    /// <summary>
    /// Eases a wheel notch instead of jumping it.
    ///
    /// uGUI's ScrollRect has inertia, but only for dragging: a wheel notch moves the content by
    /// `scrollSensitivity` in one frame and stops. On a long list - a settings pane, a roster - that reads as the
    /// content teleporting, with nothing to follow between the line you were reading and where you end up.
    ///
    /// So a notch sets a TARGET here and the position walks toward it, exponentially and independently of frame
    /// rate: the same fraction of the remaining distance per second, so a stutter does not become a jump and a
    /// fast machine does not scroll further than a slow one.
    ///
    /// TWO PLACES HAVE TO BE TAKEN OVER, and missing the second one is why the first attempt did nothing. A wired
    /// element - a button, a settings row - swallows the wheel and <see cref="Interaction"/> forwards it here by
    /// hand. But over EMPTY space in the list, uGUI's own event system finds the ScrollRect by bubbling and calls
    /// its OnScroll directly, and nothing routed through this at all. That path is closed by setting the
    /// ScrollRect's own sensitivity to zero - so its handler does nothing - and putting an EventTrigger on the
    /// viewport that comes here instead. The sensitivity it would have used is kept below.
    ///
    /// No MonoBehaviour is injected: Sideload already gets a per-frame call in <see cref="Host.WebView.TickAll"/>,
    /// and a registered Unity type is a cost this does not need for a lerp.
    ///
    /// Opt out per box with `-s1-scroll: instant`, which is what a map or anything that follows the pointer wants -
    /// there, easing is lag.
    /// </summary>
    internal static class SmoothScroll
    {
        /// <summary>
        /// How fast the position closes on its target, per second, as a fraction of the distance left. 18 lands a
        /// notch in about a sixth of a second: quick enough not to feel like waiting, slow enough that the eye can
        /// follow the content instead of re-finding it.
        /// </summary>
        private const float Rate = 18f;

        /// <summary>Below this the walk is over, in normalised units - a fraction of one screenful.</summary>
        private const float Settled = 0.0005f;

        private sealed class Ride
        {
            internal ScrollRect Scroll;
            internal float Target;
        }

        private static readonly List<Ride> _rides = new List<Ride>();

        /// <summary>Scroll areas that are smoothed, and the wheel sensitivity taken off the ScrollRect so its own
        /// handler would do nothing. A ScrollRect absent from here is one that asked for instant.</summary>
        private static readonly Dictionary<ScrollRect, float> _smoothed = new Dictionary<ScrollRect, float>();

        /// <summary>
        /// Take the wheel over for this scroll area: zero the ScrollRect's own sensitivity so uGUI stops acting on
        /// a notch, and catch the event on the viewport, which is where it lands over empty space.
        /// </summary>
        internal static void Install(ScrollRect scroll, RectTransform viewport, float sensitivity)
        {
            if (scroll == null || viewport == null) return;

            // A page rebuild throws its ScrollRects away and paints new ones, so this is the moment the dead ones
            // can be let go of - otherwise the registry grows for the whole session, one entry per reload.
            Prune();

            _smoothed[scroll] = sensitivity;
            scroll.scrollSensitivity = 0f;

            var trigger = viewport.gameObject.GetComponent<EventTrigger>();
            if (trigger == null) trigger = viewport.gameObject.AddComponent<EventTrigger>();

            var entry = new EventTrigger.Entry { eventID = EventTriggerType.Scroll };
            entry.callback = new EventTrigger.TriggerEvent();
            entry.callback.AddListener((UnityEngine.Events.UnityAction<BaseEventData>)(data =>
            {
                try
                {
                    var pointer = data.TryCast<PointerEventData>();
                    if (pointer != null) Wheel(scroll, pointer);
                }
                catch (Exception e) { Core.Log?.Warning("[Sideload] smooth scroll failed: " + e.Message); }
            }));

            trigger.triggers.Add(entry);
        }

        /// <summary>
        /// A wheel notch. Returns false when this area is not smoothed, so the caller can hand the event to uGUI
        /// exactly as before.
        /// </summary>
        internal static bool Wheel(ScrollRect scroll, PointerEventData pointer)
        {
            if (scroll == null || pointer == null) return false;
            if (!_smoothed.TryGetValue(scroll, out float sensitivity)) return false;
            if (!scroll.vertical || scroll.content == null || scroll.viewport == null) return false;

            float extent = scroll.content.rect.height - scroll.viewport.rect.height;
            if (extent <= 1f) return true;          // nothing to scroll, and uGUI must not get it either

            Ride ride = Find(scroll);
            if (ride == null)
            {
                ride = new Ride { Scroll = scroll, Target = scroll.verticalNormalizedPosition };
                _rides.Add(ride);
            }

            // Wheel up is a positive scrollDelta and means "toward the top", which is 1 in normalised terms.
            ride.Target = Mathf.Clamp01(ride.Target + pointer.scrollDelta.y * sensitivity / extent);
            return true;
        }

        /// <summary>
        /// The player took hold of the content, so whatever the wheel was aiming at is no longer what they want.
        /// Without this a drag fights a walk still in flight and the list snaps back when they let go.
        /// </summary>
        internal static void Release(ScrollRect scroll)
        {
            for (int i = _rides.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_rides[i].Scroll, scroll)) _rides.RemoveAt(i);
        }

        /// <summary>Walk every live ride one frame closer. Called from the mod's update loop.</summary>
        internal static void Advance(float deltaSeconds)
        {
            if (_rides.Count == 0) return;

            float step = 1f - Mathf.Exp(-Rate * Mathf.Max(deltaSeconds, 0f));

            for (int i = _rides.Count - 1; i >= 0; i--)
            {
                Ride ride = _rides[i];
                ScrollRect scroll = ride.Scroll;

                if (scroll == null || scroll.content == null)
                {
                    _rides.RemoveAt(i);
                    continue;
                }

                float now = scroll.verticalNormalizedPosition;
                float next = Mathf.Lerp(now, ride.Target, step);

                if (Mathf.Abs(ride.Target - next) < Settled)
                {
                    scroll.verticalNormalizedPosition = ride.Target;
                    _rides.RemoveAt(i);
                    continue;
                }

                scroll.verticalNormalizedPosition = next;
            }
        }

        /// <summary>Drop everything. A page rebuild throws its ScrollRects away, and entries pointing at destroyed
        /// ones would keep these collections growing for the session.</summary>
        internal static void Clear()
        {
            _rides.Clear();
            _smoothed.Clear();
        }

        /// <summary>Forget scroll areas whose GameObject is gone. Unity's destroyed objects compare equal to null
        /// without being null references, so this cannot be left to the garbage collector.</summary>
        private static void Prune()
        {
            if (_smoothed.Count == 0) return;

            List<ScrollRect> dead = null;
            foreach (KeyValuePair<ScrollRect, float> entry in _smoothed)
                if (entry.Key == null) (dead ??= new List<ScrollRect>()).Add(entry.Key);

            if (dead == null) return;
            foreach (ScrollRect gone in dead) _smoothed.Remove(gone);

            for (int i = _rides.Count - 1; i >= 0; i--)
                if (_rides[i].Scroll == null) _rides.RemoveAt(i);
        }

        private static Ride Find(ScrollRect scroll)
        {
            foreach (Ride ride in _rides)
                if (ReferenceEquals(ride.Scroll, scroll)) return ride;

            return null;
        }
    }
}
