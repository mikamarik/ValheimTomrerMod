using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimTomrer.Blueprints;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The capture's rectangle, drawn on the ground as a thin yellow line. It stands in the world,
    /// not in the editor's pane, so it goes on the game's "ghost" layer like a piece about to be
    /// placed. One LineRenderer, one material made in code from a shader the game already loaded.
    ///
    /// Each side is cut into short pieces and every point sits on the terrain, so the line follows
    /// a slope instead of cutting through it. The points are worked out again only when the
    /// rectangle moved, turned or changed size.
    /// </summary>
    internal sealed class CaptureBox
    {
        public static readonly Color Colour = new Color(1f, 0.9f, 0.2f, 0.9f);

        /// <summary>Line width in metres.</summary>
        public const float LineWidth = 0.05f;

        /// <summary>The line floats this far over the ground, so grass and small bumps do not hide it.</summary>
        public const float Lift = 0.25f;

        private readonly List<Vector3> _points = new List<Vector3>();

        private GameObject _object;
        private LineRenderer _line;
        private Material _material;
        private Vector3 _centre;
        private float _yaw;
        private float _width;
        private float _depth;

        /// <summary>The outline stands in the world right now.</summary>
        public bool Visible => _object != null && _object.activeSelf;

        /// <summary>Points on the line now, corners included.</summary>
        public int PointCount => _line != null ? _line.positionCount : 0;

        /// <summary>How many pieces one side is cut into: one every 4 m, 4 to 64.</summary>
        public static int Segments(float side)
        {
            return Mathf.Clamp(Mathf.CeilToInt(side / 4f), 4, 64);
        }

        /// <summary>Draws the rectangle around <paramref name="centre"/>, turned by <paramref name="yaw"/> degrees.</summary>
        public void Show(Vector3 centre, float yaw, float width, float depth)
        {
            Build();
            if (_object == null)
            {
                return;
            }

            var same = Visible && centre == _centre && yaw == _yaw && width == _width && depth == _depth;
            if (same)
            {
                return;
            }

            _centre = centre;
            _yaw = yaw;
            _width = width;
            _depth = depth;

            var turn = Quaternion.Euler(0f, yaw, 0f);
            var x = width * 0.5f;
            var z = depth * 0.5f;
            var corners = new[]
            {
                new Vector3(-x, 0f, -z),
                new Vector3(x, 0f, -z),
                new Vector3(x, 0f, z),
                new Vector3(-x, 0f, z),
            };

            _points.Clear();
            var zones = ZoneSystem.instance;
            for (var side = 0; side < 4; side++)
            {
                var from = corners[side];
                var to = corners[(side + 1) % 4];
                var steps = Segments(side % 2 == 0 ? width : depth);
                for (var i = 0; i < steps; i++)
                {
                    var point = centre + (turn * Vector3.Lerp(from, to, i / (float)steps));
                    if (zones != null)
                    {
                        point.y = zones.GetGroundHeight(point);
                    }

                    point.y += Lift;
                    _points.Add(point);
                }
            }

            _line.positionCount = _points.Count;
            for (var i = 0; i < _points.Count; i++)
            {
                _line.SetPosition(i, _points[i]);
            }

            if (!_object.activeSelf)
            {
                _object.SetActive(true);
            }
        }

        public void Hide()
        {
            if (_object != null && _object.activeSelf)
            {
                _object.SetActive(false);
            }
        }

        public void Destroy()
        {
            if (_object != null)
            {
                Object.Destroy(_object);
            }

            if (_material != null)
            {
                Object.Destroy(_material);
            }

            _object = null;
            _line = null;
            _material = null;
        }

        private void Build()
        {
            if (_object != null)
            {
                return;
            }

            _object = new GameObject("ValheimTomrer_CaptureOutline") { layer = BlueprintPreview.GhostLayer };
            Object.DontDestroyOnLoad(_object);

            // Sprites/Default when the game has it, else Unlit/Color. Both read the material colour;
            // the line's own vertex colour stays white so it does not tint it twice.
            _material = Shading.Overlay(Colour, false);
            _material.name = "ValheimTomrer_CaptureOutline";

            _line = _object.AddComponent<LineRenderer>();
            _line.sharedMaterial = _material;
            _line.useWorldSpace = true;
            _line.loop = true;
            _line.widthMultiplier = LineWidth;
            _line.startColor = Color.white;
            _line.endColor = Color.white;
            _line.numCornerVertices = 2;
            _line.numCapVertices = 0;
            _line.alignment = LineAlignment.View;
            _line.textureMode = LineTextureMode.Stretch;
            _line.shadowCastingMode = ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.generateLightingData = false;
            _line.positionCount = 0;
            _object.SetActive(false);
        }
    }
}
