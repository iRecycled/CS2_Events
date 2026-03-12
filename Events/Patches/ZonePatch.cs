using System;
using System.Reflection;
using CS2Hooks.Actions.Apply;
using Game.Tools;
using Game.Zones;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

// ZoneToolSystem.State enum: Default=0, Zoning=1, Dezoning=2
//
// Zoning path  (left-click):  Apply()  is called by OnUpdate.
// Dezoning path (right-click): Cancel() is called by OnUpdate.
// Both methods share the same state-machine shape, so the two patches below
// are structurally identical — only the "skip" transition and prefab differ.
//
// Paint drag handling:
//   Update() (not Apply) runs every frame during a drag.
//   We intercept ApplyZonesSystem.OnUpdate() (ApplyZonesPatch, below) to
//   collect the world-position of every cell the game actually marks as
//   CellFlags.Selected before those Temp entities are destroyed.
//   On commit we attach the accumulated positions to ZoneChangedEvent so that
//   ZoneApplier can replay the exact painted shape.

// ─────────────────────────────────────────────────────────────────────────────
// Paint-cell capture: intercept ApplyZonesSystem before it destroys Temp
// zone-block entities, recording the world-pos of every Selected cell.
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(ApplyZonesSystem), "OnUpdate")]
static class ApplyZonesPatch
{
    static void Prefix()
    {
        if (!ZonePaintCapture.IsActive) return;

        var em    = World.DefaultGameObjectInjectionWorld.EntityManager;
        var query = em.CreateEntityQuery(
            ComponentType.ReadOnly<Temp>(),
            ComponentType.ReadOnly<Block>());

        var entities = query.ToEntityArray(Allocator.Temp);
        query.Dispose();

        foreach (var e in entities)
        {
            var temp = em.GetComponentData<Temp>(e);
            if (temp.m_Original == Entity.Null) continue;
            if (!em.HasBuffer<Cell>(e)) continue;

            var block = em.GetComponentData<Block>(e);
            var cells = em.GetBuffer<Cell>(e, isReadOnly: true);

            for (int i = 0; i < cells.Length; i++)
            {
                if ((cells[i].m_State & CellFlags.Selected) == CellFlags.None) continue;
                int row = i / block.m_Size.x;
                int col = i % block.m_Size.x;
                ZonePaintCapture.AddPosition(
                    ZoneUtils.GetCellPosition(block, new int2(col, row)));
            }
        }

        entities.Dispose();

        if (ZonePaintCapture.PendingStop)
            ZonePaintCapture.StopNow();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Zone (left-click / Apply)
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(ZoneToolSystem), "Apply")]
static class ZoneApplyPatch
{
    static readonly FieldInfo? StartPointField   = typeof(ZoneToolSystem)
        .GetField("m_StartPoint",   BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo? RaycastPointField = typeof(ZoneToolSystem)
        .GetField("m_RaycastPoint", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo? StateField        = typeof(ZoneToolSystem)
        .GetField("m_State",        BindingFlags.NonPublic | BindingFlags.Instance);

    static ZoneToolSystem.Mode _mode;
    static float3              _start;
    static float3              _end;
    static float2              _blockDir;
    static float3              _paintPressPos; // cursor position at the moment of Paint press

    // __state carries the pre-Apply() m_State value into Postfix via Harmony's state mechanism
    static void Prefix(ZoneToolSystem __instance, out int __state)
    {
        __state   = StateField?.GetValue(__instance) is { } v ? Convert.ToInt32(v) : 0;
        _mode     = __instance.mode;
        _blockDir = default;

        // Use m_HitPosition (actual terrain hit, inside the zone cell) rather than
        // m_Position (snapped, may land exactly on a cell boundary).
        if (StartPointField?.GetValue(__instance) is ControlPoint sp)
        {
            _start = sp.m_HitPosition;

            // For Marquee: capture the road direction of the block under the start point.
            // This is needed to reconstruct the oriented marquee rectangle on replay —
            // the game uses Block.m_Direction (not axis-aligned) to build the Quad3.
            if (_mode == ZoneToolSystem.Mode.Marquee && sp.m_OriginalEntity != Entity.Null)
            {
                var em = World.DefaultGameObjectInjectionWorld.EntityManager;
                if (em.HasComponent<Block>(sp.m_OriginalEntity))
                    _blockDir = em.GetComponentData<Block>(sp.m_OriginalEntity).m_Direction;
            }
        }

        _end = RaycastPointField?.GetValue(__instance) is ControlPoint rp
            ? rp.m_HitPosition : default;

        // Paint: m_StartPoint is not set yet at the Default→Zoning press, so record
        // the cursor position (_end = m_RaycastPoint) now for use on commit.
        if (_mode == ZoneToolSystem.Mode.Paint && __state == 0)
            _paintPressPos = _end;
    }

    static void Postfix(ZoneToolSystem __instance, int __state)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        int stateAfter = StateField?.GetValue(__instance) is { } v ? Convert.ToInt32(v) : 0;

        // Default→Zoning: drag just started — not committed yet.
        if (__state == 0 /* Default */ && stateAfter == 1 /* Zoning */)
        {
            // For Paint: arm the cell-capture so ApplyZonesPatch starts collecting.
            if (_mode == ZoneToolSystem.Mode.Paint)
                ZonePaintCapture.Start();
            return;
        }

        float3 start;
        if (__state == 0 && stateAfter == 0)
            // Single-click: m_StartPoint is never set, so use cursor for both.
            start = _end;
        else if (_mode == ZoneToolSystem.Mode.Paint)
            // Paint drag commit: emit full stroke from press position to release cursor.
            start = _paintPressPos;
        else
            // FloodFill / Marquee drag commit: m_StartPoint holds the relevant anchor.
            start = _start;

        // For Paint drag commit: finalise capture (one more ApplyZonesSystem pass
        // will run this frame to catch the final mini-segment).
        float3[]? paintedCells = null;
        if (_mode == ZoneToolSystem.Mode.Paint && __state == 1 /* Zoning */)
        {
            ZonePaintCapture.CommitPending();
            if (ZonePaintCapture.PaintedPositions.Count > 0)
                paintedCells = ZonePaintCapture.PaintedPositions.ToArray();
        }

        EventBus.FireZoneChanged(new ZoneChangedEvent(
            _mode,
            __instance.prefab,  // non-null: the zone type being applied
            start,
            _end,
            _blockDir,          // non-zero only for Marquee with a block under cursor
            paintedCells        // non-null only for Paint drag (not single-click)
        ));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Dezone (right-click / Cancel)
//
// State machine for Cancel():
//   Default  → Dezoning : drag start  (singleFrameOnly=false) — skip
//   Dezoning → Default  : drag commit                         — emit ✓
//   Default  → Default  : single-click (singleFrameOnly=true) — emit ✓
// ─────────────────────────────────────────────────────────────────────────────
[HarmonyPatch(typeof(ZoneToolSystem), "Cancel")]
static class ZoneCancelPatch
{
    static readonly FieldInfo? StartPointField   = typeof(ZoneToolSystem)
        .GetField("m_StartPoint",   BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo? RaycastPointField = typeof(ZoneToolSystem)
        .GetField("m_RaycastPoint", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly FieldInfo? StateField        = typeof(ZoneToolSystem)
        .GetField("m_State",        BindingFlags.NonPublic | BindingFlags.Instance);

    static ZoneToolSystem.Mode _mode;
    static float3              _start;
    static float3              _end;
    static float2              _blockDir;
    static float3              _paintPressPos; // cursor position at the moment of Paint dezone press

    static void Prefix(ZoneToolSystem __instance, out int __state)
    {
        __state   = StateField?.GetValue(__instance) is { } v ? Convert.ToInt32(v) : 0;
        _mode     = __instance.mode;
        _blockDir = default;

        if (StartPointField?.GetValue(__instance) is ControlPoint sp)
        {
            _start = sp.m_HitPosition;

            if (_mode == ZoneToolSystem.Mode.Marquee && sp.m_OriginalEntity != Entity.Null)
            {
                var em = World.DefaultGameObjectInjectionWorld.EntityManager;
                if (em.HasComponent<Block>(sp.m_OriginalEntity))
                    _blockDir = em.GetComponentData<Block>(sp.m_OriginalEntity).m_Direction;
            }
        }

        _end = RaycastPointField?.GetValue(__instance) is ControlPoint rp
            ? rp.m_HitPosition : default;

        // Paint dezone: record cursor at press for use on commit.
        if (_mode == ZoneToolSystem.Mode.Paint && __state == 0)
            _paintPressPos = _end;
    }

    static void Postfix(ZoneToolSystem __instance, int __state)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        int stateAfter = StateField?.GetValue(__instance) is { } v ? Convert.ToInt32(v) : 0;

        // Default→Dezoning: drag start — not committed yet.
        if (__state == 0 /* Default */ && stateAfter == 2 /* Dezoning */)
        {
            // For Paint dezone: arm the cell-capture.
            if (_mode == ZoneToolSystem.Mode.Paint)
                ZonePaintCapture.Start();
            return;
        }

        float3 start;
        if (__state == 0 && stateAfter == 0)
            start = _end;
        else if (_mode == ZoneToolSystem.Mode.Paint)
            start = _paintPressPos;
        else
            start = _start;

        // For Paint dezone drag commit: finalise capture.
        float3[]? paintedCells = null;
        if (_mode == ZoneToolSystem.Mode.Paint && __state == 2 /* Dezoning */)
        {
            ZonePaintCapture.CommitPending();
            if (ZonePaintCapture.PaintedPositions.Count > 0)
                paintedCells = ZonePaintCapture.PaintedPositions.ToArray();
        }

        EventBus.FireZoneChanged(new ZoneChangedEvent(
            _mode,
            null,           // null = dezone (target is ZoneType.None)
            start,
            _end,
            _blockDir,
            paintedCells
        ));
    }
}
