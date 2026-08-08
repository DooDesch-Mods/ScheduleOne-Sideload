using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Sideload.Paint
{
    /// <summary>The visual side of one box, already reduced to numbers the shader understands.</summary>
    internal struct BoxVisual
    {
        /// <summary>Fill colour per corner, CSS order (top-left, top-right, bottom-right, bottom-left). A linear
        /// gradient is expressed by giving the corners different colours - the rasteriser reproduces it exactly.</summary>
        internal Color FillTL, FillTR, FillBR, FillBL;

        internal float RadiusTL, RadiusTR, RadiusBR, RadiusBL;

        /// <summary>Ring drawn by the shader, following the corner radii. Zero when the four sides differ.</summary>
        internal float BorderWidth;

        /// <summary>Per-side widths, used INSTEAD of <see cref="BorderWidth"/> when the sides are not all equal. Each
        /// non-zero side becomes its own solid quad: the shader's ring is a single number and cannot express four.</summary>
        internal float EdgeTop, EdgeRight, EdgeBottom, EdgeLeft;

        internal Color BorderColor;

        internal bool HasShadow;
        internal float ShadowOffsetX, ShadowOffsetY, ShadowBlur;
        internal Color ShadowColor;

        internal static BoxVisual Solid(Color color) => new BoxVisual
        {
            FillTL = color, FillTR = color, FillBR = color, FillBL = color,
            BorderColor = new Color(0f, 0f, 0f, 0f),
            ShadowColor = new Color(0f, 0f, 0f, 0f),
        };
    }

    /// <summary>
    /// Paints boxes straight into a <see cref="CanvasRenderer"/>.
    ///
    /// Deliberately NOT a Graphic subclass: overriding a virtual method of an IL2CPP base class from a managed mod is
    /// not something Il2CppInterop supports reliably - registered types here are plain MonoBehaviours whose messages
    /// Unity looks up by name. A CanvasRenderer with a mesh we build ourselves needs no such override, and since every
    /// box shares one material they still batch into a handful of draw calls.
    /// </summary>
    internal static class BoxRenderer
    {
        private const AdditionalCanvasShaderChannels RequiredChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.TexCoord2 |
            AdditionalCanvasShaderChannels.TexCoord3 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        /// <summary>Cached mesh per GameObject, tagged with the VIEW that painted it. The owner matters: two apps
        /// are mounted at once and each renders on its own schedule, so a pass that swept everything would destroy
        /// the other app's meshes and leave it with no backgrounds the next time it is opened.</summary>
        private static readonly Dictionary<int, (int Owner, Mesh Mesh)> _meshes = new();

        /// <summary>Which cached meshes this render actually used. Everything else belonged to a GameObject that no
        /// longer exists and is destroyed at the end of the pass - without this the cache grows by one Mesh per box
        /// per rebuild, and a page that rebuilds every second leaks steadily for as long as it is open.</summary>
        private static readonly HashSet<int> _touched = new HashSet<int>();

        private static bool _collecting;
        private static int _owner;

        /// <summary>
        /// Clip rectangle for the boxes painted next, in ROOT CANVAS space. Set while painting inside a scroll area.
        ///
        /// This is what actually clips a bare CanvasRenderer: RectMask2D only drives Graphic components, and
        /// _ClipRect as a plain material property never reaches the draw call - CanvasRenderer has no
        /// MaterialPropertyBlock. CanvasRenderer.EnableRectClipping is the supported API for exactly this case, and it
        /// keeps every box on the one shared material.
        /// </summary>
        internal static Rect? ActiveClip;

        /// <summary>
        /// A canvas strips vertex channels it was not told to keep, so the per-box parameters would silently arrive as
        /// zeroes. Call once for the canvas a view lives under.
        /// </summary>
        internal static void EnsureCanvasChannels(Transform anyChild)
        {
            if (anyChild == null) return;

            Canvas canvas = anyChild.GetComponentInParent<Canvas>();
            if (canvas == null) return;

            Canvas root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
            if ((root.additionalShaderChannels & RequiredChannels) == RequiredChannels) return;

            root.additionalShaderChannels |= RequiredChannels;
            Core.Log?.Msg($"[Sideload] enabled extra vertex channels on canvas '{root.name}'.");
        }

        /// <summary>
        /// Draw <paramref name="visual"/> into <paramref name="rt"/>, whose rect is <paramref name="width"/> x
        /// <paramref name="height"/> with a top-left pivot. Falls back to a flat uGUI fill when the shader is absent.
        /// </summary>
        internal static void Paint(RectTransform rt, BoxVisual visual, float width, float height)
        {
            if (rt == null) return;

            Material material = BoxMaterial.Get();
            if (material == null) { PaintFlat(rt, visual); return; }

            EnsureCanvasChannels(rt);

            var cr = rt.GetComponent<CanvasRenderer>();
            if (cr == null) cr = rt.gameObject.AddComponent<CanvasRenderer>();

            int quads = visual.HasShadow ? 2 : 1;
            if (visual.EdgeTop > 0f) quads++;
            if (visual.EdgeBottom > 0f) quads++;
            if (visual.EdgeLeft > 0f) quads++;
            if (visual.EdgeRight > 0f) quads++;
            int vertexCount = quads * 4;

            var vertices = new Vector3[vertexCount];
            var colors = new Color32[vertexCount];
            var uv0 = new Vector2[vertexCount];
            var uv1 = new Vector2[vertexCount];
            var uv2 = new Vector2[vertexCount];
            var uv3 = new Vector2[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var triangles = new int[quads * 6];

            // Where the rect's centre sits in local space depends on the pivot: the rect spans -pivot*size to
            // (1-pivot)*size on each axis. Assuming a top-left pivot would misplace anything anchored differently -
            // a full-bleed background (pivot 0.5) would land a full half-size down and to the right.
            Vector2 pivot = rt.pivot;
            var centre = new Vector2((0.5f - pivot.x) * width, (0.5f - pivot.y) * height);
            var half = new Vector2(width * 0.5f, height * 0.5f);
            var radii = new Vector4(visual.RadiusTL, visual.RadiusTR, visual.RadiusBR, visual.RadiusBL);

            int v = 0, t = 0;

            if (visual.HasShadow)
            {
                // The shape stays the size of the box; only the quad grows, so the blur has room to fade out.
                float pad = visual.ShadowBlur + 2f;
                Vector2 shadowCentre = centre + new Vector2(visual.ShadowOffsetX, -visual.ShadowOffsetY);
                Color shadow = visual.ShadowColor;

                EmitQuad(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                         shadowCentre, half + new Vector2(pad, pad), half, radii,
                         shadow, shadow, shadow, shadow,
                         borderWidth: 0f, blur: Mathf.Max(visual.ShadowBlur, 0.5f),
                         borderColor: new Color(0f, 0f, 0f, 0f));
            }

            EmitQuad(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre, half, half, radii,
                     visual.FillTL, visual.FillTR, visual.FillBR, visual.FillBL,
                     visual.BorderWidth, blur: 0f, borderColor: visual.BorderColor);

            // Single-side borders, drawn over the fill. Adjacent sides overlap in the corner by a fraction of a pixel;
            // at hairline widths that is invisible, and anything thicker wants a uniform border anyway.
            Color edge = visual.BorderColor;
            EmitEdge(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre + new Vector2(0f, half.y - visual.EdgeTop * 0.5f),
                     new Vector2(half.x, visual.EdgeTop * 0.5f), visual.EdgeTop, edge);
            EmitEdge(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre - new Vector2(0f, half.y - visual.EdgeBottom * 0.5f),
                     new Vector2(half.x, visual.EdgeBottom * 0.5f), visual.EdgeBottom, edge);
            EmitEdge(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre - new Vector2(half.x - visual.EdgeLeft * 0.5f, 0f),
                     new Vector2(visual.EdgeLeft * 0.5f, half.y), visual.EdgeLeft, edge);
            EmitEdge(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre + new Vector2(half.x - visual.EdgeRight * 0.5f, 0f),
                     new Vector2(visual.EdgeRight * 0.5f, half.y), visual.EdgeRight, edge);

            Mesh mesh = MeshFor(rt.gameObject);
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors32 = colors;
            mesh.uv = uv0;
            mesh.uv2 = uv1;
            mesh.uv3 = uv2;
            mesh.uv4 = uv3;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.triangles = triangles;

            cr.SetMesh(mesh);
            cr.materialCount = 1;
            cr.SetMaterial(material, 0);

            if (ActiveClip.HasValue) cr.EnableRectClipping(ActiveClip.Value);
            else cr.DisableRectClipping();
        }

        /// <summary>One flat, square-cornered strip in the border colour, or nothing when that side has no width.</summary>
        private static void EmitEdge(Vector3[] vertices, Color32[] colors, Vector2[] uv0, Vector2[] uv1,
                                     Vector2[] uv2, Vector2[] uv3, Vector3[] normals, Vector4[] tangents,
                                     int[] triangles, ref int v, ref int t,
                                     Vector2 centre, Vector2 half, float width, Color color)
        {
            if (width <= 0f || color.a <= 0f) return;

            EmitQuad(vertices, colors, uv0, uv1, uv2, uv3, normals, tangents, triangles, ref v, ref t,
                     centre, half, half, Vector4.zero,
                     color, color, color, color,
                     borderWidth: 0f, blur: 0f, borderColor: new Color(0f, 0f, 0f, 0f));
        }

        /// <summary>
        /// One quad. <paramref name="quadHalf"/> is how far the geometry reaches, <paramref name="shapeHalf"/> is the
        /// size of the rounded rectangle the shader evaluates - they differ only for a shadow.
        /// </summary>
        private static void EmitQuad(Vector3[] vertices, Color32[] colors, Vector2[] uv0, Vector2[] uv1,
                                     Vector2[] uv2, Vector2[] uv3, Vector3[] normals, Vector4[] tangents,
                                     int[] triangles, ref int v, ref int t,
                                     Vector2 centre, Vector2 quadHalf, Vector2 shapeHalf, Vector4 radii,
                                     Color cTL, Color cTR, Color cBR, Color cBL,
                                     float borderWidth, float blur, Color borderColor)
        {
            cTL = ToVertex(cTL); cTR = ToVertex(cTR); cBR = ToVertex(cBR); cBL = ToVertex(cBL);
            borderColor = ToVertex(borderColor);

            var offsets = new[]
            {
                new Vector2(-quadHalf.x,  quadHalf.y),   // top-left
                new Vector2( quadHalf.x,  quadHalf.y),   // top-right
                new Vector2( quadHalf.x, -quadHalf.y),   // bottom-right
                new Vector2(-quadHalf.x, -quadHalf.y),   // bottom-left
            };
            var fills = new[] { cTL, cTR, cBR, cBL };

            int baseIndex = v;
            for (int i = 0; i < 4; i++)
            {
                Vector2 local = offsets[i];
                vertices[v] = new Vector3(centre.x + local.x, centre.y + local.y, 0f);
                colors[v] = fills[i];
                uv0[v] = local;                       // px from the shape centre
                uv1[v] = shapeHalf;
                uv2[v] = new Vector2(radii.x, radii.y);
                uv3[v] = new Vector2(radii.z, radii.w);
                normals[v] = new Vector3(borderWidth, blur, 0f);
                tangents[v] = new Vector4(borderColor.r, borderColor.g, borderColor.b, borderColor.a);
                v++;
            }

            triangles[t++] = baseIndex + 0;
            triangles[t++] = baseIndex + 1;
            triangles[t++] = baseIndex + 2;
            triangles[t++] = baseIndex + 2;
            triangles[t++] = baseIndex + 3;
            triangles[t++] = baseIndex + 0;
        }

        /// <summary>
        /// Colours reach the shader through raw mesh vertices, which bypasses the sRGB-to-linear conversion uGUI's
        /// Graphic does for its own meshes. The game renders in linear space, so an untouched #101218 would be read as
        /// a LINEAR 0.063 and come back out at roughly 0.28 - dark surfaces lift to grey while bright ones barely
        /// move. Converting here restores what the hex value means.
        /// </summary>
        private static Color ToVertex(Color c)
        {
            if (!ConvertToLinear || QualitySettings.activeColorSpace != ColorSpace.Linear) return c;
            Color linear = c.linear;
            linear.a = c.a;   // alpha is never gamma-encoded
            return linear;
        }

        /// <summary>
        /// Whether the conversion above applies to the view being painted right now. Set per render by
        /// <see cref="Host.WebView"/> from the canvas the view hangs under, and true for the phone.
        ///
        /// It is not a global truth, which is what made this so quiet: the phone's screen is drawn by a camera into
        /// a render texture, and that path converts back on the way out, so pre-converting is exactly right there.
        /// A screen-space-OVERLAY canvas is composited straight into the frame with no such step, so the same
        /// pre-conversion is applied and never undone - a measured #808080 arrives as #383838 and every dark
        /// surface collapses into the black behind it, while text (which TextMeshPro converts itself) stays right.
        /// A page that looks correct on the phone and unlit anywhere else is the symptom.
        /// </summary>
        internal static bool ConvertToLinear = true;

        /// <summary>Reuse one mesh per node: repainting on every DOM mutation would otherwise leave a dead Unity mesh
        /// behind each time.</summary>
        private static Mesh MeshFor(GameObject go)
        {
            int id = go.GetInstanceID();
            if (_collecting) _touched.Add(id);

            // The null check is not paranoia: Unity REUSES an instance id once the object it belonged to is gone, so
            // a stale entry can be handed to an unrelated GameObject. Destroying the mesh with the object is what
            // makes that entry answer null here instead of silently sharing one mesh between two boxes.
            if (_meshes.TryGetValue(id, out (int Owner, Mesh Mesh) cached) && cached.Mesh != null)
            {
                if (cached.Owner != _owner) _meshes[id] = (_owner, cached.Mesh);
                return cached.Mesh;
            }

            var mesh = new Mesh { name = "sideload-box" };
            mesh.MarkDynamic();
            _meshes[id] = (_owner, mesh);
            return mesh;
        }

        /// <summary>Shader-less fallback: a flat fill, no radius, no border, no shadow.</summary>
        private static void PaintFlat(RectTransform rt, BoxVisual visual)
        {
            var img = rt.GetComponent<Image>();
            if (img == null) img = rt.gameObject.AddComponent<Image>();
            img.color = visual.FillTL;
            img.raycastTarget = false;
        }

        /// <summary>Start a render pass. Meshes not used before <see cref="EndPass"/> are freed.</summary>
        internal static void BeginPass(int owner)
        {
            _collecting = true;
            _owner = owner;
            _touched.Clear();
        }

        /// <summary>
        /// Finish a render pass and free every mesh the pass did not use. Called once per render rather than per
        /// destroyed object, because the objects are destroyed by the host in one sweep and asking each of them
        /// afterwards would mean holding references to things Unity has already collected.
        /// </summary>
        internal static void EndPass()
        {
            if (!_collecting) return;
            _collecting = false;

            List<int> stale = null;
            foreach (KeyValuePair<int, (int Owner, Mesh Mesh)> pair in _meshes)
                if (pair.Value.Owner == _owner && !_touched.Contains(pair.Key))
                    (stale ??= new List<int>()).Add(pair.Key);

            if (stale == null) return;

            foreach (int id in stale)
            {
                if (_meshes.TryGetValue(id, out (int Owner, Mesh Mesh) entry) && entry.Mesh != null)
                    Object.Destroy(entry.Mesh);
                _meshes.Remove(id);
            }
        }

        /// <summary>Drop the cached mesh of a node that is going away.</summary>
        internal static void Release(GameObject go)
        {
            if (go == null) return;
            int id = go.GetInstanceID();
            if (!_meshes.TryGetValue(id, out (int Owner, Mesh Mesh) entry)) return;

            _meshes.Remove(id);
            if (entry.Mesh != null) Object.Destroy(entry.Mesh);
        }
    }
}
