using Game.City;
using Game.Simulation;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches ServiceFeeSystem.SetFee(PlayerResource, DynamicBuffer&lt;ServiceFee&gt;, float)
/// to record a debounced service fee change.
/// Routes through EconomyDebounceSystem so slider drag steps are batched
/// into a single ServiceFeeChangedEvent fired after 1 second of inactivity.
/// </summary>
[HarmonyPatch(typeof(ServiceFeeSystem), nameof(ServiceFeeSystem.SetFee))]
static class ServiceFeePatch
{
    static void Prefix(PlayerResource resource, DynamicBuffer<ServiceFee> fees, float value)
    {
        float oldFee = ServiceFeeSystem.GetFee(resource, fees);
        EconomyDebounceSystem.RecordServiceFeeChange(resource, oldFee, value);
    }
}
