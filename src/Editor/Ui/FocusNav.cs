using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The three regions the walk covers, laid out left to right on screen. R1 goes on through
    /// them in this order and wraps, L1 goes back the same way.
    /// </summary>
    internal enum FocusRegion
    {
        Left,
        TopBar,
        Right,

        /// <summary>The dialog on top. It stands on its own: L1 and R1 cannot walk out of it.</summary>
        Dialog,
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
    /// The arrows, the D-pad and the left stick step by screen position (<see cref="Step"/>): up
    /// and down go to the row above or below, left and right along the row. Tab keeps the plain
    /// order (<see cref="Move"/>).
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

        /// <summary>The right stick pushed all the way scrolls this far a second, in the list's own units.</summary>
        public const float ScrollSpeed = 700f;

        // Where the walk starts. The top bar, whatever the left-to-right order above is.
        private static readonly FocusRegion[] EnterOrder =
        {
            FocusRegion.TopBar, FocusRegion.Left, FocusRegion.Right,
        };

        // The regions a step can go on into, when its own has nothing on that side.
        private static readonly FocusRegion[] Panels =
        {
            FocusRegion.Left, FocusRegion.TopBar, FocusRegion.Right,
        };

        private static readonly List<Selectable> Walk = new List<Selectable>();
        private static readonly List<Selectable> Beside = new List<Selectable>();
        private static readonly Vector3[] Corners = new Vector3[4];

        private static RectTransform _ring;
        private static int _generation = -1;
        private static int _index;
        private static bool _wasTyping;

        // The widget the last step left, and which way it went, so the step back returns to it.
        private static Selectable _cameFrom;
        private static Vector2Int _cameBy;

        // Where the walk was before a dialog took it, so closing the dialog puts it back.
        private static FocusRegion _before;
        private static bool _wasActive;

        /// <summary>True while the focus is in a panel instead of the 3D pane.</summary>
        public static bool Active { get; private set; }

        /// <summary>True while the walk is inside a dialog, so the dialog's own Enter stands back.</summary>
        public static bool InDialog => Active && Current == FocusRegion.Dialog;

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

            if (Dialogs.IsOpen)
            {
                EnterDialog();
                return;
            }

            // The window opens with a vanilla build-menu button selected. It has to let go, or the
            // game's own input module presses it on the first cross or Enter.
            Deselect();

            foreach (var region in EnterOrder)
            {
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

        /// <summary>
        /// The window opened again. A walk that was on when it closed stays on, on the same widget
        /// when that still exists. The game may have selected one of its own buttons meanwhile, and
        /// its input module would press that on the first cross, so it lets go first.
        /// </summary>
        public static void Resume()
        {
            if (!Active)
            {
                return;
            }

            // The dialog went with a world change, the window was built again without it.
            if (Current == FocusRegion.Dialog && !Dialogs.IsOpen)
            {
                LeaveDialog();
                return;
            }

            Deselect();
            Refresh();
        }

        /// <summary>
        /// Takes the walk into the dialog that is up, on the widget the dialog asked for. Where
        /// the walk was is remembered, so closing the dialog puts it back there.
        /// </summary>
        public static void EnterDialog()
        {
            if (!ModUi.Open || !Dialogs.IsOpen)
            {
                return;
            }

            if (Current != FocusRegion.Dialog)
            {
                _wasActive = Active;
                _before = Current;
            }

            Deselect();
            Current = FocusRegion.Dialog;
            Active = true;
            Collect(Current);
            if (Walk.Count == 0)
            {
                // Nothing to walk. Off the region again, or the next call would think the walk
                // was already in a dialog and forget where it really was.
                Current = _before;
                Leave();
                return;
            }

            var start = Dialogs.FocusStart;
            var at = start != null ? Walk.IndexOf(start) : -1;
            Focus(at >= 0 ? at : 0);
        }

        /// <summary>The dialog is gone: back to the panel the walk came from, or off.</summary>
        public static void LeaveDialog()
        {
            if (Current != FocusRegion.Dialog)
            {
                return;
            }

            LetGoOfField();
            Focused = null;
            Walk.Clear();
            _index = 0;

            // Off the dialog first, whatever happens next, or the next EnterDialog would think
            // the walk was already in one and forget where it really was.
            Current = _before;
            if (!_wasActive)
            {
                Leave();
                return;
            }

            Collect(Current);
            if (Walk.Count == 0)
            {
                Leave();
                return;
            }

            Focus(0);
        }

        /// <summary>
        /// A focused text box gives the keyboard back. The pad has no Esc, so this is how circle
        /// gets out of a box and on to the rest of the dialog.
        /// </summary>
        public static void StopTyping()
        {
            LetGoOfField();
        }

        /// <summary>
        /// One step to the widget on that side of the focused one, as it looks on screen:
        /// <paramref name="dx"/> 1 is right, <paramref name="dy"/> 1 is up. Up and down go to the
        /// row above or below, left and right go along the row. When the region has nothing on
        /// that side, the step goes on into the region next to it on that side: the top bar sits
        /// over both panels, and the two panels face each other across the 3D pane. A dialog
        /// keeps the step inside it. At an edge nothing happens, nothing wraps.
        /// </summary>
        public static void Step(int dx, int dy)
        {
            var by = new Vector2Int(Sign(dx), Sign(dy));
            if (!Active || by == Vector2Int.zero)
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

            if (was == null || !Walk.Contains(was))
            {
                Focus(Mathf.Clamp(_index, 0, Walk.Count - 1));
                return;
            }

            var from = Box(was);
            var best = Pick(was, from, Walk, by, false, out _, out _);
            if (best != null)
            {
                Focus(Walk.IndexOf(best));
                Remember(was, by);
                return;
            }

            if (Current == FocusRegion.Dialog)
            {
                return;
            }

            // Nothing on that side here: the regions that lie on that side of this one and share
            // its height (left, right) or its width (up, down). The closest widget there wins.
            var here = Box(RegionRect(Current));
            var bestRegion = Current;
            Selectable bestThere = null;
            var bestCross = float.MaxValue;
            var bestGap = float.MaxValue;
            foreach (var region in Panels)
            {
                var rect = RegionRect(region);
                if (region == Current || rect == null || !Lies(here, Box(rect), by))
                {
                    continue;
                }

                CollectInto(region, Beside);
                var there = Pick(was, from, Beside, by, true, out var cross, out var gap);
                if (there != null && (cross < bestCross - 0.5f || (cross < bestCross + 0.5f && gap < bestGap)))
                {
                    bestRegion = region;
                    bestThere = there;
                    bestCross = cross;
                    bestGap = gap;
                }
            }

            if (bestThere == null)
            {
                return;
            }

            Current = bestRegion;
            Collect(Current);
            Focus(Walk.IndexOf(bestThere));
            Remember(was, by);
        }

        /// <summary>
        /// The right stick scrolls the panel the walk is in: up on the stick goes up the list. The
        /// list is the one round the focused widget when that one has more than it shows, else the
        /// tallest one in the region that does (the palette grid on the left, the blueprint card on
        /// the right). The ring hides while its widget is scrolled out of sight, and the next step
        /// scrolls it back. False when there is nothing to scroll.
        /// </summary>
        public static bool Scroll(float stick, float dt)
        {
            if (!Active || Mathf.Abs(stick) < 0.01f || dt <= 0f)
            {
                return false;
            }

            var scroll = ScrollTarget();
            if (scroll == null)
            {
                return false;
            }

            var room = Room(scroll);
            var at = scroll.content.anchoredPosition;
            var to = Mathf.Clamp(at.y - (stick * ScrollSpeed * dt), 0f, room);
            if (Mathf.Abs(to - at.y) < 0.001f)
            {
                return false;
            }

            at.y = to;
            scroll.content.anchoredPosition = at;
            scroll.velocity = Vector2.zero;
            Place();
            return true;
        }

        /// <summary>The list the right stick scrolls now, or null. For the tests too.</summary>
        public static ScrollRect ScrollTarget()
        {
            var region = RegionRect(Current);
            if (!Active || region == null)
            {
                return null;
            }

            for (var t = Focused != null ? Focused.transform : null; t != null && t != region; t = t.parent)
            {
                var own = t.GetComponent<ScrollRect>();
                if (own != null && own.content != null && Focused.transform.IsChildOf(own.content) && Room(own) > 0.5f)
                {
                    return own;
                }
            }

            ScrollRect best = null;
            var tallest = 0f;
            foreach (var scroll in region.GetComponentsInChildren<ScrollRect>(false))
            {
                if (scroll == null || !scroll.isActiveAndEnabled || Room(scroll) <= 0.5f)
                {
                    continue;
                }

                var height = View(scroll).rect.height;
                if (height > tallest)
                {
                    tallest = height;
                    best = scroll;
                }
            }

            return best;
        }

        /// <summary>How far a vertical list can scroll, 0 when it shows all it holds.</summary>
        private static float Room(ScrollRect scroll)
        {
            if (scroll == null || !scroll.vertical || scroll.content == null)
            {
                return 0f;
            }

            return Mathf.Max(0f, scroll.content.rect.height - View(scroll).rect.height);
        }

        private static RectTransform View(ScrollRect scroll)
        {
            return scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
        }

        /// <summary>One step on in the region's order, wrapping at both ends. Tab and Shift+Tab.</summary>
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

            // L1 and R1 cannot walk out of a dialog: it covers the panels.
            if (delta == 0 || Current == FocusRegion.Dialog)
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
            CollectInto(region, Walk);
        }

        private static void CollectInto(FocusRegion region, List<Selectable> into)
        {
            into.Clear();
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
                    into.Add(selectable);
                }
            }
        }

        // ---------- the step by screen position ----------

        /// <summary>One widget on the stepped side, measured against the focused one.</summary>
        private struct Near
        {
            public Selectable Widget;
            public Rect Box;

            /// <summary>The space between the two along the step. 0 when they overlap.</summary>
            public float Gap;

            /// <summary>The space between the two across the step. 0 when one sits over the other.</summary>
            public float Cross;

            /// <summary>How far their left edges (or top edges, for a sideways step) are apart.</summary>
            public float Edge;
        }

        private static readonly List<Near> Nears = new List<Near>();

        /// <summary>
        /// The widget on that side of <paramref name="from"/>, or null.
        ///
        /// Up and down take the nearest row on that side, then the widget in it that sits over or
        /// under this one, lined up by the left edge. Left and right stay on this row. A sideways
        /// step into another region (<paramref name="across"/>) takes the closest widget there.
        /// A widget scrolled out of sight is only reached from inside its own list, where the
        /// step scrolls it into view. The step straight back always returns to where it came from.
        /// </summary>
        private static Selectable Pick(
            Selectable was,
            Rect from,
            List<Selectable> widgets,
            Vector2Int by,
            bool across,
            out float cross,
            out float gap)
        {
            cross = gap = float.MaxValue;
            var vertical = by.y != 0;
            var sign = vertical ? by.y : by.x;
            var ownList = ListOf(was);

            Nears.Clear();
            foreach (var widget in widgets)
            {
                if (widget == null || widget == was)
                {
                    continue;
                }

                var box = Box((RectTransform)widget.transform);
                if (!OnSide(from, box, vertical, sign))
                {
                    continue;
                }

                var list = ListOf(widget);
                if (list != null && list != ownList && !InSight(list, box))
                {
                    continue;
                }

                Nears.Add(new Near
                {
                    Widget = widget,
                    Box = box,
                    Gap = vertical ? Apart(from.yMin, from.yMax, box.yMin, box.yMax)
                        : Apart(from.xMin, from.xMax, box.xMin, box.xMax),
                    Cross = vertical ? Apart(from.xMin, from.xMax, box.xMin, box.xMax)
                        : Apart(from.yMin, from.yMax, box.yMin, box.yMax),
                    Edge = vertical ? Mathf.Abs(box.xMin - from.xMin) : Mathf.Abs(box.yMax - from.yMax),
                });
            }

            if (Nears.Count == 0)
            {
                return null;
            }

            var pick = -1;
            if (_cameFrom != null && _cameBy == -by)
            {
                pick = Nears.FindIndex(n => n.Widget == _cameFrom);
                if (pick >= 0)
                {
                    // Below any real distance, so it also wins over the other region's pick.
                    cross = gap = -1f;
                    return _cameFrom;
                }
            }

            if (pick < 0)
            {
                pick = vertical ? NextRow() : across ? Closest() : AlongRow(from);
            }

            if (pick < 0)
            {
                return null;
            }

            cross = Nears[pick].Cross;
            gap = Nears[pick].Gap;
            return Nears[pick].Widget;
        }

        /// <summary>Up or down: the nearest row, then the widget in it most in line with this one.</summary>
        private static int NextRow()
        {
            var near = 0;
            for (var i = 1; i < Nears.Count; i++)
            {
                if (Less(Nears[i].Gap, Nears[near].Gap, Nears[i].Cross, Nears[near].Cross))
                {
                    near = i;
                }
            }

            var row = Nears[near].Box;
            var pick = near;
            for (var i = 0; i < Nears.Count; i++)
            {
                var box = Nears[i].Box;
                if (Same(box.yMin, box.yMax, row.yMin, row.yMax)
                    && Less(Nears[i].Cross, Nears[pick].Cross, Nears[i].Edge, Nears[pick].Edge))
                {
                    pick = i;
                }
            }

            return pick;
        }

        /// <summary>Left or right: the next widget on this row, or none.</summary>
        private static int AlongRow(Rect from)
        {
            var pick = -1;
            for (var i = 0; i < Nears.Count; i++)
            {
                var box = Nears[i].Box;
                if (Same(box.yMin, box.yMax, from.yMin, from.yMax)
                    && (pick < 0 || Less(Nears[i].Gap, Nears[pick].Gap, Nears[i].Edge, Nears[pick].Edge)))
                {
                    pick = i;
                }
            }

            return pick;
        }

        /// <summary>Into the panel across the 3D pane: the widget closest in height.</summary>
        private static int Closest()
        {
            var pick = 0;
            for (var i = 1; i < Nears.Count; i++)
            {
                if (Less(Nears[i].Cross, Nears[pick].Cross, Nears[i].Gap, Nears[pick].Gap))
                {
                    pick = i;
                }
            }

            return pick;
        }

        /// <summary>First by <paramref name="a"/>, then by <paramref name="b"/>, half a pixel counting as equal.</summary>
        private static bool Less(float a, float aOther, float b, float bOther)
        {
            return a < aOther - 0.5f || (a < aOther + 0.5f && b < bOther - 0.5f);
        }

        /// <summary>
        /// True when <paramref name="box"/> lies on that side of <paramref name="from"/>: its middle
        /// is past this one's, and the two are not on the same row (a step up or down) or in the
        /// same column (a step left or right).
        /// </summary>
        private static bool OnSide(Rect from, Rect box, bool vertical, int sign)
        {
            var along = vertical ? box.center.y - from.center.y : box.center.x - from.center.x;
            if (along * sign <= 0.5f)
            {
                return false;
            }

            return vertical
                ? !Same(from.yMin, from.yMax, box.yMin, box.yMax)
                : !Same(from.xMin, from.xMax, box.xMin, box.xMax);
        }

        /// <summary>A region lies on that side of another and shares its height or width.</summary>
        private static bool Lies(Rect here, Rect there, Vector2Int by)
        {
            if (by.y != 0)
            {
                return (there.center.y - here.center.y) * by.y > 0f
                    && Apart(here.xMin, here.xMax, there.xMin, there.xMax) <= 0f;
            }

            return (there.center.x - here.center.x) * by.x > 0f
                && Apart(here.yMin, here.yMax, there.yMin, there.yMax) <= 0f;
        }

        /// <summary>The same row (or column): the two spans overlap by half the shorter one or more.</summary>
        private static bool Same(float aMin, float aMax, float bMin, float bMax)
        {
            var overlap = Mathf.Min(aMax, bMax) - Mathf.Max(aMin, bMin);
            return overlap >= 0.5f * Mathf.Min(aMax - aMin, bMax - bMin);
        }

        /// <summary>The space between two spans, 0 when they overlap.</summary>
        private static float Apart(float aMin, float aMax, float bMin, float bMax)
        {
            return Mathf.Max(0f, Mathf.Max(aMin, bMin) - Mathf.Min(aMax, bMax));
        }

        /// <summary>The scroll list a widget sits in, or null.</summary>
        private static ScrollRect ListOf(Selectable widget)
        {
            var scroll = widget != null ? widget.GetComponentInParent<ScrollRect>() : null;
            return scroll != null && scroll.content != null && widget.transform.IsChildOf(scroll.content)
                ? scroll
                : null;
        }

        private static bool InSight(ScrollRect scroll, Rect box)
        {
            var view = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
            return Box(view).Contains(box.center);
        }

        /// <summary>A widget's rectangle on screen, x to the right and y up.</summary>
        private static Rect Box(Selectable widget)
        {
            return Box((RectTransform)widget.transform);
        }

        private static Rect Box(RectTransform rect)
        {
            if (rect == null)
            {
                return Rect.zero;
            }

            rect.GetWorldCorners(Corners);
            return Rect.MinMaxRect(Corners[0].x, Corners[0].y, Corners[2].x, Corners[2].y);
        }

        private static void Remember(Selectable from, Vector2Int by)
        {
            _cameFrom = from;
            _cameBy = by;
        }

        private static int Sign(int value)
        {
            return value > 0 ? 1 : value < 0 ? -1 : 0;
        }

        private static RectTransform RegionRect(FocusRegion region)
        {
            switch (region)
            {
                case FocusRegion.TopBar: return EditorWindow.TopBar;
                case FocusRegion.Left: return EditorWindow.LeftPanel;
                case FocusRegion.Dialog: return Dialogs.Modal;
                default: return EditorWindow.RightPanel;
            }
        }

        private static void Focus(int index)
        {
            LetGoOfField();
            _cameFrom = null;
            _index = Walk.Count == 0 ? 0 : Mathf.Clamp(index, 0, Walk.Count - 1);
            Focused = Walk.Count > 0 ? Walk[_index] : null;
            if (_ring != null)
            {
                _ring.SetAsLastSibling();
            }

            ShowInList(Focused);
            Place();
        }

        /// <summary>
        /// Scrolls a focused widget into view. Without it the walk reaches a file far down the
        /// open dialog's list and the ring sits outside the window.
        /// </summary>
        private static void ShowInList(Selectable widget)
        {
            if (widget == null)
            {
                return;
            }

            var scroll = widget.GetComponentInParent<ScrollRect>();
            if (scroll == null || !scroll.vertical || scroll.content == null || scroll.viewport == null)
            {
                return;
            }

            var target = (RectTransform)widget.transform;
            if (!target.IsChildOf(scroll.content))
            {
                return;
            }

            target.GetWorldCorners(Corners);
            var view = scroll.viewport;
            var top = view.InverseTransformPoint(Corners[1]).y;
            var bottom = view.InverseTransformPoint(Corners[0]).y;

            var shift = 0f;
            if (top > view.rect.yMax)
            {
                shift = top - view.rect.yMax;
            }
            else if (bottom < view.rect.yMin)
            {
                shift = bottom - view.rect.yMin;
            }

            if (Mathf.Abs(shift) < 0.5f)
            {
                return;
            }

            var at = scroll.content.anchoredPosition;
            at.y = Mathf.Clamp(at.y - shift, 0f, Mathf.Max(0f, scroll.content.rect.height - view.rect.height));
            scroll.content.anchoredPosition = at;
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

            // Scrolled out of its list (the right stick moved the list, not the walk): no ring
            // hanging outside the panel. The next step scrolls the widget back into sight.
            var list = ListOf(Focused);
            if (list != null && !InSight(list, Box(target)))
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

            // A dialog covers the panels, so the ring waits behind it unless it is in the dialog.
            _ring.gameObject.SetActive(!Dialogs.IsOpen || Current == FocusRegion.Dialog);
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
