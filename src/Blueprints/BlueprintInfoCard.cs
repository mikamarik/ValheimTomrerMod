using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimTomrer.Blueprints
{
    /// <summary>
    /// Fills the vanilla build info card (bottom of the screen in build mode) with the active
    /// blueprint: name, piece count, summed material cost and the crafting stations it needs.
    /// </summary>
    internal static class BlueprintInfoCard
    {
        public static void Show(Hud hud, Player player, ResolvedBlueprint blueprint)
        {
            hud.m_buildSelection.text = blueprint.Name;
            hud.m_pieceDescription.text = Description(blueprint);
            hud.m_buildIcon.enabled = blueprint.Icon != null;
            hud.m_buildIcon.sprite = blueprint.Icon;
            hud.m_snappingIcon.enabled = false;

            var slots = hud.m_requirementItems;
            var slot = 0;
            foreach (var requirement in blueprint.TotalCost)
            {
                if (slot >= slots.Length)
                {
                    break;
                }

                slots[slot].SetActive(true);
                InventoryGui.SetupRequirement(slots[slot].transform, requirement, player, false, 0);
                slot++;
            }

            foreach (var station in blueprint.Stations)
            {
                if (slot >= slots.Length)
                {
                    break;
                }

                ShowStation(slots[slot], station, player, blueprint);
                slot++;
            }

            for (; slot < slots.Length; slot++)
            {
                slots[slot].SetActive(false);
            }
        }

        private static string Description(ResolvedBlueprint blueprint)
        {
            var text = $"{blueprint.Parts.Count} pieces. Wheel: rotate. {ValheimTomrerPlugin.BlueprintKey.Value}: next blueprint.";
            return string.IsNullOrEmpty(blueprint.Blueprint.Description)
                ? text
                : blueprint.Blueprint.Description + "\n" + text;
        }

        /// <summary>Same look as the vanilla station slot: grey with "None" when no station is in range.</summary>
        private static void ShowStation(GameObject slot, CraftingStation station, Player player, ResolvedBlueprint blueprint)
        {
            slot.SetActive(true);
            var icon = slot.transform.Find("res_icon").GetComponent<Image>();
            var name = slot.transform.Find("res_name").GetComponent<TMP_Text>();
            var amount = slot.transform.Find("res_amount").GetComponent<TMP_Text>();

            icon.sprite = station.m_icon;
            name.text = Localization.instance.Localize(station.m_name);

            var inRange = blueprint.OwnStations.Contains(station.m_name)
                || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench)
                || CraftingStation.HaveBuildStationInRange(station.m_name, player.transform.position) != null;
            icon.color = inRange ? Color.white : Color.gray;
            amount.text = inRange ? "" : Localization.instance.Localize("$menu_none");
            amount.color = inRange || Mathf.Sin(Time.time * 10f) <= 0f ? Color.white : Color.red;
        }
    }
}
