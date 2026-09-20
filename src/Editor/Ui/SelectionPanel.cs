using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Catalog;
using ValheimTomrer.Editor.Doc;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The right panel's middle region: what is selected. One piece gets its place and its turn as
    /// text boxes, plus whatever the file carries that the mod itself ignores; several pieces get a
    /// count and a tally per kind. The four buttons are the same actions as the keys G, R, Ctrl+D
    /// and Delete.
    ///
    /// A box applies its number on Enter or when it loses focus, and Esc puts the old one back.
    /// </summary>
    internal static class SelectionPanel
    {
        /// <summary>Decimals in the x, y and z boxes. Millimetres, the same as the file.</summary>
        public const int PositionDecimals = 4;

        /// <summary>Decimals in the yaw box.</summary>
        public const int YawDecimals = 2;

        private const float FieldHeight = 28f;
        private const float ButtonHeight = 30f;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;

        private static TextMeshProUGUI _nothing;
        private static RectTransform _one;
        private static Image _icon;
        private static TextMeshProUGUI _name;
        private static TextMeshProUGUI _prefab;
        private static TextMeshProUGUI _tilt;
        private static TextMeshProUGUI _kept;
        private static TextMeshProUGUI _scale;
        private static TextMeshProUGUI _problem;
        private static RectTransform _many;
        private static TextMeshProUGUI _count;
        private static TextMeshProUGUI _kinds;
        private static RectTransform _buttons;

        private static readonly NumberField[] Numbers = new NumberField[4];

        private static int _version = -1;
        private static int _revision = -1;

        /// <summary>0 nothing, 1 one piece, 2 several.</summary>
        public static int Mode { get; private set; }

        public static TMP_InputField XField => Numbers[0] != null ? Numbers[0].Field : null;

        public static TMP_InputField YField => Numbers[1] != null ? Numbers[1].Field : null;

        public static TMP_InputField ZField => Numbers[2] != null ? Numbers[2].Field : null;

        public static TMP_InputField YawField => Numbers[3] != null ? Numbers[3].Field : null;

        public static string NameText => _name != null ? _name.text : "";

        public static string CountText => _count != null ? _count.text : "";

        public static string KindsText => _kinds != null ? _kinds.text : "";

        public static string TiltText => Shown(_tilt);

        public static string KeptText => Shown(_kept);

        public static string ScaleText => Shown(_scale);

        public static string ProblemText => Shown(_problem);

        public static void Ensure(RectTransform host)
        {
            if (host == null)
            {
                return;
            }

            if (_host == host && _root != null && _generation == UiTheme.Generation)
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Build(host);
        }

        public static void Show()
        {
            _version = -1;
            _revision = -1;
            Refresh();
        }

        public static void Tick()
        {
            if (_root == null)
            {
                return;
            }

            var revision = EditorState.Document != null ? EditorState.Document.Revision : -1;
            if (EditorState.Version != _version || revision != _revision)
            {
                Refresh();
            }
        }

        public static void Close()
        {
            _version = -1;
            _revision = -1;
        }

        /// <summary>Reads the selection into every widget.</summary>
        public static void Refresh()
        {
            if (_root == null)
            {
                return;
            }

            _version = EditorState.Version;
            _revision = EditorState.Document != null ? EditorState.Document.Revision : -1;

            var pieces = EditorState.SelectedPieces();
            Mode = pieces.Count == 0 ? 0 : pieces.Count == 1 ? 1 : 2;
            _nothing.gameObject.SetActive(Mode == 0);
            _one.gameObject.SetActive(Mode == 1);
            _many.gameObject.SetActive(Mode == 2);
            _buttons.gameObject.SetActive(Mode != 0);

            if (Mode == 1)
            {
                FillOne(pieces[0]);
            }
            else if (Mode == 2)
            {
                FillMany(pieces);
            }
        }

        private static void FillOne(DocPiece piece)
        {
            var entry = PieceCatalog.Find(piece.PrefabName);
            _icon.sprite = entry != null ? entry.Icon : null;
            _icon.enabled = _icon.sprite != null;
            _name.text = entry != null ? entry.DisplayName : "Unknown piece";
            _prefab.text = piece.PrefabName;

            Numbers[0].Set(piece.Position.x, piece.Id);
            Numbers[1].Set(piece.Position.y, piece.Id);
            Numbers[2].Set(piece.Position.z, piece.Id);
            Numbers[3].Set(YawOf(piece.Rotation), piece.Id);

            var level = Vector3.Angle(piece.Rotation * Vector3.up, Vector3.up) < 0.01f;
            _tilt.gameObject.SetActive(!level);
            _tilt.text = "Tilted in the file. The tilt is kept; yaw turns it around the vertical axis.";

            var kept = piece.Category.Length > 0 || piece.Rest != null;
            _kept.gameObject.SetActive(kept);
            if (kept)
            {
                _kept.text = $"Category: {(piece.Category.Length > 0 ? piece.Category : "(empty)")}\n"
                    + $"Extra fields: {(piece.Rest == null ? "(none)" : piece.Rest.Length > 0 ? piece.Rest : "(empty)")}\n"
                    + "Kept as they are on save. The mod ignores them.";
            }

            var scale = Checks.ScaleOf(piece);
            var scaled = scale != null && (Mathf.Abs(scale.Value.x - 1f) > 1e-4f
                || Mathf.Abs(scale.Value.y - 1f) > 1e-4f
                || Mathf.Abs(scale.Value.z - 1f) > 1e-4f);
            _scale.gameObject.SetActive(scaled);
            if (scaled)
            {
                _scale.text = $"Scale {Number(scale.Value.x, 3)} x {Number(scale.Value.y, 3)} x "
                    + $"{Number(scale.Value.z, 3)}: the mod builds this piece at normal size.";
            }

            var problem = entry == null
                ? "Not a hammer piece of this game version. The game skips the whole blueprint."
                : null;
            _problem.gameObject.SetActive(problem != null);
            if (problem != null)
            {
                _problem.text = problem;
            }
        }

        private static void FillMany(List<DocPiece> pieces)
        {
            _count.text = pieces.Count + " pieces selected.";

            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (var piece in pieces)
            {
                if (!counts.ContainsKey(piece.PrefabName))
                {
                    counts[piece.PrefabName] = 0;
                    order.Add(piece.PrefabName);
                }

                counts[piece.PrefabName]++;
            }

            var parts = new List<string>(order.Count);
            foreach (var prefab in order)
            {
                var entry = PieceCatalog.Find(prefab);
                parts.Add($"{counts[prefab]}x {(entry != null ? entry.DisplayName : prefab)}");
            }

            _kinds.text = string.Join(", ", parts.ToArray());
        }

        /// <summary>Yaw in 0..360, the way Tomrer shows it.</summary>
        public static float YawOf(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            var degrees = Mathf.Abs(forward.x) < 1e-6f && Mathf.Abs(forward.z) < 1e-6f
                ? Mathf.Atan2(-(rotation * Vector3.right).z, (rotation * Vector3.right).x) * Mathf.Rad2Deg
                : Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var wrapped = degrees % 360f;
            if (wrapped < 0f)
            {
                wrapped += 360f;
            }

            return wrapped >= 359.99995f ? 0f : wrapped;
        }

        // ---------- widgets ----------

        private static void Build(RectTransform host)
        {
            _root = Column("Selection", host, 4f, 2);
            UiBuild.Stretch(_root, 4f, 4f, 4f, 4f);

            var heading = UiBuild.Label("Heading", _root, "Selection", 17f, TextAlignmentOptions.Left, UiTheme.Accent);
            heading.gameObject.AddComponent<LayoutElement>().minHeight = 20f;

            _nothing = Line(_root, 13f, UiTheme.TextDim);
            _nothing.text = "Nothing selected. Click a piece, or drag a box around several.";

            _one = Column("One", _root, 4f);
            var head = Row("Head", _one, 8f);
            head.gameObject.AddComponent<LayoutElement>().minHeight = 38f;
            _icon = UiBuild.Panel("Icon", head, null);
            _icon.type = Image.Type.Simple;
            _icon.preserveAspect = true;
            var size = _icon.gameObject.AddComponent<LayoutElement>();
            size.minWidth = size.preferredWidth = 36f;
            size.minHeight = size.preferredHeight = 36f;
            var names = Column("Names", head, 0f);
            names.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            _name = Line(names, 15f, UiTheme.Text);
            _prefab = Line(names, 12f, UiTheme.TextDim);

            var row = Row("Numbers", _one, 6f);
            row.gameObject.AddComponent<LayoutElement>().minHeight = FieldHeight + 16f;
            Numbers[0] = NewNumber(row, "x", PositionDecimals, (id, v) => Move(id, 0, v));
            Numbers[1] = NewNumber(row, "y", PositionDecimals, (id, v) => Move(id, 1, v));
            Numbers[2] = NewNumber(row, "z", PositionDecimals, (id, v) => Move(id, 2, v));
            Numbers[3] = NewNumber(row, "yaw", YawDecimals, EditorState.SetYaw);

            _tilt = Line(_one, 12f, UiTheme.TextDim);
            _kept = Line(_one, 12f, UiTheme.TextDim);
            _scale = Line(_one, 12f, UiTheme.Warn);
            _problem = Line(_one, 12f, UiTheme.Warn);

            _many = Column("Many", _root, 4f);
            _count = Line(_many, 14f, UiTheme.Text);
            _kinds = Line(_many, 13f, UiTheme.TextDim);

            _buttons = Row("Buttons", _root, 4f);
            _buttons.gameObject.AddComponent<LayoutElement>().minHeight = ButtonHeight;
            ActionButton("Move", () => EditorState.StartMove());
            ActionButton("Turn", () => EditorState.RotateSelection(1));
            ActionButton("Copy", () => EditorState.StartDuplicate());
            ActionButton("Delete", EditorState.DeleteSelection);

            _nothing.gameObject.SetActive(true);
            _one.gameObject.SetActive(false);
            _many.gameObject.SetActive(false);
            _buttons.gameObject.SetActive(false);
            _tilt.gameObject.SetActive(false);
            _kept.gameObject.SetActive(false);
            _scale.gameObject.SetActive(false);
            _problem.gameObject.SetActive(false);
        }

        private static void ActionButton(string text, UnityEngine.Events.UnityAction onClick)
        {
            var button = UiBuild.Button(text, _buttons, text, onClick, ButtonHeight);
            var element = button.GetComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            element.minWidth = 40f;
            button.GetComponentInChildren<TextMeshProUGUI>().fontSize = 14f;
        }

        /// <summary>The x, y or z box moved one piece.</summary>
        private static void Move(int id, int axis, float value)
        {
            var piece = EditorState.Document != null ? EditorState.Document.Find(id) : null;
            if (piece == null)
            {
                return;
            }

            var position = piece.Position;
            position[axis] = value;
            EditorState.SetPosition(id, position);
        }

        private static NumberField NewNumber(Transform parent, string caption, int decimals, Action<int, float> commit)
        {
            var column = Column(caption, parent, 2f);
            column.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var label = UiBuild.Label("Caption", column, caption, 12f, TextAlignmentOptions.Left, UiTheme.TextDim);
            label.gameObject.AddComponent<LayoutElement>().minHeight = 14f;

            var field = UiBuild.InputField(caption + "Field", column, "", FieldHeight);
            field.GetComponent<LayoutElement>().flexibleWidth = 1f;
            field.textComponent.fontSize = 13f;
            field.pointSize = 13f;
            field.characterValidation = TMP_InputField.CharacterValidation.None;

            var number = new NumberField { Field = field, Decimals = decimals, Commit = commit };
            field.onEndEdit.AddListener(number.Finish);
            return number;
        }

        private static string Number(float value, int decimals)
        {
            return BlueprintFormat.FormatNumber(value, decimals);
        }

        private static string Shown(TextMeshProUGUI label)
        {
            return label != null && label.gameObject.activeSelf ? label.text : "";
        }

        private static TextMeshProUGUI Line(Transform parent, float size, Color color)
        {
            var label = UiBuild.Label("Line", parent, "", size, TextAlignmentOptions.TopLeft, color);
            label.enableWordWrapping = true;
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            return label;
        }

        private static RectTransform Column(string name, Transform parent, float spacing, int padding = 0)
        {
            var rect = UiBuild.Column(name, parent, spacing, padding);
            rect.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            return rect;
        }

        private static RectTransform Row(string name, Transform parent, float spacing)
        {
            var rect = UiBuild.Row(name, parent, spacing);
            rect.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = true;
            return rect;
        }

        /// <summary>
        /// A box holding one number. It keeps the text the document says, applies a new number on
        /// Enter or when it loses focus, and puts the old text back on Esc or on anything it cannot
        /// read. A number that formats to the same text is not applied, so no undo step is made.
        /// </summary>
        private sealed class NumberField
        {
            public TMP_InputField Field;
            public int Decimals;
            public Action<int, float> Commit;

            private string _shown = "";
            private int _id = -1;

            public void Set(float value, int id)
            {
                _id = id;
                _shown = BlueprintFormat.FormatNumber(value, Decimals);
                if (!Field.isFocused)
                {
                    Field.SetTextWithoutNotify(_shown);
                }
            }

            public void Finish(string text)
            {
                if (Field.wasCanceled || _id < 0)
                {
                    Field.SetTextWithoutNotify(_shown);
                    return;
                }

                // Old files and some keyboards write the decimal mark as a comma.
                var typed = (text ?? "").Trim().Replace(',', '.');
                if (typed.Length > 0
                    && float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && !float.IsNaN(value) && !float.IsInfinity(value)
                    && BlueprintFormat.FormatNumber(value, Decimals) != _shown)
                {
                    Commit(_id, value);
                    return;
                }

                Field.SetTextWithoutNotify(_shown);
            }
        }
    }
}
