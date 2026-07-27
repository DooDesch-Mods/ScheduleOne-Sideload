using System;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace Sideload.Devtools
{
    /// <summary>
    /// Decides one architectural question: can a managed class injected via ClassInjector derive from an IL2CPP
    /// <see cref="MaskableGraphic"/> and have Unity call its overridden <c>OnPopulateMesh</c>?
    ///
    /// RESULT, measured in game on 2026-07-26:
    ///
    ///   * Injection and the virtual override WORK. "OnPopulateMesh called = True" in the log, and the quad renders.
    ///     The earlier assumption that Il2CppInterop cannot override IL2CPP virtuals was wrong.
    ///   * The quad was NOT clipped by the RectMask2D it sat under, even though it is a real MaskableGraphic. So the
    ///     hoped-for payoff - free masking - did not materialise; most likely MaskableGraphic's own OnEnable, which
    ///     registers the clippable, is not dispatched to an injected type.
    ///
    /// Conclusion: deriving from Graphic is viable for RENDERING, but it is not a shortcut to masking.
    /// CanvasRenderer.EnableRectClipping is proven and stays.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal sealed class ProbeGraphic : MaskableGraphic
    {
        public ProbeGraphic(IntPtr ptr) : base(ptr) { }

        internal static bool PopulateWasCalled;

        // public, not protected: Il2CppInterop generates the interop wrapper's OnPopulateMesh as public.
        public override void OnPopulateMesh(VertexHelper vh)
        {
            PopulateWasCalled = true;

            vh.Clear();
            Rect r = rectTransform.rect;
            Color32 tint = color;

            vh.AddVert(new Vector3(r.xMin, r.yMin), tint, new Vector2(0f, 0f));
            vh.AddVert(new Vector3(r.xMin, r.yMax), tint, new Vector2(0f, 1f));
            vh.AddVert(new Vector3(r.xMax, r.yMax), tint, new Vector2(1f, 1f));
            vh.AddVert(new Vector3(r.xMax, r.yMin), tint, new Vector2(1f, 0f));

            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }
    }

    internal static class GraphicProbe
    {
        private static bool _registered;

        /// <summary>Drop an oversized magenta quad into <paramref name="parent"/>. It must be visible and clipped.</summary>
        internal static void Spawn(Transform parent)
        {
            try
            {
                if (!_registered)
                {
                    ClassInjector.RegisterTypeInIl2Cpp<ProbeGraphic>();
                    _registered = true;
                    Core.Log?.Msg("[Sideload/probe] ProbeGraphic registered in Il2Cpp.");
                }

                RectTransform rt = Paint.UiFactory.Rect("graphic-probe", parent);
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(300f, 400f);            // taller than the 96px viewport on purpose
                rt.anchoredPosition = new Vector2(20f, -20f);

                var graphic = rt.gameObject.AddComponent<ProbeGraphic>();
                graphic.color = new Color(1f, 0f, 1f, 0.75f);
                graphic.raycastTarget = false;

                Core.Log?.Msg($"[Sideload/probe] ProbeGraphic added. OnPopulateMesh called so far: {ProbeGraphic.PopulateWasCalled}");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[Sideload/probe] ProbeGraphic FAILED: " + e);
            }
        }

        /// <summary>Report after a frame has rendered - OnPopulateMesh runs on the canvas rebuild, not on AddComponent.</summary>
        internal static void Report()
        {
            Core.Log?.Msg($"[Sideload/probe] RESULT OnPopulateMesh called = {ProbeGraphic.PopulateWasCalled}");
        }
    }
}
