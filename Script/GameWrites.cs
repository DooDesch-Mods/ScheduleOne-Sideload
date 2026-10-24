namespace Sideload.Script
{
    /// <summary>
    /// Which apps may CHANGE the game through <c>s1api</c>, by area. Default deny: reading is free, writing needs the
    /// app's own mod to have said so in C#.
    ///
    /// The same reasoning as <see cref="Net.HostAllowlist"/>. A page is a file anyone can edit in Mods/, and a page that
    /// could grant itself "money" would turn every reskin into a cheat. The permission is made during mod init, long
    /// before the page exists, which is why this is static and keyed by app id.
    /// </summary>
    internal static class GameWrites
    {
        private static readonly Dictionary<string, HashSet<string>> _byApp =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Let one app write one area - "money", "leveling" - or every area with "*".</summary>
        internal static void Allow(string appId, string scope)
        {
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(scope)) return;

            if (!_byApp.TryGetValue(appId.Trim(), out HashSet<string> scopes))
                _byApp[appId.Trim()] = scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            scopes.Add(scope.Trim());
        }

        internal static bool IsAllowed(string appId, string scope)
        {
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(scope)) return false;
            if (!_byApp.TryGetValue(appId.Trim(), out HashSet<string> scopes)) return false;

            return scopes.Contains("*") || scopes.Contains(scope.Trim());
        }

        /// <summary>Tests only: forget every grant.</summary>
        internal static void Clear() => _byApp.Clear();
    }
}
