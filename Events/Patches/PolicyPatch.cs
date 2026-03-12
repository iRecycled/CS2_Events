using Game.UI.InGame;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches PoliciesUISystem.SetPolicy (public) to fire OnPolicyChanged.
///
/// SetPolicy is the single entry point used for district policies, city policies,
/// transport route policies, and building policies — all route through here
/// (or SetCityPolicy, which calls ModifyPolicy directly, so we also patch that).
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), nameof(PoliciesUISystem.SetPolicy))]
static class PolicyPatch
{
    static void Prefix(Entity target, Entity policy, bool active, float adjustment)
    {
        EventBus.FirePolicyChanged(new PolicyChangedEvent(target, policy, active, adjustment));
    }
}

/// <summary>
/// Also patches SetCityPolicy, which bypasses SetPolicy and calls ModifyPolicy directly.
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), nameof(PoliciesUISystem.SetCityPolicy))]
static class CityPolicyPatch
{
    static void Prefix(Entity policy, bool active, float adjustment)
    {
        // Target is the city entity — subscribers can distinguish by checking
        // whether the target has a Game.City.City component.
        // We pass Entity.Null here; ModifyPolicy resolves City internally.
        EventBus.FirePolicyChanged(new PolicyChangedEvent(Entity.Null, policy, active, adjustment));
    }
}
