using MelonLoader.Utils;

namespace Sideload.Phone
{
    /// <summary>
    /// Remembers which way round the player left each app.
    ///
    /// Sideload's own file rather than the app's <c>s1.storage</c>: the turn belongs to the player, not to the page,
    /// and a page must not be able to overwrite - or lose - a choice it does not own. Deliberately not the game save
    /// either, for the reason storage already gives: it would travel with a save and diverge between co-op peers.
    /// </summary>
    internal static class OrientationStore
    {
        private static readonly Dictionary<string, bool> _portraitById =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        private static string Path =>
            System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "Sideload", "orientation.json");

        /// <summary>
        /// What the player last chose for this app, or null when they never chose - in which case the app's own
        /// declared default wins. An entry naming an orientation the app no longer supports is ignored rather than
        /// forced, so a mod update that drops portrait cannot strand anyone in it.
        /// </summary>
        internal static bool? Remembered(AppRegistration reg)
        {
            if (reg == null) return null;

            Load();
            if (!_portraitById.TryGetValue(reg.Id, out bool portrait)) return null;
            return reg.Supports(portrait) ? portrait : (bool?)null;
        }

        internal static void Remember(string appId, bool portrait)
        {
            if (string.IsNullOrWhiteSpace(appId)) return;

            Load();
            if (_portraitById.TryGetValue(appId, out bool was) && was == portrait) return;

            _portraitById[appId] = portrait;
            Save();
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(Path)) return;

                foreach (KeyValuePair<string, string> pair in Script.MiniJson.ParseObject(File.ReadAllText(Path)))
                    _portraitById[pair.Key] = string.Equals(pair.Value, "portrait", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                // A corrupt file is not worth failing over: every app falls back to its declared default and the next
                // turn rewrites the file.
                Core.Log?.Warning($"remembered orientations unreadable ({Path}): {e.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path) ?? ".");

                var flat = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, bool> pair in _portraitById)
                    flat[pair.Key] = pair.Value ? "portrait" : "landscape";

                File.WriteAllText(Path, Script.MiniJson.WriteObject(flat));
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"could not write remembered orientations: {e.Message}");
            }
        }
    }
}
