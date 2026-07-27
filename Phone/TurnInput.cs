using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;

namespace Sideload.Phone
{
    /// <summary>
    /// The player's half of the orientation: the rotate keys turn the phone while an app that declared both is open.
    ///
    /// The game's own <c>RotateLeft</c> / <c>RotateRight</c> rather than a key of Sideload's own, for three reasons
    /// that all point the same way: the action is already called "rotate", the player rebinds it in the game's own
    /// options along with everything else (so there is no second place to look), and it already carries a gamepad
    /// binding. Nothing in the base game reads either action, so nothing is being taken away.
    /// </summary>
    internal static class TurnInput
    {
        /// <summary>Called once a frame from the mod's update loop. Cheap and silent when no app is open.</summary>
        internal static void Tick()
        {
            PhoneAppHost host = OpenTurnableApp();
            TurnPrompt.Show(host != null);

            if (host == null) return;

            // A field with the caret in it owns every key on the board. Without this, typing "q" into a chat would
            // turn the phone instead of writing a letter.
            if (GameInput.IsTyping) return;

            if (!Singleton<GameInput>.InstanceExists) return;
            if (!GameInput.GetButtonDown(GameInput.ButtonCode.RotateLeft)
                && !GameInput.GetButtonDown(GameInput.ButtonCode.RotateRight)) return;

            host.Turn();
        }

        /// <summary>The app on screen right now, if it is one the player is allowed to turn. Null otherwise.</summary>
        private static PhoneAppHost OpenTurnableApp()
        {
            IReadOnlyList<PhoneAppHost> hosts = HomeScreenPatch.Hosts;

            for (int i = 0; i < hosts.Count; i++)
                if (hosts[i].IsOpen && hosts[i].CanTurn) return hosts[i];

            return null;
        }
    }
}
