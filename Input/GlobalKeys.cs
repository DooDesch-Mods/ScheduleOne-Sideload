using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Sideload.Model;
using UnityEngine;

namespace Sideload.Input
{
    /// <summary>
    /// Keys that reach an app with the phone still in the player's pocket.
    ///
    /// <see cref="Keys"/> is the other half of the keyboard story and answers a different question: it delivers keys to
    /// a FOCUSED FIELD inside an open page. This one is what lets a key be the way IN - press Enter, the phone comes
    /// out with the chat already open and the caret in the compose box - and it exists in the framework rather than in
    /// each mod because the interesting part is not reading the key, it is deciding whose key it is. Two messengers
    /// both wanting Enter is a question only something that can see both of them can answer; see
    /// <see cref="KeyClaims"/> for the rule.
    ///
    /// <para><b>The gate is the game's own phone key, copied.</b> GameplayMenu.Update refuses its toggle while the
    /// player is typing, while the pause menu is up, while another UI screen owns the view (a station, a shop, the
    /// console, the rename dialog - each of them registers an active UI element), and while a state that is not
    /// gameplay owns the stack (asleep, dead, arrested). Copying that one condition is what keeps this honest at the
    /// two places it would otherwise be a nuisance: Enter at a mixing station is the game's own Begin button, and
    /// Enter in the developer console is submit. Both stay theirs, because in both the gate is shut.</para>
    ///
    /// <para>The legacy <c>Input</c> class rather than the input system, for the same reason <see cref="Keys"/> uses
    /// it: these are raw keyboard keys, not rebindable game actions, and there is no action asset to read for
    /// "Enter".</para>
    /// </summary>
    internal static class GlobalKeys
    {
        private static readonly KeyClaims Claims = new KeyClaims();

        /// <summary>
        /// Record what an app asked for. Reached through the bridge from a mod's own init, which can happen before
        /// there is a phone, a scene or a player - nothing here touches Unity, so that is fine.
        /// </summary>
        internal static void Claim(string appId, string declaration, Func<string, string, bool> handler)
        {
            if (string.IsNullOrWhiteSpace(appId)) return;

            if (handler == null)
            {
                // The one call that means "give them back": a mod switching a feature off, or refusing to take a key
                // it decided it cannot honour.
                Claims.Release(appId);
                return;
            }

            IReadOnlyList<KeyDeclaration> keys = GlobalKey.Parse(declaration, out IReadOnlyList<string> refused);

            foreach (string why in refused)
                Core.Log?.Warning($"'{appId}' asked for the key {why} - ignored.");

            foreach (KeyDeclaration key in keys)
            {
                if (!KeyCodes.TryResolve(key.Name, out KeyCode _))
                {
                    Core.Log?.Error($"key '{key.Name}' parsed but has no keycode - it will never fire.");
                    continue;
                }

                Claims.Claim(appId, key, handler);
                Core.Log?.Msg($"'{appId}' claimed the key {key}.");
            }
        }

        /// <summary>
        /// An app just interrupted the player. Called from every notification, because "who spoke to you last" is the
        /// whole tie-break when two apps want the same key.
        /// </summary>
        internal static void Attend(string appId) => Claims.Attend(appId);

        /// <summary>One frame. Returns immediately when nothing is claimed, which is every frame for a player with no
        /// app that uses this.</summary>
        internal static void Tick()
        {
            if (!Claims.Any || !Config.Preferences.AppKeys) return;
            if (!GameAcceptsKeys()) return;

            IReadOnlyList<KeyDeclaration> keys = Claims.Keys;

            for (int i = 0; i < keys.Count; i++)
            {
                if (!Pressed(keys[i])) continue;

                string took = Deliver(keys[i]);

                // The hit is worth a line - it is the first thing to look for when an app opens and nobody knows why.
                // The miss is not: an app that declines its key does so on every press, and a player who taps Enter
                // out of habit would fill the log with it.
                if (took != null) Core.Log?.Msg($"{keys[i]} went to '{took}'.");
#if DEBUG
                else Core.Log?.Msg($"{keys[i]} is claimed but nobody took it.");
#endif

                // At most one key a frame, which is what a keyboard produces and what stops a chord from opening two
                // apps at once. Whether anybody took it or not: a press nobody wanted is still a press that happened.
                return;
            }
        }

        /// <summary>
        /// Hand one press to whoever should have it, and answer which app took it.
        ///
        /// The app the player is LOOKING at owns every key it claimed, and blocks the rest: without this, reading one
        /// app and pressing Enter would throw the phone at a different one because that one has a newer notification.
        /// If the app on screen does not claim the key the press goes nowhere, which is better than somewhere
        /// surprising. Resolved once here rather than per claimant - it cannot change between two handlers in the same
        /// frame.
        /// </summary>
        private static string Deliver(KeyDeclaration key)
        {
            Phone.PhoneAppHost showing = OnScreen();

            return Claims.Dispatch(
                key,
                showing == null ? null : id => string.Equals(showing.Id, id, StringComparison.OrdinalIgnoreCase),
                (appId, e) => Core.Log?.Error($"the '{key}' handler of '{appId}' threw: {e.Message}"));
        }

#if DEBUG
        // What the console commands in Devtools/KeyConsole need. Deliberately the same Deliver and the same gate a
        // real press goes through, so 'sideloadkey Enter' proves the thing the tester would otherwise have to press.

        internal static IReadOnlyList<KeyDeclaration> ClaimedKeys => Claims.Keys;

        internal static IReadOnlyList<string> Ranking(KeyDeclaration key) => Claims.Ranked(key);

        internal static bool GateOpen => Config.Preferences.AppKeys && GameAcceptsKeys();

        /// <summary>
        /// Deliver a press without the keyboard. The same routing, the same on-screen rule, the same handlers - only
        /// the <see cref="UnityEngine.Input"/> read is skipped, because that is the one part a command cannot stand
        /// in for.
        ///
        /// The gate is reported by <see cref="GateOpen"/> rather than enforced here, and that is deliberate: a human
        /// typing this has the console open, and an open console shuts the gate, so enforcing it would make the
        /// command answer "refused" every single time a person used it. The caller prints both numbers.
        /// </summary>
        internal static string Send(KeyDeclaration key) => Deliver(key);
#endif

        /// <summary>The Sideload app the player can see right now, or null. Not "the open one" - an app open on a
        /// phone in the player's pocket is not in front of them and has no claim on the keyboard.</summary>
        private static Phone.PhoneAppHost OnScreen()
        {
            IReadOnlyList<Phone.PhoneAppHost> hosts = Phone.HomeScreenPatch.Hosts;

            for (int i = 0; i < hosts.Count; i++)
                if (hosts[i].IsAlive && hosts[i].IsShowing) return hosts[i];

            return null;
        }

        /// <summary>True on the frame this key went down with exactly its modifiers held. Enter answers to the number
        /// pad as well - it is the same key to everyone pressing it.</summary>
        private static bool Pressed(KeyDeclaration key)
        {
            if (!KeyCodes.TryResolve(key.Name, out KeyCode code)) return false;

            bool down = UnityEngine.Input.GetKeyDown(code)
                        || (KeyCodes.TryResolveAlternate(key.Name, out KeyCode also)
                            && UnityEngine.Input.GetKeyDown(also));

            return down && Held(KeyCode.LeftControl, KeyCode.RightControl) == key.Ctrl
                        && Held(KeyCode.LeftShift, KeyCode.RightShift) == key.Shift
                        && Held(KeyCode.LeftAlt, KeyCode.RightAlt) == key.Alt;
        }

        private static bool Held(KeyCode a, KeyCode b) =>
            UnityEngine.Input.GetKey(a) || UnityEngine.Input.GetKey(b);

        /// <summary>
        /// The condition GameplayMenu.Update puts in front of its own phone key, term for term. Anything that stops
        /// the player taking their phone out stops an app being handed a key, which is the only reading that cannot
        /// steal a keystroke from the game.
        /// </summary>
        private static bool GameAcceptsKeys()
        {
            try
            {
                // Covers the game's own fields AND Sideload's: a focused TMP_InputField holds this true, which is why
                // Enter in an open chat sends the message instead of arriving here.
                if (GameInput.IsTyping) return false;

                if (Singleton<PauseMenu>.InstanceExists && Singleton<PauseMenu>.Instance.IsPaused) return false;

                if (!Singleton<GameplayMenu>.InstanceExists) return false;
                GameplayMenu menu = Singleton<GameplayMenu>.Instance;

                // On foot, driving or on a board - and false while sleep, death or arrest owns the stack.
                if (!menu.AcceptInputFromCurrentState()) return false;

                // Some other screen is in front of the player: a station, a shop, dialogue, the rename dialog, the
                // developer console. The phone itself counts as one, hence the second half.
                if (!menu.IsOpen
                    && PlayerSingleton<PlayerCamera>.InstanceExists
                    && PlayerSingleton<PlayerCamera>.Instance.ActiveUIElementCount != 0) return false;

                return true;
            }
            catch (Exception e)
            {
                // A key that cannot decide whether it is allowed is a key that does not fire. Warn once per frame at
                // worst, and only while something is claimed at all.
                Core.Log?.Warning("could not read the game's input state: " + e.Message);
                return false;
            }
        }
    }
}
