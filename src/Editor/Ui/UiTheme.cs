using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.U2D;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// Every pixel the editor draws comes from the running game: the TMP font off the HUD and
    /// the chrome sprites out of the UIAtlas. Nothing is shipped on disk.
    ///
    /// The HUD is rebuilt on every world load, so the cache is keyed on the live Hud object.
    /// SpriteAtlas.GetSprite hands out a copy, so the copies are destroyed on rebuild.
    /// </summary>
    internal static class UiTheme
    {
        // Parchment and wood, taken from how the vanilla windows look.
        public static readonly Color Text = new Color32(0xE6, 0xDC, 0xC8, 0xFF);
        public static readonly Color TextDim = new Color32(0x9E, 0x93, 0x80, 0xFF);
        public static readonly Color Accent = new Color32(0xFF, 0xB4, 0x4C, 0xFF);
        public static readonly Color Warn = new Color32(0xE8, 0x6A, 0x4A, 0xFF);
        public static readonly Color Good = new Color32(0x8C, 0xD0, 0x7A, 0xFF);
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.65f);
        public static readonly Color Inset = new Color(1f, 1f, 1f, 0.85f);
        public static readonly Color Viewport = new Color(0.06f, 0.07f, 0.09f, 0.96f);

        public static TMP_FontAsset Font { get; private set; }
        public static Material FontMaterial { get; private set; }

        public static Sprite Panel { get; private set; }         // woodpanel_trophys
        public static Sprite PanelBkg { get; private set; }      // panel_bkg
        public static Sprite Button { get; private set; }
        public static Sprite ButtonHighlight { get; private set; }
        public static Sprite ButtonPressed { get; private set; }
        public static Sprite TextField { get; private set; }
        public static Sprite ItemBackground { get; private set; }
        public static Sprite Sunken { get; private set; }

        /// <summary>Goes up on every rebuild, so anything built from the theme can notice.</summary>
        public static int Generation { get; private set; }

        public static bool Ready => Font != null;

        private static Hud _builtFrom;
        private static readonly List<Sprite> Copies = new List<Sprite>();

        /// <summary>True once the font and sprites are cached for the current world.</summary>
        public static bool Ensure()
        {
            var hud = Hud.instance;
            if (hud == null || hud.m_hoverName == null)
            {
                return false;
            }

            if (Font != null && _builtFrom == hud)
            {
                return true;
            }

            Clear();

            Font = hud.m_hoverName.font;
            FontMaterial = hud.m_hoverName.fontSharedMaterial;

            var atlas = Resources.FindObjectsOfTypeAll<SpriteAtlas>().FirstOrDefault(a => a.name == "UIAtlas");
            if (atlas == null)
            {
                ValheimTomrerPlugin.Log.LogWarning("UIAtlas not found: the editor window will draw without chrome.");
            }

            Panel = Take(atlas, "woodpanel_trophys");
            PanelBkg = Take(atlas, "panel_bkg");
            Button = Take(atlas, "button");
            ButtonHighlight = Take(atlas, "button_highlight");
            ButtonPressed = Take(atlas, "button_pressed");
            TextField = Take(atlas, "text_field");
            ItemBackground = Take(atlas, "item_background");
            Sunken = Take(atlas, "sunken");

            _builtFrom = hud;
            Generation++;
            ValheimTomrerPlugin.Log.LogInfo(
                $"editor theme ready | font={(Font != null ? Font.name : "none")} | sprites={Copies.Count}/8");
            return Font != null;
        }

        public static void Clear()
        {
            foreach (var copy in Copies)
            {
                if (copy != null)
                {
                    Object.Destroy(copy);
                }
            }

            Copies.Clear();
            Font = null;
            FontMaterial = null;
            Panel = PanelBkg = Button = ButtonHighlight = ButtonPressed = TextField = ItemBackground = Sunken = null;
            _builtFrom = null;
        }

        private static Sprite Take(SpriteAtlas atlas, string name)
        {
            if (atlas == null)
            {
                return null;
            }

            var sprite = atlas.GetSprite(name);
            if (sprite == null)
            {
                ValheimTomrerPlugin.Log.LogWarning($"UIAtlas has no sprite '{name}'.");
                return null;
            }

            // GetSprite hands out a copy, not the atlas entry, so it is ours to destroy.
            Copies.Add(sprite);
            return sprite;
        }
    }
}
