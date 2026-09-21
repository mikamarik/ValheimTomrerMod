#if DEBUG
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimTomrer.Dev
{
    /// <summary>
    /// Debug builds only. Makes the test world leave the character alone.
    ///
    /// Without this a run is a lottery: a greydwarf wanders in, hits the structure under test,
    /// and a support check fails for a reason that has nothing to do with the code. Four levers,
    /// all of them the game's own:
    ///
    /// <list type="bullet">
    ///   <item>no monster thinks, a prefix on <see cref="BaseAI.UpdateAI"/> returns false, and every
    ///   AI that derives from it drops out on the first line;</item>
    ///   <item>no new world spawns, <c>SpawnSystem.m_nospawn</c>, the same switch the nospawn command flips;</item>
    ///   <item>no raids, the event chance goes to zero and any running event is reset;</item>
    ///   <item>whatever is already standing around is despawned once, at the start.</item>
    /// </list>
    ///
    /// Nothing here is in a release build. The whole file is behind DEBUG, like AutoTest itself.
    /// </summary>
    internal static class AutoTestPeace
    {
        /// <summary>True once <see cref="Apply"/> has run. The AI prefix reads it every frame.</summary>
        internal static bool On;

        /// <summary>
        /// Turns the world quiet. Returns how many creatures were removed, so the caller can log it.
        /// Safe to call more than once.
        /// </summary>
        internal static int Apply()
        {
            On = true;
            SpawnSystem.m_nospawn = true;

            if (RandEventSystem.instance != null)
            {
                RandEventSystem.instance.m_eventChance = 0f;
                RandEventSystem.instance.m_eventIntervalMin = 999999f;
                RandEventSystem.instance.ResetRandomEvent();
            }

            return Despawn();
        }

        /// <summary>
        /// Removes every creature that is not the player. Tamed animals go too, a test world has none
        /// worth keeping. The list is copied first, because destroying unregisters as we walk it.
        /// </summary>
        internal static int Despawn()
        {
            if (ZNetScene.instance == null)
            {
                return 0;
            }

            var all = new List<Character>(Character.GetAllCharacters());
            var gone = 0;

            foreach (var creature in all)
            {
                if (creature == null || creature.IsPlayer())
                {
                    continue;
                }

                ZNetScene.instance.Destroy(creature.gameObject);
                gone++;
            }

            return gone;
        }

        /// <summary>
        /// Every AI in the game overrides this and bails out when the base call says false,
        /// so one prefix here is enough to stop all of them.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.UpdateAI))]
        private static class BaseAIUpdateAIPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!On)
                {
                    return true;
                }

                __result = false;
                return false;
            }
        }
    }
}
#endif
