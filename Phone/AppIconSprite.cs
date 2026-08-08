using UnityEngine;

namespace Sideload.Phone
{
    /// <summary>
    /// The picture on an app's home-screen icon. An app supplies its own by putting <c>icon.png</c> in its bundle
    /// (embedded, or dropped into the override folder); an app that does not gets a flat rounded square in a colour
    /// derived from its id, which is at least unmistakably not a vanilla app.
    /// </summary>
    internal static class AppIconSprite
    {
        private const int Fallback = 128;

        /// <summary>Loads and caches the icon for one app. Never returns null.</summary>
        internal static Sprite For(AppRegistration reg)
        {
            if (reg.IconSprite != null) return reg.IconSprite;

            reg.IconSprite = FromBundle(reg) ?? Generated(reg.Id);
            return reg.IconSprite;
        }

        private static Sprite FromBundle(AppRegistration reg)
        {
            byte[] png = reg.Bundle?.ReadBytes("icon.png");
            if (png == null || png.Length == 0) return null;

            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                if (!tex.LoadImage(png))
                {
                    Core.Log?.Warning($"'{reg.Id}' has an icon.png that is not a readable PNG.");
                    return null;
                }
                return Finish(tex);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"loading icon.png for '{reg.Id}' failed: {e.Message}");
                return null;
            }
        }

        /// <summary>A rounded square, hue taken from the id so two apps rarely collide and one app never shifts.</summary>
        private static Sprite Generated(string id)
        {
            Color32 fill = FromHue(Hash(id) % 360 / 360f);
            var tex = new Texture2D(Fallback, Fallback, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var clear = new Color32(0, 0, 0, 0);

            const float radius = 26f;
            for (int y = 0; y < Fallback; y++)
                for (int x = 0; x < Fallback; x++)
                    tex.SetPixel(x, y, Inside(x + 0.5f, y + 0.5f, radius) ? fill : clear);

            tex.Apply();
            return Finish(tex);
        }

        private static bool Inside(float x, float y, float r)
        {
            float cx = Mathf.Clamp(x, r, Fallback - r);
            float cy = Mathf.Clamp(y, r, Fallback - r);
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        private static Sprite Finish(Texture2D tex)
        {
            // The phone is rebuilt on every scene load; without this the texture and sprite are collected as unused
            // assets between loads and the icon turns into a white square.
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            if (sprite != null) sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        private static int Hash(string s)
        {
            int h = 17;
            foreach (char c in s ?? "") h = (h * 31 + c) & 0x7FFFFFF;
            return h;
        }

        /// <summary>Fixed saturation and value, so every generated icon reads as the same family.</summary>
        private static Color32 FromHue(float h) => Color.HSVToRGB(h, 0.62f, 0.86f);
    }
}
