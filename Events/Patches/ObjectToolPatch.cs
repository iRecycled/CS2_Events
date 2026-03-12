using Game.Tools;
using HarmonyLib;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

[HarmonyPatch(typeof(ObjectToolSystem), "Apply")]
static class ObjectToolApplyPatch
{
    static ObjectToolSystem.Mode _mode;
    static float3                _position;
    static quaternion            _rotation;

    static void Prefix(ObjectToolSystem __instance)
    {
        _mode = __instance.actualMode;

        // GetControlPoints() is public — first control point is the placement anchor.
        var list = __instance.GetControlPoints(out var dep);
        dep.Complete();

        if (list.Length > 0)
        {
            _position = list[0].m_Position;
            _rotation = list[0].m_Rotation;
        }
        else
        {
            _position = default;
            _rotation = quaternion.identity;
        }
    }

    static void Postfix(ObjectToolSystem __instance)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        EventBus.FireBuildingPlaced(new BuildingPlacedEvent(
            _mode,
            __instance.prefab,
            _position,
            _rotation
        ));
    }
}
