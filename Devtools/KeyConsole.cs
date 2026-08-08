#if DEBUG
using HarmonyLib;
using Sideload.Model;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only console commands for the keys apps claim. Compiled out of Release entirely.
    ///
    /// It exists because the feature cannot otherwise be checked by anything but a human hand. Automation can submit
    /// a console command; it cannot press a key. Without <c>sideloadkey</c>, whether a claim reached the dispatcher,
    /// whether the ranking is what the app expected and whether the handler does the right thing are all questions
    /// only somebody sitting at the keyboard can answer.
    ///
    /// <list type="bullet">
    /// <item><c>sideloadkeys</c> - every claim, in the order the key would be offered, and whether the gate is open
    /// right now.</item>
    /// <item><c>sideloadkey &lt;key&gt;</c> - deliver the press through the SAME path a real one takes, gate
    /// included. Only the keyboard read is skipped, because that is the one part a command cannot stand in for.</item>
    /// </list>
    ///
    /// Both <c>SubmitCommand</c> overloads are patched: the string body calls the list body, so depending on the
    /// caller either prefix can be the one that fires. Side effects are deduplicated per frame and signature, or a
    /// single submission would dispatch the key twice.
    /// </summary>
    internal static class KeyConsole
    {
        private static int _lastFrame = -1;
        private static string _lastSignature = "";

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;

            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        /// <summary>True when the line was ours and the game must not also run it.</summary>
        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;

            string command = parts[0].ToLowerInvariant();
            if (command != "sideloadkeys" && command != "sideloadkey") return false;

            string signature = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && signature == _lastSignature) return true;
            _lastFrame = Time.frameCount;
            _lastSignature = signature;

            try
            {
                if (command == "sideloadkeys") Report();
                else if (parts.Length < 2) Core.Log?.Msg("usage: sideloadkey <key>, for example 'sideloadkey Enter'.");
                else Press(parts[1]);
            }
            catch (Exception e) { Core.Log?.Warning($"{command} failed: {e.Message}"); }

            return true;
        }

        private static void Report()
        {
            IReadOnlyList<KeyDeclaration> keys = Input.GlobalKeys.ClaimedKeys;

            Core.Log?.Msg($"app keys are {(Config.Preferences.AppKeys ? "ON" : "OFF (AppKeys)")}; "
                          + $"the gate is {(Input.GlobalKeys.GateOpen ? "open" : "shut")} right now.");

            foreach (KeyDeclaration key in keys)
                Core.Log?.Msg($"  {key} -> {string.Join(", ", Input.GlobalKeys.Ranking(key))}"
                              + "   (best first; the winner is whoever notified last)");

            if (keys.Count == 0) Core.Log?.Msg("  no app has claimed a key.");

            // Where the keyboard is, because the gate above is only ever shut BECAUSE something holds it, and which
            // something decides whether data-typing is working or a search box is quietly eating every keystroke.
            foreach (Phone.PhoneAppHost host in Phone.HomeScreenPatch.Hosts)
                if (host.IsAlive) Core.Log?.Msg($"  {host.TypingReport}");
        }

        private static void Press(string token)
        {
            if (!GlobalKey.TryParse(token, out KeyDeclaration key, out string why))
            {
                Core.Log?.Msg($"'{token}' is not a key an app may claim - {why}.");
                return;
            }

            // Both halves, because they answer different questions and a human sees a misleading one on its own: an
            // open console counts as a UI screen, so somebody typing this reads "shut" while a real press out in the
            // world would be let through. Submitted over the bridge - no console UI - it reads "open", which is the
            // gate being proved rather than described.
            Core.Log?.Msg($"sideloadkey {key}: gate is "
                          + (Input.GlobalKeys.GateOpen ? "open" : "shut right now (an open console is a UI screen)")
                          + $"; delivered to {Input.GlobalKeys.Send(key) ?? "nobody - no claimant took it"}");
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(string) })]
    internal static class Sideload_Console_SubmitCommand_String_Patch
    {
        private static bool Prefix(string args)
        {
            try { return !KeyConsole.TryHandle(args) && !SurfaceConsole.TryHandle(args); } catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand),
                  new Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class Sideload_Console_SubmitCommand_List_Patch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try { return !KeyConsole.TryHandle(args) && !SurfaceConsole.TryHandle(args); } catch { return true; }
        }
    }
}
#endif
