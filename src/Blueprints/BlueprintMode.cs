using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimTomrer.Blueprints.Sites;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Blueprint mode of the build tool. The blueprint key cycles through the unfinished builds near
    /// the player ("Continue: Workshop (6/16)"), then the blueprints the player can build with the
    /// tool in hand, then off. A click builds every piece the materials pay for. While active, this
    /// replaces the vanilla single-piece preview and click.
    ///
    /// In Continue the preview is the site's own ghost (<see cref="SiteTracker"/>): it stays where the
    /// build stands, the wheel and the pad turn do nothing, and Remove twice within 3 s forgets the plan.
    /// </summary>
    internal static class BlueprintMode
    {
        /// <summary>Same rotation grid as the hammer, so blueprint pieces still line up with normal building.</summary>
        public const float RotationStep = 22.5f;

        /// <summary>The key offers an unfinished build while the player is this close to its box.</summary>
        public const float ContinueRange = 40f;

        /// <summary>The second Remove press forgets the plan when it comes within this many seconds of the first.</summary>
        public const float ForgetWindow = 3f;

        /// <summary>A character this close to a part's box (the box grows by this much in x and z) keeps it from going up.</summary>
        private const float CharacterMargin = 0.6f;

        private static readonly List<Character> Characters = new List<Character>();

        private static BlueprintPreview _preview;
        private static int _rotationSteps;
        private static float _scroll;
        private static float _padTurnTimer;
        private static bool _hasTarget;
        private static string _blockedReason;
        private static float _removePressedAt = float.NegativeInfinity;

        public static ResolvedBlueprint Current { get; private set; }

        public static bool Active => Current != null;

        /// <summary>The unfinished build being continued, or null when the hammer holds a normal blueprint (or none).</summary>
        public static Site CurrentSite { get; private set; }

        /// <summary>What the key said for the current entry: "Continue: Workshop (6/16)" or the blueprint's name. Null when off.</summary>
        public static string EntryName { get; private set; }

        /// <summary>True when the aim hits something the blueprint can stand on. Always true in Continue.</summary>
        public static bool HasTarget => _hasTarget;

        /// <summary>Why the blueprint can't go where it is now, or null.</summary>
        public static string Blocked => _blockedReason;

        /// <summary>The preview's root: the aim preview, or in Continue the site's ghost. Null while there is none.</summary>
        public static Transform PreviewRoot
        {
            get
            {
                if (CurrentSite != null)
                {
                    var ghost = CurrentSite.Ghost;
                    return ghost != null && ghost.IsAlive ? ghost.Root : null;
                }

                return _preview != null && _preview.IsAlive ? _preview.Root : null;
            }
        }

        /// <summary>
        /// Selects the next entry: the unfinished builds within <see cref="ContinueRange"/>, nearest
        /// first, then the blueprints the player can build, then back to normal building.
        /// </summary>
        public static void Cycle(Player player)
        {
            if (!Active)
            {
                // Picks up files the player added since the last time.
                BlueprintLibrary.Reload();
            }

            var sites = NearSites(player);
            var usable = UsableBlueprints(player);
            var count = sites.Count + usable.Count;
            if (count == 0)
            {
                Exit();
                player.Message(MessageHud.MessageType.Center, "No blueprints available yet. Unlock more pieces.");
                return;
            }

            int at;
            if (CurrentSite != null)
            {
                at = sites.FindIndex(s => s.Site == CurrentSite);
            }
            else
            {
                var index = Active ? usable.FindIndex(b => b.Blueprint == Current.Blueprint) : -1;
                at = index < 0 ? -1 : sites.Count + index;
            }

            var next = at + 1;
            if (next >= count)
            {
                Exit();
                player.Message(MessageHud.MessageType.Center, "Blueprints off");
                return;
            }

            if (next < sites.Count)
            {
                // Two builds of the same blueprint are told apart by how far they are and which way.
                var site = sites[next];
                var twin = sites.Count(s => s.Site.Name == site.Site.Name) > 1;
                Continue(site.Site);
                EntryName = $"Continue: {site.Site.Name} ({site.Site.BuiltCount}/{site.Site.Total})"
                    + (twin ? Where(player, site.Site, site.Distance) : "");
                player.Message(MessageHud.MessageType.Center, EntryName);
                return;
            }

            var blueprint = next - sites.Count;
            Select(player, usable[blueprint]);
            player.Message(MessageHud.MessageType.Center, $"{Current.Name} ({blueprint + 1}/{usable.Count})");
        }

        public static void Select(Player player, ResolvedBlueprint blueprint)
        {
            Exit();
            Current = blueprint;
            EntryName = blueprint.Name;
            _preview = BlueprintPreview.Create(blueprint);

            // Start with the blueprint's front (+Z) facing the player.
            _rotationSteps = Mathf.RoundToInt((player.transform.eulerAngles.y + 180f) / RotationStep);
            _scroll = 0f;
            _padTurnTimer = 0f;
            _hasTarget = false;
            _blockedReason = null;
        }

        /// <summary>
        /// Continue an unfinished build. Its ghost, which the tracker already keeps, is the preview, so
        /// no second copy is made; it stays where the build stands.
        /// </summary>
        public static void Continue(Site site)
        {
            Exit();
            CurrentSite = site;
            Current = site.Resolved;
            EntryName = $"Continue: {site.Name}";
            _rotationSteps = Mathf.RoundToInt(site.RootYaw / RotationStep);
            _scroll = 0f;
            _padTurnTimer = 0f;
            _hasTarget = true;
            _blockedReason = null;
            _removePressedAt = float.NegativeInfinity;
            SiteTracker.RefreshSoon();
        }

        /// <summary>Back to normal building. The aim preview is destroyed; a site's ghost is the tracker's and stays.</summary>
        public static void Exit()
        {
            _preview?.Destroy();
            _preview = null;
            Current = null;
            CurrentSite = null;
            EntryName = null;
            _removePressedAt = float.NegativeInfinity;
        }

        /// <summary>Which way the blueprint faces now, in steps of <see cref="RotationStep"/>.</summary>
        public static int RotationSteps => _rotationSteps;

        /// <summary>
        /// The wheel or the pad turns the blueprint, attack builds it. Called instead of the
        /// vanilla click handling.
        /// </summary>
        public static void HandleInput(Player player)
        {
            if (CurrentSite != null)
            {
                // Locked onto the build: nothing turns it. Remove, twice, forgets it.
                if (RemovePressed())
                {
                    PressRemove(player);
                }
            }
            else
            {
                _scroll += ZInput.GetMouseScrollWheel();
                if (_scroll > player.m_scrollAmountThreshold)
                {
                    _scroll = 0f;
                    _rotationSteps++;
                }
                else if (_scroll < -player.m_scrollAmountThreshold)
                {
                    _scroll = 0f;
                    _rotationSteps--;
                }

                PadTurn(Time.deltaTime);
            }

            var clicked = (ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyPlace")) && !Hud.InRadial();
            if (clicked && Time.time - player.m_lastToolUseTime > player.m_placeDelay)
            {
                TryBuild(player);
            }
        }

        /// <summary>
        /// The hammer's Remove, read the way <c>Player.UpdatePlacement</c> reads it: on release, the
        /// keyboard's only while the pad is not in use, never with the copy modifier held.
        /// </summary>
        private static bool RemovePressed()
        {
            if (ZInput.GetButton("AltPlace") || ZInput.GetButton("JoyAltKeys"))
            {
                return false;
            }

            return (!ZInput.IsGamepadActive() && ZInput.GetButtonUp("Remove")) || ZInput.GetButtonUp("JoyRemove");
        }

        /// <summary>
        /// Remove in Continue. The first press only asks; a second within <see cref="ForgetWindow"/>
        /// deletes the plan and its ghost. What stands in the world stays.
        /// </summary>
        public static void PressRemove(Player player)
        {
            var site = CurrentSite;
            if (site == null)
            {
                return;
            }

            if (Time.time - _removePressedAt > ForgetWindow)
            {
                _removePressedAt = Time.time;
                Say(player, MessageHud.MessageType.Center, "Press again to remove the plan.");
                return;
            }

            ValheimTomrerPlugin.Log.LogInfo($"site forgotten: {site.Name} at {site.RootPosition}, {site.BuiltCount} of {site.Total} built, file {site.Path} deleted");
            SiteStore.Delete(site);
            Exit();
            Say(player, MessageHud.MessageType.Center, $"Plan for {site.Name} removed. Built pieces stay.");
        }

        /// <summary>
        /// The pad turns a blueprint the way the game turns one piece (Player.UpdatePlacement):
        /// L2 and the right stick in the default layout, L2 and R2 in the other two. One step at
        /// once, then one every 0.08 s after a quarter second. While L2 is down the game's own
        /// PlayerController already keeps the right stick off the camera.
        /// </summary>
        private static void PadTurn(float dt)
        {
            var direction = 0f;
            var held = false;
            if (ZInput.IsGamepadActive())
            {
                switch (ZInput.InputLayout)
                {
                    case InputLayout.Alternative1:
                    case InputLayout.Alternative2:
                        var left = ZInput.GetButton("JoyRotate");
                        var right = ZInput.GetButton("JoyRotateRight");
                        held = left || right;
                        direction = left ? 0.5f : right ? -0.5f : 0f;
                        break;
                    case InputLayout.Default:
                        direction = ZInput.GetJoyRightStickX();
                        held = ZInput.GetButton("JoyRotate") && Mathf.Abs(direction) > 0.5f;
                        break;
                }
            }

            if (!held)
            {
                _padTurnTimer = 0f;
                return;
            }

            if (_padTurnTimer == 0f || _padTurnTimer > 0.25f)
            {
                _rotationSteps += direction < 0f ? 1 : -1;
                if (_padTurnTimer > 0.25f)
                {
                    _padTurnTimer = 0.17f;
                }
            }

            _padTurnTimer += dt;
        }

        /// <summary>Moves the preview to the aim point and marks blocked pieces. Called instead of the vanilla preview update.</summary>
        public static void UpdatePreview(Player player)
        {
            if (player.m_placementGhost)
            {
                player.m_placementGhost.SetActive(false);
            }

            if (player.m_placementMarkerInstance)
            {
                player.m_placementMarkerInstance.SetActive(false);
            }

            if (CurrentSite != null)
            {
                UpdateContinue(player);
                return;
            }

            if (_preview == null || !_preview.IsAlive)
            {
                _preview = BlueprintPreview.Create(Current);
            }

            _hasTarget = player.PieceRayTest(out var point, out _, out _, out _, out _, false);

            // The camera reads this to decide whether the wheel zooms or rotates.
            player.m_placementStatus = _hasTarget ? Player.PlacementStatus.Valid : Player.PlacementStatus.NoRayHits;

            if (!_hasTarget)
            {
                _preview.SetVisible(false);
                _blockedReason = null;
                return;
            }

            var rotation = Quaternion.Euler(0f, _rotationSteps * RotationStep, 0f);
            _preview.SetVisible(true);
            _preview.Root.SetPositionAndRotation(point - rotation * _preview.Anchor, rotation);

            _blockedReason = null;
            for (var i = 0; i < Current.Parts.Count; i++)
            {
                var part = Current.Parts[i];
                var reason = BlockedReason(part, _preview.Root.TransformPoint(part.Source.Position));
                _preview.SetInvalid(i, reason != null);
                if (_blockedReason == null)
                {
                    _blockedReason = reason;
                }
            }

            if (_blockedReason == null && OverlapsCharacter(_preview))
            {
                _blockedReason = "$msg_blocked";
            }
        }

        /// <summary>
        /// Continue: the site's ghost stays where the build stands and the tracker paints it, so there
        /// is nothing to move. The mode ends when the site is gone (finished, forgotten, another world).
        /// </summary>
        private static void UpdateContinue(Player player)
        {
            if (!SiteStore.All.Contains(CurrentSite))
            {
                Exit();
                return;
            }

            _hasTarget = true;
            _blockedReason = null;

            // The camera reads this to decide whether the wheel zooms. Locked, it does nothing.
            player.m_placementStatus = Player.PlacementStatus.Valid;
        }

        /// <summary>
        /// Builds every piece the materials pay for and that would stand, bottom to top, where the
        /// preview is (<see cref="PartialBuild"/>). With enough for all of it, the whole blueprint.
        /// When anything is left, even everything, it is kept as an unfinished build (<see cref="SiteStore"/>).
        /// In Continue it builds on the unfinished build instead (<see cref="TryContinue"/>).
        /// Returns false and tells the player why when nothing goes up.
        /// </summary>
        public static bool TryBuild(Player player)
        {
            if (CurrentSite != null)
            {
                return TryContinue(player, CurrentSite);
            }

            if (!Active || _preview == null || !_preview.IsAlive)
            {
                return false;
            }

            if (!_hasTarget)
            {
                Say(player, MessageHud.MessageType.Center, "$msg_invalidplacement");
                return false;
            }

            if (_blockedReason != null)
            {
                Say(player, MessageHud.MessageType.Center, _blockedReason);
                return false;
            }

            var error = BlueprintRules.CheckCanBuild(player, Current);
            if (error != null)
            {
                Say(player, MessageHud.MessageType.Center, error);
                return false;
            }

            var tool = player.GetRightItem();
            if (tool == null)
            {
                return false;
            }

            if (!player.HaveStamina(tool.m_shared.m_attack.m_attackStamina))
            {
                Hud.instance.StaminaBarEmptyFlash();
                return false;
            }

            var root = _preview.Root;
            var noCost = player.PlacementCostDisabled;
            var sources = noCost ? null : MaterialSources.Around(player);
            var plan = PartialBuild.Plan(Current, root.position, root.eulerAngles.y, null, sources, noCost);

            // In plan order: bottom-up, so nothing waits for support from a piece that does not exist yet.
            var cheated = player.NoCostCheat() && !PlayerProfile.s_bypassCheatChecks;
            var built = new bool[Current.Parts.Count];
            var placed = 0;
            foreach (var index in plan)
            {
                var part = Current.Parts[index];

                // The plan counted these materials, so this only fails if a chest emptied since.
                if (!noCost && !BlueprintRules.PayFor(sources, part.Piece))
                {
                    continue;
                }

                player.PlacePiece(
                    part.Piece,
                    root.TransformPoint(part.Source.Position),
                    root.rotation * part.Source.Rotation,
                    doAttack: false,
                    cheated);
                built[index] = true;
                placed++;
            }

            var total = Current.Parts.Count;
            if (placed < total)
            {
                KeepSite(root, built, placed);
            }

            if (placed == 0)
            {
                var missing = PartialBuild.MissingText(PartialBuild.Missing(Current, null, sources));
                Say(player, MessageHud.MessageType.Center,
                    $"{Current.Name} planned, nothing built yet." + (missing.Length > 0 ? " Missing: " + missing : ""));
                ValheimTomrerPlugin.Log.LogInfo($"built nothing of {Current.Name}: {(missing.Length > 0 ? "missing " + missing : "no piece would stand")}");
                return false;
            }

            Swing(player, tool);
            ValheimTomrerPlugin.Log.LogInfo($"built blueprint {Current.Name}: {placed} of {total} pieces at {root.position}, paid from {From(sources)}");
            if (placed == total)
            {
                Say(player, MessageHud.MessageType.TopLeft, $"Built {Current.Name}");
                return true;
            }

            var still = PartialBuild.MissingText(PartialBuild.Missing(Current, built, sources));
            Say(player, MessageHud.MessageType.TopLeft,
                $"Built {placed} of {total} pieces of {Current.Name}." + (still.Length > 0 ? $" Still missing: {still}." : ""));
            return true;
        }

        /// <summary>
        /// The Continue click: builds what the materials pay for now, bottom to top, from what stands
        /// today. A part a character stands in is left out of this click and stays next in line, and so
        /// is anything that would need it. The click that puts the last part up finishes the build.
        /// </summary>
        private static bool TryContinue(Player player, Site site)
        {
            if (!SiteStore.All.Contains(site))
            {
                Exit();
                return false;
            }

            var distance = Mathf.Sqrt(site.WorldBox.SqrDistance(player.transform.position));
            if (distance > ContinueRange)
            {
                Say(player, MessageHud.MessageType.Center, $"Too far from {site.Name}. Come within {ContinueRange:0} m.");
                return false;
            }

            var error = BlueprintRules.CheckCanBuild(player, site.Resolved);
            if (error != null)
            {
                Say(player, MessageHud.MessageType.Center, error);
                return false;
            }

            var tool = player.GetRightItem();
            if (tool == null)
            {
                return false;
            }

            if (!player.HaveStamina(tool.m_shared.m_attack.m_attackStamina))
            {
                Hud.instance.StaminaBarEmptyFlash();
                return false;
            }

            // What stands right now, not at the last refresh: a piece built by hand since must not get a twin.
            SiteTracker.ReadBuilt(site);
            if (site.BuiltCount >= site.Total)
            {
                Finished(player, site);
                return false;
            }

            var noCost = player.PlacementCostDisabled;
            var sources = noCost ? null : MaterialSources.Around(player);
            SiteTracker.Plan(site, sources, noCost);
            var chosen = site.ReadyOrder ?? new List<int>();
            foreach (var index in chosen)
            {
                var reason = BlockedReason(site.Resolved.Parts[index], site.WorldPosition(index));
                if (reason != null)
                {
                    Say(player, MessageHud.MessageType.Center, reason);
                    return false;
                }
            }

            var held = PartsHoldingCharacters(site, chosen);
            var order = PartialBuild.Without(site.Resolved, site.RootPosition, site.RootYaw, site.Built, chosen, held);
            var leftOut = chosen.Count - order.Count;
            var cheated = player.NoCostCheat() && !PlayerProfile.s_bypassCheatChecks;
            var placed = 0;
            foreach (var index in order)
            {
                var part = site.Resolved.Parts[index];
                if (!noCost && !BlueprintRules.PayFor(sources, part.Piece))
                {
                    continue;
                }

                player.PlacePiece(part.Piece, site.WorldPosition(index), site.WorldRotation(index), doAttack: false, cheated);
                placed++;
            }

            if (placed == 0)
            {
                if (chosen.Count > 0)
                {
                    Say(player, MessageHud.MessageType.Center, $"Someone stands where the next pieces of {site.Name} go.");
                    ValheimTomrerPlugin.Log.LogInfo($"continued {site.Name}: nothing built, {leftOut} parts held by a character");
                    return false;
                }

                var missing = PartialBuild.MissingText(PartialBuild.Missing(site.Resolved, site.Built, sources));
                Say(player, MessageHud.MessageType.Center,
                    $"Nothing of {site.Name} to build yet." + (missing.Length > 0 ? " Missing: " + missing : ""));
                ValheimTomrerPlugin.Log.LogInfo($"continued {site.Name}: nothing built, {(missing.Length > 0 ? "missing " + missing : "no piece would stand")}");
                return false;
            }

            Swing(player, tool);
            SiteTracker.ReadBuilt(site);
            ValheimTomrerPlugin.Log.LogInfo($"continued {site.Name}: {placed} more, {site.BuiltCount} of {site.Total} stand,"
                + $" {leftOut} left out for a character, paid from {From(sources)}");
            if (site.BuiltCount >= site.Total)
            {
                Finished(player, site);
                return true;
            }

            SiteTracker.RefreshSoon();
            var still = PartialBuild.MissingText(PartialBuild.Missing(site.Resolved, site.Built, sources));
            Say(player, MessageHud.MessageType.TopLeft,
                $"Built {placed} more of {site.Name}, {site.BuiltCount} of {site.Total} stand."
                + (leftOut > 0 ? $" {leftOut} left out: someone stands there." : "")
                + (still.Length > 0 ? $" Still missing: {still}." : ""));
            return true;
        }

        /// <summary>Every part stands: the tracker says so and deletes the file, and Continue ends.</summary>
        private static void Finished(Player player, Site site)
        {
            SiteTracker.Finish(player, site);
            LastMessage = SiteTracker.LastMessage;
            Exit();
        }

        /// <summary>
        /// The chosen parts a character stands in: the part's own box, in its own space, grown as the
        /// whole-blueprint test grows it. The player often stands inside the half-built house, so the
        /// whole box would refuse every click there.
        /// </summary>
        private static HashSet<int> PartsHoldingCharacters(Site site, IEnumerable<int> chosen)
        {
            var held = new HashSet<int>();
            Characters.Clear();
            Character.GetCharactersInRange(site.WorldBox.center, site.WorldBox.extents.magnitude + 1f, Characters);
            if (Characters.Count == 0)
            {
                return held;
            }

            foreach (var index in chosen)
            {
                var box = BlueprintPreview.OwnBounds(site.Resolved.Parts[index].Prefab);
                box.Expand(new Vector3(CharacterMargin, 0f, CharacterMargin));
                var back = Quaternion.Inverse(site.WorldRotation(index));
                var at = site.WorldPosition(index);
                foreach (var character in Characters)
                {
                    if (character != null && box.Contains(back * (character.transform.position + (Vector3.up * 0.5f) - at)))
                    {
                        held.Add(index);
                        break;
                    }
                }
            }

            Characters.Clear();
            return held;
        }

        /// <summary>One hammer swing for the whole click, same costs as placing a single piece.</summary>
        private static void Swing(Player player, ItemDrop.ItemData tool)
        {
            player.FaceLookDirection();
            player.m_zanim.SetTrigger(tool.m_shared.m_attack.m_attackAnimation);
            player.UseStamina(player.GetBuildStamina());
            if (player.m_buildPieces.m_skill != Skills.SkillType.None)
            {
                player.RaiseSkill(player.m_buildPieces.m_skill);
            }

            if (tool.m_shared.m_useDurability)
            {
                tool.m_durability -= player.GetPlaceDurability(tool) * Game.m_durabilityRate;
            }

            tool.m_shared.m_buildEffect.Create(player.transform.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
            player.m_lastToolUseTime = Time.time;
        }

        /// <summary>Where the materials came from, for the log.</summary>
        private static string From(MaterialSources sources)
        {
            return sources == null ? "nothing, costs are off"
                : "the inventory" + (sources.ChestCount > 0 ? $" and {sources.ChestCount} chests within {sources.Range:0} m" : "");
        }

        /// <summary>
        /// The rest of the blueprint is kept as an unfinished build, at the preview's pose. The file is
        /// written now; what stands is read from the world again on the tracker's next refresh.
        /// </summary>
        private static void KeepSite(Transform root, bool[] built, int placed)
        {
            var site = Site.Start(Current, root.position, root.eulerAngles.y);
            if (site == null)
            {
                ValheimTomrerPlugin.Log.LogWarning($"cannot keep {Current.Name} as an unfinished build");
                return;
            }

            // A first answer until the tracker reads the world.
            System.Array.Copy(built, site.Built, site.Built.Length);
            site.BuiltCount = placed;
            if (SiteStore.Add(site))
            {
                LastSite = site;
            }

            SiteTracker.RefreshSoon();
        }

        /// <summary>The unfinished build the last partial or empty click kept. For the tests.</summary>
        public static Site LastSite { get; private set; }

        /// <summary>The last thing <see cref="TryBuild"/> told the player, localized. For the tests.</summary>
        public static string LastMessage { get; private set; }

        private static void Say(Player player, MessageHud.MessageType type, string text)
        {
            LastMessage = Localization.instance.Localize(text);
            player.Message(type, text);
        }

        /// <summary>The unfinished builds the key offers: within <see cref="ContinueRange"/> of their box, buildable with this tool, nearest first.</summary>
        private static List<(Site Site, float Distance)> NearSites(Player player)
        {
            var from = player.transform.position;
            return SiteStore.All
                .Select(s => (Site: s, Distance: Mathf.Sqrt(s.WorldBox.SqrDistance(from))))
                .Where(e => e.Distance <= ContinueRange && BlueprintRules.IsAvailable(player, e.Site.Resolved))
                .OrderBy(e => e.Distance)
                .ToList();
        }

        /// <summary>", 12 m ahead": how far the build is and which way, seen from where the player looks.</summary>
        private static string Where(Player player, Site site, float distance)
        {
            if (distance < 1f)
            {
                return ", here";
            }

            var to = site.WorldBox.center - player.transform.position;
            var look = player.m_lookYaw * Vector3.forward;
            to.y = 0f;
            look.y = 0f;
            var angle = Vector3.SignedAngle(look, to, Vector3.up);
            var side = Mathf.Abs(angle) <= 45f ? "ahead"
                : Mathf.Abs(angle) >= 135f ? "behind"
                : angle > 0f ? "to the right" : "to the left";
            return $", {distance:0} m {side}";
        }

        private static List<ResolvedBlueprint> UsableBlueprints(Player player)
        {
            var usable = new List<ResolvedBlueprint>();
            foreach (var blueprint in BlueprintLibrary.All)
            {
                if (!ResolvedBlueprint.TryResolve(blueprint, out var resolved, out var error))
                {
                    ValheimTomrerPlugin.Log.LogWarning(error);
                    continue;
                }

                if (BlueprintRules.IsAvailable(player, resolved))
                {
                    usable.Add(resolved);
                }
            }

            return usable;
        }

        /// <summary>Vanilla refuses to build a piece into a player or creature; same for the whole blueprint.</summary>
        private static bool OverlapsCharacter(BlueprintPreview preview)
        {
            var root = preview.Root;
            var bounds = preview.LocalBounds;
            bounds.Expand(new Vector3(0.6f, 0f, 0.6f));

            Characters.Clear();
            Character.GetCharactersInRange(root.TransformPoint(bounds.center), bounds.extents.magnitude + 1f, Characters);
            foreach (var character in Characters)
            {
                if (bounds.Contains(root.InverseTransformPoint(character.transform.position + Vector3.up * 0.5f)))
                {
                    return true;
                }
            }

            return false;
        }

        private static string BlockedReason(ResolvedPart part, Vector3 position)
        {
            if (Location.IsInsideNoBuildLocation(position))
            {
                return "$msg_nobuildzone";
            }

            var ward = part.Prefab.GetComponent<PrivateArea>();
            if (!PrivateArea.CheckAccess(position, ward != null ? ward.m_radius : 0f, false, ward != null))
            {
                return "$msg_privatezone";
            }

            return null;
        }
    }
}
