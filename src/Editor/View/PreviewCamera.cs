using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The camera that draws the editor pane into a texture. It is switched off, so it never draws
    /// by itself: <see cref="Render"/> is called once a frame while the window is open, with the
    /// world's fog and ambient light swapped for the editor's own for the length of that one call.
    ///
    /// It only sees the editor layer, and the main camera does not see that layer at all
    /// (GameCameraAwakePatch), so the two views can never bleed into each other.
    /// </summary>
    internal sealed class PreviewCamera
    {
        /// <summary>The Tomrer editor's background, so the pane looks the same in both.</summary>
        private static readonly Color Background = new Color32(0xB9, 0xC7, 0xD2, 0xFF);
        private static readonly Color Ambient = new Color(0.42f, 0.45f, 0.5f, 1f);

        private readonly GameObject _object;
        private readonly Camera _camera;
        private RenderTexture _texture;

        public PreviewCamera(Transform parent, int layer)
        {
            _object = new GameObject("ValheimTomrer_EditorCamera") { layer = layer };
            _object.transform.SetParent(parent, false);

            _camera = _object.AddComponent<Camera>();
            _camera.enabled = false;              // drawn by hand, never every frame
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Background;
            _camera.cullingMask = 1 << layer;
            _camera.fieldOfView = 45f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 2000f;
            _camera.allowHDR = false;
            _camera.allowMSAA = true;
            _camera.useOcclusionCulling = false;
        }

        public Camera Unity => _camera;

        public Transform Transform => _object != null ? _object.transform : null;

        public RenderTexture Texture => _texture;

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool IsAlive => _object != null;

        /// <summary>
        /// Makes the texture match the pane. The old one is released first: a RenderTexture is
        /// memory on the graphics card and does not go away on its own. True when it made a new one.
        /// </summary>
        public bool Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 16, 4096);
            height = Mathf.Clamp(height, 16, 4096);
            if (_texture != null && Width == width && Height == height)
            {
                return false;
            }

            ReleaseTexture();
            Width = width;
            Height = height;
            _texture = new RenderTexture(width, height, 24)
            {
                name = "ValheimTomrer_EditorView",
                antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing),
            };
            _texture.Create();
            _camera.targetTexture = _texture;
            _camera.aspect = (float)width / height;
            return true;
        }

        public void SetNearPlane(float near)
        {
            _camera.nearClipPlane = Mathf.Max(0.02f, near);
        }

        /// <summary>One frame of the pane. The world's own lighting settings are put back at once.</summary>
        public void Render()
        {
            if (_texture == null || _camera == null)
            {
                return;
            }

            var fog = RenderSettings.fog;
            var mode = RenderSettings.ambientMode;
            var light = RenderSettings.ambientLight;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Ambient;
            try
            {
                _camera.Render();
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = mode;
                RenderSettings.ambientLight = light;
            }
        }

        public void Destroy()
        {
            if (_camera != null)
            {
                _camera.targetTexture = null;
            }

            ReleaseTexture();
            if (_object != null)
            {
                Object.Destroy(_object);
            }
        }

        /// <summary>The texture, once it is gone. The autotest reads it after closing.</summary>
        public bool TextureAlive => _texture != null;

        private void ReleaseTexture()
        {
            if (_texture == null)
            {
                return;
            }

            if (RenderTexture.active == _texture)
            {
                RenderTexture.active = null;
            }

            _texture.Release();
            Object.Destroy(_texture);
            _texture = null;
            Width = Height = 0;
        }
    }
}
