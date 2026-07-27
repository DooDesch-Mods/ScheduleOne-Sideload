namespace Sideload.Bundle
{
    /// <summary>
    /// Watches an app's override folder and reports that something changed. Debug builds only: shipping a file watcher
    /// on a player's machine buys nothing and costs a handle per app.
    ///
    /// The watcher fires on a thread-pool thread, so it does exactly one thing - set a flag. Everything that touches
    /// Unity happens on the main thread when the view next ticks. Writes are also coalesced: an editor saving a file
    /// typically produces two or three events, and reloading the page for each one is both wasteful and visibly ugly.
    /// </summary>
    internal sealed class HotReload : IDisposable
    {
        /// <summary>How long the folder has to stay quiet before a reload is considered safe. Long enough to cover an
        /// editor's write-then-rename dance, short enough to feel immediate.</summary>
        private const float QuietSeconds = 0.25f;

        private readonly FileSystemWatcher _watcher;
        private volatile bool _touched;
        private bool _pending;
        private float _quiet;

        private HotReload(FileSystemWatcher watcher) => _watcher = watcher;

        /// <summary>Start watching, or return null when there is nothing to watch.</summary>
        internal static HotReload Start(string folder, string appId)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Core.Log?.Msg($"[Sideload] no override folder for '{appId}' - create {folder} to edit the app live.");
                return null;
            }

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };

                var reload = new HotReload(watcher);
                watcher.Changed += (_, __) => reload._touched = true;
                watcher.Created += (_, __) => reload._touched = true;
                watcher.Deleted += (_, __) => reload._touched = true;
                watcher.Renamed += (_, __) => reload._touched = true;
                watcher.EnableRaisingEvents = true;

                Core.Log?.Msg($"[Sideload] hot reload watching {folder}");
                return reload;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Sideload] hot reload unavailable for '{appId}': {e.Message}");
                return null;
            }
        }

        /// <summary>True exactly once per settled burst of file changes.</summary>
        internal bool ShouldReload(float deltaSeconds)
        {
            // Any touch restarts the quiet period, so a burst of writes ends in exactly one reload.
            if (_touched)
            {
                _touched = false;
                _pending = true;
                _quiet = 0f;
                return false;
            }

            if (!_pending) return false;

            _quiet += deltaSeconds;
            if (_quiet < QuietSeconds) return false;

            _pending = false;
            return true;
        }

        public void Dispose()
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch { }
        }
    }
}
