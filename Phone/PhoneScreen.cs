using System;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;

namespace Sideload.Phone
{
    /// <summary>
    /// Taking the phone out and putting it away.
    ///
    /// Opening an app has never done this, and still does not: <see cref="Registry.SetAppOpen"/> is what pressing an
    /// icon does, and pressing an icon happens on a phone that is already in the player's hand. An app whose way in is
    /// a key rather than an icon needs the other half, and it is a separate verb precisely so that an app which emits
    /// in the background cannot yank the phone up by accident.
    ///
    /// <para>The phone is not raised by <c>Phone</c> at all - it is raised by <c>GameplayMenu</c>, which slides the
    /// whole gameplay menu in and then tells <c>Phone</c> it is open. Calling <c>Phone.SetIsOpen(true)</c> directly
    /// looks like it works: the app canvases come alive, the events fire, and <c>GameplayMenu.IsOpen</c> stays false,
    /// so the menu never slides in and can never be closed again. Do not.</para>
    ///
    /// <para>Everything here is local. <c>GameplayMenu</c> is a plain Singleton with no RPC and no SyncVar, and
    /// <c>Phone</c> is a PlayerSingleton that destroys itself on every non-owner client.</para>
    /// </summary>
    internal static class PhoneScreen
    {
        /// <summary>True while the gameplay menu is on screen showing the phone. Not the same question as
        /// <c>Phone.IsOpen</c>, which is also true for a heartbeat while the menu is on the character tab.</summary>
        internal static bool IsRaised
        {
            get
            {
                GameplayMenu menu = Menu();
                return menu != null && menu.IsOpen && menu.CurrentScreen == GameplayMenu.EGameplayScreen.Phone;
            }
        }

        /// <summary>
        /// Take the phone out, switching to the phone tab if the menu was showing the character screen. Returns false
        /// when the game will not have it - which is most of why this is not one line at the call site.
        ///
        /// <c>GameplayMenu.Open()</c> is completely unguarded: it pushes a state and nothing checks whether the player
        /// is asleep, dead, arrested or in the pause menu, so a raise during the death screen succeeds and puts a
        /// phone on top of it. The gate copied here is the one the game's own toggle key uses.
        /// </summary>
        internal static bool Raise()
        {
            try
            {
                GameplayMenu menu = Menu();
                if (menu == null) return false;

                if (Singleton<PauseMenu>.InstanceExists && Singleton<PauseMenu>.Instance.IsPaused) return false;

                // True when the player is on foot, in a vehicle or on a board - and false while a sleep, death or
                // arrest state owns the stack, which is exactly when a phone must not appear.
                if (!menu.AcceptInputFromCurrentState()) return false;

                // Pushing a state that is already stacked logs an error and does nothing, so a second raise while the
                // phone is out would fill the log rather than being the harmless no-op it reads as.
                if (!menu.IsOpen) menu.Open();

                if (menu.CurrentScreen != GameplayMenu.EGameplayScreen.Phone)
                    menu.SetScreen(GameplayMenu.EGameplayScreen.Phone);

                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] raising the phone failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Put the phone away.
        ///
        /// Through the state machine rather than <c>GameplayMenu.Close()</c>: Close pops, and popping does nothing at
        /// all when something else has been pushed on top since - a dialogue, a shop, a menu. The phone would then
        /// stay out with no error and no way back. Removing unstacks wherever it sits and still runs the close path.
        /// </summary>
        internal static bool Lower()
        {
            try
            {
                GameplayMenu menu = Menu();
                if (menu == null || !menu.IsOpen) return false;

                menu.State.RemoveFromDefaultParent();
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] lowering the phone failed: " + e.Message);
                return false;
            }
        }

        private static GameplayMenu Menu() =>
            Singleton<GameplayMenu>.InstanceExists ? Singleton<GameplayMenu>.Instance : null;
    }
}
