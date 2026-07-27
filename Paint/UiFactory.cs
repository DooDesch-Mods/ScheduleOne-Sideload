using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Sideload.Paint
{
    /// <summary>
    /// The few raw uGUI primitives the painter needs. Deliberately tiny and dependency-free: Sideload owns its whole
    /// render path, so it does not build on S1API's UIFactory or DooDesch.UI.
    /// </summary>
    internal static class UiFactory
    {
        /// <summary>A parented, unscaled, unrotated RectTransform - the base of every painted node.</summary>
        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name);
            if (parent != null) go.layer = parent.gameObject.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.localPosition = Vector3.zero;
            return rt;
        }

        /// <summary>Stretch to the parent's full rect, inset by the given edge distances (CSS order: top right bottom left).</summary>
        internal static void Stretch(RectTransform rt, float top = 0f, float right = 0f, float bottom = 0f, float left = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// Place a node by its computed layout rect: x/y measured from the parent's TOP-LEFT and growing down, which is
        /// how CSS thinks. uGUI anchors at the bottom-left and grows up, so y is negated here - this is the single
        /// place where the two coordinate systems meet.
        /// </summary>
        internal static void PlaceFromTopLeft(RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        /// <summary>A flat colour fill on an existing node.</summary>
        internal static Image Fill(RectTransform rt, Color color, bool raycastTarget = false)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycastTarget;
            return img;
        }

        /// <summary>Destroy every child of a transform (used to empty a cloned vanilla panel before mounting).</summary>
        internal static void ClearChildren(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--)
                Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}
