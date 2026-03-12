using CS2Hooks.Actions.Apply;
using CS2Hooks.Events;

namespace CS2Hooks;

/// <summary>
/// Loopback test harness. Proves the event → apply round-trip works.
///
/// Building test:
///   1. Place a building   → OnBuildingPlaced fires, event is stored
///   2. Bulldoze anything  → OnObjectDemolished fires, stored building is re-queued
///   3. BuildingApplier creates the entity → building reappears
///
/// Zone test (FloodFill):
///   1. Paint zone A       → OnZoneChanged fires (Prefab≠null), event is stored
///   2. Right-click to dezone A → OnZoneChanged fires (Prefab==null), _lastZone kept as A
///   3. Paint zone B anywhere   → OnZoneChanged fires (Prefab≠null):
///        stored zone A is re-queued into ZoneApplier → zone A reappears
///        zone B becomes the new stored zone
///
/// Road / net test:
///   1. Place road A       → OnRoadPlaced fires, event is stored
///   2. Bulldoze road A    → OnObjectDemolished fires, stored road is re-queued
///   3. NetCourseApplier creates a definition entity → game pipeline builds road A
///
/// Enable in Mod.cs for local testing only. Remove before shipping.
/// </summary>
internal static class TestHarness
{
    private static BuildingPlacedEvent? _lastBuilding;
    private static ZoneChangedEvent?   _lastZone;
    private static RoadPlacedEvent?    _lastRoad;

    internal static void Enable()
    {
        EventBus.OnBuildingPlaced   += OnBuildingPlaced;
        EventBus.OnZoneChanged      += OnZoneChanged;
        EventBus.OnObjectDemolished += OnObjectDemolished;
        EventBus.OnRoadPlaced       += OnRoadPlaced;

        DebugLogger.Write("[TESTHARNESS]     enabled — place a building, zone, or road then bulldoze to re-apply");
    }

    private static void OnBuildingPlaced(BuildingPlacedEvent e)
    {
        _lastBuilding = e;
        DebugLogger.Write($"[TESTHARNESS]     stored building '{e.Prefab?.name}' at {e.Position}");
    }

    private static void OnRoadPlaced(RoadPlacedEvent e)
    {
        _lastRoad = e;
        DebugLogger.Write(
            $"[TESTHARNESS]     stored road '{e.Prefab?.name}' mode={e.Mode}" +
            $" start={e.ControlPoints[0].Position}");
    }

    private static void OnZoneChanged(ZoneChangedEvent e)
    {
        if (e.Prefab == null)
        {
            // Dezone action — re-queue the dezone so the applier can mirror it,
            // but do NOT overwrite _lastZone (we still want to re-apply zone A later).
            DebugLogger.Write($"[TESTHARNESS]     dezone {e.Mode} {e.EndPosition} — re-queuing");
            ZoneApplier.Enqueue(e);
            return;
        }

        // Zone action — re-apply the previously stored zone (if any), then store this one.
        // Test flow: zone A → dezone → zone B anywhere → A comes back.
        if (_lastZone is { } z)
        {
            DebugLogger.Write($"[TESTHARNESS]     re-queuing zone '{z.Prefab?.name}' {z.Mode}");
            ZoneApplier.Enqueue(z);
        }

        _lastZone = e;
        DebugLogger.Write($"[TESTHARNESS]     stored zone '{e.Prefab?.name}' {e.Mode} {e.StartPosition} → {e.EndPosition}");
    }

    private static void OnObjectDemolished(ObjectDemolishedEvent e)
    {
        if (_lastBuilding is { } b)
        {
            DebugLogger.Write($"[TESTHARNESS]     re-queuing building '{b.Prefab?.name}' at {b.Position}");
            BuildingApplier.Enqueue(b);
            _lastBuilding = null;
        }

        if (_lastRoad is { } r)
        {
            DebugLogger.Write(
                $"[TESTHARNESS]     re-queuing road '{r.Prefab?.name}' mode={r.Mode}" +
                $" pts={r.ControlPoints.Length}");
            NetCourseApplier.Enqueue(r);
            _lastRoad = null;
        }
    }
}
