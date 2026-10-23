using System.Security.Cryptography;
using Il2CppTMPro;
using Sideload.Bundle;
using Sideload.Css;
using UnityEngine;

namespace Sideload.Paint
{
    /// <summary>
    /// The TMP font assets an app's own <c>@font-face</c> rules ask for, made at runtime from the TTF or OTF in its
    /// bundle.
    ///
    /// <para>The one route this IL2CPP build leaves open is <c>TMP_FontAsset.CreateFontAsset(path, ...)</c>, which
    /// hands a FILE PATH to the font engine. There is no byte-array overload anywhere: <c>FontEngine.LoadFontFace</c>
    /// takes a path, a <c>Font</c> or a family name, and a <c>Font</c> cannot be made from bytes either - its only
    /// loader, <c>CreateDynamicFontFromOSFont</c>, is stripped, as is <c>AssetBundle.LoadFromMemory</c> for a baked
    /// asset. So the bytes are written to <c>UserData/Sideload/fonts/&lt;app&gt;/&lt;hash&gt;.ttf</c> first. The
    /// copy also keeps the font engine's file handle off the override file a developer is editing.</para>
    ///
    /// <para>Cached per app (the scope) and per file CONTENT. A rebuild never touches the cache; a page load
    /// reuses every face whose bytes did not change and destroys the assets of the faces that did or that the sheet
    /// no longer declares - which is what keeps a hot-reload loop from growing an atlas per save.</para>
    /// </summary>
    internal static class BundleFonts
    {
        /// <summary>Sampled at 90 points with 9 of padding: TMP's own defaults, because these faces are headline
        /// faces as often as body text and a smaller sample goes soft at 40px. Dynamic, so any glyph the file has
        /// is drawn the first time it is asked for, and multi-atlas, so a long page cannot fill it up.</summary>
        private const int SamplingPoint = 90, Padding = 9, AtlasSize = 1024;

        /// <summary>The loaded faces of one family, the rules and their assets index for index - kept as two lists
        /// because <see cref="FontFaces.Pick"/> takes the rules, and it runs per measured string.</summary>
        private sealed class Family
        {
            internal readonly List<FontFaceRule> Rules = new List<FontFaceRule>();
            internal readonly List<TMP_FontAsset> Assets = new List<TMP_FontAsset>();
        }

        private sealed class ScopeFonts
        {
            internal readonly Dictionary<string, Family> Families =
                new Dictionary<string, Family>(StringComparer.OrdinalIgnoreCase);

            internal readonly Dictionary<string, TMP_FontAsset> ByHash =
                new Dictionary<string, TMP_FontAsset>(StringComparer.Ordinal);
        }

        private static readonly Dictionary<string, ScopeFonts> _scopes =
            new Dictionary<string, ScopeFonts>(StringComparer.Ordinal);

        /// <summary>Warnings already printed, so a page that rebuilds every second says each thing once.</summary>
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Scopes whose cache folder has been swept this session. Swept once, before the first load,
        /// because a file the font engine already opened cannot be deleted on Windows.</summary>
        private static readonly HashSet<string> _swept = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Load every face the sheet declares for this app, reusing what is already loaded. Called once per page
        /// build, before the first measurement - not per rebuild.
        /// </summary>
        internal static void Prepare(string scope, AppBundle bundle, IReadOnlyList<FontFaceRule> faces)
        {
            if (string.IsNullOrEmpty(scope))
            {
                if (faces != null && faces.Count > 0)
                    Core.Log?.Warning("@font-face ignored: the page has no app id to keep its fonts under.");
                return;
            }

            _scopes.TryGetValue(scope, out ScopeFonts old);
            var next = new ScopeFonts();

            if (faces != null && faces.Count > 0)
            {
                if (bundle == null)
                    Report(scope + "|nobundle", $"{scope}: @font-face ignored - the page has no bundle to read files from.");
                else
                {
                    Sweep(scope, faces, bundle);
                    foreach (FontFaceRule rule in faces) Load(scope, bundle, rule, old, next);
                }
            }

            _scopes[scope] = next;
            Release(scope, old, next);
        }

        /// <summary>
        /// The asset for a family at this weight and style, or null when the family has no face that loaded. The
        /// caller then draws the UI face; the family is reported once.
        /// </summary>
        internal static TMP_FontAsset Resolve(string scope, string family, int weight, bool italic)
        {
            if (_scopes.TryGetValue(scope ?? "", out ScopeFonts fonts)
                && fonts.Families.TryGetValue(family ?? "", out Family faces))
            {
                int pick = FontFaces.Pick(faces.Rules, weight, italic);
                TMP_FontAsset asset = pick >= 0 ? faces.Assets[pick] : null;
                if (asset != null) return asset;
            }

            Report(scope + "|family|" + family,
                   $"{scope}: font-family '{family}' has no @font-face that loaded - text falls back to game-ui.");
            return null;
        }

        private static void Load(string scope, AppBundle bundle, FontFaceRule rule, ScopeFonts old, ScopeFonts next)
        {
            string label = $"{scope}: @font-face {rule.Describe()}";

            if (rule.Source == null)
            {
                Report(scope + "|" + rule.Describe() + "|" + rule.Problem, $"{label} is not loaded - {rule.Problem}.");
                return;
            }

            byte[] bytes = bundle.ReadBytes(rule.Source);
            if (bytes == null || bytes.Length == 0)
            {
                Report(scope + "|missing|" + rule.Source,
                       $"{label} is not loaded - '{rule.Source}' is neither in the bundle nor in {bundle.OverrideRoot}.");
                return;
            }

            string hash = Hash(bytes);
            TMP_FontAsset asset = null;

            if (next.ByHash.TryGetValue(hash, out TMP_FontAsset shared)) asset = shared;
            else if (old != null && old.ByHash.TryGetValue(hash, out TMP_FontAsset kept) && kept != null) asset = kept;
            else asset = Create(scope, rule, bytes, hash, label);

            if (asset == null) return;

            next.ByHash[hash] = asset;
            string key = rule.Family.Trim();
            if (!next.Families.TryGetValue(key, out Family family)) next.Families[key] = family = new Family();
            family.Rules.Add(rule);
            family.Assets.Add(asset);
        }

        private static TMP_FontAsset Create(string scope, FontFaceRule rule, byte[] bytes, string hash, string label)
        {
            string file;
            try
            {
                string folder = Folder(scope);
                Directory.CreateDirectory(folder);

                string extension = Path.GetExtension(rule.Source).ToLowerInvariant();
                file = Path.Combine(folder, hash + (extension == ".otf" ? ".otf" : ".ttf"));
                if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
            }
            catch (Exception e)
            {
                Core.Log?.Error($"{label} is not loaded - the font file could not be written to the cache: {e.Message}");
                return null;
            }

            try
            {
                TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(
                    file, 0, SamplingPoint, Padding, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA,
                    AtlasSize, AtlasSize);

                if (asset == null)
                {
                    Core.Log?.Error($"{label} is not loaded - the font engine refused '{rule.Source}' "
                                    + "(not a TrueType or OpenType file it can read).");
                    return null;
                }

                // Kept alive by hand: nothing in a scene holds it, and a load screen would otherwise collect it out
                // from under every text that uses it. Released in Release() when the page stops declaring it.
                UnityEngine.Object.DontDestroyOnLoad(asset);
                asset.hideFlags = HideFlags.HideAndDontSave;

                // "Italic" in the name is how TextSupport.Apply tells a true italic from an upright face it has to
                // slant, the same test it applies to the game's own Open Sans cuts.
                asset.name = rule.Family.Trim() + " " + rule.WeightMin + (rule.Italic ? " Italic" : "");

                AddFallback(asset, label);

                Core.Log?.Msg($"{label} loaded from '{rule.Source}' ({bytes.Length} bytes, dynamic atlas).");
                return asset;
            }
            catch (Exception e)
            {
                Core.Log?.Error($"{label} is not loaded - creating the font asset failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The game's UI face behind the app's own, so a character the file does not carry - a subset font meeting
        /// a player name - draws in Open Sans instead of as a box.
        /// </summary>
        private static void AddFallback(TMP_FontAsset asset, string label)
        {
            try
            {
                TMP_FontAsset ui = FontRegistry.Resolve(FontFamilies.Default, 400, FontStyleKind.Normal);
                if (ui == null)
                {
                    Core.Log?.Warning($"{label}: no game UI face to fall back on for glyphs the file lacks.");
                    return;
                }

                var table = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
                table.Add(ui);
                asset.fallbackFontAssetTable = table;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"{label}: the fallback face could not be attached ({e.Message}) - glyphs the "
                                  + "file lacks draw as boxes.");
            }
        }

        /// <summary>Destroy every asset the previous load held that the new one did not carry over.</summary>
        private static void Release(string scope, ScopeFonts old, ScopeFonts next)
        {
            if (old == null) return;

            int released = 0;
            foreach (KeyValuePair<string, TMP_FontAsset> pair in old.ByHash)
            {
                if (next.ByHash.ContainsKey(pair.Key)) continue;
                if (Destroy(pair.Value, scope)) released++;
            }

            if (released > 0) Core.Log?.Msg($"{scope}: released {released} font asset(s) the page no longer uses.");
        }

        private static bool Destroy(TMP_FontAsset asset, string scope)
        {
            if (asset == null) return false;

            try
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Texture2D> atlases = asset.atlasTextures;
                if (atlases != null)
                    foreach (Texture2D atlas in atlases)
                        if (atlas != null) UnityEngine.Object.Destroy(atlas);

                if (asset.material != null) UnityEngine.Object.Destroy(asset.material);
                UnityEngine.Object.Destroy(asset);
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"{scope}: a font asset could not be released ({e.Message}) - it stays in memory "
                                  + "until the game closes.");
                return false;
            }
        }

        /// <summary>
        /// Delete cached files of this app that no face in the sheet points at any more. Once per session, before
        /// anything is loaded, so no file is held open yet.
        /// </summary>
        private static void Sweep(string scope, IReadOnlyList<FontFaceRule> faces, AppBundle bundle)
        {
            if (!_swept.Add(scope)) return;

            string folder = Folder(scope);
            if (!Directory.Exists(folder)) return;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FontFaceRule rule in faces)
            {
                if (rule.Source == null) continue;
                byte[] bytes = bundle.ReadBytes(rule.Source);
                if (bytes != null && bytes.Length > 0) wanted.Add(Hash(bytes));
            }

            foreach (string file in Directory.GetFiles(folder))
            {
                if (wanted.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                try { File.Delete(file); }
                catch (Exception e) { Core.Log?.Warning($"{scope}: stale font cache file '{file}' not deleted: {e.Message}"); }
            }
        }

        private static string Folder(string scope)
        {
            string safe = scope;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "Sideload", "fonts", safe);
        }

        private static string Hash(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes), 0, 12).ToLowerInvariant();
        }

        private static void Report(string key, string line)
        {
            if (_reported.Add(key)) Core.Log?.Warning(line);
        }
    }
}
