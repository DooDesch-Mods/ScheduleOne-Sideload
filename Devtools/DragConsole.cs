#if DEBUG
using System.Globalization;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// DEBUG-only: drive a held-button drag and the keys of an open option list from the console.
    ///
    /// <c>sideloaddrag &lt;appId&gt; &lt;fromX,fromY&gt; &lt;toX,toY&gt; &lt;steps&gt; &lt;selector&gt;</c> presses at one point of the
    /// element, moves to the other in that many frames and releases. The points are fractions of the element's box,
    /// 0,0 top left and 1,1 bottom right. The selector is looked up in the page and then in an open popup, so
    /// <c>[data-part=sv]</c> reaches the colour field.
    ///
    /// <c>sideloadpopup &lt;appId&gt; &lt;key&gt;</c> sends ArrowDown, ArrowUp, Home, End, Enter or Escape to an open option
    /// list.
    ///
    /// Automation can submit a console command; it cannot hold a mouse button. Both go through the same press,
    /// poll and release code the mouse does - only the pointer's position and buttons are supplied from here.
    /// </summary>
    internal static class DragConsole
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

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;

            bool drag = parts[0].Equals("sideloaddrag", StringComparison.OrdinalIgnoreCase);
            bool key = parts[0].Equals("sideloadpopup", StringComparison.OrdinalIgnoreCase);
            if (!drag && !key) return false;

            // Both SubmitCommand overloads are patched and the string one calls the list one, so a single
            // submission arrives twice. Same guard as KeyConsole, for the same reason.
            string signature = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && signature == _lastSignature) return true;
            _lastFrame = Time.frameCount;
            _lastSignature = signature;

            try
            {
                if (drag) Drag(parts);
                else Key(parts);
            }
            catch (Exception e) { Core.Log?.Warning(parts[0] + " failed: " + e.Message); }

            return true;
        }

        private static void Drag(string[] parts)
        {
            if (parts.Length < 6
                || !Point(parts[2], out float fromX, out float fromY)
                || !Point(parts[3], out float toX, out float toY)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int steps))
            {
                Core.Log?.Msg("usage: sideloaddrag <appId> <fromX,fromY> <toX,toY> <steps> <selector> - points are "
                              + "fractions of the element, for example 'sideloaddrag sideload-selftest 0,0.5 1,0.5 12 #vol'.");
                return;
            }

            Host.WebView page = Host.WebView.Find(parts[1]);
            if (page == null) { Core.Log?.Msg($"sideloaddrag: no page mounted for {parts[1]}."); return; }

            string selector = string.Join(" ", parts, 5, parts.Length - 5);
            page.DebugDrag(selector, fromX, fromY, toX, toY, steps);
        }

        private static void Key(string[] parts)
        {
            if (parts.Length < 3)
            {
                Core.Log?.Msg("usage: sideloadpopup <appId> <ArrowDown|ArrowUp|Home|End|Enter|Escape>");
                return;
            }

            Host.WebView page = Host.WebView.Find(parts[1]);
            if (page == null) { Core.Log?.Msg($"sideloadpopup: no page mounted for {parts[1]}."); return; }

            bool taken = page.PopupKey(parts[2]);
            Core.Log?.Msg($"[Sideload/probe] key {parts[2]} {(taken ? "taken by the open list" : "ignored - no option list is open, or the key means nothing there")}.");
        }

        private static bool Point(string text, out float x, out float y)
        {
            x = y = 0f;
            string[] xy = text.Split(',');
            return xy.Length == 2
                   && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                   && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }
    }
}
#endif
