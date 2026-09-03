using Sideload.Css;

namespace Sideload.Paint
{
    /// <summary>
    /// Runs `transition`. A state change - hover, press, a class the script toggled - no longer snaps: the box is
    /// repainted every frame somewhere between the style it had and the style it is going to.
    ///
    /// Only one tween exists per box at a time. Starting a second one while the first is mid-flight is the common
    /// case (a pointer that leaves a button before the hover finished arriving), and it must not jump: the new tween
    /// starts from where the box IS, not from where the old one started, which is why the current interpolated style
    /// is captured rather than the old target.
    ///
    /// ONE registry serves every mounted view, so a tween has to say which view it belongs to. Two pages can be up
    /// at once - the phone and a world panel, a surface and an overlay - and three things went wrong without an
    /// owner on the tween: the paint settings came from whichever view rendered last (see
    /// <see cref="PaintSettings"/>), a rebuild of one page cancelled the other page's animations, and every view
    /// driving the same global <see cref="Tick"/> advanced every tween once per view per frame, so a fade ran at
    /// double speed with two pages open. The owner is what separates them.
    /// </summary>
    internal static class Transitions
    {
        private sealed class Tween
        {
            internal Painter.PaintedBox Box;

            /// <summary>The view this tween's box belongs to. Reference identity only - never dereferenced here.</summary>
            internal object Owner;

            /// <summary>What the painter has to have in force while this box is drawn.</summary>
            internal PaintSettings Settings;

            internal ComputedStyle From;
            internal ComputedStyle To;
            internal float Elapsed;
            internal float Duration;
            internal float Delay;
            internal EasingKind Easing;
        }

        private static readonly Dictionary<int, Tween> _active = new();

        /// <summary>Nothing to run means nothing to do - the common case on a page that declares no transitions.</summary>
        internal static bool Idle => _active.Count == 0;

        /// <summary>
        /// Move a box to a new style. With no transition declared this is an ordinary immediate repaint, so a page
        /// that says nothing about transitions behaves exactly as it did before they existed.
        /// </summary>
        internal static void To(Painter.PaintedBox box, ComputedStyle from, ComputedStyle to,
                                object owner, PaintSettings settings)
        {
            if (box.Rect == null || to == null) return;

            if (to.TransitionSeconds <= 0f || from == null)
            {
                Cancel(box);

                // This repaint runs from an interaction, not from a render, so nothing has told the painter which
                // view it is drawing. Same reason Tick has to say it.
                settings.Apply();
                Painter.Repaint(box, to);
                return;
            }

            int key = box.Rect.GetInstanceID();

            // Interrupting: continue from the frame that is on screen, not from where the previous tween began.
            ComputedStyle start = from;
            if (_active.TryGetValue(key, out Tween running))
                start = Blend(running.From, running.To, Progress(running));

            _active[key] = new Tween
            {
                Box = box,
                Owner = owner,
                Settings = settings,
                From = start,
                To = to,
                Duration = to.TransitionSeconds,
                Delay = to.TransitionDelaySeconds,
                Easing = to.TransitionEasing,
            };
        }

        internal static void Cancel(Painter.PaintedBox box)
        {
            if (box.Rect != null) _active.Remove(box.Rect.GetInstanceID());
        }

        /// <summary>
        /// A rebuild replaces every GameObject OF ONE VIEW, so only that view's tweens are aimed at boxes that no
        /// longer exist. Clearing the whole registry cancelled the other mounted page's animations mid-flight -
        /// they stopped wherever they had got to, because nothing repaints them again.
        /// </summary>
        internal static void Clear(object owner)
        {
            if (_active.Count == 0) return;

            List<int> mine = null;
            foreach (KeyValuePair<int, Tween> pair in _active)
                if (ReferenceEquals(pair.Value.Owner, owner)) (mine ??= new List<int>()).Add(pair.Key);

            if (mine == null) return;
            foreach (int key in mine) _active.Remove(key);
        }

        /// <summary>
        /// One pass over every mounted view's tweens. Driven ONCE per frame - see
        /// <see cref="Host.WebView.TickAll"/> - because this registry is global: called from each view's own tick
        /// it advanced and repainted every tween once per mounted view, so a fade ran at double speed with a second
        /// page open and each box was drawn twice.
        /// </summary>
        internal static void Tick(float deltaSeconds)
        {
            if (_active.Count == 0) return;

            List<int> finished = null;

            foreach (KeyValuePair<int, Tween> pair in _active)
            {
                Tween tween = pair.Value;
                tween.Elapsed += deltaSeconds;

                if (tween.Box.Rect == null)
                {
                    (finished ??= new List<int>()).Add(pair.Key);
                    continue;
                }

                float t = Progress(tween);

                // The paint settings of the view this box belongs to, not of whichever view rendered last.
                tween.Settings.Apply();
                Painter.RepaintBetween(tween.Box, tween.From, tween.To, Ease(t, tween.Easing));

                if (t >= 1f) (finished ??= new List<int>()).Add(pair.Key);
            }

            if (finished == null) return;
            foreach (int key in finished) _active.Remove(key);
        }

        private static float Progress(Tween tween)
        {
            float t = (tween.Elapsed - tween.Delay) / Math.Max(tween.Duration, 0.0001f);
            return Math.Clamp(t, 0f, 1f);
        }

        private static float Ease(float t, EasingKind kind) => kind switch
        {
            EasingKind.Linear => t,
            EasingKind.EaseIn => t * t,
            EasingKind.EaseInOut => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
            _ => 1f - (1f - t) * (1f - t),     // ease-out: fast to start, settles gently
        };

        /// <summary>
        /// The style a box is showing right now, as a real ComputedStyle - so an interrupted tween can be handed a
        /// starting point that is a style like any other. Only the animatable values are blended; everything else is
        /// copied from the target, because nothing else was ever going to move.
        /// </summary>
        private static ComputedStyle Blend(ComputedStyle from, ComputedStyle to, float t)
        {
            ComputedStyle blended = to.Clone();

            blended.BackgroundColor = Lerp(from.BackgroundColor, to.BackgroundColor, t);
            blended.BorderColor = Lerp(from.BorderColor, to.BorderColor, t);
            blended.Color = Lerp(from.Color, to.Color, t);
            blended.ShadowColor = Lerp(from.ShadowColor, to.ShadowColor, t);
            blended.Opacity = from.Opacity + (to.Opacity - from.Opacity) * t;

            blended.TranslateX = from.TranslateX + (to.TranslateX - from.TranslateX) * t;
            blended.TranslateY = from.TranslateY + (to.TranslateY - from.TranslateY) * t;
            blended.ScaleX = from.ScaleX + (to.ScaleX - from.ScaleX) * t;
            blended.ScaleY = from.ScaleY + (to.ScaleY - from.ScaleY) * t;
            blended.RotateDeg = from.RotateDeg + (to.RotateDeg - from.RotateDeg) * t;

            return blended;
        }

        private static RgbaColor Lerp(RgbaColor a, RgbaColor b, float t) => new RgbaColor(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }
}
