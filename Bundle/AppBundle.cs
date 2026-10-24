using System.Reflection;
using System.Text;
using MelonLoader.Utils;

namespace Sideload.Bundle
{
    /// <summary>
    /// Resolves the files of one app's web bundle. Two sources, file wins:
    ///
    ///   1. <c>Mods/&lt;appId&gt;/&lt;path&gt;</c> on disk - the override. Absent in a normal install; this is what the
    ///      hot-reload watcher listens on and what lets players reskin an app.
    ///   2. an embedded resource <c>&lt;bundlePrefix&gt;.&lt;path with / turned into .&gt;</c> in the host mod's assembly -
    ///      the shipped default, so a mod is still a single DLL to install.
    ///
    /// Paths are always bundle-relative and use forward slashes ("index.html", "css/app.css").
    /// </summary>
    internal sealed class AppBundle
    {
        private readonly string _prefix;
        private readonly Assembly _asm;

        internal AppBundle(string appId, string bundlePrefix, Assembly hostAssembly, string folderRoot = null)
        {
            _prefix = bundlePrefix ?? "";
            _asm = hostAssembly;
            OverrideRoot = SafeCombine(MelonEnvironment.ModsDirectory, appId);
            FolderRoot = string.IsNullOrEmpty(folderRoot) ? null : folderRoot;
        }

        /// <summary>Folder that overrides embedded files. It does not have to exist.</summary>
        internal string OverrideRoot { get; }

        /// <summary>
        /// Where an app that is nothing but files keeps them - a folder with a sideload.json (<see cref="ManifestApps"/>).
        /// Null for an app a mod registered. Read after the override and before any embedded copy, so a player can
        /// still reskin a file-only app through <c>Mods/&lt;id&gt;/</c> exactly like any other.
        /// </summary>
        internal string FolderRoot { get; }

        /// <summary>Absolute path the override would live at - also the path the hot-reload watcher reports.</summary>
        internal string OverridePathOf(string path) => SafeCombine(OverrideRoot, (path ?? "").Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Resource name the embedded copy lives under.</summary>
        internal string ResourceNameOf(string path)
        {
            string p = (path ?? "").Replace('/', '.').Replace('\\', '.');
            return string.IsNullOrEmpty(_prefix) ? p : _prefix + "." + p;
        }

        internal bool Exists(string path)
        {
            if (File.Exists(OverridePathOf(path))) return true;
            if (FolderPathOf(path) is string folder && File.Exists(folder)) return true;
            using var s = OpenEmbedded(path);
            return s != null;
        }

        /// <summary>File contents as UTF-8 text, or null when the file exists in neither source.</summary>
        internal string ReadText(string path)
        {
            byte[] bytes = ReadBytes(path);
            if (bytes == null) return null;
            // Strip a UTF-8 BOM: an editor-saved override would otherwise put U+FEFF in front of "<!doctype html>".
            int start = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        /// <summary>Raw file contents, or null when the file exists in neither source.</summary>
        internal byte[] ReadBytes(string path)
        {
            string file = OverridePathOf(path);
            if (File.Exists(file))
            {
                try { return File.ReadAllBytes(file); }
                catch (Exception e) { Core.Log?.Warning($"override unreadable ({file}): {e.Message}"); }
            }

            string fromFolder = FolderPathOf(path);
            if (fromFolder != null && File.Exists(fromFolder))
            {
                try { return File.ReadAllBytes(fromFolder); }
                catch (Exception e) { Core.Log?.Warning($"app file unreadable ({fromFolder}): {e.Message}"); }
            }

            using Stream s = OpenEmbedded(path);
            if (s == null) return null;
            try
            {
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
            catch (Exception e)
            {
                Core.Log?.Warning($"embedded resource unreadable ({ResourceNameOf(path)}): {e.Message}");
                return null;
            }
        }

        /// <summary>The file in <see cref="FolderRoot"/>, or null for an app without one or a path that tries to leave
        /// it. A file-only app is data from a download, so ".." is refused here rather than trusted.</summary>
        private string FolderPathOf(string path)
        {
            if (FolderRoot == null || string.IsNullOrEmpty(path)) return null;

            string candidate = SafeCombine(FolderRoot, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                string full = Path.GetFullPath(candidate);
                string root = Path.GetFullPath(FolderRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch { return null; }
        }

        private Stream OpenEmbedded(string path)
        {
            if (_asm == null) return null;
            try { return _asm.GetManifestResourceStream(ResourceNameOf(path)); }
            catch { return null; }
        }

        private static string SafeCombine(string a, string b)
        {
            try { return Path.Combine(a ?? "", b ?? ""); }
            catch { return b ?? ""; }
        }
    }
}
