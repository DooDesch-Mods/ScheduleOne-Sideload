using System.Reflection;
using MelonLoader.Utils;

namespace Sideload.Bundle
{
    /// <summary>
    /// Apps that are nothing but files: a folder holding <c>sideload.json</c> next to <c>index.html</c>, <c>app.css</c>
    /// and <c>app.js</c>, and no mod of their own. Sideload registers each one it finds at start-up.
    ///
    /// Two places are looked in, because the two ways a player installs things put files in different places:
    ///
    ///   <c>Mods/&lt;folder&gt;/sideload.json</c>                 what a mod manager unpacks a package into
    ///   <c>UserData/Sideload/apps/&lt;folder&gt;/sideload.json</c>   where a hand install or a studio export goes
    ///
    /// <c>sideload.json</c>, not <c>manifest.json</c>: a Thunderstore package already has a manifest.json in its root,
    /// and a file of the same name meaning something else is a support question waiting to happen.
    ///
    /// <code>
    /// {
    ///   "id": "bank-dashboard",
    ///   "title": "Bank",
    ///   "iconLabel": "Bank",
    ///   "orientation": "landscape,portrait",
    ///   "version": "1.0.0",
    ///   "author": "Someone"
    /// }
    /// </code>
    ///
    /// A file-only app can show and read - <c>s1.storage</c>, <c>s1api</c> - and nothing more. It has no mod to call
    /// <c>AllowHost</c> or <c>AllowGameWrites</c>, so its fetch reaches nothing and every s1api write is refused. That
    /// is the point: a folder from a download must not be able to do what installed code can.
    /// </summary>
    internal static class ManifestApps
    {
        internal const string FileName = "sideload.json";

        internal static void LoadAll(Assembly host)
        {
            int found = 0;
            found += Scan(MelonEnvironment.ModsDirectory, host);
            found += Scan(Path.Combine(MelonEnvironment.UserDataDirectory, "Sideload", "apps"), host);

            if (found > 0) Core.Log?.Msg($"{found} file-only app(s) registered from {FileName}.");
        }

        private static int Scan(string root, Assembly host)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

            int found = 0;
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception e)
            {
                Core.Log?.Warning($"could not list {root} for app folders: {e.Message}");
                return 0;
            }

            foreach (string folder in folders)
            {
                string manifest = Path.Combine(folder, FileName);
                if (!File.Exists(manifest)) continue;

                try
                {
                    if (Register(folder, File.ReadAllText(manifest), host)) found++;
                }
                catch (Exception e)
                {
                    Core.Log?.Warning($"{manifest} could not be read: {e.Message}");
                }
            }

            return found;
        }

        /// <summary>Register one folder. Returns false, after saying why, for a manifest that names no usable id or a
        /// folder without an index.html.</summary>
        internal static bool Register(string folder, string json, Assembly host)
        {
            Dictionary<string, string> m = Script.MiniJson.ParseObject(json);

            string id = Get(m, "id") ?? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsUsableId(id))
            {
                Core.Log?.Warning($"{Path.Combine(folder, FileName)} has no usable id (letters, digits, '-', '_' and '.' only) - skipped.");
                return false;
            }

            if (!File.Exists(Path.Combine(folder, "index.html")))
            {
                Core.Log?.Warning($"{folder} has a {FileName} but no index.html - skipped.");
                return false;
            }

            string title = Get(m, "title") ?? id;
            Registry.RegisterApp(id, title, Get(m, "iconLabel") ?? title, "", host, folder);

            string orientation = Get(m, "orientation");
            if (orientation != null) Registry.DeclareOrientations(id, orientation);

            Core.Log?.Msg($"file-only app '{id}' {Get(m, "version") ?? ""} from {folder}".TrimEnd());
            return true;
        }

        private static string Get(Dictionary<string, string> m, string key) =>
            m.TryGetValue(key, out string v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        /// <summary>An id is also a folder name under Mods/ and a GameObject name, so it stays plain.</summary>
        internal static bool IsUsableId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;

            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) return false;

            return id != "." && id != "..";
        }
    }
}
