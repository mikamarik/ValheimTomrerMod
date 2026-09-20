namespace ValheimTomrer.Editor.Input
{
    /// <summary>
    /// Button names for on-screen hints, in the wording of the pad in hand. Same table as the
    /// Tomrer editor (src/view/gamepad.ts), with one change: cross is "×" (U+00D7), not "✕",
    /// which the game's font does not have and draws as an empty box.
    /// </summary>
    internal sealed class Glyphs
    {
        private readonly string[] _names;

        public readonly string Ls;
        public readonly string Rs;
        public readonly string Dpad;

        private Glyphs(string[] names, string ls, string rs, string dpad)
        {
            _names = names;
            Ls = ls;
            Rs = rs;
            Dpad = dpad;
        }

        public string Of(PadButton button) => _names[(int)button];

        public static readonly Glyphs Ps = new Glyphs(
            new[]
            {
                "×", "○", "□", "△", "L1", "R1", "L2", "R2", "Options", "L3", "R3",
                "D-pad up", "D-pad down", "D-pad left", "D-pad right",
            },
            "Left stick", "Right stick", "D-pad");

        public static readonly Glyphs Xbox = new Glyphs(
            new[]
            {
                "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Menu", "LS click", "RS click",
                "D-pad up", "D-pad down", "D-pad left", "D-pad right",
            },
            "Left stick", "Right stick", "D-pad");

        /// <summary>Both names, for when no pad is in use: "B / circle".</summary>
        public static readonly Glyphs Any = Merge();

        public static Glyphs For(bool playStation) => playStation ? Ps : Xbox;

        private static Glyphs Merge()
        {
            var names = new string[Ps._names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = Ps._names[i] == Xbox._names[i] ? Ps._names[i] : Xbox._names[i] + " / " + Ps._names[i];
            }

            return new Glyphs(names, Ps.Ls, Ps.Rs, Ps.Dpad);
        }
    }
}
