using UnityEngine;

namespace Sideload.Phone
{
    /// <summary>
    /// Bring a page up over a fifth of a second instead of snapping it on.
    ///
    /// Only ever used for the FIRST open of an app, which is the one that has to build the page. Even warmed, that
    /// build costs somewhere around 150 ms of frozen frames, and at the end of it the finished page appears all at
    /// once - the jump is what reads as a hitch, more than the wait itself. A page that arrives fading looks like
    /// it was loading; the same page appearing instantly after a stall looks like the game stuttered.
    ///
    /// <para>Later opens are not faded. There is nothing to cover: the page is already built and the phone's own
    /// animation carries it. Adding a fade there would be 200 ms of delay bought for nothing.</para>
    /// </summary>
    internal static class AppFade
    {
        private const float Seconds = 0.2f;

        private static readonly List<CanvasGroup> _groups = new();
        private static readonly List<float> _elapsed = new();

        /// <summary>Hide the container before the page is built, so the first frame of it is never seen at full
        /// strength. Returns the group to hand to <see cref="Play"/> once the build is done.</summary>
        internal static CanvasGroup Hide(GameObject container)
        {
            if (container == null) return null;

            try
            {
                CanvasGroup group = container.GetComponent<CanvasGroup>() ?? container.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                return group;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] could not prepare the fade: " + e.Message);
                return null;
            }
        }

        /// <summary>Start raising it. Safe with null, and safe to call twice - the second call restarts the fade.</summary>
        internal static void Play(CanvasGroup group)
        {
            if (group == null) return;

            int at = _groups.IndexOf(group);
            if (at >= 0) { _elapsed[at] = 0f; return; }

            _groups.Add(group);
            _elapsed.Add(0f);
        }

        /// <summary>One frame of every running fade. Driven from the mod's update, like the rest of the phone.</summary>
        internal static void Tick(float deltaSeconds)
        {
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                CanvasGroup group = _groups[i];
                if (group == null) { _groups.RemoveAt(i); _elapsed.RemoveAt(i); continue; }

                float t = _elapsed[i] + deltaSeconds;
                _elapsed[i] = t;

                float p = Mathf.Clamp01(t / Seconds);

                // Eased rather than linear: a linear alpha ramp reads as a light being turned up, which is not what
                // an app appearing should look like.
                group.alpha = p * p * (3f - 2f * p);

                if (p < 1f) continue;

                group.alpha = 1f;
                _groups.RemoveAt(i);
                _elapsed.RemoveAt(i);
            }
        }

        /// <summary>Nothing half-faded survives a scene change; the containers are gone with it.</summary>
        internal static void Clear()
        {
            _groups.Clear();
            _elapsed.Clear();
        }
    }
}
