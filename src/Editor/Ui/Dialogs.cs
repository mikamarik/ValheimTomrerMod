using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Doc;
using ValheimTomrer.Editor.Input;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The editor's windows on top of the window: open, save as, help, the unsaved-changes
    /// question and the settings. One at a time, over a dark backdrop that swallows clicks.
    ///
    /// While one is up the editor's keys are silent (<see cref="Bindings.Press"/> reads
    /// <see cref="IsOpen"/>) and Esc closes it instead of the editor.
    /// </summary>
    internal static class Dialogs
    {
        /// <summary>A blueprint name has to look like this, and must not hold "..".</summary>
        public const string NamePattern = "^[A-Za-z0-9_][A-Za-z0-9_ .-]*$";

        private const float Head = 40f;
        private const float FootHeight = 44f;
        private const float Pad = 14f;
        private const float RowHeight = 26f;

        private static readonly Regex Name = new Regex(NamePattern);

        private static RectTransform _host;
        private static RectTransform _root;
        private static RectTransform _modal;
        private static RectTransform _body;
        private static RectTransform _buttons;
        private static TextMeshProUGUI _title;
        private static int _generation = -1;

        private static readonly List<BlueprintEntry> Rows = new List<BlueprintEntry>();
        private static readonly List<TextMeshProUGUI> RowLabels = new List<TextMeshProUGUI>();

        private static Action _confirmRun;
        private static TextMeshProUGUI _note;
        private static TextMeshProUGUI _fileLine;
        private static Button _submit;

        /// <summary>"open", "saveAs", "help", "confirm", "settings", or "" when none is up.</summary>
        public static string Kind { get; private set; } = "";

        public static bool IsOpen => Kind.Length > 0;

        /// <summary>The name box of the save-as dialog.</summary>
        public static TMP_InputField NameField { get; private set; }

        /// <summary>The line under a field: why the name is refused, or that it exists already.</summary>
        public static string NoteText => _note != null && _note.gameObject.activeSelf ? _note.text : "";

        public static string TitleText => _title != null ? _title.text : "";

        /// <summary>Rows of the open dialog, kits first.</summary>
        public static int RowCount => Rows.Count;

        public static string RowText(int index)
        {
            return index >= 0 && index < RowLabels.Count ? RowLabels[index].text : "";
        }

        public static BlueprintEntry Row(int index)
        {
            return index >= 0 && index < Rows.Count ? Rows[index] : null;
        }

        /// <summary>What a click on a row does. The test calls it without a mouse.</summary>
        public static void ClickRow(int index)
        {
            var entry = Row(index);
            if (entry == null)
            {
                return;
            }

            if (entry.Error != null)
            {
                Toasts.Error(entry.Error);
                return;
            }

            EditorCommands.OpenEntry(entry);
        }

        public static void Ensure(RectTransform host)
        {
            if (host == null || (_host == host && _root != null && _generation == UiTheme.Generation))
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Kind = "";
            Build(host);
        }

        /// <summary>Enter answers the question that is up. Esc goes through Bindings.Cancel.</summary>
        public static void Tick()
        {
            if (!IsOpen || ModUi.Typing)
            {
                return;
            }

            // A selected button gets cross and Enter itself, or one press would fire twice.
            if (!ModUi.HasSelection && EditorInput.Confirm)
            {
                Submit();
            }
        }

        /// <summary>Presses the dialog's own button, the one Enter presses.</summary>
        public static void Submit()
        {
            if (_submit != null && _submit.interactable)
            {
                _submit.onClick.Invoke();
            }
        }

        /// <summary>Shuts whatever is up. True when there was something.</summary>
        public static bool Close()
        {
            if (!IsOpen)
            {
                return false;
            }

            Kind = "";
            _confirmRun = null;
            NameField = null;
            _note = null;
            _fileLine = null;
            _submit = null;
            Rows.Clear();
            RowLabels.Clear();
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
            }

            return true;
        }

        // ---------- the dialogs ----------

        /// <summary>The kits inside the mod and the player's own files, with size and date.</summary>
        public static void Open()
        {
            if (!Begin("open", "Open a blueprint", 780f, 580f))
            {
                return;
            }

            var scroll = UiBuild.Scroll("Files", _body, 2f);
            UiBuild.Stretch((RectTransform)scroll.transform);

            Heading(scroll.content, "Kits in the mod");
            AddRows(scroll.content, DocumentStore.ListKits());
            Heading(scroll.content, "Your blueprints");
            Dim(scroll.content, BlueprintLibrary.UserFolder);
            var files = DocumentStore.ListUserFiles();
            if (files.Count == 0)
            {
                Dim(scroll.content, "No files there yet. Save as puts one here.");
            }

            AddRows(scroll.content, files);
            Foot("Close", () => Close(), null, null);
        }

        /// <summary>The name a blueprint is saved under. It becomes #Name: and, slugged, the file name.</summary>
        public static void SaveAs(string initial)
        {
            if (!Begin("saveAs", "Save as", 580f, 320f))
            {
                return;
            }

            var label = UiBuild.Label("Label", _body, "Blueprint name", 16f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            Line(label.rectTransform, 0f, 20f);

            NameField = UiBuild.InputField("Name", _body, "Name", 34f);
            Line(NameField.GetComponent<RectTransform>(), 24f, 34f);
            NameField.text = initial ?? "";
            NameField.onValueChanged.AddListener(_ => CheckName());
            NameField.onSubmit.AddListener(_ => SubmitSaveAs());

            _fileLine = UiBuild.Label("File", _body, "", 14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            Line(_fileLine.rectTransform, 64f, 20f);

            _note = UiBuild.Label("Note", _body, "", 14f, TextAlignmentOptions.TopLeft, UiTheme.Accent);
            _note.enableWordWrapping = true;
            Line(_note.rectTransform, 88f, 40f);

            var hint = UiBuild.Label("Hint", _body,
                "Letters, digits, spaces, dots, dashes and underscores. Files go into the mod's blueprint folder.",
                14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            hint.enableWordWrapping = true;
            Line(hint.rectTransform, 132f, 40f);

            Foot("Cancel", () => Close(), "Save", SubmitSaveAs);
            CheckName();
            NameField.ActivateInputField();
        }

        /// <summary>Everything the mouse, the keys and the controller do.</summary>
        public static void Help()
        {
            if (!Begin("help", "Mouse, keys and controller", 1000f, 860f))
            {
                return;
            }

            var scroll = UiBuild.Scroll("Help", _body, 2f);
            UiBuild.Stretch((RectTransform)scroll.transform);

            Heading(scroll.content, "Mouse and keyboard");
            foreach (var row in Bindings.Keys)
            {
                KeyRow(scroll.content, row);
            }

            Dim(scroll.content, "On a Mac, Cmd works everywhere Ctrl does.");
            Heading(scroll.content, "Controller");
            foreach (var row in Bindings.Pad)
            {
                KeyRow(scroll.content, row);
            }

            Dim(scroll.content,
                "Placing works like the game: the piece touches the surface you aim at, then snaps to the "
                + "closest snap point within 0.5 m. When that finds nothing the editor also slides it to the "
                + "closest spot that touches the point under the cursor. The red arrow marks the front: it "
                + "faces the player when the mod builds the blueprint.");
            Foot("Close", () => Close(), null, null);
        }

        /// <summary>A yes or no question. The answer runs after the dialog is gone.</summary>
        public static void Confirm(string title, string text, string ok, Action run)
        {
            if (!Begin("confirm", title, 560f, 240f))
            {
                return;
            }

            _confirmRun = run;
            var label = UiBuild.Label("Text", _body, text, 16f, TextAlignmentOptions.TopLeft);
            label.enableWordWrapping = true;
            UiBuild.Stretch(label.rectTransform);
            Foot("Cancel", () => Close(), ok, Answer);
        }

        /// <summary>The editor's own settings, the ones worth reaching without the config file.</summary>
        public static void Settings()
        {
            if (!Begin("settings", "Settings", 660f, 360f))
            {
                return;
            }

            var key = EditorConfig.Key != null ? EditorConfig.Key.Value.ToString() : "F7";

            var line = UiBuild.Label("Key", _body, $"Opens the editor: {key}", 16f, TextAlignmentOptions.TopLeft);
            Line(line.rectTransform, 0f, 24f);

            var button = UiBuild.Button("ShowAll", _body, "", null, 32f);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.text = ShowAllLabel();
            button.onClick.AddListener(() =>
            {
                EditorCommands.ToggleShowAllPieces();
                label.text = ShowAllLabel();
            });
            var rect = (RectTransform)button.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -34f);
            rect.sizeDelta = new Vector2(280f, 32f);

            var where = UiBuild.Label("Where", _body,
                "Blueprints: " + BlueprintLibrary.UserFolder
                + "\nEverything else is in the mod's config file, under Editor.",
                14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            where.enableWordWrapping = true;
            Line(where.rectTransform, 80f, 80f);

            Foot("Close", () => Close(), null, null);
        }

        private static string ShowAllLabel()
        {
            return EditorConfig.ShowAllPieces != null && EditorConfig.ShowAllPieces.Value
                ? "Showing every piece"
                : "Showing unlocked pieces only";
        }

        // ---------- building ----------

        /// <summary>Clears whatever was up and starts a new one. False when the UI is not built.</summary>
        private static bool Begin(string kind, string title, float width, float height)
        {
            if (_root == null)
            {
                return false;
            }

            Close();
            Kind = kind;
            _title.text = title;

            for (var i = _body.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(_body.GetChild(i).gameObject);
            }

            for (var i = _buttons.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(_buttons.GetChild(i).gameObject);
            }

            var space = _host.rect;
            _modal.sizeDelta = new Vector2(
                Mathf.Min(width, Mathf.Max(360f, space.width - 120f)),
                Mathf.Min(height, Mathf.Max(220f, space.height - 120f)));
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();   // over the piece menu, which also sets itself last
            return true;
        }

        /// <summary>The buttons along the bottom. The second one is the one Enter presses.</summary>
        private static void Foot(string left, Action onLeft, string right, Action onRight)
        {
            var cancel = UiBuild.Button("Left", _buttons, left, () => onLeft());
            Width(cancel, 120f);
            if (right == null)
            {
                _submit = cancel;
                return;
            }

            _submit = UiBuild.Button("Right", _buttons, right, () => onRight());
            Width(_submit, 140f);

            // The pad walks the two buttons; cross presses the selected one (Tick stands back).
            UiBuild.LinkRow(new List<Selectable> { cancel, _submit });
        }

        private static void Width(Button button, float width)
        {
            var element = button.GetComponent<LayoutElement>();
            element.minWidth = width;
            element.preferredWidth = width;
        }

        private static void Answer()
        {
            var run = _confirmRun;
            Close();
            run?.Invoke();
        }

        /// <summary>A label pinned a set distance below the body's top edge.</summary>
        private static void Line(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, -top - height);
            rect.offsetMax = new Vector2(0f, -top);
        }

        private static void Heading(Transform parent, string text)
        {
            if (parent == null)
            {
                return;
            }

            var label = UiBuild.Label("Heading", parent, text, 17f, TextAlignmentOptions.Left, UiTheme.Accent);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
        }

        private static void Dim(Transform parent, string text)
        {
            if (parent == null || text == null)
            {
                return;
            }

            var label = UiBuild.Label("Note", parent, text, 14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            label.enableWordWrapping = true;
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = Mathf.Max(20f, label.GetPreferredValues(text, 900f, 0f).y + 4f);
        }

        /// <summary>One line of the help table: the keys on the left, what they do on the right.</summary>
        private static void KeyRow(Transform parent, HelpRow row)
        {
            var host = UiBuild.Rect("Row", parent);
            host.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;

            var keys = UiBuild.Label("Keys", host, row.Keys, 14f, TextAlignmentOptions.TopLeft, UiTheme.Accent);
            keys.rectTransform.anchorMin = new Vector2(0f, 0f);
            keys.rectTransform.anchorMax = new Vector2(0f, 1f);
            keys.rectTransform.pivot = new Vector2(0f, 0.5f);
            keys.rectTransform.anchoredPosition = Vector2.zero;
            keys.rectTransform.sizeDelta = new Vector2(250f, 0f);

            var what = UiBuild.Label("What", host, row.What, 14f, TextAlignmentOptions.TopLeft);
            UiBuild.Stretch(what.rectTransform, 258f, 0f, 0f, 0f);
        }

        /// <summary>One clickable file row: where it is, how big it is and when it changed.</summary>
        private static void AddRows(Transform parent, List<BlueprintEntry> entries)
        {
            foreach (var entry in entries)
            {
                var index = Rows.Count;
                Rows.Add(entry);

                var background = UiBuild.Panel("Row", parent, UiTheme.ItemBackground);
                background.rectTransform.gameObject.AddComponent<LayoutElement>().preferredHeight = RowHeight;
                var button = background.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                button.onClick.AddListener(() => ClickRow(index));

                var text = RowLine(entry);
                var label = UiBuild.Label("Text", background.transform, text, 14f, TextAlignmentOptions.Left,
                    entry.Error != null ? UiTheme.Warn : UiTheme.Text);
                UiBuild.Stretch(label.rectTransform, 8f, 0f, 8f, 0f);
                label.overflowMode = TextOverflowModes.Ellipsis;
                RowLabels.Add(label);
            }
        }

        /// <summary>path, size and date, in one line, the way the browser editor lists them.</summary>
        private static string RowLine(BlueprintEntry entry)
        {
            var where = entry.IsKit
                ? entry.Name
                : Relative(entry.Path);
            var parts = where + "   " + Size(entry.Size);
            parts += entry.IsKit ? "   in the mod" : "   " + entry.Modified.ToString("d MMM yyyy HH:mm");
            if (entry.Error != null)
            {
                return parts + "   " + entry.Error;
            }

            return parts + $"   {entry.Pieces} pieces";
        }

        private static string Relative(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }

            var folder = BlueprintLibrary.UserFolder;
            return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar)
                : path;
        }

        private static string Size(long bytes)
        {
            return bytes < 1024 ? bytes + " B" : (bytes / 1024f).ToString("0.0") + " KB";
        }

        // ---------- save as ----------

        /// <summary>Reads the name box and says what would happen. Also switches Save on and off.</summary>
        private static void CheckName()
        {
            if (NameField == null)
            {
                return;
            }

            var wanted = NameField.text ?? "";
            var valid = wanted.Length > 0 && Name.IsMatch(wanted) && !wanted.Contains("..");
            var file = BlueprintFormat.FileNameFor(wanted);
            _fileLine.text = valid ? "File: " + file : "";

            if (!valid)
            {
                _note.text = wanted.Length == 0
                    ? "Give the blueprint a name."
                    : "Use letters, digits, spaces, dots, dashes or underscores.";
                _note.color = UiTheme.Warn;
            }
            else if (File.Exists(Path.Combine(BlueprintLibrary.UserFolder, file)))
            {
                _note.text = file + " exists. You will be asked before it is replaced.";
                _note.color = UiTheme.Accent;
            }
            else
            {
                _note.text = "";
            }

            _note.gameObject.SetActive(_note.text.Length > 0);
            if (_submit != null)
            {
                _submit.interactable = valid;
            }
        }

        private static void SubmitSaveAs()
        {
            if (NameField == null || _submit == null || !_submit.interactable)
            {
                return;
            }

            var wanted = NameField.text ?? "";
            var file = BlueprintFormat.FileNameFor(wanted);
            var target = Path.Combine(BlueprintLibrary.UserFolder, file);
            var document = EditorState.Document;
            var same = document != null && !string.IsNullOrEmpty(document.SourcePath)
                && string.Equals(document.SourcePath, target, StringComparison.OrdinalIgnoreCase);
            if (!same && File.Exists(target))
            {
                Confirm("Replace the file?", $"{file} is already there. Saving writes over it.", "Replace",
                    () => EditorCommands.SaveAs(wanted, true));
                return;
            }

            EditorCommands.SaveAs(wanted, true);
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = UiBuild.Rect("Dialogs", host);
            UiBuild.Stretch(_root);
            _root.SetAsLastSibling();

            var backdrop = UiBuild.Panel("Backdrop", _root, null, new Color(0f, 0f, 0f, 0.55f));
            UiBuild.Stretch(backdrop.rectTransform);
            backdrop.gameObject.AddComponent<Button>().onClick.AddListener(() => Close());

            var panel = UiBuild.Panel("Modal", _root, UiTheme.Panel);
            _modal = panel.rectTransform;
            _modal.anchorMin = _modal.anchorMax = new Vector2(0.5f, 0.5f);
            _modal.pivot = new Vector2(0.5f, 0.5f);
            _modal.anchoredPosition = Vector2.zero;
            _modal.sizeDelta = new Vector2(700f, 500f);

            _title = UiBuild.Label("Title", _modal, "", 22f, TextAlignmentOptions.Left, UiTheme.Accent);
            _title.rectTransform.anchorMin = new Vector2(0f, 1f);
            _title.rectTransform.anchorMax = new Vector2(1f, 1f);
            _title.rectTransform.pivot = new Vector2(0.5f, 1f);
            _title.rectTransform.offsetMin = new Vector2(Pad + 8f, -Head - 6f);
            _title.rectTransform.offsetMax = new Vector2(-Pad - 40f, -8f);

            var close = UiBuild.Button("Close", _modal, "X", () => Close(), 26f);
            var closeRect = (RectTransform)close.transform;
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-Pad, -10f);
            closeRect.sizeDelta = new Vector2(30f, 26f);

            _body = UiBuild.Rect("Body", _modal);
            _body.anchorMin = Vector2.zero;
            _body.anchorMax = Vector2.one;
            _body.offsetMin = new Vector2(Pad + 8f, Pad + FootHeight);
            _body.offsetMax = new Vector2(-Pad - 8f, -Head - 12f);

            _buttons = UiBuild.Row("Buttons", _modal, 8f);
            _buttons.anchorMin = new Vector2(1f, 0f);
            _buttons.anchorMax = new Vector2(1f, 0f);
            _buttons.pivot = new Vector2(1f, 0f);
            _buttons.anchoredPosition = new Vector2(-Pad - 8f, Pad);
            _buttons.sizeDelta = new Vector2(300f, 34f);
            _buttons.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;

            _root.gameObject.SetActive(false);
        }
    }
}
