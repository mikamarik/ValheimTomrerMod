using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    /// <summary>One row per wheel event, independent of the platform's wheel-delta units.</summary>
    internal sealed class RowScrollRect : ScrollRect
    {
        /// <summary>Uniform row pitch. Zero uses the actual top edges of variable-height children.</summary>
        public float RowStep;

        private readonly List<float> _tops = new List<float>();

        internal static float NextOffset(float current, float room, float step, int rows)
        {
            step = Mathf.Max(1f, step);
            var index = current / step;
            var next = rows > 0 ? Mathf.Floor(index + 0.001f) + rows
                : Mathf.Ceil(index - 0.001f) + rows;
            return Mathf.Clamp(next * step, 0f, Mathf.Max(0f, room));
        }

        public override void OnScroll(PointerEventData data)
        {
            if (!IsActive() || content == null || viewport == null || !vertical
                || Mathf.Approximately(data.scrollDelta.y, 0f))
            {
                return;
            }

            Canvas.ForceUpdateCanvases();
            StopMovement();
            var room = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            var current = Mathf.Clamp(content.anchoredPosition.y, 0f, room);
            var rows = Mathf.Max(1, EditorConfig.MenuRowsPerNotch?.Value ?? 1)
                * (data.scrollDelta.y < 0f ? 1 : -1);
            var next = RowStep > 0f ? NextOffset(current, room, RowStep, rows)
                : NextChild(current, room, rows);
            var position = content.anchoredPosition;
            position.y = next;
            SetContentAnchoredPosition(position);
            // Virtualised lists need their visible rows immediately, not a frame later.
            onValueChanged.Invoke(normalizedPosition);
            data.Use();
        }

        private float NextChild(float current, float room, int rows)
        {
            _tops.Clear();
            _tops.Add(0f);
            for (var i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var layout = child.GetComponent<LayoutElement>();
                if (layout != null && layout.ignoreLayout)
                {
                    continue;
                }
                var top = content.rect.yMax - child.localPosition.y - child.rect.yMax;
                _tops.Add(Mathf.Clamp(top, 0f, room));
            }
            _tops.Add(room);
            _tops.Sort();
            var next = current;
            for (var remaining = Mathf.Abs(rows); remaining > 0; remaining--)
            {
                if (rows > 0)
                {
                    for (var i = 0; i < _tops.Count; i++)
                    {
                        if (_tops[i] > next + 0.1f)
                        {
                            next = _tops[i];
                            break;
                        }
                    }
                }
                else
                {
                    for (var i = _tops.Count - 1; i >= 0; i--)
                    {
                        if (_tops[i] < next - 0.1f)
                        {
                            next = _tops[i];
                            break;
                        }
                    }
                }
            }
            return next;
        }
    }
}
