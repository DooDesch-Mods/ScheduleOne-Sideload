using UnityEngine;

namespace Sideload.Paint
{
    /// <summary>
    /// The clip rectangle each renderer of a page was last given, so it can be given back.
    ///
    /// <para>uGUI takes it away. A Graphic that is switched off runs <c>MaskableGraphic.OnDisable</c>, which leaves the
    /// RectMask2D it registered with on creation, and leaving calls <c>SetClipRect(zero, false)</c> - a
    /// <c>DisableRectClipping</c> on its renderer. Opting out with <c>maskable = false</c> afterwards does not undo
    /// that registration. So closing an app wiped the clip of every text and picture on its page, and the next open
    /// showed them uncut: a two-line message preview ran on over the rows beneath it. A page built ahead of time and
    /// switched off again hit the same thing before it had ever been seen.</para>
    ///
    /// <para>Keyed by the renderer, not by the element, because the renderer is what holds the clip and what a box
    /// mesh has without any Graphic on it.</para>
    /// </summary>
    internal static class ClipMemory
    {
        private static readonly Dictionary<int, Entry> _clips = new();
        private static int _sweptAt;

        private struct Entry
        {
            internal CanvasRenderer Renderer;
            internal Rect? Clip;
        }

        /// <summary>Apply a clip and remember it.</summary>
        internal static void Set(CanvasRenderer renderer, Rect? clip)
        {
            if (renderer == null) return;

            if (clip.HasValue) renderer.EnableRectClipping(clip.Value);
            else renderer.DisableRectClipping();

            _clips[renderer.GetInstanceID()] = new Entry { Renderer = renderer, Clip = clip };
        }

        /// <summary>
        /// Give every renderer under <paramref name="root"/> the clip it last had. A renderer with no remembered clip
        /// gets <paramref name="fallback"/> when <paramref name="useFallback"/> is set - a caret TMP_InputField made
        /// at runtime, say - and is left alone otherwise. Returns how many were reapplied.
        /// </summary>
        internal static int Reassert(Transform root, Rect? fallback, bool useFallback)
        {
            if (root == null) return 0;

            int applied = 0;
            var renderers = root.GetComponentsInChildren<CanvasRenderer>(true);
            for (int i = 0; renderers != null && i < renderers.Length; i++)
            {
                CanvasRenderer cr = renderers[i];
                if (cr == null) continue;

                Rect? clip;
                if (_clips.TryGetValue(cr.GetInstanceID(), out Entry entry)) clip = entry.Clip;
                else if (useFallback) clip = fallback;
                else continue;

                if (clip.HasValue) cr.EnableRectClipping(clip.Value);
                else cr.DisableRectClipping();
                applied++;
            }

            return applied;
        }

        /// <summary>
        /// Drop the entries of renderers that no longer exist. Every rebuild leaves its old page behind, and an app
        /// that redraws on a timer rebuilds once a second, so this runs once a few thousand entries have piled up
        /// rather than on every paint.
        /// </summary>
        internal static void Sweep()
        {
            if (_clips.Count - _sweptAt < 4000) return;

            var dead = new List<int>();
            foreach (KeyValuePair<int, Entry> pair in _clips)
                if (pair.Value.Renderer == null) dead.Add(pair.Key);

            for (int i = 0; i < dead.Count; i++) _clips.Remove(dead[i]);
            _sweptAt = _clips.Count;
        }
    }
}
