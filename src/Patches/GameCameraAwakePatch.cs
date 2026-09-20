using HarmonyLib;
using ValheimTomrer.Editor;

namespace ValheimTomrer.Patches
{
    /// <summary>
    /// Takes the editor's layer out of what the player's camera draws. The game ships it as
    /// 0xFFFFFFFF, "draw everything", so without this the editor's own copies would show up in
    /// the world. Nothing in the game writes cullingMask again after Awake (checked against
    /// Valheim 1.0.15), so once is enough.
    /// </summary>
    [HarmonyPatch(typeof(GameCamera), "Awake")]
    internal static class GameCameraAwakePatch
    {
        private static void Postfix(GameCamera __instance)
        {
            if (!ValheimTomrerPlugin.ModEnabled.Value)
            {
                return;
            }

            var mask = ~(1 << EditorConfig.Layer);
            if (__instance.m_camera != null)
            {
                __instance.m_camera.cullingMask &= mask;
            }

            if (__instance.m_skyCamera != null)
            {
                __instance.m_skyCamera.cullingMask &= mask;
            }

            ValheimTomrerPlugin.Log.LogInfo($"main camera drops layer {EditorConfig.Layer}"
                + $" (mask 0x{(__instance.m_camera != null ? __instance.m_camera.cullingMask : 0):X8})");
        }
    }
}
