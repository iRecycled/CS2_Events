using System;
using System.Reflection;
using CS2Hooks.Events;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches RouteToolSystem.Apply to fire OnTransportLinePlaced:
///
///   applyMode == Apply  → full loop closed (IsComplete=true)
///   applyMode == Clear  → individual stop confirmed mid-placement (IsComplete=false)
///
/// m_ControlPoints and m_State are reset inside Apply, so we capture them in Prefix.
/// For the partial-placement case (Clear), m_ControlPoints holds the stops placed
/// BEFORE this click — the new stop is appended after the condition inside Apply.
/// For the complete case (Apply), m_ControlPoints holds all stops BEFORE Clear() runs.
/// </summary>
[HarmonyPatch(typeof(RouteToolSystem), "Apply")]
static class RouteToolPatch
{
    private static FieldInfo? _prefabField;
    private static FieldInfo? _stateField;
    private static FieldInfo? _cpField;

    private static RoutePrefab?           _capturedPrefab;
    private static RouteToolSystem.State  _capturedState;
    private static float3[]               _capturedPositions    = Array.Empty<float3>();
    private static Entity[]               _capturedSnapEntities = Array.Empty<Entity>();

    static void Prefix(RouteToolSystem __instance)
    {
        _capturedPrefab       = null;
        _capturedState        = RouteToolSystem.State.Default;
        _capturedPositions    = Array.Empty<float3>();
        _capturedSnapEntities = Array.Empty<Entity>();

        _prefabField ??= typeof(RouteToolSystem)
            .GetField("m_SelectedPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
        _stateField  ??= typeof(RouteToolSystem)
            .GetField("m_State", BindingFlags.NonPublic | BindingFlags.Instance);
        _cpField     ??= typeof(RouteToolSystem)
            .GetField("m_ControlPoints", BindingFlags.NonPublic | BindingFlags.Instance);

        _capturedPrefab = _prefabField?.GetValue(__instance) as RoutePrefab;

        if (_stateField?.GetValue(__instance) is RouteToolSystem.State s)
            _capturedState = s;

        // Capture control points before Apply may clear them (Complete case).
        if (_cpField?.GetValue(__instance) is NativeList<ControlPoint> cp && cp.IsCreated && cp.Length > 0)
        {
            int len = cp.Length;
            _capturedPositions    = new float3[len];
            _capturedSnapEntities = new Entity[len];
            for (int i = 0; i < len; i++)
            {
                _capturedPositions[i]    = cp[i].m_Position;
                _capturedSnapEntities[i] = cp[i].m_OriginalEntity;
            }
        }
    }

    static void Postfix(RouteToolSystem __instance)
    {
        if (_capturedPrefab == null) return;

        bool isComplete  = __instance.applyMode == ApplyMode.Apply;
        bool isStopAdded = __instance.applyMode == ApplyMode.Clear
                           && _capturedState == RouteToolSystem.State.Create;

        if (!isComplete && !isStopAdded) return;

        // Partial events are only meaningful once at least one stop is already placed.
        if (!isComplete && _capturedPositions.Length == 0) return;

        var prefabSystem = __instance.World.GetOrCreateSystemManaged<PrefabSystem>();
        Entity prefabEntity = prefabSystem.GetEntity(_capturedPrefab);

        var em = __instance.EntityManager;

        // Only fire for player-placed transport lines, not work/cargo routes.
        if (!em.HasComponent<RouteData>(prefabEntity)) return;
        if (em.GetComponentData<RouteData>(prefabEntity).m_Type != RouteType.TransportLine) return;

        var transportType = TransportType.None;
        if (em.HasComponent<TransportLineData>(prefabEntity))
            transportType = em.GetComponentData<TransportLineData>(prefabEntity).m_TransportType;

        EventBus.FireTransportLinePlaced(new TransportLinePlacedEvent(
            _capturedPrefab,
            prefabEntity,
            transportType,
            _capturedState == RouteToolSystem.State.Modify,
            isComplete,
            _capturedPositions,
            _capturedSnapEntities));
    }
}
