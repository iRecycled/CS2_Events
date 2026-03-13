using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches CityServiceBudgetSystem.SetServiceBudget (public) to record a debounced
/// budget change. Routes through EconomyDebounceSystem so rapid slider steps are
/// batched into a single BudgetChangedEvent fired after 1 second of inactivity.
/// </summary>
[HarmonyPatch(typeof(CityServiceBudgetSystem), nameof(CityServiceBudgetSystem.SetServiceBudget))]
static class BudgetPatch
{
    static void Prefix(CityServiceBudgetSystem __instance, Entity servicePrefab, int percentage)
    {
        int oldPercentage = __instance.GetServiceBudget(servicePrefab);
        EconomyDebounceSystem.RecordBudgetChange(servicePrefab, oldPercentage, percentage);
    }
}
