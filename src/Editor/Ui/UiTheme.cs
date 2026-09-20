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
        // Text is white. Not off-white, not parchment, not a tint of the wood behind it.
        public static readonly Color Text = Color.white;

        /// <summary>Captions, footers, hints and placeholders. Also white, on purpose.</summary>
        public static readonly Color TextDim = Color.white;
        public static readonly Color Accent = new Color32(0xFF, 0xB4, 0x4C, 0xFF);

        /// <summary>The label on an orange chip or button. Dark text on that orange is unreadable.</summary>
        public static readonly Color TextOnAccent = Color.white;
        public static readonly Color Warn = new Color32(0xE8, 0x6A, 0x4A, 0xFF);
        public static readonly Color Good = new Color32(0x8C, 0xD0, 0x7A, 0xFF);
        public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.65f);
        public static readonly Color Inset = new Color(1f, 1f, 1f, 0.85f);

        /// <summary>The panel interiors: the game's wood at 70 per cent, so light text still reads.</summary>
        public static readonly Color PanelInterior = new Color(0.70f, 0.70f, 0.70f, 1f);
        public static readonly Color Viewport = new Color(0.06f, 0.07f, 0.09f, 0.96f);

        public static TMP_FontAsset Font { get; private set; }
        public static Material FontMaterial { get; private set; }

        /// <summary>
        /// The same, with a black outline and a shadow. For the text drawn straight over the 3D
        /// picture, where the background is whatever the camera happens to be looking at.
        /// </summary>
        public static Material FontOutlined { get; private set; }

        public static Sprite Panel { get; private set; }         // woodpanel_trophys
        public static Sprite PanelBkg { get; private set; }      // panel_bkg
        public static Sprite PanelWood { get; private set; }     // woodpanel_400_tileable
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
            FontMaterial = OwnTextMaterial(hud.m_hoverName.fontSharedMaterial, false);
            FontOutlined = OwnTextMaterial(hud.m_hoverName.fontSharedMaterial, true);

            var atlas = Resources.FindObjectsOfTypeAll<SpriteAtlas>().FirstOrDefault(a => a.name == "UIAtlas");
            if (atlas == null)
            {
                ValheimTomrerPlugin.Log.LogWarning("UIAtlas not found: the editor window will draw without chrome.");
            }

            Panel = Take(atlas, "woodpanel_trophys");
            PanelBkg = Take(atlas, "panel_bkg");
            PanelWood = Take(atlas, "woodpanel_400_tileable");
            Button = Take(atlas, "button");
            ButtonHighlight = Take(atlas, "button_highlight");
            ButtonPressed = Take(atlas, "button_pressed");
            TextField = Take(atlas, "text_field");
            ItemBackground = Take(atlas, "item_background");
            Sunken = Take(atlas, "sunken");

            _builtFrom = hud;
            Generation++;
            ValheimTomrerPlugin.Log.LogInfo(
                $"editor theme ready | font={(Font != null ? Font.name : "none")} | sprites={Copies.Count}/9");
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
            PadGlyphs.Clear();
            Font = null;
            if (FontMaterial != null)
            {
                Object.Destroy(FontMaterial);
            }

            if (FontOutlined != null)
            {
                Object.Destroy(FontOutlined);
            }

            FontMaterial = null;
            FontOutlined = null;
            Panel = PanelBkg = PanelWood = Button = ButtonHighlight = ButtonPressed = TextField
                = ItemBackground = Sunken = null;
            _builtFrom = null;
        }

        /// <summary>
        /// The editor's own copies of the HUD's text material.
        ///
        /// TMP multiplies the label's colour by the material's face colour and then draws the
        /// material's outline and shadow over the glyph. The hover-name material the HUD uses is
        /// tuned for white names over a dark world: a fat black outline and a soft shadow. At the
        /// 12 to 16 point sizes the panels use, that eats the strokes and every label reads grey
        /// however light its colour is.
        ///
        /// So there are two of our own, both with a white face at full strength so the label's
        /// colour is the only thing deciding how it looks:
        /// <list type="bullet">
        /// <item>plain, no outline and no shadow, for text on a panel, where the wood behind it
        /// is dark and known.</item>
        /// <item>outlined, a thin black edge and a shadow, for text over the 3D picture, where the
        /// background is whatever the camera is pointed at and can be as light as the sky.</item>
        /// </list>
        /// Both are copies of a loaded material, made at runtime. Nothing is written to disk.
        /// </summary>
        private static Material OwnTextMaterial(Material source, bool outlined)
        {
            if (source == null)
            {
                return null;
            }

            var mine = new Material(source) { name = outlined ? "ValheimTomrerTextOutlined" : "ValheimTomrerText" };
            Set(mine, ShaderUtilities.ID_FaceColor, Color.white);
            Set(mine, ShaderUtilities.ID_GlowColor, new Color(0f, 0f, 0f, 0f));
            Set(mine, ShaderUtilities.ID_GlowPower, 0f);
            mine.DisableKeyword(ShaderUtilities.Keyword_Glow);

            if (outlined)
            {
                Set(mine, ShaderUtilities.ID_FaceDilate, 0.1f);
                Set(mine, ShaderUtilities.ID_OutlineColor, Color.black);
                Set(mine, ShaderUtilities.ID_OutlineWidth, 0.2f);
                Set(mine, ShaderUtilities.ID_OutlineSoftness, 0f);
                Set(mine, ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0.65f));
                Set(mine, ShaderUtilities.ID_UnderlayOffsetX, 0.5f);
                Set(mine, ShaderUtilities.ID_UnderlayOffsetY, -0.5f);
                Set(mine, ShaderUtilities.ID_UnderlayDilate, 0.1f);
                Set(mine, ShaderUtilities.ID_UnderlaySoftness, 0.2f);
                mine.EnableKeyword(ShaderUtilities.Keyword_Outline);
                mine.EnableKeyword(ShaderUtilities.Keyword_Underlay);
                return mine;
            }

            Set(mine, ShaderUtilities.ID_FaceDilate, 0.05f);
            Set(mine, ShaderUtilities.ID_OutlineColor, new Color(0f, 0f, 0f, 0f));
            Set(mine, ShaderUtilities.ID_OutlineWidth, 0f);
            Set(mine, ShaderUtilities.ID_OutlineSoftness, 0f);
            Set(mine, ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0f));
            Set(mine, ShaderUtilities.ID_UnderlayOffsetX, 0f);
            Set(mine, ShaderUtilities.ID_UnderlayOffsetY, 0f);
            Set(mine, ShaderUtilities.ID_UnderlayDilate, 0f);
            Set(mine, ShaderUtilities.ID_UnderlaySoftness, 0f);
            mine.DisableKeyword(ShaderUtilities.Keyword_Outline);
            mine.DisableKeyword(ShaderUtilities.Keyword_Underlay);
            return mine;
        }

        /// <summary>The distance-field shaders come in variants, so a property can be missing.</summary>
        private static void Set(Material material, int id, float value)
        {
            if (material.HasProperty(id))
            {
                material.SetFloat(id, value);
            }
        }

        private static void Set(Material material, int id, Color value)
        {
            if (material.HasProperty(id))
            {
                material.SetColor(id, value);
            }
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
