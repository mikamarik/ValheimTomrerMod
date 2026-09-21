using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using ValheimTomrer.Editor.Input;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The game's own controller icons. Valheim keeps them in a TextMeshPro sprite asset named
    /// gamepad_glyphs and writes them into its own texts as
    /// <c>&lt;sprite="gamepad_glyphs" name="button_a"&gt;</c>. The editor wants them as pictures it
    /// can size and line up next to a key cap, so this hands out a Sprite instead of a tag.
    ///
    /// Nothing comes off disk. The asset is already loaded, and a sprite cut out of its sheet is
    /// made at runtime, the same way the editor clones a prefab's mesh.
    /// </summary>
    internal static class PadGlyphs
    {
        private static TMP_SpriteAsset _asset;
        private static bool _looked;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        private static readonly List<Sprite> Cut = new List<Sprite>();

        /// <summary>True when the game's icons were found, so a hint can show pictures.</summary>
        public static bool Ready => Asset() != null;

        /// <summary>The picture for a button, in the wording of the pad in hand, or null.</summary>
        public static Sprite Of(PadButton button)
        {
            var ps = EditorInput.Pad == null || EditorInput.Pad.Ps;
            switch (button)
            {
                case PadButton.Cross: return First(ps ? "button_cross" : "button_a");
                case PadButton.Circle: return First(ps ? "button_circle" : "button_b");
                case PadButton.Square: return First(ps ? "button_square" : "button_x");
                case PadButton.Triangle: return First(ps ? "button_triangle" : "button_y");
                case PadButton.L1: return First(ps ? "button_l1" : "button_lb", "lshoulder");
                case PadButton.R1: return First(ps ? "button_r1" : "button_rb", "rshoulder");
                case PadButton.L2: return First(ps ? "button_l2" : "button_lt", "ltrigger");
                case PadButton.R2: return First(ps ? "button_r2" : "button_rt", "rtrigger");
                case PadButton.Options: return First(ps ? "button_options" : "button_start", "button_plus");
                case PadButton.L3: return First(ps ? "button_l3" : "button_ls", "lstick_pressed");
                case PadButton.R3: return First(ps ? "button_r3" : "button_rs", "rstick_pressed");
                case PadButton.Up: return First("dpad_up");
                case PadButton.Down: return First("dpad_down");
                case PadButton.Left: return First("dpad_left");
                case PadButton.Right: return First("dpad_right");
                default: return null;
            }
        }

        /// <summary>The whole D-pad, for a "move with these" hint.</summary>
        public static Sprite Dpad => First("dpad", "dpad_blank");

        /// <summary>A stick, left or right.</summary>
        public static Sprite Stick(bool right) => right ? First("rstick", "button_rs") : First("lstick", "button_ls");

        /// <summary>Drops the cut sprites. Called when the theme is rebuilt after a world load.</summary>
        public static void Clear()
        {
            foreach (var sprite in Cut)
            {
                if (sprite != null)
                {
                    Object.Destroy(sprite);
                }
            }

            Cut.Clear();
            Cache.Clear();
            _asset = null;
            _looked = false;
        }

        private static Sprite First(params string[] names)
        {
            foreach (var name in names)
            {
                var sprite = Find(name);
                if (sprite != null)
                {
                    return sprite;
                }
            }

            return null;
        }

        private static Sprite Find(string name)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var asset = Asset();
            var sprite = asset != null ? FromAsset(asset, name) : null;
            Cache[name] = sprite;
            return sprite;
        }

        private static TMP_SpriteAsset Asset()
        {
            if (_looked)
            {
                return _asset;
            }

            _looked = true;
            var all = Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>();
            _asset = all.FirstOrDefault(a => a.name == "gamepad_glyphs")
                ?? all.FirstOrDefault(a => Holds(a, "button_a") || Holds(a, "button_cross"));

            if (_asset == null)
            {
                ValheimTomrerPlugin.Log.LogWarning(
                    "the game's controller icons were not found: the hints will show button names instead.");
            }

            return _asset;
        }

        private static bool Holds(TMP_SpriteAsset asset, string name)
        {
            var table = asset.spriteCharacterTable;
            return table != null && table.Any(c => c != null && c.name == name);
        }

        private static Sprite FromAsset(TMP_SpriteAsset asset, string name)
        {
            var table = asset.spriteCharacterTable;
            if (table != null)
            {
                foreach (var character in table)
                {
                    if (character == null || character.name != name)
                    {
                        continue;
                    }

                    var glyph = character.glyph as TMP_SpriteGlyph;
                    if (glyph == null)
                    {
                        continue;
                    }

                    return glyph.sprite != null ? glyph.sprite : CutOut(asset, glyph);
                }
            }

            // Assets made the old way keep their entries in a second list.
            var legacy = asset.spriteInfoList;
            if (legacy != null)
            {
                foreach (var sprite in legacy)
                {
                    if (sprite != null && sprite.name == name && sprite.sprite != null)
                    {
                        return sprite.sprite;
                    }
                }
            }

            foreach (var fallback in asset.fallbackSpriteAssets ?? new List<TMP_SpriteAsset>())
            {
                if (fallback == null || fallback == asset)
                {
                    continue;
                }

                var found = FromAsset(fallback, name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>One icon out of the sheet, when the asset kept no Sprite of its own.</summary>
        private static Sprite CutOut(TMP_SpriteAsset asset, TMP_SpriteGlyph glyph)
        {
            var sheet = asset.spriteSheet as Texture2D;
            if (sheet == null)
            {
                return null;
            }

            var rect = glyph.glyphRect;
            if (rect.width <= 0 || rect.height <= 0)
            {
                return null;
            }

            var cut = Sprite.Create(
                sheet,
                new Rect(rect.x, rect.y, rect.width, rect.height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            cut.name = "ValheimTomrerGlyph";
            Cut.Add(cut);
            return cut;
        }
    }
}
