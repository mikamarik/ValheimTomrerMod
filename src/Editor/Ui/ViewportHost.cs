using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using ValheimTomrer.Blueprints;
using ValheimTomrer.Editor.Input;
using ValheimTomrer.Editor.View;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>
    /// The 3D pane in the middle of the window: a RawImage showing what the preview camera draws,
    /// the two camera-mode buttons and the crosshair, plus the once-a-frame work behind it
    /// (build the model in steps, keep the texture the size of the pane, read input, render).
    ///
    /// The scene, the camera and the texture live only while the editor is open. Closing it
    /// destroys all three.
    /// </summary>
    internal static class ViewportHost
    {
        private const float Inset = 6f;          // lets the sunken frame show around the picture
        private const float ButtonWidth = 74f;
        private const float ButtonHeight = 26f;

        private static RectTransform _host;
        private static RawImage _image;
        private static GameObject _crosshair;
        private static TextMeshProUGUI _orbitLabel;
        private static TextMeshProUGUI _freeLabel;
        private static int _generation = -1;

        private static EditorScene _scene;
        private static PreviewCamera _preview;
        private static ViewportRaycast _raycast;
        private static EditorCamera _camera;
        private static BlueprintPreview _model;
        private static IEnumerator _fill;
        private static ResolvedBlueprint _blueprint;
        private static float _panDepth = 10f;

        // Kept after closing, only so the autotest can prove nothing was left behind.
        private static EditorScene _closedScene;
        private static PreviewCamera _closedCamera;

        /// <summary>True once every piece stands and the bounds are known.</summary>
        public static bool Ready => _model != null && _model.Done;

        public static EditorCamera Camera => _camera;

        public static EditorScene Scene => _scene;

        public static PreviewCamera Preview => _preview;

        public static BlueprintPreview Model => _model;

        public static ViewportRaycast Raycast => _raycast;

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

        /// <summary>Puts a blueprint in the pane and starts building it, a few pieces a frame.</summary>
        public static void Show(ResolvedBlueprint blueprint)
        {
            Close();
            if (_image == null || blueprint == null)
            {
                return;
            }

            _blueprint = blueprint;
            _scene = new EditorScene(EditorConfig.Layer);
            _preview = new PreviewCamera(_scene.Root, EditorConfig.Layer);
            _raycast = new ViewportRaycast(_preview, _image.rectTransform, _scene);
            _camera = new EditorCamera(_preview, _raycast.SurfaceDistance);
            _model = BlueprintPreview.Empty(blueprint, PreviewStyle.Solid(EditorConfig.Layer));
            _model.Root.SetParent(_scene.Root, false);
            _fill = _model.Fill();
            Fit();
            ValheimTomrerPlugin.Log.LogInfo($"editor view opened on '{blueprint.Name}' ({_model.Total} pieces)");
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
            ReadInput();
            ModUi.LockCursor = _camera.WantsCursorLock;
            if (_crosshair != null)
            {
                _crosshair.SetActive(_camera.Mode == CameraMode.Free);
            }

            _preview.Render();
        }

        /// <summary>Looks at the whole blueprint from its front.</summary>
        public static void Frame()
        {
            if (_camera != null && _model != null && _model.Done)
            {
                _camera.Frame(_model.LocalBounds);
            }
        }

        public static void SetMode(CameraMode mode)
        {
            if (_camera == null)
            {
                return;
            }

            _camera.SetMode(mode);
            ModUi.LockCursor = _camera.WantsCursorLock;
            if (_orbitLabel != null)
            {
                _orbitLabel.color = mode == CameraMode.Orbit ? UiTheme.Accent : UiTheme.Text;
                _freeLabel.color = mode == CameraMode.Free ? UiTheme.Accent : UiTheme.Text;
            }
        }

        /// <summary>Esc in free mode gives the cursor back instead of closing the window.</summary>
        public static bool LeaveFreeLook()
        {
            if (_camera == null || _camera.Mode != CameraMode.Free)
            {
                return false;
            }

            SetMode(CameraMode.Orbit);
            return true;
        }

        public static void Close()
        {
            ModUi.LockCursor = false;
            if (_model != null)
            {
                _model.Destroy();
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
            _fill = null;
            _blueprint = null;
            _scene = null;
            _preview = null;
            _raycast = null;
            _camera = null;
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
            Frame();
            Status($"{_blueprint.Name}: {_model.Total} pieces");
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

        private static void ReadInput()
        {
            var dt = Mathf.Min(EditorInput.Dt, 0.1f);

            var wish = Vector3.zero;
            wish.z += Key(KeyCode.W) - Key(KeyCode.S);
            wish.x += Key(KeyCode.D) - Key(KeyCode.A);
            wish.y += Key(KeyCode.E) - Key(KeyCode.Q);
            _camera.FlyKeys(wish, Key(KeyCode.LeftShift) + Key(KeyCode.RightShift) > 0f, dt);

            var pad = EditorInput.Pad;
            if (pad != null)
            {
                var up = (pad.Held(PadButton.R2) ? 1f : 0f) - (pad.Held(PadButton.L2) ? 1f : 0f);
                _camera.FlyPad(new Vector3(pad.Ls.x, up, pad.Ls.y), pad.Held(PadButton.L1), dt);
                _camera.TurnPad(pad.Rs, dt);
            }

            // First person: the cursor is held, so there are no drag events left to read.
            if (_camera.Mode == CameraMode.Free && Mouse.current != null)
            {
                _camera.MouseLook(Mouse.current.delta.ReadValue());
            }
        }

        private static float Key(KeyCode key)
        {
            return ZInput.GetKey(key, false) ? 1f : 0f;
        }

        private static void Status(string text)
        {
            if (EditorWindow.StatusText != null)
            {
                EditorWindow.StatusText.text = text + "   |   F7 or Esc closes";
            }
        }

        // ---------- mouse, from the UI event system ----------

        private static void OnDragStart(PointerEventData data)
        {
            // Orbit pans around its orbit point; free grabs whatever is under the mouse.
            var depth = _camera != null ? _camera.Distance : 10f;
            if (_camera != null && _camera.Mode == CameraMode.Free && _raycast.ScreenToViewport(data.position, out var at))
            {
                depth = _raycast.SurfaceDistance(at) ?? depth;
            }

            _panDepth = Mathf.Min(depth, 500f);
        }

        private static void OnDrag(PointerEventData data)
        {
            if (_camera == null || data.button == PointerEventData.InputButton.Left)
            {
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

            // The UI module reports one notch as 1; Tomrer's zoom curve is written for the browser's 100.
            if (!_raycast.ScreenToViewport(data.position, out var at))
            {
                at = new Vector2(0.5f, 0.5f);
            }

            _camera.Zoom(-data.scrollDelta.y * 100f, at);
        }

        private static float PaneHeight()
        {
            var canvas = _image != null ? _image.canvas : null;
            return _image.rectTransform.rect.height * (canvas != null ? canvas.scaleFactor : 1f);
        }

        // ---------- widgets ----------

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

            var modes = UiBuild.Row("Modes", host, 6f);
            modes.anchorMin = new Vector2(1f, 1f);
            modes.anchorMax = new Vector2(1f, 1f);
            modes.pivot = new Vector2(1f, 1f);
            modes.anchoredPosition = new Vector2(-Inset - 8f, -Inset - 8f);
            modes.sizeDelta = new Vector2(ButtonWidth * 2f + 6f, ButtonHeight);
            _orbitLabel = ModeButton(modes, "Orbit", CameraMode.Orbit);
            _freeLabel = ModeButton(modes, "Free", CameraMode.Free);
            _orbitLabel.color = UiTheme.Accent;
        }

        private static TextMeshProUGUI ModeButton(Transform parent, string text, CameraMode mode)
        {
            var button = UiBuild.Button(text, parent, text, () => SetMode(mode), ButtonHeight);
            var element = button.GetComponent<LayoutElement>();
            element.preferredWidth = ButtonWidth;
            element.minWidth = ButtonWidth;
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.fontSize = 15f;
            return label;
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
        /// The mouse over the picture. uGUI only sends drag and wheel events to a component that
        /// asks for them, so the pane has this little one of its own.
        /// </summary>
        private sealed class ViewportPointer : MonoBehaviour,
            IBeginDragHandler, IDragHandler, IScrollHandler
        {
            public void OnBeginDrag(PointerEventData eventData) => OnDragStart(eventData);

            public void OnDrag(PointerEventData eventData) => ViewportHost.OnDrag(eventData);

            public void OnScroll(PointerEventData eventData) => ViewportHost.OnScroll(eventData);
        }
    }
}
