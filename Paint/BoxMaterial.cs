using System.Reflection;
using UnityEngine;

namespace Sideload.Paint
{
    /// <summary>
    /// Owns the one material every box shares. Loading is attempted once; if the bundle is missing or the shader
    /// cannot be used on this machine, <see cref="Available"/> stays false and the painter draws flat fills instead -
    /// a Sideload app then looks plain but still works, which is the same fail-soft contract the rest of the engine
    /// follows.
    /// </summary>
    internal static class BoxMaterial
    {
        private const string ResourceName = "Sideload.Assets.Bundles.sideload-ui";

        private static bool _tried;
        private static Material _material;

        /// <summary>True once the SDF shader is loaded and usable.</summary>
        internal static bool Available => Get() != null;

        /// <summary>The shared material, or null when the shader is unavailable.</summary>
        internal static Material Get()
        {
            if (_tried) return _material;
            _tried = true;

            try
            {
                byte[] bytes = ReadEmbedded(ResourceName);
                if (bytes == null)
                {
                    Core.Log?.Warning("UI shader bundle not embedded - boxes will be flat fills.");
                    return null;
                }

                // NOT UnityEngine.AssetBundle.LoadFromMemory: that method is stripped from this IL2CPP build and
                // throws "Method unstripping failed". MelonLoader's manager goes straight to the native icall.
                Il2CppAssetBundle bundle = Il2CppAssetBundleManager.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    Core.Log?.Warning("UI shader bundle failed to load - boxes will be flat fills.");
                    return null;
                }

                Shader shader = bundle.LoadAsset<Shader>("Assets/SideloadBox.shader");
                if (shader == null) shader = bundle.LoadAsset<Shader>("SideloadBox");
                if (shader == null) shader = bundle.LoadAsset<Shader>("Sideload/Box");
                if (shader == null)
                {
                    var all = bundle.LoadAllAssets<Shader>();
                    if (all != null && all.Length > 0) shader = all[0];
                }

                if (shader == null)
                {
                    Core.Log?.Warning("UI shader not found in bundle - boxes will be flat fills.");
                    return null;
                }

                if (!shader.isSupported)
                {
                    Core.Log?.Warning($"UI shader '{shader.name}' is not supported here - boxes will be flat fills.");
                    return null;
                }

                _material = new Material(shader) { name = "SideloadBox" };
                Core.Log?.Msg($"UI shader loaded: {shader.name}");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("UI shader load failed: " + e.Message);
                _material = null;
            }

            return _material;
        }

        /// <summary>
        /// A private copy of the box material that clips to a rectangle. Needed because RectMask2D only clips
        /// <see cref="UnityEngine.UI.Graphic"/> components, and Sideload's boxes are raw CanvasRenderer meshes - they
        /// are simply not on the mask's list. TextMeshPro inside the same area IS a Graphic and gets clipped for free.
        ///
        /// Costs the batching inside a scrolled area, which is the price of clipping without a Graphic.
        /// </summary>
        internal static Material CreateClipped(Vector4 clipRect)
        {
            Material shared = Get();
            if (shared == null) return null;

            var clone = new Material(shared) { name = "SideloadBox (clipped)" };

            // Harmless if the shader clips unconditionally, decisive if the bundle still carries the keyword variant.
            clone.EnableKeyword("UNITY_UI_CLIP_RECT");

            // Set BEFORE the material is handed to any CanvasRenderer: if uGUI takes its own copy on assignment, a
            // later SetVector would land on an object nobody renders with.
            clone.SetVector("_ClipRect", clipRect);

            Core.Log?.Msg($"[Sideload/clip] material '{clone.name}' shader='{clone.shader?.name}' " +
                          $"supported={clone.shader?.isSupported} rect={clipRect}");
            return clone;
        }


        private static byte[] ReadEmbedded(string name)
        {
            try
            {
                using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) return null;

                var buf = new byte[s.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = s.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return buf;
            }
            catch { return null; }
        }
    }
}
