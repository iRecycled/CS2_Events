using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches CityServiceBudgetSystem.SetServiceBudget (public) to fire OnBudgetChanged.
///
/// This is a Prefix patch so the event fires with the new values before they
/// are written to the ServiceBudgetData buffer — useful for before/after comparisons
/// if a subscriber also reads the old value from the EntityManager.
/// </summary>
[HarmonyPatch(typeof(CityServiceBudgetSystem), nameof(CityServiceBudgetSystem.SetServiceBudget))]
static class BudgetPatch
{
    static void Prefix(Entity servicePrefab, int percentage)
    {
        EventBus.FireBudgetChanged(new BudgetChangedEvent(servicePrefab, percentage));
    }
}
