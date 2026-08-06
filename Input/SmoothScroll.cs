using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sideload.Input
{
    /// <summary>
    /// Eases a wheel notch instead of jumping it.
    ///
    /// uGUI's own ScrollRect has inertia, but only for dragging: a wheel notch moves the content by
    /// `scrollSensitivity` in one frame and stops. On a long list - a settings pane, a roster - that reads as the
    /// content teleporting, and there is nothing to follow with your eye between the position you were reading and
    /// the one you end up at.
    ///
    /// So a notch sets a TARGET here and the position walks toward it. The walk is exponential and framerate
    /// independent, which matters because this runs off the mod's update loop rather than a fixed tick.
    ///
    /// No MonoBehaviour is injected for this. Sideload already gets a per-frame call
    /// (<see cref="Host.WebView.TickAll"/>), and a registered Unity type is a cost and a risk this does not need.
    ///
    /// Opt out per box with `-s1-scroll: instant`, which is what a map or anything that follows the pointer wants -
    /// there, smoothing is lag.
    /// </summary>
    internal static class SmoothScroll
    {
        /// <summary>
        /// How fast the position closes on its target, per second, as the fraction of the remaining distance.
        /// 18 lands a notch in about a sixth of a second: fast enough not to feel like waiting, slow enough that
        /// the eye can follow the content rather than re-finding it.
        /// </summary>
        private const float Rate = 18f;

        /// <summary>Below this the walk is over. In normalised units, so it is a fraction of one screenful.</summary>
        private const float Settled = 0.0005f;

        private sealed class Ride
        {
            internal ScrollRect Scroll;
            internal float Target;
        }

        private static readonly List<Ride> _rides = new List<Ride>();

        /// <summary>Boxes that asked for `-s1-scroll: instant`. Kept as the ScrollRect itself, so it goes away with
        /// the page rather than needing a teardown of its own.</summary>
        private static readonly HashSet<ScrollRect> _instant = new HashSet<ScrollRect>();

        /// <summary>Remember that this scroll area does NOT want smoothing.</summary>
        internal static void MarkInstant(ScrollRect scroll)
        {
            if (scroll != null) _instant.Add(scroll);
        }

        /// <summary>
        /// A wheel notch. Returns false when this area is not smoothed, so the caller can hand the event to uGUI
        /// exactly as before.
        /// </summary>
        internal static bool Wheel(ScrollRect scroll, PointerEventData pointer)
        {
            if (scroll == null || pointer == null) return false;
            if (_instant.Contains(scroll)) return false;
            if (!scroll.vertical || scroll.content == null || scroll.viewport == null) return false;

            float extent = scroll.content.rect.height - scroll.viewport.rect.height;
            if (extent <= 1f) return false;          // nothing to scroll: let uGUI have it and do nothing

            Ride ride = Find(scroll);
            if (ride == null)
            {
                ride = new Ride { Scroll = scroll, Target = scroll.verticalNormalizedPosition };
                _rides.Add(ride);
            }

            // Wheel up is a positive scrollDelta and means "toward the top", which is 1 in normalised terms.
            ride.Target = Mathf.Clamp01(ride.Target + pointer.scrollDelta.y * scroll.scrollSensitivity / extent);
            return true;
        }

        /// <summary>
        /// The player took hold of the content, so whatever the wheel was aiming at is no longer what they want.
        /// Without this a drag fights a walk that is still running and the list snaps back when they let go.
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

            // Exponential approach: the same fraction of the REMAINING distance per second whatever the frame rate,
            // so a stutter does not turn into a jump and a fast machine does not scroll faster than a slow one.
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

        /// <summary>Drop everything. A page rebuild throws its ScrollRects away, and a ride pointing at a destroyed
        /// one would keep this list growing for the session.</summary>
        internal static void Clear()
        {
            _rides.Clear();
            _instant.Clear();
        }

        private static Ride Find(ScrollRect scroll)
        {
            foreach (Ride ride in _rides)
                if (ReferenceEquals(ride.Scroll, scroll)) return ride;

            return null;
        }
    }
}
