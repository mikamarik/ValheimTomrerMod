using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ValheimTomrer.Blueprints.Sites;
using ValheimTomrer.Editor.Ui;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Fills the vanilla build card (bottom of the screen in build mode) with the active blueprint:
    /// its name, icon and a hint line. The game's six requirement slots are hidden; a materials list
    /// (<see cref="MaterialList"/>) stands on the card's right instead, with every item, every
    /// station and what the next click builds. The list is a child of the card, so it hides with the
    /// game's HUD (Ctrl+F3). For a normal piece the list hides and the game fills its own slots.
    ///
    /// <c>Hud.SetupPieceInfo</c> runs every frame in place mode, so this does too. The numbers are
    /// worked out at most every <see cref="RefreshPeriod"/> seconds, and at once after a click or
    /// when the blueprint changes. The list writes only the texts that changed.
    /// </summary>
    internal static class BlueprintInfoCard
    {
        public const float RefreshPeriod = 0.5f;

        /// <summary>The list's width in the card's own units (the game scales the card by 1.25).</summary>
        public const float OneColumnWidth = 290f;

        /// <summary>Two columns of 250 and the gap between them.</summary>
        public const float TwoColumnWidth = 516f;

        /// <summary>Inside the list's background, left and right.</summary>
        private const float Padding = 10f;

        /// <summary>Inside the list's background, top and bottom. With three rows the list is then as tall as the card.</summary>
        private const float PaddingY = 8f;

        /// <summary>The list's background is at least this dark (the card's own is 0.5).</summary>
        private const float MinimumShade = 0.7f;

        /// <summary>Between the card and the list.</summary>
        private const float CardGap = 8f;

        /// <summary>Kept clear at the screen's edge, in pixels.</summary>
        private const float ScreenMargin = 8f;

        /// <summary>
        /// A plan slower than this (ms) is worked out again only once the preview has held still for
        /// a whole refresh, so moving a big blueprint around never stutters.
        /// </summary>
        private const double SlowPlanMs = 5.0;

        /// <summary>The plan is worked out again once the preview moved this far, or turned this much, from where it was made.</summary>
        private const float MoveMetres = 0.5f;

        private const float TurnDegrees = 7.5f;

        /// <summary>Less than this between two refreshes counts as holding still.</summary>
        private const float StillMetres = 0.1f;

        /// <summary>Stations of a needed kind this close to the preview are part of the plan's key.</summary>
        private const float StationReach = 64f;

        private static readonly StringBuilder Key = new StringBuilder();

        private static Image _panel;
        private static MaterialList _list;
        private static Hud _builtFor;
        private static object _shownFor;
        private static float _nextRefresh;

        private static ResolvedBlueprint _planFor;
        private static Piece[] _kinds;
        private static string _planRest;
        private static Vector3 _planPosition;
        private static float _planYaw;
        private static bool _planAimed;
        private static Vector3 _lastPosition;
        private static float _lastYaw;
        private static int _planCount = -1;
        private static double _planMs;

        /// <summary>The list's background, a child of the card. Null until the first blueprint shows.</summary>
        public static RectTransform Panel => _panel != null ? _panel.rectTransform : null;

        public static MaterialList List => _list;

        /// <summary>What the list shows now. For the tests.</summary>
        public static Tally LastTally { get; private set; }

        public static int Refreshes { get; private set; }

        /// <summary>How many times the normal blueprint's plan was worked out. For the tests.</summary>
        public static int PlanRuns { get; private set; }

        /// <summary>How long the last one took.</summary>
        public static double LastPlanMs => _planMs;

        /// <summary>Why it ran: "new blueprint", "materials, stations or keys", "first aim" or "moved". For the log and the tests.</summary>
        public static string LastPlanReason { get; private set; }

        /// <summary>Where the last plan was made. For the tests.</summary>
        public static Vector3 LastPlanAt => _planPosition;

        public static bool Visible => _panel != null && _panel.gameObject.activeInHierarchy;

        public static void Show(Hud hud, Player player, ResolvedBlueprint blueprint)
        {
            var site = BlueprintMode.CurrentSite;
            hud.m_buildSelection.text = blueprint.Name;
            hud.m_pieceDescription.text = Description(blueprint, site != null);
            hud.m_buildIcon.enabled = blueprint.Icon != null;
            hud.m_buildIcon.sprite = blueprint.Icon;
            hud.m_snappingIcon.enabled = false;

            // The list shows everything the slots would, and more. The game sets the slots again every
            // frame, so a normal piece gets them back without anything to undo here.
            foreach (var slot in hud.m_requirementItems)
            {
                if (slot != null && slot.activeSelf)
                {
                    slot.SetActive(false);
                }
            }

            if (!Ensure(hud))
            {
                return;
            }

            var shown = (object)site ?? blueprint;
            if (!ReferenceEquals(shown, _shownFor))
            {
                _shownFor = shown;
                _nextRefresh = 0f;
            }

            if (!_panel.gameObject.activeSelf)
            {
                _panel.gameObject.SetActive(true);
            }

            if (Time.time >= _nextRefresh)
            {
                Refresh(player, blueprint, site);
            }
        }

        /// <summary>Hides the list. Every frame the card shows something that is not a blueprint.</summary>
        public static void Hide()
        {
            _shownFor = null;
            if (_panel != null && _panel.gameObject.activeSelf)
            {
                _panel.gameObject.SetActive(false);
            }
        }

        /// <summary>The next frame works the numbers out again. Called right after a build click.</summary>
        public static void RefreshSoon()
        {
            _nextRefresh = 0f;
        }

        /// <summary>Takes the list off the card. On plugin unload.</summary>
        public static void Destroy()
        {
            if (_panel != null)
            {
                Object.Destroy(_panel.gameObject);
            }

            _panel = null;
            _list = null;
            _builtFor = null;
            _shownFor = null;
            _planFor = null;
            LastTally = null;
        }

        private static void Refresh(Player player, ResolvedBlueprint blueprint, Site site)
        {
            Refreshes++;
            _nextRefresh = Time.time + RefreshPeriod;
            var noCost = player.PlacementCostDisabled;

            // One list per refresh: it walks every loaded piece.
            var sources = noCost ? null : MaterialSources.Around(player);
            Tally tally;
            if (site != null)
            {
                // The tracker's plan, cached on the same key the ghosts use.
                SiteTracker.Plan(site, sources, noCost);
                tally = MaterialTally.For(site.Resolved, site.Built, sources, noCost);
                tally.CanBuildNow = site.ReadyCount;
            }
            else
            {
                tally = MaterialTally.For(blueprint, null, sources, noCost);
                tally.CanBuildNow = CanBuildNow(player, blueprint, sources, noCost);
            }

            LastTally = tally;
            _list.Width = MaterialList.ColumnsFor(MaterialList.RowCount(tally), _list.MaxColumns) > 1 ? TwoColumnWidth : OneColumnWidth;
            _list.Show(tally);

            // Never lower than the card, so a short list lines up with the card's top.
            var card = _panel.rectTransform.parent as RectTransform;
            var height = Mathf.Max(card != null ? card.rect.height : 0f, _list.Height + (2f * PaddingY));
            _panel.rectTransform.sizeDelta = new Vector2(_list.Width + (2f * Padding), height);
            KeepOnScreen();
        }

        /// <summary>
        /// What a click would build where the preview stands now. <see cref="PartialBuild.Plan(ResolvedBlueprint, Vector3, float, bool[], MaterialSources, bool)"/>
        /// is not cheap on a big blueprint, so it runs again only when what it reads changed: the item
        /// counts, the stations near the preview, the free-build keys, costs on or off, or the preview's
        /// spot (moved 0.5 m or turned 7.5 degrees from where the last plan was made). A slow plan also
        /// waits for the preview to hold still.
        /// </summary>
        private static int CanBuildNow(Player player, ResolvedBlueprint blueprint, MaterialSources sources, bool noCost)
        {
            var fresh = !ReferenceEquals(blueprint, _planFor);
            if (fresh)
            {
                _planFor = blueprint;
                _kinds = blueprint.Parts.Select(p => p.Piece).Distinct().ToArray();
                _planRest = null;
            }

            var root = BlueprintMode.PreviewRoot;
            var aimed = root != null && BlueprintMode.HasTarget;
            Vector3 position;
            float yaw;
            if (aimed)
            {
                position = root.position;
                yaw = root.eulerAngles.y;
            }
            else if (_planRest != null)
            {
                // Nothing aimed at (the aim can flicker at the edge of the build range): the last spot
                // the preview stood on.
                position = _planPosition;
                yaw = _planYaw;
            }
            else
            {
                position = player.transform.position;
                yaw = BlueprintMode.RotationSteps * BlueprintMode.RotationStep;
            }

            if (fresh)
            {
                _lastPosition = position;
                _lastYaw = yaw;
            }

            // A distance, not a rounded spot: the aim wobbles by centimetres, and a rounded spot next to
            // a cell's edge would flip back and forth.
            var rest = RestKey(blueprint, sources, noCost, position);
            var moved = (position - _planPosition).magnitude > MoveMetres || Mathf.Abs(Mathf.DeltaAngle(yaw, _planYaw)) > TurnDegrees;
            var still = (position - _lastPosition).magnitude < StillMetres && Mathf.Abs(Mathf.DeltaAngle(yaw, _lastYaw)) < 1f;
            _lastPosition = position;
            _lastYaw = yaw;

            // A plan made before the preview had a spot (at the player's feet) is redone as soon as it has one.
            var firstAim = aimed && !_planAimed;
            if (rest == _planRest && !moved && !firstAim)
            {
                return _planCount;
            }

            if (rest == _planRest && _planMs > SlowPlanMs && !still && !firstAim)
            {
                return _planCount;
            }

            LastPlanReason = fresh ? "new blueprint" : rest != _planRest ? "materials, stations or keys" : firstAim ? "first aim" : "moved";
            var watch = Stopwatch.StartNew();
            _planCount = PartialBuild.Plan(blueprint, position, yaw, null, sources, noCost).Count;
            _planMs = watch.Elapsed.TotalMilliseconds;
            _planRest = rest;
            _planPosition = position;
            _planYaw = yaw;
            _planAimed = aimed;
            PlanRuns++;
            return _planCount;
        }

        /// <summary>Everything else the plan reads, as text. The same idea as the site tracker's key.</summary>
        private static string RestKey(ResolvedBlueprint blueprint, MaterialSources sources, bool noCost, Vector3 position)
        {
            Key.Length = 0;
            Key.Append(noCost ? 'f' : 'c').Append('|');
            foreach (var cost in blueprint.TotalCost)
            {
                Key.Append(sources != null ? sources.Count(cost.m_resItem.m_itemData.m_shared.m_name) : 0).Append(',');
            }

            Key.Append('|');
            var zones = ZoneSystem.instance;
            Key.Append(zones != null && zones.GetGlobalKey(GlobalKeys.NoWorkbench) ? 'n' : 'w');
            foreach (var kind in _kinds)
            {
                Key.Append(zones != null && zones.GetGlobalKey(kind.FreeBuildKey()) ? '1' : '0');
            }

            Key.Append('|');
            foreach (var needed in blueprint.Stations)
            {
                foreach (var station in CraftingStation.m_allStations)
                {
                    if (station == null || station.m_name != needed.m_name
                        || (station.transform.position - position).sqrMagnitude > StationReach * StationReach)
                    {
                        continue;
                    }

                    // Read, not asked for: asking runs the station's own update, which throws on a ghost.
                    var range = Mathf.Max(station.m_buildRange, station.m_rangeBuild);
                    Key.Append(station.GetInstanceID()).Append(':').Append(Mathf.RoundToInt(range * 10f)).Append(',');
                }
            }

            return Key.ToString();
        }

        /// <summary>
        /// The list and its background, on the card's right, bottom edges level. Made again after a
        /// world load, when the HUD and the theme are new.
        /// </summary>
        private static bool Ensure(Hud hud)
        {
            if (_panel != null && _builtFor == hud && _list != null)
            {
                return true;
            }

            if (_panel != null)
            {
                Object.Destroy(_panel.gameObject);
            }

            _panel = null;
            _list = null;
            _builtFor = null;
            var card = CardOf(hud);
            if (card == null || !UiTheme.Ensure())
            {
                return false;
            }

            // The card's own background (its child "Bkg2", black at half), so the list reads as part of it.
            // A little darker: red numbers over bright grass need it.
            var look = BackgroundOf(card);
            var tint = look != null ? look.color : UiTheme.Backdrop;
            tint.a = Mathf.Max(tint.a, MinimumShade);
            _panel = UiBuild.Panel("ValheimTomrer_Materials", card, look != null ? look.sprite : UiTheme.PanelBkg, tint);
            if (look != null)
            {
                _panel.type = look.type;
                _panel.pixelsPerUnitMultiplier = look.pixelsPerUnitMultiplier;
            }

            _panel.raycastTarget = false;
            var rect = _panel.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(CardGap, 0f);

            _list = MaterialList.Create(rect, OneColumnWidth);
            _list.Root.anchoredPosition = new Vector2(Padding, -PaddingY);
            _builtFor = hud;
            _shownFor = null;
            ValheimTomrerPlugin.Log.LogInfo($"materials list added to the build card '{card.name}'");
            return true;
        }

        /// <summary>The image that fills the card, switched on. Null when the game's card has none.</summary>
        private static Image BackgroundOf(RectTransform card)
        {
            var own = card.GetComponent<Image>();
            if (own != null && own.enabled)
            {
                return own;
            }

            foreach (RectTransform child in card)
            {
                var image = child.GetComponent<Image>();
                if (image != null && image.enabled && child.gameObject.activeSelf
                    && child.anchorMin == Vector2.zero && child.anchorMax == Vector2.one)
                {
                    return image;
                }
            }

            return null;
        }

        /// <summary>The card: the requirement slots' ancestor right under the build HUD.</summary>
        private static RectTransform CardOf(Hud hud)
        {
            if (hud == null || hud.m_requirementItems == null || hud.m_requirementItems.Length == 0 || hud.m_requirementItems[0] == null)
            {
                return null;
            }

            var build = hud.m_buildHud != null ? hud.m_buildHud.transform : null;
            for (var t = hud.m_requirementItems[0].transform; t != null; t = t.parent)
            {
                if (t.parent == build)
                {
                    return t as RectTransform;
                }
            }

            return null;
        }

        /// <summary>A wide list on a narrow screen slides left until its right edge is on the screen.</summary>
        private static void KeepOnScreen()
        {
            var rect = _panel.rectTransform;
            rect.anchoredPosition = new Vector2(CardGap, 0f);
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var over = corners[2].x - (Screen.width - ScreenMargin);
            var scale = rect.lossyScale.x;
            if (over > 0f && scale > 0f)
            {
                rect.anchoredPosition = new Vector2(CardGap - (over / scale), 0f);
            }
        }

        private static string Description(ResolvedBlueprint blueprint, bool continuing)
        {
            var next = ValheimTomrerPlugin.BlueprintKey.Value;
            var edit = Editor.EditorConfig.Key != null
                ? $" {Editor.EditorConfig.Key.Value}: edit it."
                : "";
            string text;
            if (continuing)
            {
                // Locked onto the build: the wheel does nothing, Remove twice forgets the plan.
                text = $"Click: build what you can. {RemoveName()} twice: forget the plan. {next}: next.{edit}";
            }
            else
            {
                text = $"{blueprint.Parts.Count} pieces. Wheel: rotate. {next}: next blueprint.{edit}";
            }

            return string.IsNullOrEmpty(blueprint.Blueprint.Description)
                ? text
                : blueprint.Blueprint.Description + "\n" + text;
        }

        /// <summary>"Remove (Left System)": the game's own name for the key, as its hint bar shows it. The pad gets the word alone.</summary>
        private static string RemoveName()
        {
            if (ZInput.IsGamepadActive() || Localization.instance == null)
            {
                return "Remove";
            }

            var key = Localization.instance.GetBoundKeyString("Remove", emptyStringOnMissing: true);
            return string.IsNullOrEmpty(key) || key.Contains("<") ? "Remove" : $"Remove ({key})";
        }
    }
}
