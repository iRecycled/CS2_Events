using System.Reflection;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

[HarmonyPatch(typeof(UpgradeToolSystem), "Apply")]
static class UpgradeToolApplyPatch
{
    static readonly FieldInfo? UpgradingObjectField = typeof(UpgradeToolSystem)
        .GetField("m_UpgradingObject", BindingFlags.NonPublic | BindingFlags.Instance);

    static Entity _upgradingObject;
    static float3 _position;

    static void Prefix(UpgradeToolSystem __instance)
    {
        _upgradingObject = UpgradingObjectField?.GetValue(__instance) is Entity e ? e : Entity.Null;

        // Read the building's world position from the EntityManager.
        if (_upgradingObject != Entity.Null)
        {
            var em = Unity.Entities.World.DefaultGameObjectInjectionWorld?.EntityManager;
            if (em.HasValue && em.Value.HasComponent<Game.Objects.Transform>(_upgradingObject))
                _position = em.Value.GetComponentData<Game.Objects.Transform>(_upgradingObject).m_Position;
            else
                _position = default;
        }
        else
        {
            _position = default;
        }
    }

    static void Postfix(UpgradeToolSystem __instance)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        EventBus.FireBuildingUpgraded(new BuildingUpgradedEvent(
            _upgradingObject,
            __instance.prefab,
            _position
        ));
    }
}
