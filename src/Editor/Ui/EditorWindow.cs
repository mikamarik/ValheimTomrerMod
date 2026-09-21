using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The editor's canvas and its five empty regions. Built the first time it is opened and
    /// again after a world load, because the canvas dies with the scene.
    ///
    /// The recipe is the vanilla one: own Canvas with overrideSorting, CanvasScaler (reference
    /// pixels per unit 50) before GuiScaler, CanvasGroup before UIGroupHandler. Sort order 950
    /// puts it over the inventory and the store, under the centre messages and the pause menu.
    /// </summary>
    internal static class EditorWindow
    {
        public const int SortOrder = 950;
        public const int GroupPriority = 5;

        private const float Margin = 24f;   // window to screen edge
        private const float Pad = 12f;      // window frame to regions
        private const float Gap = 8f;       // region to region
        private const float TopBarHeight = 44f;
        private const float StatusBarHeight = 26f;
        private const float LeftWidth = 300f;
        private const float RightWidth = 340f;
        private const float BlueprintHeight = 368f;
        private const float SelectionHeight = 252f;

        private static GameObject _root;
        private static int _generation = -1;
        private static TextMeshProUGUI[] _leftTabs;

        /// <summary>The whole canvas. Dialogs and toasts hang here, over everything else.</summary>
        public static RectTransform Root { get; private set; }

        public static RectTransform TopBar { get; private set; }
        public static RectTransform LeftPanel { get; private set; }
        public static RectTransform ViewportHost { get; private set; }
        public static RectTransform RightPanel { get; private set; }
        public static RectTransform StatusBar { get; private set; }
        public static TextMeshProUGUI StatusText { get; private set; }

        /// <summary>The left panel's two tabs: the piece palette and the open blueprint's pieces.</summary>
        public static RectTransform PalettePane { get; private set; }

        public static RectTransform PieceListPane { get; private set; }

        /// <summary>0 = Pieces, 1 = In blueprint.</summary>
        public static int LeftTab { get; private set; }

        /// <summary>The right panel's three regions, top to bottom.</summary>
        public static RectTransform BlueprintPane { get; private set; }

        public static RectTransform SelectionPane { get; private set; }

        public static RectTransform ChecksPane { get; private set; }

        public static bool Visible => _root != null && _root.activeSelf;

        /// <summary>Builds the window if it is missing. False when the game is not ready for it.</summary>
        public static bool Ensure()
        {
            if (!UiTheme.Ensure())
            {
                return false;
            }

            if (_root != null && _generation == UiTheme.Generation)
            {
                return true;
            }

            Destroy();

            var parent = Hud.instance != null ? Hud.instance.transform.parent : null;
            if (parent == null)
            {
                return false;
            }

            _root = CreateRoot("ValheimTomrerEditor", SortOrder, parent);
            Root = (RectTransform)_root.transform;
            _generation = UiTheme.Generation;
            Build((RectTransform)_root.transform);
            ValheimTomrerPlugin.Log.LogInfo("editor window built");
            return true;
        }

        public static void Show(bool visible)
        {
            if (_root != null)
            {
                _root.SetActive(visible);
            }
        }

        public static void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            _root = null;
            Root = null;
            _generation = -1;
            TopBar = LeftPanel = ViewportHost = RightPanel = StatusBar = null;
            PalettePane = PieceListPane = null;
            BlueprintPane = SelectionPane = ChecksPane = null;
            _leftTabs = null;
            StatusText = null;
        }

        /// <summary>Switches the left panel between the palette and the blueprint's piece list.</summary>
        public static void SetLeftTab(int tab)
        {
            LeftTab = Mathf.Clamp(tab, 0, 1);
            if (PalettePane == null || _leftTabs == null)
            {
                return;
            }

            PalettePane.gameObject.SetActive(LeftTab == 0);
            PieceListPane.gameObject.SetActive(LeftTab == 1);
            for (var i = 0; i < _leftTabs.Length; i++)
            {
                _leftTabs[i].color = i == LeftTab ? UiTheme.Accent : UiTheme.Text;
            }
        }

        /// <summary>The canvas recipe the game itself uses (SessionPlayerList). Order matters twice.</summary>
        private static GameObject CreateRoot(string name, int order, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            UiBuild.Stretch((RectTransform)go.transform);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = order;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1
                | AdditionalCanvasShaderChannels.Normal
                | AdditionalCanvasShaderChannels.Tangent;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 50f;
            go.AddComponent<GuiScaler>();          // reads the CanvasScaler in Awake
            go.AddComponent<GraphicRaycaster>();

            go.AddComponent<CanvasGroup>();        // read by UIGroupHandler in Awake
            go.AddComponent<UIGroupHandler>().m_groupPriority = GroupPriority;
            return go;
        }

        /// <summary>
        /// Five regions at their real sizes: 300 | rest | 340 between a 44 px top bar and a 26 px
        /// status bar. The middle one holds the 3D pane (ViewportHost).
        /// </summary>
        private static void Build(RectTransform root)
        {
            // Dims the world and eats clicks that miss the window.
            UiBuild.Stretch(UiBuild.Panel("Backdrop", root, null, UiTheme.Backdrop).rectTransform);

            var window = UiBuild.Panel("Window", root, UiTheme.Panel);
            UiBuild.Stretch(window.rectTransform, Margin, Margin, Margin, Margin);
            var frame = window.rectTransform;

            var bandTop = Pad + TopBarHeight + Gap;
            var bandBottom = Pad + StatusBarHeight + Gap;

            TopBar = Region("TopBar", frame, UiTheme.PanelWood, UiTheme.PanelInterior);
            TopBar.anchorMin = new Vector2(0f, 1f);
            TopBar.anchorMax = new Vector2(1f, 1f);
            TopBar.offsetMin = new Vector2(Pad, -Pad - TopBarHeight);
            TopBar.offsetMax = new Vector2(-Pad, -Pad);

            StatusBar = Region("StatusBar", frame, UiTheme.PanelWood, UiTheme.PanelInterior);
            StatusBar.anchorMin = new Vector2(0f, 0f);
            StatusBar.anchorMax = new Vector2(1f, 0f);
            StatusBar.offsetMin = new Vector2(Pad, Pad);
            StatusBar.offsetMax = new Vector2(-Pad, Pad + StatusBarHeight);

            LeftPanel = Region("LeftPanel", frame, UiTheme.PanelWood, UiTheme.PanelInterior);
            LeftPanel.anchorMin = new Vector2(0f, 0f);
            LeftPanel.anchorMax = new Vector2(0f, 1f);
            LeftPanel.offsetMin = new Vector2(Pad, bandBottom);
            LeftPanel.offsetMax = new Vector2(Pad + LeftWidth, -bandTop);

            RightPanel = Region("RightPanel", frame, UiTheme.PanelWood, UiTheme.PanelInterior);
            RightPanel.anchorMin = new Vector2(1f, 0f);
            RightPanel.anchorMax = new Vector2(1f, 1f);
            RightPanel.offsetMin = new Vector2(-Pad - RightWidth, bandBottom);
            RightPanel.offsetMax = new Vector2(-Pad, -bandTop);

            // Flat dark first, then the sunken frame on top: the frame sprite has a see-through
            // middle, so on its own the window's wood would shine through the pane.
            ViewportHost = Region("ViewportHost", frame, null, UiTheme.Viewport);
            ViewportHost.anchorMin = Vector2.zero;
            ViewportHost.anchorMax = Vector2.one;
            ViewportHost.offsetMin = new Vector2(Pad + LeftWidth + Gap, bandBottom);
            ViewportHost.offsetMax = new Vector2(-Pad - RightWidth - Gap, -bandTop);
            var sunken = UiBuild.Panel("Frame", ViewportHost, UiTheme.Sunken);
            sunken.raycastTarget = false;
            UiBuild.Stretch(sunken.rectTransform);

            StatusText = Caption(StatusBar, "F7 or Esc closes", 16f, TextAlignmentOptions.Left, UiTheme.TextDim);
            BuildLeftTabs();
            BuildRightPanes();
        }

        /// <summary>
        /// The right panel, top to bottom: the blueprint and its build card, the selection, then
        /// the problem list, which takes whatever is left.
        /// </summary>
        private static void BuildRightPanes()
        {
            const float Edge = 6f;
            var blueprintTop = Edge;
            var selectionTop = blueprintTop + BlueprintHeight + Gap;
            var checksTop = selectionTop + SelectionHeight + Gap;

            BlueprintPane = Band("BlueprintPane", blueprintTop, BlueprintHeight, Edge);
            SelectionPane = Band("SelectionPane", selectionTop, SelectionHeight, Edge);

            ChecksPane = UiBuild.Rect("ChecksPane", RightPanel);
            ChecksPane.anchorMin = Vector2.zero;
            ChecksPane.anchorMax = Vector2.one;
            ChecksPane.offsetMin = new Vector2(Edge, Edge);
            ChecksPane.offsetMax = new Vector2(-Edge, -checksTop);
        }

        /// <summary>One region of fixed height, measured down from the right panel's top edge.</summary>
        private static RectTransform Band(string name, float top, float height, float edge)
        {
            var rect = UiBuild.Rect(name, RightPanel);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(edge, -top - height);
            rect.offsetMax = new Vector2(-edge, -top);
            return rect;
        }

        /// <summary>Two tabs over the left panel, and an empty pane under each.</summary>
        private static void BuildLeftTabs()
        {
            const float Height = 26f;
            var names = new[] { "Pieces", "In blueprint" };
            _leftTabs = new TextMeshProUGUI[names.Length];

            var bar = UiBuild.Rect("Tabs", LeftPanel);
            bar.anchorMin = new Vector2(0f, 1f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(0.5f, 1f);
            bar.offsetMin = new Vector2(6f, -6f - Height);
            bar.offsetMax = new Vector2(-6f, -6f);

            var walk = new List<Selectable>();
            for (var i = 0; i < names.Length; i++)
            {
                var tab = i;
                var button = UiBuild.Button(names[i], bar, names[i], () => SetLeftTab(tab), Height);
                walk.Add(button);
                var rect = (RectTransform)button.transform;
                rect.anchorMin = new Vector2(i / (float)names.Length, 0f);
                rect.anchorMax = new Vector2((i + 1) / (float)names.Length, 1f);
                rect.offsetMin = new Vector2(i == 0 ? 0f : 2f, 0f);
                rect.offsetMax = Vector2.zero;
                _leftTabs[i] = button.GetComponentInChildren<TextMeshProUGUI>();
                _leftTabs[i].fontSize = 15f;
            }

            UiBuild.LinkRow(walk);
            PalettePane = Pane("PalettePane", Height);
            PieceListPane = Pane("PieceListPane", Height);

            // Built again after a world load: the tab that was open stays open.
            SetLeftTab(LeftTab);
        }

        private static RectTransform Pane(string name, float tabHeight)
        {
            var pane = UiBuild.Rect(name, LeftPanel);
            UiBuild.Stretch(pane, 0f, 0f, 0f, tabHeight + 10f);
            return pane;
        }

        private static RectTransform Region(string name, Transform parent, Sprite sprite, Color tint)
        {
            return UiBuild.Panel(name, parent, sprite, tint).rectTransform;
        }

        private static TextMeshProUGUI Caption(RectTransform parent, string text, float size, TextAlignmentOptions align, Color color)
        {
            var label = UiBuild.Label("Caption", parent, text, size, align, color);
            UiBuild.Stretch(label.rectTransform, 10f, 4f, 10f, 4f);
            return label;
        }
    }
}
