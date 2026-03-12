using System;
using Colossal.Mathematics;
using Game.Common;
using Game.Tools;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

[HarmonyPatch(typeof(NetToolSystem), "Apply")]
static class NetToolApplyPatch
{
    static NetToolSystem.Mode _mode;
    static PlacementPoint[]   _points = Array.Empty<PlacementPoint>();
    static Bezier4x3?         _capturedCurve;

    static void Prefix(NetToolSystem __instance)
    {
        _mode = __instance.actualMode;

        // GetControlPoints() is public — capture before Apply clears the list.
        var list = __instance.GetControlPoints(out var dep);
        dep.Complete();

        _points = new PlacementPoint[list.Length];
        for (int i = 0; i < list.Length; i++)
            _points[i] = new PlacementPoint(
                list[i].m_Position,
                list[i].m_Rotation,
                list[i].m_Elevation,
                list[i].m_OriginalEntity,
                list[i].m_ElementIndex);

        // Capture the exact Bezier4x3 from the game's definition entity.
        // At Prefix time, the preview definition entity from the previous frame still
        // exists in ECS (ECB destruction is deferred). Its NetCourse.m_Curve holds
        // the exact cubic bezier the game computed — far more accurate than
        // reconstructing from control point positions.
        _capturedCurve = null;
        var em = __instance.EntityManager;
        var query = em.CreateEntityQuery(
            ComponentType.ReadOnly<CreationDefinition>(),
            ComponentType.ReadOnly<NetCourse>());

        int count = query.CalculateEntityCount();
        if (count == 1)
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            _capturedCurve = em.GetComponentData<NetCourse>(entities[0]).m_Curve;
            entities.Dispose();
        }
        else if (count > 1)
        {
            // Multiple definition entities (U-turn split, grid mode, etc.)
            // Fall back to BuildCurve() on replay.
            DebugLogger.Write($"[NET TOOL]        {count} definition entities — curve capture skipped");
        }

        query.Dispose();
    }

    static void Postfix(NetToolSystem __instance)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        if (_mode == NetToolSystem.Mode.Replace)
            EventBus.FireRoadUpgraded(new RoadUpgradedEvent(__instance.prefab, _points, _capturedCurve));
        else
            EventBus.FireRoadPlaced(new RoadPlacedEvent(_mode, __instance.prefab, _points, _capturedCurve));
    }
}
