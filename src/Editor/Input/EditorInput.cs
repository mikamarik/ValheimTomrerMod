using UnityEngine;

namespace ValheimTomrer.Editor.Input
{
    /// <summary>
    /// The editor's raw input layer: the pad read once per frame, plus the keys the editor
    /// itself owns. Keys go through ZInput, which is the game's own wrapper over the new
    /// input system, so a rebound keyboard layout keeps working.
    /// </summary>
    internal static class EditorInput
    {
        private static readonly PadReader Reader = new PadReader();
        private static int _polledFrame = -1;

        /// <summary>This frame's pad, or null when none is connected or the window lost focus.</summary>
        public static PadFrame Pad { get; private set; }

        /// <summary>Seconds since the last poll, unaffected by pause.</summary>
        public static float Dt { get; private set; }

        public static Glyphs Glyphs => Pad != null ? Glyphs.For(Pad.Ps) : Glyphs.Any;

        /// <summary>Esc, or the pad's B/circle.</summary>
        public static bool Cancel => ZInput.GetKeyDown(KeyCode.Escape) || Pressed(PadButton.Circle);

        /// <summary>Enter, or the pad's A/cross.</summary>
        public static bool Confirm => ZInput.GetKeyDown(KeyCode.Return) || Pressed(PadButton.Cross);

        public static void Poll()
        {
            if (_polledFrame == Time.frameCount)
            {
                return;
            }

            _polledFrame = Time.frameCount;
            Dt = Time.unscaledDeltaTime;
            Pad = Reader.Read();
        }

        public static bool Pressed(PadButton button) => Pad != null && Pad.Pressed(button);

        public static bool Held(PadButton button) => Pad != null && Pad.Held(button);

        /// <summary>Forget what was held, so an open or close press is not read twice.</summary>
        public static void Reset()
        {
            Reader.Reset();
            Pad = null;
            _polledFrame = -1;
        }
    }
}
