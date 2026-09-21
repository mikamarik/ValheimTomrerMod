using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimTomrer.Editor.Ui
{
    internal enum ToastLevel
    {
        Info,
        Ok,
        Error,
    }

    /// <summary>
    /// Short messages over the bottom right of the window: what a command did, or why it could
    /// not. They stack newest first and go away on their own, errors after twice as long.
    /// </summary>
    internal static class Toasts
    {
        /// <summary>How long a message stays, and how long an error stays.</summary>
        public const float Seconds = 4.5f;

        public const float ErrorSeconds = 9f;

        private const float Width = 420f;
        private const float Gap = 6f;
        private const int Cap = 4;

        private static RectTransform _host;
        private static RectTransform _root;
        private static int _generation = -1;
        private static readonly List<Toast> Live = new List<Toast>();

        /// <summary>The messages on screen, newest first.</summary>
        public static int Count => Live.Count;

        public static string TextOf(int index)
        {
            return index >= 0 && index < Live.Count ? Live[index].Label.text : "";
        }

        public static ToastLevel LevelOf(int index)
        {
            return index >= 0 && index < Live.Count ? Live[index].Level : ToastLevel.Info;
        }

        public static void Ensure(RectTransform host)
        {
            if (host == null || (_host == host && _root != null && _generation == UiTheme.Generation))
            {
                return;
            }

            _host = host;
            _generation = UiTheme.Generation;
            Live.Clear();
            _root = UiBuild.Rect("Toasts", host);
            _root.anchorMin = new Vector2(1f, 0f);
            _root.anchorMax = new Vector2(1f, 0f);
            _root.pivot = new Vector2(1f, 0f);
            _root.anchoredPosition = new Vector2(-40f, 52f);
            _root.sizeDelta = new Vector2(Width, 10f);
        }

        public static void Info(string text) => Show(ToastLevel.Info, text);

        public static void Ok(string text) => Show(ToastLevel.Ok, text);

        public static void Error(string text) => Show(ToastLevel.Error, text);

        public static void Show(ToastLevel level, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            ValheimTomrerPlugin.Log.LogInfo($"editor says ({level}): {text}");
            if (_root == null)
            {
                return;
            }

            var toast = New(level, text);
            Live.Insert(0, toast);
            while (Live.Count > Cap)
            {
                Drop(Live.Count - 1);
            }

            Layout();
        }

        /// <summary>Drops the messages whose time is up. Called once a frame.</summary>
        public static void Tick()
        {
            var now = Time.unscaledTime;
            var changed = false;
            for (var i = Live.Count - 1; i >= 0; i--)
            {
                if (now >= Live[i].Until)
                {
                    Drop(i);
                    changed = true;
                }
            }

            if (changed)
            {
                Layout();
            }
        }

        public static void Clear()
        {
            for (var i = Live.Count - 1; i >= 0; i--)
            {
                Drop(i);
            }
        }

        private static void Drop(int index)
        {
            if (index < 0 || index >= Live.Count)
            {
                return;
            }

            if (Live[index].Rect != null)
            {
                Object.Destroy(Live[index].Rect.gameObject);
            }

            Live.RemoveAt(index);
        }

        /// <summary>Stacks them upward, newest at the bottom, so a new one never moves the old ones.</summary>
        private static void Layout()
        {
            var y = 0f;
            for (var i = 0; i < Live.Count; i++)
            {
                var rect = Live[i].Rect;
                if (rect == null)
                {
                    continue;
                }

                rect.anchoredPosition = new Vector2(0f, y);
                y += rect.sizeDelta.y + Gap;
            }
        }

        private static Toast New(ToastLevel level, string text)
        {
            var panel = UiBuild.Panel("Toast", _root, UiTheme.PanelBkg, new Color(1f, 1f, 1f, 0.96f));
            var rect = panel.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);

            var colour = level == ToastLevel.Error ? UiTheme.Warn
                : level == ToastLevel.Ok ? UiTheme.Good : UiTheme.Text;
            var label = UiBuild.Label("Text", rect, text, 15f, TextAlignmentOptions.TopLeft, colour);
            label.enableWordWrapping = true;
            UiBuild.Stretch(label.rectTransform, 10f, 6f, 10f, 6f);

            var height = Mathf.Max(30f, label.GetPreferredValues(text, Width - 20f, 0f).y + 12f);
            rect.sizeDelta = new Vector2(0f, height);

            return new Toast
            {
                Rect = rect,
                Label = label,
                Level = level,
                Until = Time.unscaledTime + (level == ToastLevel.Error ? ErrorSeconds : Seconds),
            };
        }

        private sealed class Toast
        {
            public RectTransform Rect;
            public TextMeshProUGUI Label;
            public ToastLevel Level;
            public float Until;
        }
    }
}
