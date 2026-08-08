using UnityEngine;
using UnityEngine.InputSystem;
using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Input;
using Object = UnityEngine.Object;
using Il2CppActionList = Il2CppSystem.Collections.Generic.List<UnityEngine.InputSystem.InputActionReference>;
using Il2CppDescriptorList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.UI.Input.InputPromptsDescriptorData>;

namespace Sideload.Phone
{
    /// <summary>
    /// The one thing Sideload puts on screen for the player: a "Rotate Phone" line in the game's own key-hint strip,
    /// for as long as an app that can be turned is open.
    ///
    /// It goes there rather than into the app because the app's screen belongs to whoever wrote it, and because the
    /// game already teaches Tab and Escape in exactly that spot - a player who has read one has read the others.
    ///
    /// The line is ASKED FOR, not drawn: <see cref="InputPromptsManager.LoadModule(InputPromptsData,
    /// EInputPromptPosition, string)"/> is the same entry point the base game uses for its own strips, so the
    /// typeface, the shade, the spacing and the key badge all come from the game. The badge shows whatever the
    /// player has rotate bound to and keeps showing the right thing after a rebind, because the manager resolves it
    /// from the action rather than from a string.
    ///
    /// <para>Until 1.14.4 this file CLONED a row out of a strip that happened to be on screen and then placed the
    /// copy by hand. Two things were wrong with that and both showed. Finding a row to copy meant sweeping the
    /// scene, which cost 22 ms of every frame while an app was open - so 1.14.2 had to bound the sweep, after which
    /// the hint gave up permanently the first time the strip was away. And the strip legitimately comes and goes
    /// with the player's context, so "away" is normal: once it had given up, the hint never came back.</para>
    ///
    /// <para>Asking has neither problem. There is nothing to search for, so there is no cost to bound and nothing
    /// to give up on; the panel is this module's own, so it does not need another one to already be up.</para>
    /// </summary>
    internal static class TurnPrompt
    {
        /// <summary>Panel id. The manager lowercases it, so it is written lowercase and compared lowercase.</summary>
        private const string ModuleId = "sideload-turn";

        private static InputPromptsData _module;
        private static bool _up;
        private static bool _warned;

        /// <summary>
        /// Put the line up, or take it down. Called every frame, and doing nothing is the common case: both the
        /// wanted state and the current state are booleans, so an ordinary frame is one comparison.
        /// </summary>
        internal static void Show(bool wanted)
        {
            if (wanted == _up) return;

            try
            {
                if (wanted) Up();
                else Down();
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[Sideload] the turn hint could not be changed: " + e.Message);
                _up = false;
            }
        }

        private static void Up()
        {
            if (!Singleton<InputPromptsManager>.InstanceExists) { WarnOnce("the input prompts manager is not up"); return; }

            InputPromptsData module = Module();
            if (module == null) return;

            Singleton<InputPromptsManager>.Instance.LoadModule(module, EInputPromptPosition.BottomLeftInGame);
            _up = true;
        }

        private static void Down()
        {
            _up = false;
            if (Singleton<InputPromptsManager>.InstanceExists)
                Singleton<InputPromptsManager>.Instance.UnloadModule(ModuleId);
        }

        /// <summary>
        /// The module the game draws the line from, built once and kept.
        ///
        /// This is the same shape the base game's own strips are: an <see cref="InputPromptsData"/> holding
        /// descriptors, each a label plus the actions it stands for. <c>BuildStart_Base</c> hands the manager one of
        /// these when building starts; the difference is only that theirs is an asset somebody authored in the editor
        /// and this one is made at runtime, because a mod has no asset bundle in the game's own registry.
        ///
        /// <para>Everything about how the line LOOKS therefore comes from the game: the typeface, the shade, the
        /// spacing, the key badge for whatever the player has rotate bound to, and where in the strip it sits. Until
        /// 1.14.4 this file cloned a row out of a strip that happened to be on screen and then placed the copy by
        /// hand - which needed a scene sweep to find a row to copy, and had nothing to copy whenever the player's
        /// context had no strip up.</para>
        /// </summary>
        private static InputPromptsData Module()
        {
            if (_module != null) return _module;

            InputActionReference left = RotateAction(true);
            InputActionReference right = RotateAction(false);

            // No action, no hint. A line that names a key the player does not have is worse than no line at all -
            // and this is reachable, since BuildManager is a NetworkSingleton and may not be up yet. Nothing is
            // cached in that case, so the next frame that wants the hint asks again.
            if (left == null && right == null) return null;

            var actions = new Il2CppActionList();
            if (left != null) actions.Add(left);
            if (right != null) actions.Add(right);

            // The game's own label for this exact pair, on the one thing it already rotates, is "Rotate Conveyor".
            // Following that pattern is why this says Rotate Phone rather than anything shorter.
            var descriptor = ScriptableObject.CreateInstance<InputPromptsDescriptorData>();
            descriptor.DisplayName = "Rotate Phone";
            descriptor.DisplayColor = Color.white;
            descriptor.Actions = actions;

            var descriptors = new Il2CppDescriptorList();
            descriptors.Add(descriptor);

            _module = ScriptableObject.CreateInstance<InputPromptsData>();
            _module.Id = ModuleId;
            _module.Position = EInputPromptPosition.BottomLeftInGame;
            _module.Descriptors = descriptors;
            _module.EnablePulseAnimation = false;

            // Neither object belongs to a scene, so without this the first scene change destroys both and the hint
            // silently stops working for the rest of the session.
            Object.DontDestroyOnLoad(descriptor);
            Object.DontDestroyOnLoad(_module);

            return _module;
        }

        /// <summary>
        /// The rotate-left / rotate-right action, read off the object that owns it. Shared with
        /// <see cref="TurnInput"/> so the knowledge of where these live sits in one place.
        ///
        /// <para><see cref="BuildManager"/> holds both as public fields and is the only thing in the base game that
        /// reads them - <c>BuildUpdate_Grid</c>, <c>BuildUpdate_ProceduralGrid</c> and <c>BuildUpdate_Surface</c> all
        /// do <c>NetworkSingleton&lt;BuildManager&gt;.Instance.RotateLeftAction.action.WasPressedThisFrame()</c>. So
        /// the phone asks the same object the build ghost asks, and a player who rebinds the key in the game's own
        /// options moves both.</para>
        ///
        /// <para>This used to search every loaded <see cref="InputActionReference"/> for an asset whose NAME ended in
        /// "/RotateLeft". The field is called RotateLeftAction; the asset behind it is named whatever the designer
        /// typed, and it is not that - so the search never matched, on any build, and the rotate keys have never
        /// worked. It also cost a full <c>Resources.FindObjectsOfTypeAll</c> sweep to fail. Reading the field is one
        /// property access and cannot be wrong about a name.</para>
        /// </summary>
        internal static InputActionReference RotateAction(bool left)
        {
            // Not cached. The singleton is one static lookup and the field access is a pointer offset, which is
            // cheaper than the branch that would decide whether a cache is still valid - and a cache would have to
            // be invalidated on every scene change, which is exactly how the old lookup got stuck.
            if (!NetworkSingleton<BuildManager>.InstanceExists) return null;

            BuildManager manager = NetworkSingleton<BuildManager>.Instance;
            if (manager == null) return null;

            return left ? manager.RotateLeftAction : manager.RotateRightAction;
        }

        /// <summary>
        /// A new scene took the strip with it, so nothing is on screen any more whatever this thought.
        ///
        /// Only the flag is cleared. The module itself survives a scene change on purpose (it is marked
        /// DontDestroyOnLoad) - rebuilding it would mean resolving the actions again for no gain, and the actions do
        /// not change with the scene.
        /// </summary>
        internal static void SceneChanged() => _up = false;

        private static void WarnOnce(string reason)
        {
            if (_warned) return;
            _warned = true;
            Core.Log?.Warning($"[Sideload] no turn hint in the key strip - {reason}. Turning still works.");
        }
    }
}
