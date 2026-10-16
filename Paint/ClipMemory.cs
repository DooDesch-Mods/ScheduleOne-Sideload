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
    ///
    /// <para>Inside a scroll area a clip has two parts that move differently. The scroll area's own rectangle stays
    /// where the viewport is; an <c>overflow: hidden</c> box inside the list - a round mugshot, a two-line preview -
    /// travels with its row. So such a clip is kept as the moving part alone plus the <see cref="ScrollFrame"/> it
    /// belongs to, and is shifted by however far the list has scrolled before the scroll rectangle cuts it. Kept as
    /// one finished rectangle, the row's clip stayed where the row was painted, and its pictures and text were cut
    /// away as soon as the row scrolled out from under it.</para>
    /// </summary>
    internal static class ClipMemory
    {
        private static readonly Dictionary<int, Entry> _clips = new();
        private static int _sweptAt;

        /// <summary>
        /// The scroll area the clips being set right now belong to, or null outside every scroll area. The painter
        /// sets it for the stretch of its walk inside a list, and for a repaint of a box that was painted there.
        /// </summary>
        internal static ScrollFrame Frame;

        private struct Entry
        {
            internal CanvasRenderer Renderer;

            /// <summary>The clip itself outside a scroll area; inside one, only the part that moves with the
            /// list, null when nothing inside the list narrows it.</summary>
            internal Rect? Clip;

            internal ScrollFrame Frame;
        }

        /// <summary>
        /// Apply a clip and remember it. Under a <see cref="Frame"/> the clip is the moving part only, measured with
        /// the list unscrolled, and the scroll area's rectangle is added here.
        /// </summary>
        internal static void Set(CanvasRenderer renderer, Rect? clip)
        {
            if (renderer == null) return;

            var entry = new Entry { Renderer = renderer, Clip = clip, Frame = Frame };
            Apply(renderer, Resolve(entry, Frame != null ? Frame.Offset() : Vector2.zero));
            _clips[renderer.GetInstanceID()] = entry;
        }

        /// <summary>
        /// Give every renderer under <paramref name="root"/> the clip it last had, moved to where its list now
        /// stands. A renderer with no remembered clip gets <paramref name="fallback"/> when
        /// <paramref name="useFallback"/> is set - a caret TMP_InputField made at runtime, say - and is left alone
        /// otherwise. Returns how many were reapplied.
        /// </summary>
        internal static int Reassert(Transform root, Rect? fallback, bool useFallback)
        {
            if (root == null) return 0;

            // One list moves all of its rows by the same amount, so the offset is measured once per list.
            ScrollFrame lastFrame = null;
            Vector2 lastOffset = Vector2.zero;

            int applied = 0;
            var renderers = root.GetComponentsInChildren<CanvasRenderer>(true);
            for (int i = 0; renderers != null && i < renderers.Length; i++)
            {
                CanvasRenderer cr = renderers[i];
                if (cr == null) continue;

                Rect? clip;
                if (_clips.TryGetValue(cr.GetInstanceID(), out Entry entry))
                {
                    if (entry.Frame != null && entry.Frame != lastFrame)
                    {
                        lastFrame = entry.Frame;
                        lastOffset = entry.Frame.Offset();
                    }

                    clip = Resolve(entry, lastOffset);
                }
                else if (useFallback) clip = fallback;
                else continue;

                Apply(cr, clip);
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

        private static Rect? Resolve(Entry entry, Vector2 offset)
        {
            if (entry.Frame == null) return entry.Clip;
            if (!entry.Clip.HasValue) return entry.Frame.Clip;

            Rect moved = entry.Clip.Value;
            moved.position += offset;
            return Narrow(moved, entry.Frame.Clip);
        }

        private static void Apply(CanvasRenderer renderer, Rect? clip)
        {
            if (clip.HasValue) renderer.EnableRectClipping(clip.Value);
            else renderer.DisableRectClipping();
        }

        /// <summary>The part of an inner clip that survives an outer one. Null outer means nothing narrows it.</summary>
        internal static Rect? Narrow(Rect? inner, Rect? outer)
        {
            if (!inner.HasValue) return outer;
            if (!outer.HasValue) return inner;

            Rect a = inner.Value, b = outer.Value;
            float xMin = Math.Max(a.xMin, b.xMin), xMax = Math.Min(a.xMax, b.xMax);
            float yMin = Math.Max(a.yMin, b.yMin), yMax = Math.Min(a.yMax, b.yMax);
            return new Rect(xMin, yMin, Math.Max(0f, xMax - xMin), Math.Max(0f, yMax - yMin));
        }
    }

    /// <summary>
    /// One scroll area, as the clips inside it need it: the rectangle that stays put, and the content that moves.
    /// </summary>
    internal sealed class ScrollFrame
    {
        /// <summary>The scroll area's own clip, in canvas space. Does not move when the list does.</summary>
        internal Rect? Clip;

        /// <summary>The node the ScrollRect moves.</summary>
        internal RectTransform Content;

        /// <summary>Where <see cref="Content"/> stood when the page was laid out - the position every clip inside
        /// was measured at, since the layout knows nothing of scrolling.</summary>
        internal Vector2 Home;

        /// <summary>The canvas whose space the clips are in.</summary>
        internal Transform Space;

        /// <summary>How far the list has moved since it was laid out, in the clips' canvas space.</summary>
        internal Vector2 Offset()
        {
            if (Content == null || Space == null) return Vector2.zero;

            Vector2 local = Content.anchoredPosition - Home;
            if (local == Vector2.zero) return Vector2.zero;

            Transform parent = Content.parent;
            Vector3 world = parent != null ? parent.TransformVector(local) : (Vector3)local;
            Vector3 canvas = Space.InverseTransformVector(world);
            return new Vector2(canvas.x, canvas.y);
        }
    }
}
