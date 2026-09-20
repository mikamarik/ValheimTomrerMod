using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>The three regions the walk covers, in the order it walks them.</summary>
    internal enum FocusRegion
    {
        TopBar,
        Left,
        Right,
    }

    /// <summary>
    /// The panel walk: one focused widget at a time in the top bar, the left panel or the right
    /// panel, so a controller alone (or Tab) can reach every button and text box.
    ///
    /// The editor owns this focus, the EventSystem does not. The game's input module reads the
    /// real <c>Gamepad.current</c>, so anything selected in the EventSystem gets the same cross
    /// press twice: once from the module, once from us. So:
    ///
    /// 1. the walk keeps its own index into its own list,
    /// 2. the EventSystem selection stays null while a button is focused, and
    /// 3. pressing is <c>onClick.Invoke()</c>, done here.
    ///
    /// The one exception is a text box: typing needs the EventSystem, so focusing a box selects
    /// it for real. <see cref="ModUi.Typing"/> then goes true and the pad stands back on its own.
    ///
    /// The ring is one object that moves and resizes over the focused widget. Nothing is cached:
    /// the list is read again on every move, because a tab switch, a search term or the material
    /// list changes what the left panel holds.
    /// </summary>
    internal static class FocusNav
    {
        /// <summary>The ring: 2 px thick, 3 px outside the widget.</summary>
        private const float Thickness = 2f;

        private const float Outset = 3f;

        private const int Regions = 3;

        private static readonly List<Selectable> Walk = new List<Selectable>();
        private static readonly Vector3[] Corners = new Vector3[4];

        private static RectTransform _ring;
        private static int _generation = -1;
        private static int _index;
        private static bool _wasTyping;

        /// <summary>True while the focus is in a panel instead of the 3D pane.</summary>
        public static bool Active { get; private set; }

        public static FocusRegion Current { get; private set; }

        /// <summary>The focused widget, or null when the walk is off.</summary>
        public static Selectable Focused { get; private set; }

        /// <summary>Where the focus sits in the current region's list.</summary>
        public static int Index => _index;

        /// <summary>How many widgets the current region holds.</summary>
        public static int Count => Walk.Count;

        /// <summary>The current region's widgets, in walk order. Read only.</summary>
        public static IReadOnlyList<Selectable> Widgets => Walk;

        /// <summary>The ring object, for the autotest to measure.</summary>
        public static RectTransform Ring => _ring;

        /// <summary>Builds the ring under the window's canvas. Rebuilt after a world load.</summary>
        public static void Ensure(RectTransform root)
        {
            if (root == null || (_ring != null && _generation == UiTheme.Generation))
            {
                return;
            }

            _generation = UiTheme.Generation;
            Build(root);
        }

        /// <summary>Turns the walk on: the first region that has a widget, and its first widget.</summary>
        public static void Enter()
        {
            if (!ModUi.Open)
            {
                return;
            }

            // The window opens with a vanilla build-menu button selected. It has to let go, or the
            // game's own input module presses it on the first cross or Enter.
            Deselect();

            for (var i = 0; i < Regions; i++)
            {
                var region = (FocusRegion)i;
                Collect(region);
                if (Walk.Count > 0)
                {
                    Current = region;
                    Active = true;
                    Focus(0);
                    return;
                }
            }
        }

        /// <summary>Turns the walk off: the ring goes, and a box that was typing lets go.</summary>
        public static void Leave()
        {
            LetGoOfField();
            Active = false;
            Focused = null;
            Walk.Clear();
            _index = 0;
            if (_ring != null)
            {
                _ring.gameObject.SetActive(false);
            }
        }

        /// <summary>One step on in the region, wrapping at both ends.</summary>
        public static void Move(int delta)
        {
            if (!Active || delta == 0)
            {
                return;
            }

            var was = Focused;
            Collect(Current);
            if (Walk.Count == 0)
            {
                Leave();
                return;
            }

            var at = was != null ? Walk.IndexOf(was) : -1;
            var next = at >= 0 ? at + delta : _index;
            Focus(((next % Walk.Count) + Walk.Count) % Walk.Count);
        }

        /// <summary>The next or previous region, wrapping, landing on its first widget.</summary>
        public static void NextRegion(int delta)
        {
            if (!Active)
            {
                Enter();
                return;
            }

            if (delta == 0)
            {
                return;
            }

            for (var step = 1; step <= Regions; step++)
            {
                var next = (FocusRegion)((((int)Current + (delta * step)) % Regions + Regions) % Regions);
                Collect(next);
                if (Walk.Count > 0)
                {
                    Current = next;
                    Focus(0);
                    return;
                }
            }
        }

        /// <summary>Cross or Enter: a button fires, a text box starts typing.</summary>
        public static void Press()
        {
            if (!Active || Focused == null)
            {
                return;
            }

            if (Focused is TMP_InputField field)
            {
                var system = EventSystem.current;
                if (system != null)
                {
                    system.SetSelectedGameObject(field.gameObject);
                }

                field.ActivateInputField();
                _wasTyping = false;
                return;
            }

            if (Focused is Button button && button.interactable)
            {
                button.onClick.Invoke();
            }
        }

        /// <summary>
        /// Once a frame: keeps the ring on the focused widget, and picks a new one when the old
        /// one went away (a tab switch, a filter, a button that switched itself off).
        /// </summary>
        public static void Refresh()
        {
            if (!Active)
            {
                return;
            }

            if (!ModUi.Open)
            {
                Leave();
                return;
            }

            // Esc puts a text box back but leaves it selected. Let it go, so nothing of ours is
            // ever selected while the ring is on a button.
            if (Focused is TMP_InputField field)
            {
                if (field.isFocused)
                {
                    _wasTyping = true;
                }
                else if (_wasTyping)
                {
                    _wasTyping = false;
                    Deselect(field.gameObject);
                }
            }

            var was = Focused;
            Collect(Current);
            if (Walk.Count == 0)
            {
                Leave();
                return;
            }

            var at = was != null ? Walk.IndexOf(was) : -1;
            if (at >= 0)
            {
                _index = at;
                Place();
                return;
            }

            Focus(Mathf.Clamp(_index, 0, Walk.Count - 1));
        }

        // ---------- the list ----------

        private static void Collect(FocusRegion region)
        {
            Walk.Clear();
            var rect = RegionRect(region);
            if (rect == null)
            {
                return;
            }

            // false: only the widgets that are really on screen, so a hidden tab's pane is out.
            foreach (var selectable in rect.GetComponentsInChildren<Selectable>(false))
            {
                if (selectable != null && selectable.interactable && selectable.gameObject.activeInHierarchy)
                {
                    Walk.Add(selectable);
                }
            }
        }

        private static RectTransform RegionRect(FocusRegion region)
        {
            switch (region)
            {
                case FocusRegion.TopBar: return EditorWindow.TopBar;
                case FocusRegion.Left: return EditorWindow.LeftPanel;
                default: return EditorWindow.RightPanel;
            }
        }

        private static void Focus(int index)
        {
            LetGoOfField();
            _index = Walk.Count == 0 ? 0 : Mathf.Clamp(index, 0, Walk.Count - 1);
            Focused = Walk.Count > 0 ? Walk[_index] : null;
            if (_ring != null)
            {
                _ring.SetAsLastSibling();
            }

            Place();
        }

        /// <summary>The focused box stops typing and lets the EventSystem go.</summary>
        private static void LetGoOfField()
        {
            _wasTyping = false;
            if (!(Focused is TMP_InputField field))
            {
                return;
            }

            field.DeactivateInputField();
            Deselect(field.gameObject);
        }

        private static void Deselect(GameObject only = null)
        {
            var system = EventSystem.current;
            if (system == null || system.currentSelectedGameObject == null)
            {
                return;
            }

            if (only == null || system.currentSelectedGameObject == only)
            {
                system.SetSelectedGameObject(null);
            }
        }

        // ---------- the ring ----------

        private static void Place()
        {
            if (_ring == null)
            {
                return;
            }

            var parent = _ring.parent as RectTransform;
            var target = Active && Focused != null ? (RectTransform)Focused.transform : null;
            if (target == null || parent == null)
            {
                _ring.gameObject.SetActive(false);
                return;
            }

            target.GetWorldCorners(Corners);
            var min = (Vector2)parent.InverseTransformPoint(Corners[0]);
            var max = (Vector2)parent.InverseTransformPoint(Corners[2]);
            var origin = new Vector2(parent.rect.xMin, parent.rect.yMin);
            _ring.anchoredPosition = min - origin - new Vector2(Outset, Outset);
            _ring.sizeDelta = (max - min) + new Vector2(Outset * 2f, Outset * 2f);

            // A dialog covers the panels, so the ring waits behind it.
            _ring.gameObject.SetActive(!Dialogs.IsOpen);
        }

        /// <summary>Four plain bars, tinted with the theme's accent. Nothing is loaded from disk.</summary>
        private static void Build(RectTransform root)
        {
            var ring = UiBuild.Rect("FocusRing", root);
            ring.anchorMin = Vector2.zero;
            ring.anchorMax = Vector2.zero;
            ring.pivot = Vector2.zero;

            Bar(ring, "Top", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, Thickness));
            Bar(ring, "Bottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, Thickness));
            Bar(ring, "Left", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(Thickness, 0f));
            Bar(ring, "Right", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(Thickness, 0f));

            ring.gameObject.SetActive(false);
            _ring = ring;
        }

        private static void Bar(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var image = UiBuild.Panel(name, parent, null, UiTheme.Accent);
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }
    }
}
