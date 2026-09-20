using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

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

        /// <summary>
        /// A text box has the keyboard, so the editing keys, Esc and the editor key are not ours
        /// this frame. The field stays selected after Esc deactivates it, so the focus flag is
        /// what has to be read, not the selection.
        /// </summary>
        public static bool Typing
        {
            get
            {
                var system = EventSystem.current;
                var selected = system != null ? system.currentSelectedGameObject : null;
                var field = selected != null ? selected.GetComponent<TMP_InputField>() : null;
                return field != null && field.isFocused;
            }
        }

        public static void MarkClosed()
        {
            Open = false;
            LockCursor = false;
            _closedFrame = Time.frameCount;
        }
    }
}
