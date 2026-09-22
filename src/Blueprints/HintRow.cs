using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Editor;
using ValheimTomrer.Editor.Input;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// The controls of blueprint mode, Continue and the capture, in the game's own hint row along the
    /// bottom of the screen (<c>KeyHints</c>), in its own look.
    ///
    /// How: a group of our own sits next to the game's groups under <c>KeyHints</c>, laid out like the
    /// game's build hints (a keyboard row and a pad row, both right-aligned). Every entry is a copy of
    /// one of the game's own: the label, the key cap, the "+" between two caps and the wheel icon come
    /// from the keyboard row, the pad entry from the pad row, with the game's own button icons
    /// (<c>ZInput.GetBoundKeyString</c>). So the font, size, colour, material and spacing are the game's.
    ///
    /// In place of the game's <c>KeyHints.UpdateHints</c> (<c>KeyHintsUpdateHintsPatch</c>), while a
    /// mode is up and the game would show the row at all: the game's groups go off once and its update
    /// is skipped, so it does not switch them on and off every frame. When the mode ends the game's
    /// update runs again and sets every group as it always does, so its row is back as it was, with
    /// nothing to undo here.
    /// </summary>
    internal static class HintRow
    {
        public enum Mode
        {
            None,
            Blueprint,
            Continue,
            Capture,
        }

        /// <summary>How often the texts are read again when nothing obvious changed: a rebound key shows within this.</summary>
        private const float RecheckSeconds = 1f;

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly StringBuilder Text = new StringBuilder();

        private static KeyHints _builtFor;
        private static RectTransform _root;
        private static RectTransform _keyboard;
        private static RectTransform _gamepad;

        private static RectTransform _kbEntry;
        private static TMP_Text _kbLabel;
        private static RectTransform _kbKey;
        private static TMP_Text _kbPlus;
        private static Image _kbWheel;
        private static TMP_Text _gpEntry;
        private static bool _warned;

        private static int _cheapKey;
        private static float _checkedAt;
        private static string _content;

        /// <summary>Which set shows now. None while the game's own row is up.</summary>
        public static Mode Shown { get; private set; }

        /// <summary>The entries of the set that shows, in order. For the tests.</summary>
        public static IReadOnlyList<Entry> ShownEntries => Entries;

        /// <summary>Our group, the keyboard row and the pad row. Null until a mode first shows.</summary>
        public static RectTransform Root => _root;

        public static RectTransform KeyboardRow => _keyboard;

        public static RectTransform GamepadRow => _gamepad;

        /// <summary>One hint, as shown: its label, its keyboard entry, its pad entry.</summary>
        internal sealed class Entry
        {
            public string Label;

            /// <summary>The keyboard side as words: "Shift + wheel", "F8".</summary>
            public string Keys;

            /// <summary>The pad side: the game's icon tags, "&lt;sprite=...&gt; + &lt;sprite=...&gt;".</summary>
            public string Pad;

            public RectTransform KeyboardEntry;

            public TMP_Text GamepadEntry;
        }

        /// <summary>A hint before it is drawn.</summary>
        private sealed class Hint
        {
            public string Label;

            /// <summary>Key caps by name, "+" for the plus between two, "wheel" for the wheel icon.</summary>
            public readonly List<string> Keys = new List<string>();

            public string Pad;
        }

        /// <summary>
        /// Before the game's own <c>KeyHints.UpdateHints</c>. True: our set shows in place of the
        /// game's groups, and the game's update is skipped this frame. False: ours is off and the game
        /// sets its row as usual.
        /// </summary>
        public static bool TakeOver(KeyHints hints)
        {
            var mode = WantedMode();
            if (mode == Mode.None || hints == null || !GameShowsRow(hints) || !Ensure(hints))
            {
                Hide();
                return false;
            }

            SetActive(hints.m_buildHints, false);
            SetActive(hints.m_combatHints, false);
            SetActive(hints.m_fishingHints, false);
            SetActive(hints.m_inventoryHints, false);
            SetActive(hints.m_inventoryWithContainerHints, false);
            SetActive(hints.m_barberHints, false);
            SetActive(hints.m_radialHints, false);

            // The game's own rule for its build hints (UIInputHint with no pad-and-mouse row).
            var pad = ZInput.IsGamepadActive();
            SetActive(_gamepad.gameObject, pad);
            SetActive(_keyboard.gameObject, !pad && ZInput.IsMouseActive());
            SetActive(_root.gameObject, true);

            var cheap = CheapKey(mode, pad);
            if (mode != Shown || cheap != _cheapKey || Time.unscaledTime - _checkedAt >= RecheckSeconds)
            {
                _cheapKey = cheap;
                _checkedAt = Time.unscaledTime;
                Fill(mode, Hints(mode));
            }

            Shown = mode;
            return true;
        }

        /// <summary>Our group off. The game's own update, which runs again, puts its row back.</summary>
        public static void Hide()
        {
            Shown = Mode.None;
            if (_root != null)
            {
                SetActive(_root.gameObject, false);
            }
        }

        /// <summary>Plugin OnDestroy.</summary>
        public static void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
            }

            _root = null;
            _keyboard = null;
            _gamepad = null;
            _builtFor = null;
            _content = null;
            Entries.Clear();
            Shown = Mode.None;
        }

        /// <summary>Capture wins over blueprint mode: while its rectangle is up, its keys are the ones that do something.</summary>
        private static Mode WantedMode()
        {
            var enabled = ValheimTomrerPlugin.ModEnabled;
            if (enabled == null || !enabled.Value || ModUi.Open)
            {
                return Mode.None;
            }

            if (WorldCapture.Active)
            {
                return Mode.Capture;
            }

            var player = Player.m_localPlayer;
            if (BlueprintMode.Active && player != null && player.InPlaceMode())
            {
                return BlueprintMode.CurrentSite != null ? Mode.Continue : Mode.Blueprint;
            }

            return Mode.None;
        }

        /// <summary>
        /// The game would show its row now: the player's hint setting, the player alive, no chat, no
        /// pause, and no menu with hints of its own (inventory, radial, build menu, barber). Those keep
        /// the game's row.
        /// </summary>
        private static bool GameShowsRow(KeyHints hints)
        {
            var player = Player.m_localPlayer;
            if (!hints.m_keyHintsEnabled || player == null || player.IsDead() || Game.IsPaused()
                || (Chat.instance != null && Chat.instance.IsChatDialogWindowVisible()))
            {
                return false;
            }

            var inventory = InventoryGui.instance;
            if (inventory != null && (inventory.IsSkillsPanelOpen || inventory.IsTrophisPanelOpen
                || inventory.IsAchievementsPanelOpen || inventory.IsTextPanelOpen))
            {
                return false;
            }

            var hud = Hud.instance;
            return !InventoryGui.IsVisible()
                && !(hud != null && hud.m_radialMenu != null && hud.m_radialMenu.Active)
                && !Hud.IsPieceSelectionVisible()
                && !PlayerCustomizaton.IsBarberGuiVisible();
        }

        /// <summary>
        /// What changes the texts often enough to look at every frame, as one number, so the frame
        /// makes no garbage. The rest is read once a second.
        /// </summary>
        private static int CheapKey(Mode mode, bool pad)
        {
            unchecked
            {
                var key = (int)mode;
                key = (key * 31) + (pad ? 1 : 0);
                key = (key * 31) + (int)ZInput.InputLayout;
                key = (key * 31) + (int)ZInput.CurrentGlyph;
                key = (key * 31) + (int)ZInput.ConnectedGamepadType;
                key = (key * 31) + (int)ValheimTomrerPlugin.BlueprintKey.Value;
                key = (key * 31) + (EditorConfig.Key != null ? (int)EditorConfig.Key.Value : -1);
                key = (key * 31) + (EditorConfig.CaptureKey != null ? (int)EditorConfig.CaptureKey.Value : -1);
                return key;
            }
        }

        // ---------- the sets ----------

        /// <summary>
        /// The set for a mode, from the buttons the code reads: the game's own names (its layout and the
        /// player's bindings decide the button), the mod's config keys, and the wheel.
        /// </summary>
        private static List<Hint> Hints(Mode mode)
        {
            var list = new List<Hint>();
            var next = KeyName(ValheimTomrerPlugin.BlueprintKey.Value);
            var edit = EditorConfig.Key != null ? KeyName(EditorConfig.Key.Value) : null;
            switch (mode)
            {
                case Mode.Blueprint:
                    // Rotate last, where the game's own row has it: its wheel is taller than a key
                    // cap, and further left it would sit right under the materials list.
                    list.Add(Make("Build", Pad("JoyPlace"), Bound("Attack")));
                    list.Add(Make("Next blueprint", Pad(WorldPad.Square), next));
                    AddEdit(list, edit);
                    list.Add(Make(Word("$hud_buildmenu", "Build Menu"), Pad(BuildMenuButton()), Bound("BuildMenu")));
                    list.Add(Make(Word("$hud_rotate", "Rotate"), RotatePad(), "wheel"));
                    break;
                case Mode.Continue:
                    // Locked onto the build: nothing turns it, so no Rotate.
                    list.Add(Make("Build", Pad("JoyPlace"), Bound("Attack")));
                    list.Add(Make(Word("$hud_remove", "Remove"), Pad("JoyRemove"), Bound("Remove")));
                    list.Add(Make("Next", Pad(WorldPad.Square), next));
                    AddEdit(list, edit);
                    list.Add(Make(Word("$hud_buildmenu", "Build Menu"), Pad(BuildMenuButton()), Bound("BuildMenu")));
                    break;
                case Mode.Capture:
                    var capture = EditorConfig.CaptureKey != null ? KeyName(EditorConfig.CaptureKey.Value) : "F8";
                    var mod = Pad(WorldPad.Modifier);
                    var leftRight = Pair(WorldPad.DpadLeft, WorldPad.DpadRight);
                    var upDown = Pair(WorldPad.DpadUp, WorldPad.DpadDown);
                    list.Add(Make("Capture", Combo(mod, Pad(WorldPad.Triangle)), capture));
                    list.Add(Make("Turn", leftRight, "wheel"));
                    list.Add(Make("Width", Combo(mod, leftRight), "Shift", "+", "wheel"));
                    list.Add(Make("Depth", Combo(mod, upDown), "Alt", "+", "wheel"));
                    list.Add(Make("Both sides", upDown, "Shift", "+", "Alt", "+", "wheel"));
                    list.Add(Make("Stop", Pad(WorldPad.Circle), KeyName(KeyCode.Escape)));
                    break;
            }

            return list;
        }

        private static void AddEdit(List<Hint> list, string key)
        {
            if (key != null)
            {
                list.Add(Make("Edit", Combo(Pad(WorldPad.Modifier), Pad(WorldPad.Square)), key));
            }
        }

        private static Hint Make(string label, string pad, params string[] keys)
        {
            var hint = new Hint { Label = label, Pad = pad };
            hint.Keys.AddRange(keys);
            return hint;
        }

        /// <summary>
        /// The pad's turn, the way <c>BlueprintMode</c> reads it: the game's rotate button and the right
        /// stick in the default layout (shown the way the game's own Rotate shows it), the two rotate
        /// buttons in the other two.
        /// </summary>
        private static string RotatePad()
        {
            return ZInput.InputLayout == InputLayout.Default
                ? Combo(Pad("JoyRotate"), Pad("JoyRStick"))
                : Pair("JoyRotate", "JoyRotateRight");
        }

        /// <summary>The build menu's pad button, as <c>Player.UpdateBuildGuiInput</c> reads it.</summary>
        private static string BuildMenuButton()
        {
            return ZInput.IsNonClassicFunctionality() ? "JoyBuildMenu" : "JoyUse";
        }

        /// <summary>The game's icon for one of its pad buttons, in the family of the pad in hand, or "".</summary>
        private static string Pad(string gameButton)
        {
            return Localization.instance != null ? Localization.instance.GetBoundKeyString(gameButton, emptyStringOnMissing: true) : "";
        }

        /// <summary>"A + B", as the game writes two buttons held together.</summary>
        private static string Combo(string first, string second)
        {
            return Join(first, " + ", second);
        }

        /// <summary>"A / B", as the game writes either of two buttons.</summary>
        private static string Pair(string first, string second)
        {
            return Join(Pad(first), " / ", Pad(second));
        }

        private static string Join(string first, string between, string second)
        {
            if (string.IsNullOrEmpty(first))
            {
                return second ?? "";
            }

            return string.IsNullOrEmpty(second) ? first : first + between + second;
        }

        /// <summary>The game's name for the key or mouse button bound to one of its buttons: "Mouse-1", "Left System".</summary>
        private static string Bound(string gameButton)
        {
            var name = Localization.instance != null ? Localization.instance.GetBoundKeyString(gameButton, emptyStringOnMissing: true) : "";
            return string.IsNullOrEmpty(name) ? gameButton : name;
        }

        /// <summary>The game's name for a key: "B", "F7", "Esc".</summary>
        private static string KeyName(KeyCode key)
        {
            try
            {
                var name = ZInput.KeyCodeToDisplayName(key);
                if (!string.IsNullOrEmpty(name) && !name.StartsWith("$"))
                {
                    return name;
                }
            }
            catch (System.Exception)
            {
                // No keyboard to ask: the key's own name below.
            }

            return key.ToString();
        }

        private static string Word(string token, string fallback)
        {
            if (Localization.instance == null)
            {
                return fallback;
            }

            var word = Localization.instance.Localize(token);
            return string.IsNullOrEmpty(word) || word.StartsWith("[") ? fallback : word;
        }

        // ---------- drawing ----------

        /// <summary>Writes the entries again, only when a text changed.</summary>
        private static void Fill(Mode mode, List<Hint> hints)
        {
            Text.Length = 0;
            Text.Append((int)mode);
            foreach (var hint in hints)
            {
                Text.Append('|').Append(hint.Label).Append('~').Append(string.Join(" ", hint.Keys)).Append('~').Append(hint.Pad);
            }

            var content = Text.ToString();
            if (content == _content && Entries.Count == hints.Count)
            {
                return;
            }

            _content = content;
            Clear(_keyboard);
            Clear(_gamepad);
            Entries.Clear();
            foreach (var hint in hints)
            {
                Entries.Add(new Entry
                {
                    Label = hint.Label,
                    Keys = string.Join(" ", hint.Keys),
                    Pad = hint.Pad,
                    KeyboardEntry = KeyboardEntry(hint),
                    GamepadEntry = GamepadEntry(hint),
                });
            }
        }

        /// <summary>A copy of the game's "Place" entry: the label, then a cap, "+" or wheel per part.</summary>
        private static RectTransform KeyboardEntry(Hint hint)
        {
            var entry = (RectTransform)Object.Instantiate(_kbEntry.gameObject, _keyboard, false).transform;
            entry.name = "VT " + hint.Label;
            Clear(entry);
            entry.gameObject.SetActive(true);

            var label = Copy(_kbLabel, entry);
            label.text = hint.Label;
            foreach (var part in hint.Keys)
            {
                if (part == "+")
                {
                    Copy(_kbPlus, entry).text = "+";
                }
                else if (part == "wheel")
                {
                    var wheel = Object.Instantiate(_kbWheel.gameObject, entry, false);
                    wheel.SetActive(true);
                }
                else
                {
                    var cap = Object.Instantiate(_kbKey.gameObject, entry, false);
                    cap.SetActive(true);
                    var text = cap.GetComponentInChildren<TMP_Text>(true);
                    if (text != null)
                    {
                        text.text = part;
                        Fixed(text);
                    }
                }
            }

            return entry;
        }

        /// <summary>A copy of the game's pad "Place" entry, written the way the game writes its own.</summary>
        private static TMP_Text GamepadEntry(Hint hint)
        {
            var entry = Copy(_gpEntry, _gamepad);
            entry.name = "VT " + hint.Label;
            entry.text = $"{hint.Label} <mspace=0.6em> {hint.Pad}</mspace>";
            return entry;
        }

        private static TMP_Text Copy(TMP_Text template, Transform parent)
        {
            var copy = Object.Instantiate(template.gameObject, parent, false).GetComponent<TMP_Text>();
            copy.gameObject.SetActive(true);
            Fixed(copy);
            return copy;
        }

        /// <summary>
        /// The game's labels size themselves down to fit (18 at most). Its row always gives them room,
        /// so they show at 18. Ours are fixed at that size, so a first layout pass with no room yet
        /// never leaves one small.
        /// </summary>
        private static void Fixed(TMP_Text text)
        {
            if (text.enableAutoSizing)
            {
                text.enableAutoSizing = false;
                text.fontSize = text.fontSizeMax;
            }
        }

        /// <summary>Off at once, gone at the end of the frame: the layout ignores it from now on.</summary>
        private static void Clear(Transform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.SetActive(false);
                child.transform.SetParent(null, false);
                Object.Destroy(child);
            }
        }

        /// <summary>
        /// Our group under the game's KeyHints, with a keyboard row and a pad row copied from the game's
        /// build hints (their layout, no entries). Made again after a world load, when the HUD is new.
        /// False, and the game's row stays, when the game's row does not look as expected.
        /// </summary>
        private static bool Ensure(KeyHints hints)
        {
            if (_root != null && _builtFor == hints)
            {
                return true;
            }

            Destroy();
            var build = hints.m_buildHints != null ? hints.m_buildHints.transform as RectTransform : null;
            var keyboard = build != null ? build.Find("Keyboard") as RectTransform : null;
            var gamepad = build != null ? build.Find("Gamepad") as RectTransform : null;
            if (!FindTemplates(keyboard, gamepad))
            {
                if (!_warned)
                {
                    _warned = true;
                    ValheimTomrerPlugin.Log.LogWarning("hint row: the game's build hints do not look as expected, its own row stays");
                }

                return false;
            }

            var root = new GameObject("ValheimTomrer_Hints", typeof(RectTransform));
            _root = (RectTransform)root.transform;
            _root.SetParent(build.parent, false);
            _root.SetSiblingIndex(build.GetSiblingIndex() + 1);
            _root.anchorMin = build.anchorMin;
            _root.anchorMax = build.anchorMax;
            _root.pivot = build.pivot;
            _root.anchoredPosition = build.anchoredPosition;
            _root.sizeDelta = build.sizeDelta;
            _root.localScale = build.localScale;

            _keyboard = Row(keyboard, "Keyboard");
            _gamepad = Row(gamepad, "Gamepad");
            _builtFor = hints;
            _content = null;
            ValheimTomrerPlugin.Log.LogInfo("hint row added next to the game's build hints");
            return true;
        }

        /// <summary>A copy of one of the game's rows, emptied.</summary>
        private static RectTransform Row(RectTransform source, string name)
        {
            var row = (RectTransform)Object.Instantiate(source.gameObject, _root, false).transform;
            row.name = name;
            Clear(row);
            row.gameObject.SetActive(false);
            return row;
        }

        /// <summary>
        /// The game's own pieces to copy: the keyboard "Place" entry (label and key cap), the "+" of its
        /// Copy entry, the wheel of its Rotate entry, and the pad "Place" entry.
        /// </summary>
        private static bool FindTemplates(RectTransform keyboard, RectTransform gamepad)
        {
            _kbEntry = null;
            _kbLabel = null;
            _kbKey = null;
            _kbPlus = null;
            _kbWheel = null;
            _gpEntry = null;
            if (keyboard == null || gamepad == null)
            {
                return false;
            }

            _kbEntry = keyboard.Find("Place") as RectTransform;
            _kbLabel = _kbEntry != null ? _kbEntry.Find("Text")?.GetComponent<TMP_Text>() : null;
            _kbKey = _kbEntry != null ? _kbEntry.Find("key_bkg") as RectTransform : null;
            foreach (var text in keyboard.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.text != null && text.text.Trim() == "+")
                {
                    _kbPlus = text;
                    break;
                }
            }

            foreach (var image in keyboard.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite != null && image.sprite.name == "mousew_icon")
                {
                    _kbWheel = image;
                    break;
                }
            }

            _gpEntry = gamepad.Find("Text - Place")?.GetComponent<TMP_Text>();
            return _kbEntry != null && _kbLabel != null && _kbKey != null && _kbPlus != null && _kbWheel != null && _gpEntry != null;
        }

        private static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on)
            {
                go.SetActive(on);
            }
        }
    }
}
