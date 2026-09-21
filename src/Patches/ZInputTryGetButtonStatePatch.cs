using HarmonyLib;
using ValheimTomrer.Editor.Input;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Holds back from the game the pad buttons the mod's world combos use (<see cref="WorldPad"/>):
    /// the D-pad and circle while a capture is up, square and triangle while the game's modifier
    /// (L2) is held. Every GetButton, GetButtonDown and GetButtonUp of the game comes through this
    /// one private method, in Update and in FixedUpdate alike, so the jump read in FixedUpdate is
    /// held back too. The keyboard does not come through here, it is never touched.
    ///
    /// The private method, not the public wrappers: those are small enough to be inlined.
    /// </summary>
    [HarmonyPatch(typeof(ZInput), "TryGetButtonState")]
    internal static class ZInputTryGetButtonStatePatch
    {
        private static bool Prefix(string name, ref bool __result)
        {
            if (!WorldPad.HeldBack(name))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }
}
