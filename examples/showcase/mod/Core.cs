using System.Globalization;
using Sideload.Api;
using MelonLoader;

[assembly: MelonInfo(typeof(Showcase.Core), "Sideload Showcase", "1.0.0", "DooDesch")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Showcase
{
    /// <summary>
    /// The whole mod. A Sideload app is a bundle plus the handful of questions the page is allowed to ask the
    /// game, and this file is the second half of that - forty lines, no uGUI, no Harmony.
    /// </summary>
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            // Registering before Sideload itself has loaded is fine: the shim queues the call and replays it the
            // moment the framework appears, so load order never has to be arranged.
            //
            // The bundle prefix is the LogicalName the csproj stamps on the embedded files. The two are one string
            // written twice, and a typo shows up as an app that registers and then opens an empty panel.
            Apps.Register(id: "showcase",
                          bundlePrefix: "Showcase.Assets.showcase",
                          title: "Showcase",
                          iconLabel: "Showcase")
                .Orientation("landscape", "portrait")

                // `s1.call(name, arg)` on the page reaches exactly these, synchronously, and whatever string comes
                // back IS the return value. Anything the page should not be able to do belongs on this side.
                .OnCall("hello", arg => $"hello {arg} - answered by the mod at "
                                        + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))

                .OnCall("time", _ => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }
    }
}
