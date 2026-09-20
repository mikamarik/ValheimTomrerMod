using UnityEngine;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The one flag every input patch reads. It stays true for one extra frame after closing,
    /// so the Esc press that closed our window does not also open the pause menu.
    /// </summary>
    internal static class ModUi
    {
        private static int _closedFrame = -10;

        public static bool Open;

        /// <summary>The free camera holds the cursor, so the mouse turns the view instead of pointing.</summary>
        public static bool LockCursor;

        public static bool Blocking => Open || Time.frameCount - _closedFrame <= 1;

        public static void MarkClosed()
        {
            Open = false;
            LockCursor = false;
            _closedFrame = Time.frameCount;
        }
    }
}
