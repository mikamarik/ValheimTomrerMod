using System.Collections.Generic;
using UnityEngine;

namespace ValheimTomrer.Editor.View
{
    /// <summary>
    /// The glow on world pieces while a capture is up: yellow on what it will take, orange on
    /// what reaches in across the edge but stays out. The colour goes on through the game's own
    /// MaterialMan, with the same two calls WearNTear.Highlight makes, and comes off with
    /// ResetValue on those two only, so nothing else on the piece is touched.
    ///
    /// It keeps its own set of what glows, and <see cref="Clear"/> takes every one of them back.
    /// A refresh only touches what changed: MaterialMan queues each change in a list it searches
    /// with Contains, so setting every piece again each time grows with the square of the count.
    /// The one exception is a piece the player aimed at lately: with a build tool out the game
    /// lights that piece itself every frame and then resets it, which wipes our colour, so those
    /// get theirs again on every refresh.
    /// </summary>
    internal static class CaptureTint
    {
        /// <summary>A piece the capture will take.</summary>
        public static readonly Color Inside = new Color(1f, 0.9f, 0.12f);

        /// <summary>A piece across the edge that the capture leaves out.</summary>
        public static readonly Color LeftOut = new Color(1f, 0.45f, 0.05f);

        /// <summary>The emission is this much of the colour, a little under the game's own 0.4.</summary>
        public const float Emission = 0.35f;

        /// <summary>At most this many pieces glow at once. The counts still say the real number.</summary>
        public const int Max = 1600;

        /// <summary>A piece aimed at within this long gets its colour again on every refresh.</summary>
        private const float HoverMemory = 2f;

        private static readonly Dictionary<GameObject, bool> Lit = new Dictionary<GameObject, bool>();
        private static readonly Dictionary<GameObject, float> RecentHover = new Dictionary<GameObject, float>();
        private static readonly List<GameObject> Scratch = new List<GameObject>();
        private static readonly Dictionary<GameObject, bool> Next = new Dictionary<GameObject, bool>();

        /// <summary>Pieces glowing yellow now.</summary>
        public static int InsideCount { get; private set; }

        /// <summary>Pieces glowing orange now.</summary>
        public static int LeftOutCount { get; private set; }

        /// <summary>Everything that glows now.</summary>
        public static int Count => Lit.Count;

        /// <summary>Pieces whose colour the last refresh set or took off.</summary>
        public static int LastChanged { get; private set; }

        /// <summary>True when this object glows, and which colour.</summary>
        public static bool Glows(GameObject go, out bool inside)
        {
            inside = false;
            return go != null && Lit.TryGetValue(go, out inside);
        }

        /// <summary>The player aims at this piece now. The game's own highlight will reset it soon.</summary>
        public static void Hovered(GameObject go)
        {
            if (go != null && Lit.ContainsKey(go))
            {
                RecentHover[go] = Time.unscaledTime;
            }
        }

        /// <summary>
        /// Makes exactly these glow: <paramref name="inside"/> yellow, then <paramref name="leftOut"/>
        /// orange, up to <see cref="Max"/> in all. What glowed before and is not listed goes back.
        /// <paramref name="all"/> sets every one again, not only what changed.
        /// </summary>
        public static void Show(IList<Piece> inside, IList<Piece> leftOut, bool all = false)
        {
            var man = MaterialMan.instance;
            if (man == null)
            {
                return;
            }

            Next.Clear();
            Add(inside, true);
            Add(leftOut, false);

            var changed = 0;
            foreach (var pair in Lit)
            {
                if (pair.Key != null && !Next.ContainsKey(pair.Key))
                {
                    Reset(man, pair.Key);
                    changed++;
                }
            }

            var now = Time.unscaledTime;
            foreach (var pair in Next)
            {
                var same = Lit.TryGetValue(pair.Key, out var was) && was == pair.Value;
                if (same && !all && !(RecentHover.TryGetValue(pair.Key, out var at) && now - at < HoverMemory))
                {
                    continue;
                }

                Set(man, pair.Key, pair.Value);
                changed++;
            }

            Lit.Clear();
            InsideCount = 0;
            LeftOutCount = 0;
            foreach (var pair in Next)
            {
                Lit[pair.Key] = pair.Value;
                if (pair.Value)
                {
                    InsideCount++;
                }
                else
                {
                    LeftOutCount++;
                }
            }

            ForgetOldHovers(now);
            LastChanged = changed;
        }

        /// <summary>Every piece in the set gets its own colours back. Safe to call at any time.</summary>
        public static void Clear()
        {
            if (Lit.Count == 0)
            {
                RecentHover.Clear();
                return;
            }

            var man = MaterialMan.instance;
            foreach (var go in Lit.Keys)
            {
                if (man != null && go != null)
                {
                    Reset(man, go);
                }
            }

            Lit.Clear();
            RecentHover.Clear();
            InsideCount = 0;
            LeftOutCount = 0;
        }

        private static void Add(IList<Piece> pieces, bool inside)
        {
            if (pieces == null)
            {
                return;
            }

            foreach (var piece in pieces)
            {
                if (Next.Count >= Max)
                {
                    return;
                }

                if (piece != null && !Next.ContainsKey(piece.gameObject))
                {
                    Next[piece.gameObject] = inside;
                }
            }
        }

        private static void Set(MaterialMan man, GameObject go, bool inside)
        {
            var colour = inside ? Inside : LeftOut;
            man.SetValue(go, ShaderProps._Color, colour);
            man.SetValue(go, ShaderProps._EmissionColor, colour * Emission);
        }

        private static void Reset(MaterialMan man, GameObject go)
        {
            man.ResetValue(go, ShaderProps._Color);
            man.ResetValue(go, ShaderProps._EmissionColor);
        }

        private static void ForgetOldHovers(float now)
        {
            if (RecentHover.Count == 0)
            {
                return;
            }

            Scratch.Clear();
            foreach (var pair in RecentHover)
            {
                if (pair.Key == null || now - pair.Value >= HoverMemory || !Lit.ContainsKey(pair.Key))
                {
                    Scratch.Add(pair.Key);
                }
            }

            foreach (var go in Scratch)
            {
                RecentHover.Remove(go);
            }
        }
    }
}
