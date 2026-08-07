using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;

namespace Sideload.Phone
{
    /// <summary>
    /// The way out of an app whose field is holding the keyboard.
    ///
    /// <c>GameInput.HandleExitInputs</c> returns on its very first line while <c>IsTyping</c> is true, so neither
    /// Escape nor right-click raises the exit action at all - and the phone key is refused for the same reason. Every
    /// one of the game's own screens survives that because their fields let go of the caret when the player presses
    /// Escape: TMP deactivates the field, the next press finds <c>IsTyping</c> false, and the second press leaves.
    ///
    /// <para>A page that declares <c>data-typing</c> does not have a second press. The caret comes back the frame
    /// after it is dropped - that is the whole point of the attribute - so the field would swallow every Escape in
    /// turn and the player would be shut inside the app with no key that does anything. Right-click, Escape and the
    /// phone key are all gone at once, which is the state this codebase warns about in three other places.</para>
    ///
    /// <para>So Sideload watches for the press itself and hands it to the app as if the game had delivered it. The
    /// same two <c>InputActionReference</c> assets the game reads, so a rebound Escape follows without this file
    /// knowing anything about keys, and the rescue is narrow on purpose: only while a field OF THE APP ON SCREEN has
    /// the caret. With the game's console open its field holds the keyboard, not ours, and the console keeps its own
    /// Escape.</para>
    ///
    /// <para>Worth having beyond the new attribute: an app with any focused field needed two Escapes before this, and
    /// the first one looked like nothing happened.</para>
    /// </summary>
    internal static class TypingExit
    {
        internal static void Tick()
        {
            PhoneAppHost host = KeyboardOwner();
            if (host == null) return;

            if (!ExitPressed(out bool secondary)) return;

            host.ExitWhileTyping(secondary);
        }

        /// <summary>
        /// The app on screen whose page owns the keyboard, or would take it back the next frame. Null when the caret
        /// belongs to something else - the console, a rename dialog, a vanilla screen.
        ///
        /// Deliberately NOT gated on <c>GameInput.IsTyping</c>, which reads differently depending on an ordering
        /// nothing here controls. Escape reaches TextMeshPro through the EventSystem and reaches the game through
        /// <c>GameInput.Update</c>, and which of those two MonoBehaviours runs first is a script execution order, not
        /// a contract. If TMP goes first the field is already deactivated by the time this runs and <c>IsTyping</c>
        /// reads false - so a rescue that trusted it would do nothing on exactly the frame it is needed, and the
        /// player would be shut in. Asking who OWNS the keyboard is the same question with an answer that does not
        /// move inside a frame.
        /// </summary>
        private static PhoneAppHost KeyboardOwner()
        {
            IReadOnlyList<PhoneAppHost> hosts = HomeScreenPatch.Hosts;

            for (int i = 0; i < hosts.Count; i++)
                if (hosts[i].IsAlive && hosts[i].IsShowing && hosts[i].OwnsTyping) return hosts[i];

            return null;
        }

        /// <summary>True on the frame the player asked to leave, with <paramref name="secondary"/> telling right-click
        /// from Escape - the same distinction <c>ExitType</c> carries, because a page reads it as
        /// <c>e.source</c>.</summary>
        private static bool ExitPressed(out bool secondary)
        {
            secondary = false;

            try
            {
                if (!Singleton<GameInput>.InstanceExists) return false;
                GameInput input = Singleton<GameInput>.Instance;

                if (input.PrimaryExitAction?.action != null && input.PrimaryExitAction.action.WasPressedThisFrame())
                    return true;

                if (input.SecondaryExitAction?.action != null && input.SecondaryExitAction.action.WasPressedThisFrame())
                {
                    secondary = true;
                    return true;
                }

                return false;
            }
            catch { return false; }
        }
    }
}
