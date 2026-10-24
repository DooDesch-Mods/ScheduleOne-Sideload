namespace Sideload.Script
{
    /// <summary>
    /// Calling every listener on a multicast tap, each on its own. A plain <c>tap(...)</c> stops at the first one that
    /// throws, and the listeners on a tap belong to different mods - one broken consumer must not silence the others,
    /// and none of them may take the game frame that raised the event down with it.
    /// </summary>
    internal static class Taps
    {
        internal static void Each<T>(T tap, Action<T> call) where T : Delegate
        {
            if (tap == null || call == null) return;

            foreach (Delegate one in tap.GetInvocationList())
            {
                try { call((T)one); }
                catch (Exception e) { Model.Platform.Warning($"a tap failed: {e.Message}"); }
            }
        }
    }
}
