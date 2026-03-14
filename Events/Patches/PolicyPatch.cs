using CS2Hooks.Actions.Apply;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Returns true if <paramref name="policyEntity"/> is the "Route Out of Service"
/// policy — identified by having <see cref="RouteOptionData"/> with the
/// <see cref="RouteOption.Inactive"/> bit set.
/// </summary>
static class RouteToggleHelper
{
    internal static bool IsOutOfServicePolicy(EntityManager em, Entity policyEntity)
    {
        if (!em.HasComponent<RouteOptionData>(policyEntity)) return false;
        var data = em.GetComponentData<RouteOptionData>(policyEntity);
        return (data.m_OptionMask & (uint)(1 << (int)RouteOption.Inactive)) != 0;
    }
}

/// <summary>
/// Patches PoliciesUISystem.SetPolicy (public) to fire OnPolicyChanged.
///
/// SetPolicy is the single entry point used for district policies,
/// transport route policies, and building policies.
/// Captures old state from the entity's Policy buffer before the change.
/// Also fires OnTransportLineToggled when the "Route Out of Service" policy is toggled
/// via the Lines Overview panel (LinesSection path).
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), nameof(PoliciesUISystem.SetPolicy))]
static class PolicyPatch
{
    static void Prefix(PoliciesUISystem __instance, Entity target, Entity policy, bool active, float adjustment)
    {
        if (PolicyApplier.IsApplying) return;
        GetOldState(__instance.EntityManager, target, policy, out bool oldActive, out float oldAdjustment);
        EventBus.FirePolicyChanged(new PolicyChangedEvent(target, policy, oldActive, active, oldAdjustment, adjustment));

        // Also fire TransportLineToggled when the out-of-service policy changes via LinesSection.
        if (RouteToggleHelper.IsOutOfServicePolicy(__instance.EntityManager, policy))
            EventBus.FireTransportLineToggled(new TransportLineToggledEvent(target, policy, !active));
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
/// Patches PoliciesUISystem.SetSelectedInfoPolicy(Entity policy, bool active, float adjustment)
/// — the overload called by ActionsSection when the player clicks the
/// "Deactivate / Activate" toggle button on a selected transport line.
/// This overload bypasses SetPolicy entirely (goes straight to ModifyPolicy).
/// </summary>
[HarmonyPatch(typeof(PoliciesUISystem), "SetSelectedInfoPolicy",
    new[] { typeof(Entity), typeof(bool), typeof(float) })]
static class SelectedInfoPolicyPatch
{
    static void Prefix(PoliciesUISystem __instance, Entity policy, bool active)
    {
        if (PolicyApplier.IsApplying) return;
        if (!RouteToggleHelper.IsOutOfServicePolicy(__instance.EntityManager, policy)) return;

        Entity line = __instance.World
            .GetOrCreateSystemManaged<SelectedInfoUISystem>()
            .selectedEntity;
        if (line == Entity.Null) return;

        // active=true  → "Route Out of Service" policy is being activated → line going INACTIVE
        // active=false → "Route Out of Service" policy is being deactivated → line going ACTIVE
        EventBus.FireTransportLineToggled(new TransportLineToggledEvent(line, policy, !active));
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
