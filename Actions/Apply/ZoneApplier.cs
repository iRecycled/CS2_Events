using CS2Hooks.Events;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Re-applies zone changes by directly modifying Cell buffers on zone Block entities.
///
/// Mode strategies:
///   FloodFill — BFS in the BASE BLOCK's coordinate space (mirrors GenerateZonesSystem).
///               CanShareCells gates cross-block expansion; stateMask+matchZone gate
///               cell acceptance. Seed block is chosen deterministically (closest cell
///               centre to cursor). Negative / out-of-bounds BFS coords extrapolate
///               naturally into adjacent blocks via GetCellPosition.
///   Paint     — Mirrors BaseLineIterator exactly: GetCellIndex on both endpoints gives
///               a row/column range; each cell in that rectangle is tested with
///               MathUtils.Intersect(cellQuad, line).
///   Marquee   — Cells within the road-oriented Quad2 built from the block direction
///               at the start point (mirrors CreateDefinitionsJob / MarqueeIterator).
/// </summary>
public class ZoneApplier : GameSystemBase
{
    private static readonly Queue<ZoneChangedEvent> _pending = new();

    private PrefabSystem _prefabSystem = null!;
    private EntityQuery  _blockQuery;

    public static void Enqueue(ZoneChangedEvent e) => _pending.Enqueue(e);

    protected override void OnCreate()
    {
        base.OnCreate();
        _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        _blockQuery   = GetEntityQuery(
            ComponentType.ReadOnly<Block>(),
            ComponentType.ReadWrite<Cell>());
    }

    protected override void OnUpdate()
    {
        while (_pending.TryDequeue(out var e))
            Apply(e);
    }

    // -------------------------------------------------------------------------
    private void Apply(ZoneChangedEvent e)
    {
        ZoneType targetZone;

        if (e.Prefab == null)
        {
            // Dezone action — target is ZoneType.None (remove zone from cells).
            targetZone = default; // ZoneType.None
            DebugLogger.Write($"[ZONE APPLIER]    dezone {e.Mode}");
        }
        else
        {
            Entity prefabEntity;
            try   { prefabEntity = _prefabSystem.GetEntity(e.Prefab); }
            catch (System.Exception ex)
            {
                DebugLogger.Write($"[ZONE APPLIER]    GetEntity threw: {ex.Message}");
                return;
            }

            if (prefabEntity == Entity.Null)
            {
                DebugLogger.Write($"[ZONE APPLIER]    '{e.Prefab.name}' → Entity.Null, skipping");
                return;
            }

            if (!EntityManager.HasComponent<ZoneData>(prefabEntity))
            {
                DebugLogger.Write($"[ZONE APPLIER]    '{e.Prefab.name}' has no ZoneData");
                return;
            }

            targetZone = EntityManager.GetComponentData<ZoneData>(prefabEntity).m_ZoneType;
            DebugLogger.Write($"[ZONE APPLIER]    '{e.Prefab.name}' {e.Mode}");
        }

        var start = e.StartPosition.xz;
        var end   = e.EndPosition.xz;

        DebugLogger.Write($"[ZONE APPLIER]    {start} → {end}");

        switch (e.Mode)
        {
            case ZoneToolSystem.Mode.FloodFill:
                ApplyFloodFill(targetZone, seedPos: end, dragStart: start);
                break;
            case ZoneToolSystem.Mode.Paint:
                if (e.PaintedCellPositions is { Length: > 0 } positions)
                    ApplyPaintFromPositions(targetZone, positions);
                else
                    ApplyStroke(targetZone, start, end);
                break;
            case ZoneToolSystem.Mode.Marquee:
                ApplyMarquee(targetZone, start, end, e.MarqueeDirection);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // FloodFill: BFS from seedPos, mirroring the game's FloodFillBlocks logic.
    //
    // Key design (matches GenerateZonesSystem.FloodFillBlocks exactly):
    //   1. Find the BASE block — the one whose cell is closest to seedPos.
    //   2. BFS in BASE BLOCK coordinate space. Coords can go negative / beyond
    //      block.m_Size as we extrapolate into adjacent blocks.
    //   3. For each BFS position: compute world-pos via GetCellPosition(baseBlock,ci),
    //      find which block contains that world-pos AND passes CanShareCells(base,block).
    //   4. Check cell conditions (stateMask, matchZone, not Overridden).
    //   5. Expand to 4 neighbours — always in BASE BLOCK coordinate increments.
    //
    // This ensures:
    //   • Deterministic seed block (closest cell centre wins).
    //   • CanShareCells prevents jumping to non-adjacent / non-aligned blocks
    //     (the main cause of over-expansion and slowness in the old code).
    //   • stateMask (Visible|Occupied) matches the game's containment logic.
    //
    // Performance: same 3-pass strategy (read-only snapshot → BFS on managed
    // arrays → minimal writable write-back) to avoid sync-point stalls.
    // -------------------------------------------------------------------------
    private void ApplyFloodFill(ZoneType targetZone, float2 seedPos, float2 dragStart)
    {
        var blockEntities = _blockQuery.ToEntityArray(Allocator.Temp);

        // ── Pass 1: snapshot all blocks within radius ─────────────────────────
        const float MAX_RADIUS = 512f;

        var blockById  = new Dictionary<int, Block>(64);
        var entityById = new Dictionary<int, Entity>(64);
        var cellsById  = new Dictionary<int, Cell[]>(64);

        foreach (var be in blockEntities)
        {
            var block = EntityManager.GetComponentData<Block>(be);
            float halfDiag = math.cmax((float2)block.m_Size) * ZoneUtils.CELL_SIZE * 0.5f;
            if (math.distance(block.m_Position.xz, seedPos) > MAX_RADIUS + halfDiag)
                continue;

            blockById[be.Index]  = block;
            entityById[be.Index] = be;

            var buf = EntityManager.GetBuffer<Cell>(be, isReadOnly: true);
            var arr = new Cell[buf.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = buf[i];
            cellsById[be.Index] = arr;
        }

        // ── Locate seed cell — pick the block whose cell centre is CLOSEST ────
        // (deterministic; avoids ambiguity when the cursor is near a block edge)
        int       baseIdx    = -1;
        int2      baseCI     = default;
        Block     baseBlock  = default;
        ZoneType  matchZone  = default;
        CellFlags stateMask  = default;
        float     bestDistSq = float.MaxValue;

        foreach (var (idx, block) in blockById)
        {
            var ci = ZoneUtils.GetCellIndex(block, seedPos);
            if (!math.all(ci >= 0) || !math.all(ci < block.m_Size)) continue;

            var cell = cellsById[idx][ci.y * block.m_Size.x + ci.x];
            if ((cell.m_State & CellFlags.Visible) == CellFlags.None) continue;

            float2 centre = ZoneUtils.GetCellPosition(block, ci).xz;
            float  distSq = math.distancesq(centre, seedPos);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                baseIdx    = idx;
                baseCI     = ci;
                baseBlock  = block;
                matchZone  = cell.m_Zone;
                stateMask  = cell.m_State & (CellFlags.Visible | CellFlags.Occupied);
            }
        }

        if (baseIdx < 0)
        {
            DebugLogger.Write("[ZONE APPLIER]    FloodFill: no visible block at seed position");
            blockEntities.Dispose();
            return;
        }

        DebugLogger.Write($"[ZONE APPLIER]    FloodFill seed block={baseIdx} ci={baseCI} matchZone={matchZone.m_Index}");

        // ── Pass 2: BFS in BASE BLOCK coordinate space ────────────────────────
        // Coords can go out of baseBlock's bounds — GetCellPosition extrapolates
        // into adjacent block territory, just like the game does.
        var frontier  = new Queue<int2>();
        var visited   = new HashSet<long>();
        var modified  = new HashSet<int>();
        int cellCount = 0;

        FloodFillEnqueue(baseCI, visited, frontier);

        while (frontier.Count > 0)
        {
            int2 ci = frontier.Dequeue();

            // World-space centre of this BFS position (may be outside baseBlock).
            float2 worldPos = ZoneUtils.GetCellPosition(baseBlock, ci).xz;

            // Find the block at worldPos that shares cells with the base block.
            int    foundIdx   = -1;
            int2   foundCI    = default;
            Block  foundBlock = default;
            float  foundDist  = float.MaxValue;

            foreach (var (idx, block) in blockById)
            {
                var localCI = ZoneUtils.GetCellIndex(block, worldPos);
                if (!math.all(localCI >= 0) || !math.all(localCI < block.m_Size)) continue;

                // CanShareCells is the containment gate — matches the game's
                // FloodFillIterator which checks CanShareCells(baseBlock, block).
                if (!ZoneUtils.CanShareCells(baseBlock, block)) continue;

                // If multiple blocks contain worldPos and pass CanShareCells,
                // take the one whose cell centre is closest (deterministic).
                float2 centre = ZoneUtils.GetCellPosition(block, localCI).xz;
                float  dist   = math.distancesq(centre, worldPos);
                if (dist < foundDist)
                {
                    foundDist  = dist;
                    foundIdx   = idx;
                    foundCI    = localCI;
                    foundBlock = block;
                }
            }

            if (foundIdx < 0) continue;

            var cells   = cellsById[foundIdx];
            int cellIdx = foundCI.y * foundBlock.m_Size.x + foundCI.x;
            ref Cell cell = ref cells[cellIdx];

            // Mirrors FloodFillIterator conditions (stateMask + matchZone + not Overridden).
            if ((cell.m_State & (CellFlags.Visible | CellFlags.Occupied)) != stateMask) continue;
            if ((cell.m_State & CellFlags.Overridden) != CellFlags.None)               continue;
            if (!cell.m_Zone.Equals(matchZone))                                        continue;

            cell.m_Zone = targetZone;
            modified.Add(foundIdx);
            cellCount++;

            // Expand in BASE BLOCK coordinate increments (not foundBlock's axes).
            FloodFillEnqueue(new int2(ci.x - 1, ci.y), visited, frontier);
            FloodFillEnqueue(new int2(ci.x + 1, ci.y), visited, frontier);
            FloodFillEnqueue(new int2(ci.x, ci.y - 1), visited, frontier);
            FloodFillEnqueue(new int2(ci.x, ci.y + 1), visited, frontier);
        }

        // ── Pass 3: write back only modified blocks ───────────────────────────
        foreach (var idx in modified)
        {
            var entity = entityById[idx];
            var arr    = cellsById[idx];
            var buf    = EntityManager.GetBuffer<Cell>(entity);
            for (int i = 0; i < buf.Length; i++) buf[i] = arr[i];
            MarkUpdated(entity);
        }

        blockEntities.Dispose();
        DebugLogger.Write($"[ZONE APPLIER]    FloodFill done — {cellCount} cells in {modified.Count} blocks");
    }

    // Pack an int2 BFS coord into a unique 64-bit key.
    // Uses upper 32 bits for y and lower 32 bits for x so negative coords
    // are handled correctly without collision.
    private static long FloodFillKey(int2 ci) =>
        ((long)(uint)ci.y << 32) | (uint)ci.x;

    private static void FloodFillEnqueue(int2 ci, HashSet<long> visited, Queue<int2> frontier)
    {
        if (visited.Add(FloodFillKey(ci)))
            frontier.Enqueue(ci);
    }

    // -------------------------------------------------------------------------
    // Paint: paint every visible cell within the bounding box of the two stroke
    // endpoints (in each block's local cell-index space).
    //
    // The game paints incrementally frame-by-frame via Update(), so a single
    // press→release capture represents a multi-segment path.  Using the full
    // cell-index bounding box (rather than strict line intersection) ensures
    // every cell the drag swept through is covered on replay.
    // -------------------------------------------------------------------------
    private void ApplyStroke(ZoneType targetZone, float2 start, float2 end)
    {
        var line          = new Line2.Segment(start, end);
        var blockEntities = _blockQuery.ToEntityArray(Allocator.Temp);
        int cells_changed = 0, blocks_changed = 0;

        foreach (var blockEntity in blockEntities)
        {
            var block  = EntityManager.GetComponentData<Block>(blockEntity);
            var bounds = ZoneUtils.CalculateBounds(block);

            // Quick AABB reject.
            if (!MathUtils.Intersect(bounds, line, out var _)) continue;

            // Cell-index rectangle spanned by the two stroke endpoints (clamped to block).
            int2 ci_a = ZoneUtils.GetCellIndex(block, start);
            int2 ci_b = ZoneUtils.GetCellIndex(block, end);
            int2 lo   = math.max(math.min(ci_a, ci_b), 0);
            int2 hi   = math.min(math.max(ci_a, ci_b), block.m_Size - 1);
            if (!math.all(hi >= lo)) continue;

            var  cells   = EntityManager.GetBuffer<Cell>(blockEntity);
            bool changed = false;

            for (int row = lo.y; row <= hi.y; row++)
            for (int col = lo.x; col <= hi.x; col++)
            {
                int idx  = row * block.m_Size.x + col;
                var cell = cells[idx];

                if ((cell.m_State & CellFlags.Visible)    == CellFlags.None) continue;
                if ((cell.m_State & CellFlags.Overridden) != CellFlags.None) continue;

                cell.m_Zone = targetZone;
                cells[idx]  = cell;
                changed     = true;
                cells_changed++;
            }

            if (changed) { MarkUpdated(blockEntity); blocks_changed++; }
        }

        blockEntities.Dispose();
        DebugLogger.Write($"[ZONE APPLIER]    Paint done — {cells_changed} cells in {blocks_changed} blocks");
    }

    // -------------------------------------------------------------------------
    // Paint (captured): apply the exact world-space cell positions recorded by
    // ZonePaintCapture / ApplyZonesPatch during the original drag.
    //
    // For each position we look up the block it belongs to (using GetCellIndex),
    // then apply the zone type directly to that cell.  This is exact — no
    // approximation from start/end endpoints — regardless of how curved or
    // non-linear the original drag path was.
    // -------------------------------------------------------------------------
    private void ApplyPaintFromPositions(ZoneType targetZone, float3[] positions)
    {
        var blockEntities = _blockQuery.ToEntityArray(Allocator.Temp);
        int cells_changed = 0, blocks_changed = 0;

        foreach (var blockEntity in blockEntities)
        {
            var block = EntityManager.GetComponentData<Block>(blockEntity);
            var cells = EntityManager.GetBuffer<Cell>(blockEntity);
            bool changed = false;

            foreach (var pos in positions)
            {
                var ci = ZoneUtils.GetCellIndex(block, pos.xz);
                if (!math.all(ci >= 0) || !math.all(ci < block.m_Size)) continue;

                int idx  = ci.y * block.m_Size.x + ci.x;
                var cell = cells[idx];
                if ((cell.m_State & CellFlags.Visible)    == CellFlags.None) continue;
                if ((cell.m_State & CellFlags.Overridden) != CellFlags.None) continue;

                cell.m_Zone = targetZone;
                cells[idx]  = cell;
                changed     = true;
                cells_changed++;
            }

            if (changed) { MarkUpdated(blockEntity); blocks_changed++; }
        }

        blockEntities.Dispose();
        DebugLogger.Write($"[ZONE APPLIER]    Paint (capture) done — {cells_changed} cells in {blocks_changed} blocks");
    }

    // -------------------------------------------------------------------------
    // Marquee: oriented rectangle aligned to the road direction at the start point.
    //
    // Mirrors CreateDefinitionsJob (Mode.Marquee) + MarqueeIterator exactly:
    //   • blockDir (Block.m_Direction) is the road-forward axis.
    //   • right = MathUtils.Right(blockDir) is perpendicular.
    //   • The drag vector is projected onto (fwd, right) to get extents (num, num2).
    //   • Corners are padded by 4 world units (half a cell) on every side.
    //   • Cell containment uses MathUtils.Intersect(Quad2, cellPos) — exact point-in-quad.
    //
    // Falls back to an AABB quad if no block direction was captured (edge case:
    // cursor not over a zone block when the marquee drag began).
    // -------------------------------------------------------------------------
    private void ApplyMarquee(ZoneType targetZone, float2 start, float2 end, float2 blockDir)
    {
        // Build the oriented marquee quad.
        // Winding order matches the game's CreateDefinitionsJob: SW → SE → NE → NW (CW).
        Quad2 quad;
        if (math.lengthsq(blockDir) > 0.001f)
        {
            float2 fwd   = blockDir;
            float2 right = MathUtils.Right(fwd);
            float2 drag  = end - start;
            float  num   = math.dot(drag, fwd);
            float  num2  = math.dot(drag, right);
            if (num  < 0f) { fwd   = -fwd;   num  = -num; }
            if (num2 < 0f) { right = -right; num2 = -num2; }

            // a=SW, b=SE, c=NE, d=NW (CW)
            quad = new Quad2(
                start - (fwd + right) * 4f,
                start - fwd * 4f + right * (num2 + 4f),
                start + fwd * (num + 4f) + right * (num2 + 4f),
                start + fwd * (num + 4f) - right * 4f
            );
            DebugLogger.Write($"[ZONE APPLIER]    Marquee oriented: fwd={fwd} drag={drag} num={num:F1} num2={num2:F1}");
        }
        else
        {
            // AABB fallback — no road direction captured.
            // Winding order: SW → SE → NE → NW (CW) to match game convention.
            var lo = math.min(start, end) - 4f;
            var hi = math.max(start, end) + 4f;
            quad = new Quad2(lo, new float2(hi.x, lo.y), hi, new float2(lo.x, hi.y));
            DebugLogger.Write($"[ZONE APPLIER]    Marquee AABB fallback: start={start} end={end} lo={lo} hi={hi}");
        }

        var quadBounds    = MathUtils.Bounds(quad);
        var blockEntities = _blockQuery.ToEntityArray(Allocator.Temp);
        int cells_changed = 0, blocks_changed = 0;

        foreach (var blockEntity in blockEntities)
        {
            var block  = EntityManager.GetComponentData<Block>(blockEntity);
            var bounds = ZoneUtils.CalculateBounds(block);

            // Quick AABB reject before the more expensive oriented-quad test.
            if (!MathUtils.Intersect(bounds, quadBounds)) continue;

            // Oriented intersection with the block's own quad.
            if (!MathUtils.Intersect(quad, ZoneUtils.CalculateCorners(block))) continue;

            var cells   = EntityManager.GetBuffer<Cell>(blockEntity);
            bool changed = false;

            for (int row = 0; row < block.m_Size.y; row++)
            for (int col = 0; col < block.m_Size.x; col++)
            {
                int idx  = row * block.m_Size.x + col;
                var cell = cells[idx];
                if ((cell.m_State & CellFlags.Visible)    == CellFlags.None) continue;
                if ((cell.m_State & CellFlags.Overridden) != CellFlags.None) continue;

                float2 cellPos = ZoneUtils.GetCellPosition(block, new int2(col, row)).xz;
                if (!MathUtils.Intersect(quad, cellPos)) continue;

                cell.m_Zone = targetZone;
                cells[idx]  = cell;
                changed      = true;
                cells_changed++;
            }

            if (changed) { MarkUpdated(blockEntity); blocks_changed++; }
        }

        blockEntities.Dispose();
        DebugLogger.Write($"[ZONE APPLIER]    Marquee done — {cells_changed} cells in {blocks_changed} blocks");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private void MarkUpdated(Entity entity)
    {
        if (!EntityManager.HasComponent<Updated>(entity))
            EntityManager.AddComponentData(entity, default(Updated));
    }

}
