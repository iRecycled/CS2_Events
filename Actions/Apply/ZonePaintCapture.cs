using CS2Hooks.Events;
using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Accumulates the exact world-space centre positions of every zone-block cell
/// marked <c>CellFlags.Selected</c> by <c>GenerateZonesSystem</c> during a
/// Paint (or Paint-dezone) drag.
///
/// Lifecycle (managed by ZonePatch):
///   Default→Zoning  : Start()         — clears previous data, arms capture
///   every frame      : AddPosition()   — called from ApplyZonesPatch.Prefix
///   Zoning→Default  : CommitPending() — signals the last ApplyZonesSystem
///                                        pass to stop after accumulating
/// Read by ZoneApplier to replay the exact painted shape.
/// </summary>
internal static class ZonePaintCapture
{
    internal static readonly List<float3> PaintedPositions = new();

    internal static bool IsActive    { get; private set; }
    internal static bool PendingStop { get; private set; }

    /// <summary>Begin accumulating for a new Paint drag.</summary>
    internal static void Start()
    {
        PaintedPositions.Clear();
        IsActive    = true;
        PendingStop = false;
    }

    /// <summary>
    /// Signal drag commit. Capture stays active for one more
    /// ApplyZonesSystem pass (to catch the final frame's cells), then stops.
    /// </summary>
    internal static void CommitPending() => PendingStop = true;

    internal static void StopNow()
    {
        IsActive    = false;
        PendingStop = false;
    }

    internal static void AddPosition(float3 worldPos) =>
        PaintedPositions.Add(worldPos);
}
