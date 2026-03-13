using CS2Hooks.Actions.Apply;
using Game.Policies;
using Game.Simulation;
using Game.UI.InGame;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches PoliciesUISystem.SetPolicy (public) to fire OnPolicyChanged.
///
/// SetPolicy is the single entry point used for district policies,
/// transport route policies, and building policies.
/// Captures old state from the entity's Policy buffer before the change.
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), nameof(PoliciesUISystem.SetPolicy))]
static class PolicyPatch
{
    static void Prefix(PoliciesUISystem __instance, Entity target, Entity policy, bool active, float adjustment)
    {
        if (PolicyApplier.IsApplying) return;
        GetOldState(__instance.EntityManager, target, policy, out bool oldActive, out float oldAdjustment);
        EventBus.FirePolicyChanged(new PolicyChangedEvent(target, policy, oldActive, active, oldAdjustment, adjustment));
    }

    internal static void GetOldState(EntityManager em, Entity target, Entity policy,
                                     out bool oldActive, out float oldAdjustment)
    {
        oldActive     = false;
        oldAdjustment = 0f;
        if (target == Entity.Null || !em.HasBuffer<Policy>(target)) return;
        var buffer = em.GetBuffer<Policy>(target, isReadOnly: true);
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i].m_Policy == policy)
            {
                oldActive     = (buffer[i].m_Flags & PolicyFlags.Active) != 0;
                oldAdjustment = buffer[i].m_Adjustment;
                return;
            }
        }
    }
}

/// <summary>
/// Also patches SetCityPolicy, which bypasses SetPolicy and calls ModifyPolicy directly.
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), nameof(PoliciesUISystem.SetCityPolicy))]
static class CityPolicyPatch
{
    static void Prefix(PoliciesUISystem __instance, Entity policy, bool active, float adjustment)
    {
        if (PolicyApplier.IsApplying) return;
        Entity city = __instance.World.GetOrCreateSystemManaged<CitySystem>().City;
        PolicyPatch.GetOldState(__instance.EntityManager, city, policy, out bool oldActive, out float oldAdjustment);
        // Pass Entity.Null as Target — subscribers can distinguish city policies by this sentinel.
        EventBus.FirePolicyChanged(new PolicyChangedEvent(Entity.Null, policy, oldActive, active, oldAdjustment, adjustment));
    }
}
