using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Input;
using ValheimTomrer.Editor.Placement;
using ValheimTomrer.Editor.View;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The 3D pane in the middle of the window: a RawImage showing what the preview camera draws,
    /// the crosshair and the text over the picture, plus the once-a-frame work behind it (keep the
    /// model and the document in step, aim, draw the ghost, the snap dots and the selection boxes,
    /// read input, render).
    ///
    /// <see cref="Captured"/> says who has the mouse. The window opens with the mouse on the
    /// panels; a click on the picture gives it to the pane, and Esc gives it back.
    ///
    /// The scene, the camera and the texture live only while the editor is open. Closing it
    /// destroys all three.
    /// </summary>
    internal static class ViewportHost
    {
        private const float Inset = 6f;          // lets the sunken frame show around the picture
        private const float BoxSelectThreshold = 4f;
        private const float MessageSeconds = 6f;

        /// <summary>How far the mouse has to move over the pane to take the aim back from the pad.</summary>
        private const float MouseTakeOver = 3f;

        private static RectTransform _host;
        private static RawImage _image;
        private static GameObject _crosshair;
        private static TextMeshProUGUI _hint;
        private static TextMeshProUGUI _placeLine;
        private static TextMeshProUGUI _stateLine;
        private static TextMeshProUGUI _aimName;
        private static Image _selectRect;
        private static int _generation = -1;

        private static EditorScene _scene;
        private static PreviewCamera _preview;
        private static ViewportRaycast _raycast;
        private static EditorCamera _camera;
        private static BlueprintPreview _model;
        private static Transform _modelRoot;
        private static SceneModel _pieces;
        private static GhostRenderer _ghost;
        private static SnapDots _dots;
        private static SelectionBoxes _boxes;
        private static IEnumerator _fill;
        private static float _panDepth = 10f;

        private static MovingSet _ghostSet;
        private static Vector2 _aimAt = new Vector2(0.5f, 0.5f);
        private static int _aimPiece = -1;
        private static Vector2 _dragStart;
        private static Vector2 _lastMouse;
        private static bool _hasLastMouse;
        private static bool _boxSelecting;
        private static long _boxSignature = -1;
        private static readonly List<Bounds> BoxList = new List<Bounds>();

        // Kept after closing, only so the autotest can prove nothing was left behind.
        private static EditorScene _closedScene;
        private static PreviewCamera _closedCamera;

        /// <summary>True once every piece stands and the bounds are known.</summary>
        public static bool Ready => _scene != null && _fill == null && (_model == null || _model.Done);

        public static EditorCamera Camera => _camera;

        public static EditorScene Scene => _scene;

        public static PreviewCamera Preview => _preview;

        public static BlueprintPreview Model => _model;

        public static ViewportRaycast Raycast => _raycast;

        /// <summary>The copies standing in the pane, kept in step with the document.</summary>
        public static SceneModel Pieces => _pieces;

        /// <summary>The see-through copy of what is in hand.</summary>
        public static GhostRenderer Ghost => _ghost;

        public static SnapDots Dots => _dots;

        public static SelectionBoxes Boxes => _boxes;

        /// <summary>Where in the pane the placing rule is aiming, 0..1.</summary>
        public static Vector2 AimAt => _aimAt;

        /// <summary>The piece the aim is on, or -1.</summary>
        public static int AimPiece => _aimPiece;

        /// <summary>
        /// The controller aims, with the crosshair in the middle of the view, like the game.
        /// A real mouse move over the pane gives the aim back.
        /// </summary>
        public static bool PadAim { get; private set; }

        /// <summary>
        /// True while the pane has the mouse: the cursor is held, the mouse turns the view and the
        /// crosshair aims. A click on the picture takes it, Esc gives it back. It is not a camera
        /// setting, the camera always flies free.
        /// </summary>
        public static bool Captured { get; private set; }

        /// <summary>A click on the picture: the pane takes the mouse, and the crosshair aims.</summary>
        public static void Capture()
        {
            if (_camera == null)
            {
                return;
            }

            Captured = true;
            ModUi.LockCursor = true;
            GiveAimBack();
        }

        /// <summary>Esc: the cursor goes back to the window. False means the pane did not have it.</summary>
        public static bool Release()
        {
            if (!Captured)
            {
                return false;
            }

            Captured = false;
            ModUi.LockCursor = false;
            return true;
        }

        /// <summary>The pad woke: the crosshair takes the aim and the UI lets go of its button.</summary>
        public static void TakeAim()
        {
            PadAim = true;
            ModUi.ClearSelection();
        }

        /// <summary>The mouse is in charge again: a click on the pane, or a real move over it.</summary>
        public static void GiveAimBack()
        {
            PadAim = false;
        }

        /// <summary>
        /// The mouse moved to a screen point. Over the pane, a move of more than 3 px takes the
        /// aim back from the pad, the way the Tomrer editor does it.
        /// </summary>
        public static void MouseMoved(Vector2 screen)
        {
            if (_raycast == null || Captured)
            {
                // The pane has the mouse: the cursor is held, so there is no real move to read.
                return;
            }

            if (!_raycast.ScreenToViewport(screen, out _))
            {
                // Off the pane: the next move back onto it counts as a real one.
                _hasLastMouse = false;
                return;
            }

            if (PadAim && (!_hasLastMouse || Vector2.Distance(screen, _lastMouse) > MouseTakeOver))
            {
                GiveAimBack();
            }

            _lastMouse = screen;
            _hasLastMouse = true;
        }

        /// <summary>Builds the pane's widgets. Called after the window itself is built.</summary>
        public static void Ensure(RectTransform host)
        {
            if (host == null)
            {
                return;
            }

            if (_host == host && _image != null && _generation == UiTheme.Generation)
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Build(host);
        }

        /// <summary>
        /// Puts a blueprint in the pane and starts building it, a few pieces a frame. A null
        /// blueprint opens an empty scene, which is what a new blueprint starts from.
        /// </summary>
        public static void Show(ResolvedBlueprint blueprint)
        {
            Close();
            if (_image == null)
            {
                return;
            }

            _scene = new EditorScene(EditorConfig.Layer);
            _preview = new PreviewCamera(_scene.Root, EditorConfig.Layer);
            _raycast = new ViewportRaycast(_preview, _image.rectTransform, _scene);
            _camera = new EditorCamera(_preview);
            _ghost = new GhostRenderer(_scene.Root, EditorConfig.Layer);
            _dots = new SnapDots(_scene.Root, EditorConfig.Layer);
            _boxes = new SelectionBoxes(_scene.Root, EditorConfig.Layer);

            if (blueprint != null)
            {
                _model = BlueprintPreview.Empty(blueprint, PreviewStyle.Solid(EditorConfig.Layer));
                _model.Root.SetParent(_scene.Root, false);
                _modelRoot = _model.Root;
                _fill = _model.Fill();
            }
            else
            {
                var root = new GameObject("ValheimTomrer_Blueprint") { layer = EditorConfig.Layer };
                root.transform.SetParent(_scene.Root, false);
                _modelRoot = root.transform;
                _pieces = new SceneModel(_modelRoot, EditorConfig.Layer);
                Frame();
            }

            Fit();
            Release();   // the window opens with the mouse on the panels, not trapped in the pane
            ValheimTomrerPlugin.Log.LogInfo(blueprint != null
                ? $"editor view opened on '{blueprint.Name}' ({_model.Total} pieces)"
                : "editor view opened on an empty blueprint");
        }

        /// <summary>Everything the pane does once a frame, while the window is open.</summary>
        public static void Tick()
        {
            if (_scene == null || !_scene.IsAlive || _preview == null)
            {
                return;
            }

            StepFill();
            Fit();
            Bindings.Tick();
            PadBindings.Tick();
            ReadInput();
            ModUi.LockCursor = Captured;
            if (_crosshair != null)
            {
                _crosshair.SetActive(Captured || PadAim);
            }

            UpdateModel();
            UpdateAim();
            UpdateBoxes();
            UpdateOverlays();

            _dots.Show(EditorState.SnapDotsOn ? EditorState.Aimed : null);
            _preview.Render();
        }

        /// <summary>F: looks at the selection, or at the whole blueprint when nothing is selected.</summary>
        public static void Frame()
        {
            if (_camera == null)
            {
                return;
            }

            var selected = EditorState.SelectedPieces();
            if (selected.Count > 0)
            {
                var box = EditorState.BoxOf(selected);
                if (box.HasValue)
                {
                    _camera.Frame(box.Value);
                    return;
                }
            }

            if (_model != null && _model.Done)
            {
                _camera.Frame(_model.LocalBounds);
                return;
            }

            var pieces = new List<Doc.DocPiece>();
            if (EditorState.Document != null)
            {
                pieces.AddRange(EditorState.Document.Pieces);
            }

            _camera.Frame(EditorState.BoxOf(pieces) ?? new Bounds(Vector3.zero, Vector3.one * 4f));
        }

        public static void Close()
        {
            ModUi.LockCursor = false;
            _ghost?.Destroy();
            _dots?.Destroy();
            _boxes?.Destroy();
            _pieces?.Clear();

            if (_model != null)
            {
                _model.Destroy();
            }
            else if (_modelRoot != null)
            {
                UnityEngine.Object.Destroy(_modelRoot.gameObject);
            }

            if (_scene != null)
            {
                _scene.Destroy();
                _closedScene = _scene;
            }

            if (_preview != null)
            {
                _preview.Destroy();
                _closedCamera = _preview;
            }

            _model = null;
            _modelRoot = null;
            _pieces = null;
            _ghost = null;
            _ghostSet = null;
            _dots = null;
            _boxes = null;
            _boxSignature = -1;
            _aimPiece = -1;
            _boxSelecting = false;
            Captured = false;
            PadAim = false;
            _hasLastMouse = false;
            _fill = null;
            _scene = null;
            _preview = null;
            _raycast = null;
            _camera = null;
            HideSelectRect();
            if (_image != null)
            {
                _image.texture = null;
                _image.color = new Color(0f, 0f, 0f, 0f);
            }
        }

        /// <summary>Objects from the last closed view that Unity has not destroyed. Must be 0.</summary>
        public static int Leaked()
        {
            var leaked = 0;
            if (_closedScene != null)
            {
                leaked += _closedScene.AliveOwned() + (_closedScene.IsAlive ? 1 : 0);
            }

            if (_closedCamera != null)
            {
                leaked += (_closedCamera.IsAlive ? 1 : 0) + (_closedCamera.TextureAlive ? 1 : 0);
            }

            return leaked;
        }

        // ---------- the once-a-frame work ----------

        private static void StepFill()
        {
            if (_fill == null)
            {
                return;
            }

            if (_fill.MoveNext())
            {
                Status($"building {_model.Built}/{_model.Total} pieces");
                return;
            }

            _fill = null;
            _scene.SetFront(_model.LocalBounds);
            _pieces = new SceneModel(_modelRoot, EditorConfig.Layer);
            _pieces.Adopt(EditorState.Document, _model);
            Frame();
        }

        /// <summary>The copies follow the document: a new piece appears, a deleted one goes.</summary>
        private static void UpdateModel()
        {
            if (_pieces == null)
            {
                return;
            }

            _pieces.Sync(EditorState.Document);
            _pieces.Hide(EditorState.Carrying);
        }

        /// <summary>
        /// Where the placing rule is aiming, and what is under it. The ray is turned into the
        /// editor scene's own space, where y = 0 is the grid the rule works against.
        /// </summary>
        private static void UpdateAim()
        {
            _aimAt = AimPoint();
            _aimPiece = -1;
            if (_pieces != null && _raycast.Pick(_aimAt, out var hit))
            {
                _aimPiece = _pieces.IdOf(hit.collider != null ? hit.collider.transform : null);
            }

            if (EditorState.Mode != EditMode.Place || EditorState.Moving == null)
            {
                if (_ghostSet != null)
                {
                    _ghost.Show(null);
                    _ghostSet = null;
                }

                EditorState.ClearAim();
                return;
            }

            if (!ReferenceEquals(_ghostSet, EditorState.Moving))
            {
                _ghost.Show(EditorState.Moving);
                _ghostSet = EditorState.Moving;
            }

            var ray = _raycast.RayAt(_aimAt);
            var origin = _scene.Root.InverseTransformPoint(ray.origin);
            var direction = _scene.Root.InverseTransformDirection(ray.direction);
            if (EditorState.Aim(origin, direction, out var result))
            {
                _ghost.Place(result.Pos, result.Rot, result.Duplicate);
            }
            else
            {
                _ghost.Hide();
            }
        }

        /// <summary>The gold boxes. Rebuilt only when the selection, the document or the aim changed.</summary>
        private static void UpdateBoxes()
        {
            var document = EditorState.Document;
            var signature = ((long)EditorState.Version * 1000003L)
                + ((document != null ? document.Revision : 0) * 1009L)
                + (EditorState.PieceBoxesOn ? 131071L : 0L)
                + _aimPiece;
            if (signature == _boxSignature)
            {
                return;
            }

            _boxSignature = signature;
            BoxList.Clear();
            var selected = EditorState.SelectedPieces();
            foreach (var piece in selected)
            {
                BoxList.Add(EditorState.BoxOf(piece));
            }

            Bounds? group = null;
            if (BoxList.Count > 1)
            {
                var box = BoxList[0];
                for (var i = 1; i < BoxList.Count; i++)
                {
                    box.Encapsulate(BoxList[i]);
                }

                group = box;
            }

            Bounds? aim = null;
            if (_aimPiece >= 0 && document != null && !EditorState.IsSelected(_aimPiece))
            {
                var piece = document.Find(_aimPiece);
                if (piece != null)
                {
                    aim = EditorState.BoxOf(piece);
                }
            }

            _boxes.Show(BoxList, group, aim);
            ShowPieceBoxes(document);
        }

        /// <summary>The Boxes view: the models stop drawing and a wire box stands in for each.</summary>
        private static void ShowPieceBoxes(Doc.BlueprintDocument document)
        {
            if (_pieces == null)
            {
                return;
            }

            _pieces.SetBoxes(EditorState.PieceBoxesOn);
            if (!EditorState.PieceBoxesOn || document == null)
            {
                _boxes.ShowAll(null);
                return;
            }

            var all = new List<Bounds>(document.Pieces.Count);
            foreach (var piece in document.Pieces)
            {
                all.Add(EditorState.BoxOf(piece));
            }

            _boxes.ShowAll(all);
        }

        /// <summary>The lines over the picture: the hint, the place HUD, the aimed piece, the status.</summary>
        private static void UpdateOverlays()
        {
            var placing = EditorState.Mode == EditMode.Place;
            if (_hint != null)
            {
                _hint.gameObject.SetActive(!placing);
                _hint.text = PadAim ? PadHint() : Captured ? CapturedHint : MouseHint;
            }

            if (_placeLine != null)
            {
                _placeLine.gameObject.SetActive(placing);
                _stateLine.gameObject.SetActive(placing);
                if (placing)
                {
                    _placeLine.text = PlaceHud();
                    _stateLine.text = PlaceState();
                }
            }

            if (_aimName != null)
            {
                var document = EditorState.Document;
                var piece = _aimPiece >= 0 && document != null ? document.Find(_aimPiece) : null;
                var entry = piece != null ? Catalog.PieceCatalog.Find(piece.PrefabName) : null;
                _aimName.text = piece == null ? "" : entry != null ? entry.DisplayName : piece.PrefabName;
                _aimName.rectTransform.anchoredPosition =
                    new Vector2(0f, Captured || PadAim ? -18f : -80f);
            }

            if (_fill == null)
            {
                Status(DocumentLine());
            }
        }

        /// <summary>The line along the bottom of the pane, before the pane has the mouse.</summary>
        private const string MouseHint =
            "Click the view to look around with the mouse.   Drag a box to select many pieces."
            + "   Pick a piece on the left to place it.";

        /// <summary>And once it has it.</summary>
        private const string CapturedHint =
            "The mouse looks around, W A S D fly, the wheel zooms.   Click to select a piece, or to drop "
            + "what is in hand.   G moves, Ctrl+D copies, R turns, Del removes.   Esc gives the cursor back.";

        private static string PadHint()
        {
            var g = EditorInput.Glyphs;
            return $"{g.Of(PadButton.Cross)}: pieces menu   |   {g.Of(PadButton.R2)}: place or select   |   "
                + $"{g.Of(PadButton.Square)}: move   |   {g.Of(PadButton.Triangle)}: copy   |   "
                + $"{g.Of(PadButton.R1)}: delete   |   {g.Of(PadButton.Circle)}: back   |   "
                + $"{g.Of(PadButton.Options)}: help";
        }

        private static string PlaceHud()
        {
            var what = EditorState.Held != null
                ? EditorState.Held.DisplayName
                : EditorState.Action == PlaceAction.Move
                    ? Count(EditorState.Moving != null ? EditorState.Moving.Count : 0)
                    : "a copy of " + Count(EditorState.Moving != null ? EditorState.Moving.Count : 0);

            var line = "Placing " + what;
            if (EditorState.Steps != 0)
            {
                line += $" - turned {EditorState.Steps * Placer.RotateStep:0.#} deg";
            }

            var snap = EditorState.ManualName();
            if (snap != null)
            {
                line += " - snap point " + snap;
            }

            return line;
        }

        private static string PlaceState()
        {
            var result = EditorState.Aimed;
            if (result == null)
            {
                return "nothing under the aim";
            }

            if (result.Duplicate)
            {
                return "a piece of this kind is already there";
            }

            if (result.Snapped)
            {
                return result.Assisted ? "snapped (helped)" : "snapped";
            }

            return result.SnapSkipped ? "snap refused: the same piece is there" : "free";
        }

        private static string DocumentLine()
        {
            var document = EditorState.Document;
            if (document == null)
            {
                return "no blueprint open";
            }

            var name = string.IsNullOrEmpty(document.Name) ? "New blueprint" : document.Name;
            var line = $"{name}: {Count(document.Pieces.Count)}";
            if (EditorState.SelectionCount > 0)
            {
                line += $"   |   {EditorState.SelectionCount} selected";
            }

            if (EditorState.Message != null && Time.unscaledTime - EditorState.MessageAt < MessageSeconds)
            {
                line += "   |   " + EditorState.Message;
            }

            return line;
        }

        private static string Count(int n)
        {
            return n == 1 ? "1 piece" : n + " pieces";
        }

        /// <summary>
        /// Keeps the texture the size of the pane in real pixels. A window resize or a GUI-scale
        /// change makes a new one; the old one is released, not left on the graphics card.
        /// </summary>
        private static void Fit()
        {
            var rect = _image.rectTransform.rect;
            var canvas = _image.canvas;
            var scale = canvas != null ? canvas.scaleFactor : 1f;
            if (_preview.Resize(Mathf.RoundToInt(rect.width * scale), Mathf.RoundToInt(rect.height * scale)))
            {
                _image.texture = _preview.Texture;
                _image.color = Color.white;
            }
        }

        /// <summary>
        /// The mouse, while the pane has it. The keys are in <see cref="Bindings"/> and the pad is
        /// in <see cref="PadBindings"/>, both already run this frame.
        /// </summary>
        private static void ReadInput()
        {
            if (Dialogs.IsOpen)
            {
                return;
            }

            // The cursor is held, so there are no drag events left to read.
            if (Captured && Mouse.current != null)
            {
                _camera.MouseLook(Mouse.current.delta.ReadValue());
            }
        }

        private static float Key(KeyCode key)
        {
            return ZInput.GetKey(key, false) ? 1f : 0f;
        }

        private static bool Down(KeyCode key)
        {
            return ZInput.GetKeyDown(key, false);
        }

        private static void Status(string text)
        {
            if (EditorWindow.StatusText != null)
            {
                EditorWindow.StatusText.text = text + "   |   F7 or Esc closes";
            }
        }

        // ---------- mouse, from the UI event system ----------

        private static Vector2 AimPoint()
        {
            // The crosshair aims once the pane has the mouse, and while the controller is in charge.
            if (Captured || PadAim)
            {
                return new Vector2(0.5f, 0.5f);
            }

            var mouse = Mouse.current;
            if (mouse != null && _raycast.ScreenToViewport(mouse.position.ReadValue(), out var at))
            {
                return at;
            }

            return new Vector2(0.5f, 0.5f);
        }

        private static void OnPointerDown(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            // A click on the pane puts the mouse in charge, like the browser editor does.
            GiveAimBack();
            _dragStart = data.position;
            _boxSelecting = false;
            HideSelectRect();
        }

        private static void OnPointerUp(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left || !_boxSelecting)
            {
                return;
            }

            BoxSelect(_dragStart, data.position, Key(KeyCode.LeftShift) + Key(KeyCode.RightShift) > 0f);
            HideSelectRect();
        }

        private static void OnPointerClick(PointerEventData data)
        {
            if (data.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            if (_boxSelecting)
            {
                _boxSelecting = false;
                return;
            }

            // The first click hands the mouse to the pane. After that a click selects or places.
            if (!Captured)
            {
                Capture();
                return;
            }

            ClickAt(data.position, Key(KeyCode.LeftShift) + Key(KeyCode.RightShift) > 0f);
        }

        /// <summary>A click on the picture: drop what is in hand, or pick what is under the cursor.</summary>
        public static void ClickAt(Vector2 screen, bool additive)
        {
            if (_raycast == null || !_raycast.ScreenToViewport(screen, out var at))
            {
                return;
            }

            if (EditorState.Mode == EditMode.Place)
            {
                EditorState.CommitPlacement(EditorState.Aimed);
                return;
            }

            if (_pieces != null && _raycast.Pick(at, out var hit))
            {
                var id = _pieces.IdOf(hit.collider != null ? hit.collider.transform : null);
                if (id >= 0)
                {
                    EditorState.Select(id, additive ? SelectHow.Toggle : SelectHow.Set);
                    return;
                }
            }

            if (!additive)
            {
                EditorState.Select(Array.Empty<int>());
            }
        }

        /// <summary>Everything whose box centre shows inside the dragged rectangle.</summary>
        public static void BoxSelect(Vector2 from, Vector2 to, bool additive)
        {
            var document = EditorState.Document;
            if (document == null || _raycast == null)
            {
                return;
            }

            var min = Vector2.Min(from, to);
            var max = Vector2.Max(from, to);
            var ids = new List<int>();
            foreach (var piece in document.Pieces)
            {
                var centre = _scene.Root.TransformPoint(EditorState.BoxOf(piece).center);
                if (!_raycast.Project(centre, out var screen))
                {
                    continue;
                }

                if (screen.x >= min.x && screen.x <= max.x && screen.y >= min.y && screen.y <= max.y)
                {
                    ids.Add(piece.Id);
                }
            }

            EditorState.Select(ids, additive ? SelectHow.Add : SelectHow.Set);
        }

        private static void OnDragStart(PointerEventData data)
        {
            // Pan grabs whatever is under the cursor, and the point in front when nothing is.
            var depth = _camera != null ? _camera.Distance : 10f;
            if (_raycast != null && _raycast.ScreenToViewport(data.position, out var at))
            {
                depth = _raycast.SurfaceDistance(at) ?? depth;
            }

            _panDepth = Mathf.Min(depth, 500f);
        }

        private static void OnDrag(PointerEventData data)
        {
            if (_camera == null)
            {
                return;
            }

            // Left drag is the selection rectangle, not a camera move.
            if (data.button == PointerEventData.InputButton.Left)
            {
                if (Captured)
                {
                    return;
                }

                if (!_boxSelecting && Vector2.Distance(data.position, _dragStart) >= BoxSelectThreshold)
                {
                    _boxSelecting = true;
                }

                if (_boxSelecting)
                {
                    ShowSelectRect(_dragStart, data.position);
                }

                return;
            }

            var pan = data.button == PointerEventData.InputButton.Middle
                || Key(KeyCode.LeftShift) + Key(KeyCode.RightShift) > 0f;
            var height = PaneHeight();
            if (pan)
            {
                _camera.Pan(data.delta, _panDepth, height);
            }
            else
            {
                _camera.Drag(data.delta, height);
            }
        }

        private static void OnScroll(PointerEventData data)
        {
            if (_camera == null)
            {
                return;
            }

            if (Captured || !_raycast.ScreenToViewport(data.position, out var at))
            {
                // With the cursor held there is nothing to aim with, so the middle it is.
                at = new Vector2(0.5f, 0.5f);
            }

            Bindings.Wheel(data.scrollDelta.y, at);
        }

        private static float PaneHeight()
        {
            var canvas = _image != null ? _image.canvas : null;
            return _image.rectTransform.rect.height * (canvas != null ? canvas.scaleFactor : 1f);
        }

        // ---------- widgets ----------

        private static void ShowSelectRect(Vector2 from, Vector2 to)
        {
            if (_selectRect == null || _image == null)
            {
                return;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_image.rectTransform, from, null, out var a)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_image.rectTransform, to, null, out var b))
            {
                return;
            }

            var rect = _selectRect.rectTransform;
            rect.anchoredPosition = Vector2.Min(a, b);
            rect.sizeDelta = new Vector2(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
            _selectRect.gameObject.SetActive(true);
        }

        private static void HideSelectRect()
        {
            if (_selectRect != null && _selectRect.gameObject.activeSelf)
            {
                _selectRect.gameObject.SetActive(false);
            }
        }

        private static void Build(RectTransform host)
        {
            _image = UiBuild.Rect("View", host).gameObject.AddComponent<RawImage>();
            UiBuild.Stretch(_image.rectTransform, Inset, Inset, Inset, Inset);
            _image.color = new Color(0f, 0f, 0f, 0f);   // nothing to show until the first render
            _image.raycastTarget = true;
            _image.gameObject.AddComponent<ViewportPointer>();

            _crosshair = UiBuild.Rect("Crosshair", _image.rectTransform).gameObject;
            UiBuild.Stretch((RectTransform)_crosshair.transform);
            Bar("H", _crosshair.transform, new Vector2(14f, 2f));
            Bar("V", _crosshair.transform, new Vector2(2f, 14f));
            _crosshair.SetActive(false);

            _aimName = UiBuild.Label("AimName", _image.rectTransform, "", 15f, TextAlignmentOptions.Top, UiTheme.Text);
            Centre(_aimName.rectTransform, new Vector2(0f, -80f), new Vector2(420f, 22f));

            _hint = UiBuild.Label("Hint", _image.rectTransform, MouseHint,
                14f, TextAlignmentOptions.BottomLeft, UiTheme.TextDim);
            Strip(_hint.rectTransform, false, 8f, 44f);

            _placeLine = UiBuild.Label("Placing", _image.rectTransform, "", 17f, TextAlignmentOptions.TopLeft, UiTheme.Accent);
            Strip(_placeLine.rectTransform, true, 10f, 24f);

            _stateLine = UiBuild.Label("PlaceState", _image.rectTransform, "", 14f, TextAlignmentOptions.TopLeft, UiTheme.TextDim);
            Strip(_stateLine.rectTransform, true, 36f, 20f);
            _placeLine.gameObject.SetActive(false);
            _stateLine.gameObject.SetActive(false);

            _selectRect = UiBuild.Panel("SelectRect", _image.rectTransform, null, new Color(1f, 0.71f, 0.3f, 0.22f));
            _selectRect.type = Image.Type.Simple;
            _selectRect.raycastTarget = false;
            _selectRect.rectTransform.anchorMin = _selectRect.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _selectRect.rectTransform.pivot = Vector2.zero;
            _selectRect.gameObject.SetActive(false);
        }

        /// <summary>A label pinned to the middle of the pane.</summary>
        private static void Centre(RectTransform rect, Vector2 offset, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = size;
        }

        /// <summary>A line of text across the pane, a set distance from its top or its bottom.</summary>
        private static void Strip(RectTransform rect, bool fromTop, float distance, float height)
        {
            var y = fromTop ? 1f : 0f;
            rect.anchorMin = new Vector2(0f, y);
            rect.anchorMax = new Vector2(1f, y);
            rect.pivot = new Vector2(0.5f, y);
            if (fromTop)
            {
                rect.offsetMin = new Vector2(12f, -distance - height);
                rect.offsetMax = new Vector2(-12f, -distance);
            }
            else
            {
                rect.offsetMin = new Vector2(12f, distance);
                rect.offsetMax = new Vector2(-12f, distance + height);
            }
        }

        private static void Bar(string name, Transform parent, Vector2 size)
        {
            var bar = UiBuild.Panel(name, parent, null, new Color(1f, 1f, 1f, 0.7f)).rectTransform;
            bar.anchorMin = bar.anchorMax = new Vector2(0.5f, 0.5f);
            bar.pivot = new Vector2(0.5f, 0.5f);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = size;
        }

        /// <summary>
        /// The mouse over the picture. uGUI only sends drag, click and wheel events to a component
        /// that asks for them, so the pane has this little one of its own.
        /// </summary>
        private sealed class ViewportPointer : MonoBehaviour,
            IBeginDragHandler, IDragHandler, IScrollHandler,
            IPointerDownHandler, IPointerUpHandler, IPointerClickHandler, IPointerMoveHandler
        {
            public void OnBeginDrag(PointerEventData eventData) => OnDragStart(eventData);

            public void OnPointerMove(PointerEventData eventData) => MouseMoved(eventData.position);

            public void OnDrag(PointerEventData eventData) => ViewportHost.OnDrag(eventData);

            public void OnScroll(PointerEventData eventData) => ViewportHost.OnScroll(eventData);

            public void OnPointerDown(PointerEventData eventData) => ViewportHost.OnPointerDown(eventData);

            public void OnPointerUp(PointerEventData eventData) => ViewportHost.OnPointerUp(eventData);

            public void OnPointerClick(PointerEventData eventData) => ViewportHost.OnPointerClick(eventData);
        }
    }
}
