// Debug-only in full: this drives WebView's Debug-only harness surface, so guarding the call site is not
// enough - the file itself must not reach a Release compile.
#if DEBUG
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using UnityEngine;

namespace Sideload.Devtools
{
    /// <summary>
    /// Debug-only: raises the phone and opens the first registered app a moment after the world finishes loading.
    ///
    /// This exists so a milestone can be signed off from a screenshot without a human pressing keys - the automated
    /// test harness can drive the game but not the player's hands. It never runs in a Release build.
    /// </summary>
    internal static class AutoOpen
    {
        private const float OpenAfterSeconds = 3f;
        // Long enough that the app is still up when a human wants to poke at it; the close path still gets exercised.
        private const float CloseAfterSeconds = 900f;

        /// <summary>
        /// Scripted interaction per app, run a second apart once the app is up. Each step is something a player would
        /// do; together they walk the whole loop without a human at the keyboard, which is what lets a milestone be
        /// signed off from a log and a screenshot.
        /// </summary>
        private static readonly Dictionary<string, (float At, string Eval, string Click)[]> Scripts = new()
        {
            [SelfTestApp.Id] = new[]
            {
                (5f,  (string)null, "#add"),                                          // empty field: expect a refusal
                (6f,  "document.getElementById('entry').value = 'wire up the phone'", (string)null),
                (7f,  (string)null, "#add"),                                          // now it should be accepted
                (8f,  (string)null, ".item"),                                         // tick the row off
                (9f,  "console.log('after probe:', document.getElementById('status').textContent)", (string)null),
            },

            ["whatsdab"] = new[]
            {
                (5f,  (string)null, ".thread:nth-child(3)"),                           // open a 1:1 conversation
                (6f,  "document.getElementById('entry').value = 'heading over now'", (string)null),
                (7f,  (string)null, "#send"),
                (13f, "console.log('after probe:', document.getElementById('head-name').textContent, '/', document.querySelectorAll('.bubble').length + ' bubbles')", (string)null),
            },
        };

        private static (float At, string Eval, string Click)[] _steps;

        private static float _armedAt = -1f;
        private static bool _opened;
        private static bool _closed;
        private static int _step;

        /// <summary>Start the countdown. Called once the apps are live on the home screen.</summary>
        internal static void Arm()
        {
            if (_opened) return;

            // Off by preference: an app opened this way sits on top of the game's own screens and cannot be
            // dismissed from the home screen, which makes photographing vanilla impossible.
            if (!Config.Preferences.AutoOpenAppInDebug) return;

            _armedAt = Time.time;
        }

        internal static void Tick()
        {
            if (_armedAt < 0f) return;
            float elapsed = Time.time - _armedAt;

            // Second stage: hand the phone back to the home screen, so the icon row can be inspected too - and so the
            // close path is exercised rather than assumed.
            if (_opened && !_closed && elapsed >= CloseAfterSeconds)
            {
                _closed = true;
                try
                {
                    var live = Phone.HomeScreenPatch.Hosts;
                    if (live.Count > 0) live[0].Close();
                    Phone.HomeScreenPatch.LogIconRow();
                    Core.Log?.Msg("[Sideload/auto] closed the app again.");
                }
                catch (Exception e) { Core.Log?.Warning("[Sideload/auto] auto-close failed: " + e.Message); }
                return;
            }

            if (_opened)
            {
                RunSteps(elapsed);
                return;
            }

            if (elapsed < OpenAfterSeconds) return;
            _opened = true;

            try
            {
                // Opening the overlay is what actually raises the phone model; Phone.SetIsOpen alone only flips a flag
                // and fires events, leaving the phone stowed and the app invisible.
                if (Singleton<GameplayMenu>.InstanceExists)
                    Singleton<GameplayMenu>.Instance.SetIsOpen(true);

                var hosts = Phone.HomeScreenPatch.Hosts;
                if (hosts.Count == 0) { Core.Log?.Warning("[Sideload/auto] no app to open."); return; }

                // A real app beats the self-test: if another mod registered one, that is the thing worth looking at.
                Phone.PhoneAppHost target = hosts[0];
                foreach (Phone.PhoneAppHost host in hosts)
                    if (host.Id != SelfTestApp.Id) { target = host; break; }

                Scripts.TryGetValue(target.Id, out _steps);

                target.Open();
                Core.Log?.Msg($"[Sideload/auto] opened '{target.Id}' for inspection.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload/auto] auto-open failed: " + e.Message);
            }
        }

        private static void RunSteps(float elapsed)
        {
            if (_steps == null) return;

            while (_step < _steps.Length && elapsed >= _steps[_step].At)
            {
                (float _, string eval, string click) = _steps[_step++];

                try
                {
                    Host.WebView view = Host.WebView.Newest;
                    if (view == null) return;

                    if (eval != null) view.DebugEval(eval);
                    if (click != null) view.DebugClick(click);
                }
                catch (Exception e) { Core.Log?.Warning("[Sideload/auto] probe step failed: " + e.Message); }
            }
        }
    }
}
#endif
