using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using ValheimTomrer.Editor;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Filter the wheel at its GameCamera call site. This survives JIT inlining of ZInput's
    /// small wrappers, while leaving camera tracking, cursor capture and editor input running.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    internal static class EditorCameraWheelPatch
    {
        internal static float FilterWheel(float wheel)
        {
            return ModUi.Blocking || WorldCapture.Active ? 0f : wheel;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var publicRead = AccessTools.Method(typeof(ZInput), "GetMouseScrollWheel");
            var internalRead = AccessTools.Method(typeof(ZInput), "Internal_GetMouseScrollWheel");
            var filter = AccessTools.Method(typeof(EditorCameraWheelPatch), nameof(FilterWheel));
            var reads = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if ((publicRead != null && instruction.Calls(publicRead))
                    || (internalRead != null && instruction.Calls(internalRead)))
                {
                    yield return new CodeInstruction(OpCodes.Call, filter);
                    reads++;
                }
            }

            if (reads == 0)
            {
                ValheimTomrerPlugin.Log.LogWarning("Editor camera wheel guard: no ZInput wheel read found in GameCamera.UpdateCamera. Send the BepInEx log if player zoom still changes.");
            }
            else
            {
                ValheimTomrerPlugin.Log.LogInfo("Editor camera wheel guard installed at " + reads + " GameCamera wheel read(s).");
            }
        }
    }
}
