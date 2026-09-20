using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The row of buttons along the top: Build this, the file commands, undo and redo, the two
    /// view switches and the help. Save says how many errors the blueprint has and turns red, but
    /// still saves, the way the browser editor does.
    /// </summary>
    internal static class TopBar
    {
        private const float Height = 30f;
        private const float BrandWidth = 190f;
        private const float FileWidth = 260f;
        private const float Edge = 8f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;

        private static TextMeshProUGUI _file;
        private static TextMeshProUGUI _busy;
        private static TextMeshProUGUI _saveLabel;
        private static Button _save;
        private static Button _build;
        private static TextMeshProUGUI _buildLabel;
        private static Button _undo;
        private static Button _redo;
        private static Button _center;
        private static TextMeshProUGUI _boxes;
        private static TextMeshProUGUI _dots;

        /// <summary>Every button in the bar, in the order the pad walks them.</summary>
        private static readonly List<Selectable> Walk = new List<Selectable>();

        /// <summary>What the Save button reads, "Save" or "Save (2 errors)".</summary>
        public static string SaveText => _saveLabel != null ? _saveLabel.text : "";

        /// <summary>True while Save is red because the blueprint would be skipped by the game.</summary>
        public static bool SaveIsRed => _saveLabel != null && _saveLabel.color == UiTheme.Warn;

        /// <summary>What the Build button reads.</summary>
        public static string BuildText => _buildLabel != null ? _buildLabel.text : "";

        /// <summary>True while the Build button can be pressed.</summary>
        public static bool BuildEnabled => _build != null && _build.interactable;

        /// <summary>Presses the Build button, exactly as a click on it does.</summary>
        public static void ClickBuild()
        {
            if (_build != null && _build.interactable)
            {
                _build.onClick.Invoke();
            }
        }

        /// <summary>The file line: the name, a dot when it has changes, and where it lives.</summary>
        public static string FileText => _file != null ? _file.text : "";

        /// <summary>The busy chip, or "" when the editor is not doing anything.</summary>
        public static string BusyText => _busy != null && _busy.gameObject.activeSelf ? _busy.text : "";

        public static void Ensure(RectTransform host)
        {
            if (host == null || (_host == host && _root != null && _generation == UiTheme.Generation))
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Build(host);
        }

        public static void Tick()
        {
            if (_root == null)
            {
                return;
            }

            EditorCommands.Tick();
            var document = EditorState.Document;
            _file.text = FileLine(document);

            var errors = EditorCommands.Errors;
            _saveLabel.text = errors == 0 ? "Save" : $"Save ({errors} error{(errors == 1 ? "" : "s")})";
            _saveLabel.color = errors == 0 ? UiTheme.Text : UiTheme.Warn;
            _save.interactable = document != null;

            _build.interactable = document != null && document.Pieces.Count > 0;
            _buildLabel.color = _build.interactable ? UiTheme.Accent : UiTheme.TextDim;

            _undo.interactable = document != null && document.CanUndo;
            _redo.interactable = document != null && document.CanRedo;
            _center.interactable = document != null && !document.HasSections && document.Pieces.Count > 0;

            _boxes.color = EditorState.PieceBoxesOn ? UiTheme.Accent : UiTheme.Text;
            _dots.color = EditorState.SnapDotsOn ? UiTheme.Accent : UiTheme.Text;

            var busy = EditorCommands.Busy;
            _busy.gameObject.SetActive(busy != null);
            if (busy != null)
            {
                _busy.text = busy + "…";
            }
        }

        /// <summary>The name, a dot when there are changes, and where the file is.</summary>
        private static string FileLine(BlueprintDocument document)
        {
            if (document == null)
            {
                return "no blueprint open";
            }

            var name = string.IsNullOrEmpty(document.Name) ? "New blueprint" : document.Name;
            var where = document.ReadOnly
                ? "read only"
                : string.IsNullOrEmpty(document.SourcePath)
                    ? "not saved yet"
                    : Path.GetFileName(document.SourcePath);
            var dot = document.Dirty ? " ●" : "";
            return $"{name}{dot}   <color=#{ColorUtility.ToHtmlStringRGB(UiTheme.TextDim)}>{where}</color>";
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            Walk.Clear();
            _root = UiBuild.Rect("TopBarContent", host);
            UiBuild.Stretch(_root, Edge, 0f, Edge, 0f);

            var brand = UiBuild.Label("Brand", _root, "Valheim Tømrer", 22f,
                TextAlignmentOptions.Left, UiTheme.Accent);
            Pin(brand.rectTransform, 0f, BrandWidth);

            _file = UiBuild.Label("File", _root, "", 15f, TextAlignmentOptions.Left);
            Pin(_file.rectTransform, BrandWidth + 8f, FileWidth);
            _file.overflowMode = TextOverflowModes.Ellipsis;

            _busy = UiBuild.Label("Busy", _root, "", 15f, TextAlignmentOptions.Left, UiTheme.Accent);
            Pin(_busy.rectTransform, BrandWidth + FileWidth + 16f, 120f);
            _busy.gameObject.SetActive(false);

            var row = UiBuild.Row("Buttons", _root, 6f);
            row.anchorMin = new Vector2(1f, 0.5f);
            row.anchorMax = new Vector2(1f, 0.5f);
            row.pivot = new Vector2(1f, 0.5f);
            row.anchoredPosition = Vector2.zero;
            row.sizeDelta = new Vector2(1000f, Height);
            row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;

            _build = Add(row, "Build this", () => EditorCommands.BuildThis());
            _buildLabel = _build.GetComponentInChildren<TextMeshProUGUI>();
            Gap(row);

            Add(row, "New", EditorCommands.NewBlueprint);
            Add(row, "Open…", EditorCommands.OpenDialog);
            _save = Add(row, "Save", () => EditorCommands.Save());
            _saveLabel = _save.GetComponentInChildren<TextMeshProUGUI>();
            Add(row, "Save as…",
                () => Dialogs.SaveAs(EditorState.Document != null ? EditorState.Document.Name : "New blueprint"));

            Gap(row);
            _undo = Add(row, "Undo", () => EditorState.Undo());
            _redo = Add(row, "Redo", () => EditorState.Redo());
            _center = Add(row, "Center origin", EditorCommands.CenterOrigin);

            Gap(row);
            _boxes = Add(row, "Boxes", EditorCommands.ToggleBoxes)
                .GetComponentInChildren<TextMeshProUGUI>();
            _dots = Add(row, "Snap dots", EditorCommands.ToggleSnapDots)
                .GetComponentInChildren<TextMeshProUGUI>();
            Add(row, "?", EditorCommands.Help);
            Add(row, "Settings", EditorCommands.Settings);

            // The pad walks the bar left and right, around the ends.
            UiBuild.LinkRow(Walk, true);
        }

        /// <summary>A label pinned a set distance from the bar's left edge.</summary>
        private static void Pin(RectTransform rect, float left, float width)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(left, 0f);
            rect.sizeDelta = new Vector2(width, 0f);
        }

        private static Button Add(Transform row, string text, UnityEngine.Events.UnityAction click)
        {
            var button = UiBuild.Button(text, row, text, click, Height);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.fontSize = 15f;

            // Measured, not guessed: the font is the game's and the words differ in length a lot.
            var wide = label.GetPreferredValues(text, 4000f, 0f).x;
            var width = Mathf.Max(34f, (wide > 1f ? wide : text.Length * 8f) + 20f);
            var element = button.GetComponent<LayoutElement>();
            element.minWidth = width;
            element.preferredWidth = width;

            Walk.Add(button);
            return button;
        }

        /// <summary>A little air between the groups of buttons.</summary>
        private static void Gap(Transform row)
        {
            var gap = UiBuild.Rect("Gap", row);
            gap.gameObject.AddComponent<LayoutElement>().preferredWidth = 12f;
        }
    }
}
