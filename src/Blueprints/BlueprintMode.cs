using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Blueprint mode of the build tool. The blueprint key cycles through the blueprints the
    /// player can build with the tool in hand; a click builds the whole blueprint at once.
    /// While active, this replaces the vanilla single-piece preview and click.
    /// </summary>
    internal static class BlueprintMode
    {
        /// <summary>Same rotation grid as the hammer, so blueprint pieces still line up with normal building.</summary>
        public const float RotationStep = 22.5f;

        private static readonly List<Character> Characters = new List<Character>();

        private static BlueprintPreview _preview;
        private static int _rotationSteps;
        private static float _scroll;
        private static bool _hasTarget;
        private static string _blockedReason;

        public static ResolvedBlueprint Current { get; private set; }

        public static bool Active => Current != null;

        /// <summary>True when the aim hits something the blueprint can stand on.</summary>
        public static bool HasTarget => _hasTarget;

        /// <summary>Why the blueprint can't go where it is now, or null.</summary>
        public static string Blocked => _blockedReason;

        public static Transform PreviewRoot => _preview != null && _preview.IsAlive ? _preview.Root : null;

        /// <summary>Selects the next blueprint the player can build; after the last one, back to normal building.</summary>
        public static void Cycle(Player player)
        {
            if (!Active)
            {
                // Picks up files the player added since the last time.
                BlueprintLibrary.Reload();
            }

            var usable = UsableBlueprints(player);
            if (usable.Count == 0)
            {
                Exit();
                player.Message(MessageHud.MessageType.Center, "No blueprints available yet. Unlock more pieces.");
                return;
            }

            var next = Active ? usable.FindIndex(b => b.Blueprint == Current.Blueprint) + 1 : 0;
            if (next >= usable.Count)
            {
                Exit();
                player.Message(MessageHud.MessageType.Center, "Blueprints off");
                return;
            }

            Select(player, usable[next]);
            player.Message(MessageHud.MessageType.Center, $"{Current.Name} ({next + 1}/{usable.Count})");
        }

        public static void Select(Player player, ResolvedBlueprint blueprint)
        {
            Exit();
            Current = blueprint;
            _preview = BlueprintPreview.Create(blueprint);

            // Start with the blueprint's front (+Z) facing the player.
            _rotationSteps = Mathf.RoundToInt((player.transform.eulerAngles.y + 180f) / RotationStep);
            _scroll = 0f;
            _hasTarget = false;
            _blockedReason = null;
        }

        public static void Exit()
        {
            _preview?.Destroy();
            _preview = null;
            Current = null;
        }

        /// <summary>Mouse wheel turns the blueprint, attack builds it. Called instead of the vanilla click handling.</summary>
        public static void HandleInput(Player player)
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

            var clicked = (ZInput.GetButtonDown("Attack") || ZInput.GetButtonDown("JoyPlace")) && !Hud.InRadial();
            if (clicked && Time.time - player.m_lastToolUseTime > player.m_placeDelay)
            {
                TryBuild(player);
            }
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

        /// <summary>Builds the whole blueprint where the preview is. Returns false and tells the player why when it can't.</summary>
        public static bool TryBuild(Player player)
        {
            if (!Active || _preview == null || !_preview.IsAlive)
            {
                return false;
            }

            if (!_hasTarget)
            {
                player.Message(MessageHud.MessageType.Center, "$msg_invalidplacement");
                return false;
            }

            if (_blockedReason != null)
            {
                player.Message(MessageHud.MessageType.Center, _blockedReason);
                return false;
            }

            var error = BlueprintRules.CheckCanBuild(player, Current);
            if (error != null)
            {
                player.Message(MessageHud.MessageType.Center, error);
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

            // Bottom-up, so nothing waits for support from a piece that does not exist yet.
            var root = _preview.Root;
            var cheated = player.NoCostCheat() && !PlayerProfile.s_bypassCheatChecks;
            foreach (var part in Current.Parts.OrderBy(p => p.Source.Position.y))
            {
                player.PlacePiece(
                    part.Piece,
                    root.TransformPoint(part.Source.Position),
                    root.rotation * part.Source.Rotation,
                    doAttack: false,
                    cheated);
            }

            BlueprintRules.Pay(player, Current);

            // One hammer swing for the whole blueprint, same costs as placing a single piece.
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
            player.Message(MessageHud.MessageType.TopLeft, $"Built {Current.Name}");
            ValheimTomrerPlugin.Log.LogInfo($"built blueprint {Current.Name}: {Current.Parts.Count} pieces at {root.position}");
            return true;
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
