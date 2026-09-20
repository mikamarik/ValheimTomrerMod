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

        /// <summary>
        /// The button the UI has selected inside our window, or null. A pad's cross presses it
        /// through the game's own UI module, so anything we also bind to cross has to stand back
        /// while it is there, or one press fires twice.
        /// </summary>
        public static GameObject Selected
        {
            get
            {
                var system = EventSystem.current;
                var selected = system != null ? system.currentSelectedGameObject : null;
                if (selected == null || !selected.activeInHierarchy)
                {
                    return null;
                }

                var root = EditorWindow.Root;
                return root != null && selected.transform.IsChildOf(root)
                    && selected.GetComponent<UnityEngine.UI.Selectable>() != null
                    ? selected
                    : null;
            }
        }

        public static bool HasSelection => Selected != null;

        /// <summary>Lets go of our selected button, so the pad aims the pane instead.</summary>
        public static void ClearSelection()
        {
            if (Selected != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
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
