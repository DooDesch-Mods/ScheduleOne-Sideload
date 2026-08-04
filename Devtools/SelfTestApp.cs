using System.Globalization;

namespace Sideload.Devtools
{
    /// <summary>
    /// A Debug-only app registered by Sideload itself. It exists so the whole chain - registration, bundle lookup,
    /// panel clone, icon, mount, paint, script - can be exercised with nothing else installed. Release builds ship no
    /// app of their own; there the framework is inert until a host mod registers one.
    /// </summary>
    internal static class SelfTestApp
    {
        internal const string Id = "sideload-selftest";

        internal static void Register()
        {
            Registry.RegisterApp(
                id: Id,
                title: "Sideload Self-Test",
                iconLabel: "Sideload",
                bundlePrefix: "Sideload.Assets.selftest",
                host: typeof(Core).Assembly);

            // The other half of the bridge. A real mod registers its own handlers the same way; these two exist so the
            // page can prove the round trip works without depending on any game system.
            Script.Bridge.Handle(Id, "host.clock", (app, arg) =>
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

            Script.Bridge.Handle(Id, "host.info", (app, arg) =>
                $"{app} on Unity {UnityEngine.Application.unityVersion}, frame {UnityEngine.Time.frameCount}");

            // Takes the phone out of the player's pocket and puts it back. The only part of the 1.5.0 surface that
            // cannot be checked by looking at the screen, because it is what decides whether there is a screen: drive
            // it from a probe with s1.call('host.phone', 'down') and a timer that calls back with 'up'.
            Script.Bridge.Handle(Id, "host.phone", (app, arg) =>
            {
                bool up = !string.Equals(arg, "down", StringComparison.OrdinalIgnoreCase);
                bool allowed = Registry.SetPhoneRaised(up);

                return $"{(up ? "raise" : "lower")}: {(allowed ? "done" : "refused")}, raised={Registry.IsPhoneRaised()}";
            });
        }
    }
}
