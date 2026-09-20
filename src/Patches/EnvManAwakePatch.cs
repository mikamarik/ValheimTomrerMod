using HarmonyLib;
using ValheimTomrer.Editor;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Keeps the world's sun out of the editor's little scene. A directional light reaches
    /// everywhere, so without this the pane would be lit twice, once by the sun of whatever time
    /// of day it is outside and once by the editor's own light, and it would change colour at
    /// sunset. The editor's lights only light the editor layer, so nothing goes the other way.
    /// </summary>
    [HarmonyPatch(typeof(EnvMan), "Awake")]
    internal static class EnvManAwakePatch
    {
        private static void Postfix(EnvMan __instance)
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value || __instance.m_dirLight == null)
            {
                return;
            }

            __instance.m_dirLight.cullingMask &= ~(1 << EditorConfig.Layer);
            ValheimTomrerPlugin.Log.LogInfo($"the world's sun drops layer {EditorConfig.Layer}"
                + $" (mask 0x{__instance.m_dirLight.cullingMask:X8})");
        }
    }
}
