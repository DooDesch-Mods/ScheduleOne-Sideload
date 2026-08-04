using System;
using System.Reflection;
using HarmonyLib;
using Il2CppTMPro;

namespace Sideload.Input
{
    /// <summary>
    /// Keeps TextMeshPro's caret off the keys a page claimed.
    ///
    /// A declared key still reaches the field: Sideload polls the keyboard, it does not intercept the EventSystem, so
    /// nothing stops TMP_InputField from also acting on the press. For most keys that is harmless - Tab and the
    /// function row do nothing to a single-line field, and Ctrl+letter combinations TMP does not know about fall
    /// through. The exceptions are the four that move the caret, and they are exactly the ones a list-walking page
    /// wants: Up, Down, Home and End. Without this, pressing Up to reach the previous command also drags the caret to
    /// the start of the line, and the next character typed lands in front of the word.
    ///
    /// <para>Left and Right are deliberately NOT guarded. They are the one pair where the field's own behaviour is
    /// still wanted alongside the page's - a page that watches Right to accept an inline completion still wants Right
    /// to move the caret the rest of the time.</para>
    ///
    /// <para>Patched by hand rather than by attribute, and each target independently: TMP's protected overload set
    /// has changed between Unity versions, and a missing one must cost this guard, not the whole mod. Sideload's
    /// PatchAll also carries HomeScreenPatch, without which no app reaches the phone at all.</para>
    /// </summary>
    internal static class TmpCaretGuard
    {
        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipUp));
            Patch(harmony, "MoveUp", prefix, typeof(bool));
            Patch(harmony, "MoveUp", prefix, typeof(bool), typeof(bool));

            prefix = new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipDown));
            Patch(harmony, "MoveDown", prefix, typeof(bool));
            Patch(harmony, "MoveDown", prefix, typeof(bool), typeof(bool));

            Patch(harmony, "MoveTextStart", new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipHome)), typeof(bool));
            Patch(harmony, "MoveTextEnd", new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipEnd)), typeof(bool));

            // Backspace is the one key here that TMP handles WITHOUT looking at modifiers: Ctrl+Backspace deletes a
            // single character, the same as a bare one. A page that wants the Windows behaviour - delete the word -
            // has to do it itself, and TMP has to be stopped from eating a character on the way past.
            Patch(harmony, "Backspace", new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipBackspace)));
            Patch(harmony, "DeleteKey", new HarmonyMethod(typeof(TmpCaretGuard), nameof(SkipDelete)));
        }

        private static void Patch(HarmonyLib.Harmony harmony, string name, HarmonyMethod prefix, params Type[] parameters)
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(TMP_InputField), name, parameters);
                if (target == null)
                {
                    Core.Log?.Warning($"[Sideload] caret guard: TMP_InputField.{name}"
                                      + $"({parameters.Length} arg) not found - that key will move the caret.");
                    return;
                }

                harmony.Patch(target, prefix);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[Sideload] caret guard: patching TMP_InputField.{name} failed: {e.Message}");
            }
        }

        private static bool SkipUp(TMP_InputField __instance) => !Keys.Suppresses(__instance, "ArrowUp");

        private static bool SkipDown(TMP_InputField __instance) => !Keys.Suppresses(__instance, "ArrowDown");

        private static bool SkipHome(TMP_InputField __instance) => !Keys.Suppresses(__instance, "Home");

        private static bool SkipEnd(TMP_InputField __instance) => !Keys.Suppresses(__instance, "End");

        private static bool SkipBackspace(TMP_InputField __instance) => !Keys.Suppresses(__instance, "Backspace");

        private static bool SkipDelete(TMP_InputField __instance) => !Keys.Suppresses(__instance, "Delete");
    }
}
