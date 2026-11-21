using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(ResizeProbe.Plugin), "ResizeProbe", "0.0.1", "test")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace ResizeProbe
{
    /// <summary>Throwaway test plugin: when UserData/resize-probe.txt appears ("W H"), switch the window to that
    /// size, windowed, without touching the game's own display settings.</summary>
    public sealed class Plugin : MelonPlugin
    {
        private int _frame;

        public override void OnUpdate()
        {
            if (++_frame % 30 != 0) return;
            string flag = Path.Combine(MelonEnvironment.UserDataDirectory, "resize-probe.txt");
            if (!File.Exists(flag)) return;
            try
            {
                string[] p = File.ReadAllText(flag).Trim().Split(' ');
                File.Delete(flag);
                int w = int.Parse(p[0]), h = int.Parse(p[1]);
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
                LoggerInstance.Msg($"[resizeprobe] requested {w}x{h} windowed; screen was {Screen.width}x{Screen.height}");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("[resizeprobe] resize failed: " + e);
            }
        }
    }
}
