using CS2Hooks.Events;
using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays a <see cref="RoadPlacedEvent"/> by injecting a game-standard definition entity
/// (CreationDefinition + NetCourse + Updated) and letting the normal pipeline build the road.
///
/// Two-frame sequence (confirmed via live diagnostic, 2026-03-08):
///
///   Frame N (PreTool):
///     Create definition entity via EntityManager directly.
///
///   Frame N (ToolUpdate, after ToolOutputSystem):
///     GenerateNodesSystem + GenerateEdgesSystem read definition → write Temp entities to ECB.
///     ToolOutputBarrier flushes → Temp entities now live in ECS.
///
///   Frame N+1 (PreTool):
///     Destroy definition entity (before Generate* run again).
///     Call UpdateSystem.Update(ApplyTool) directly → ApplyNetSystem promotes Temp → permanent.
///     Road exists at end of frame N+1.
/// </summary>
public class NetCourseApplier : GameSystemBase
{
    private static readonly Queue<RoadPlacedEvent> _pending = new();

    private PrefabSystem _prefabSystem = null!;

    private bool   _waitingForApply;
    private Entity _defEntity;

    /// <summary>Set in Phase2 (PreTool); read by NetCourseApplyPatch.</summary>
    public static bool PendingApply;

    /// <summary>Shared EntityManager for diagnostics in static Harmony patches.</summary>
    public static EntityManager SharedEntityManager;

    public static void Enqueue(RoadPlacedEvent e) => _pending.Enqueue(e);

    protected override void OnCreate()
    {
        base.OnCreate();
        _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
        SharedEntityManager = EntityManager;
    }

    protected override void OnUpdate()
    {
        // ── Frame N+1: commit ────────────────────────────────────────────────
        if (_waitingForApply)
        {
            // Destroy definition entity before GenerateNodes/GenerateEdges run this frame.
            if (_defEntity != Entity.Null && EntityManager.Exists(_defEntity))
                EntityManager.DestroyEntity(_defEntity);
            _defEntity = Entity.Null;

            // Signal NetCourseApplyPatch (Prefix on ToolOutputSystem.OnUpdate) to call
            // UpdateSystem.Update(ApplyTool) from within the correct phase context.
            // We cannot call it here (PreTool) — SafeCommandBufferSystem barriers are not open.
            PendingApply = true;
            _waitingForApply = false;

            DebugLogger.Write("[NET COURSE]      Phase2 — def destroyed, PendingApply=true, waiting for ToolOutputSystem");
            return;
        }

        // ── Frame N: create definition entity ────────────────────────────────
        if (_pending.Count == 0) return;

        var e = _pending.Dequeue();

        if (e.Prefab == null || e.ControlPoints.Length < 2)
        {
            DebugLogger.Write("[NET COURSE]      skipped — null prefab or <2 control points");
            return;
        }

        Entity prefabEntity;
        try   { prefabEntity = _prefabSystem.GetEntity(e.Prefab); }
        catch (Exception ex)
        {
            DebugLogger.Write($"[NET COURSE]      GetEntity threw: {ex.Message}");
            return;
        }

        if (prefabEntity == Entity.Null)
        {
            DebugLogger.Write($"[NET COURSE]      prefab '{e.Prefab.name}' resolved to Entity.Null");
            return;
        }

        _defEntity       = CreateDefinitionEntity(e, prefabEntity);
        _waitingForApply = true;

        // Enable pipeline diagnostics so we can see GenerateNodes/Edges/Composition activity.
        CS2Hooks.Events.Patches.DiagPatch.PipelineDiagFrames = 8;

        DebugLogger.Write(
            $"[NET COURSE]      Phase1 — def={_defEntity.Index}" +
            $" prefab='{e.Prefab.name}' mode={e.Mode} pts={e.ControlPoints.Length}" +
            $" — pipeline diag enabled");
    }

    // ─────────────────────────────────────────────────────────────────────────
    private Entity CreateDefinitionEntity(RoadPlacedEvent e, Entity prefabEntity)
    {
        float3 startPos = e.ControlPoints[0].Position;
        float3 endPos   = e.ControlPoints[^1].Position;

        Bezier4x3 curve  = e.Curve ?? BuildCurve(e);
        float     length = MathUtils.Length(curve);

        // Use Entity.Null for snapped entities — safe after demolition since original
        // nodes/edges no longer exist. GenerateNodesSystem will create fresh nodes at
        // the positions. Future improvement: scan nearby nodes and snap by position.
        var startCoursePos = new CoursePos
        {
            m_Entity        = Entity.Null,
            m_Position      = startPos,
            m_Rotation      = e.ControlPoints[0].Rotation,
            m_Elevation     = new float2(e.ControlPoints[0].Elevation, 0f),
            m_CourseDelta   = 0f,
            m_SplitPosition = 0f,
            m_Flags         = CoursePosFlags.IsFirst | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            m_ParentMesh    = -1,
        };

        var endCoursePos = new CoursePos
        {
            m_Entity        = Entity.Null,
            m_Position      = endPos,
            m_Rotation      = e.ControlPoints[^1].Rotation,
            m_Elevation     = new float2(e.ControlPoints[^1].Elevation, 0f),
            m_CourseDelta   = 1f,
            m_SplitPosition = 0f,
            m_Flags         = CoursePosFlags.IsLast | CoursePosFlags.IsLeft | CoursePosFlags.IsRight,
            m_ParentMesh    = -1,
        };

        var def = EntityManager.CreateEntity();

        EntityManager.AddComponentData(def, new CreationDefinition
        {
            m_Prefab     = prefabEntity,
            m_SubPrefab  = Entity.Null,
            m_Original   = Entity.Null,
            m_Owner      = Entity.Null,
            m_Attached   = Entity.Null,
            m_Flags      = CreationFlags.SubElevation,
            m_RandomSeed = (int)((uint)math.hash(startPos) ^ (uint)math.hash(endPos)),
        });

        EntityManager.AddComponentData(def, new NetCourse
        {
            m_Curve         = curve,
            m_StartPosition = startCoursePos,
            m_EndPosition   = endCoursePos,
            m_Elevation     = new float2(e.ControlPoints[0].Elevation, e.ControlPoints[^1].Elevation),
            m_Length        = length,
            m_FixedIndex    = -1,
        });

        EntityManager.AddComponentData(def, default(Updated));

        return def;
    }

    // ─────────────────────────────────────────────────────────────────────────
    private static Bezier4x3 BuildCurve(RoadPlacedEvent e)
    {
        var    pts   = e.ControlPoints;
        float3 start = pts[0].Position;
        float3 end   = pts[^1].Position;

        switch (e.Mode)
        {
            // Straight / Continuous: standard straight cubic bezier.
            case NetToolSystem.Mode.Straight:
            case NetToolSystem.Mode.Continuous:
                return NetUtils.StraightCurve(start, end);

            // SimpleCurve (3 points): quadratic → cubic conversion.
            // The middle point is the quadratic handle; multiply by 2/3 to get cubic b/c.
            case NetToolSystem.Mode.SimpleCurve when pts.Length >= 3:
            {
                float3 h = pts[1].Position;
                return new Bezier4x3
                {
                    a = start,
                    b = start + (h - start) * (2f / 3f),
                    c = end   + (h - end)   * (2f / 3f),
                    d = end,
                };
            }

            // ComplexCurve (4 points): direct cubic bezier control points.
            case NetToolSystem.Mode.ComplexCurve when pts.Length >= 4:
                return new Bezier4x3
                {
                    a = start,
                    b = pts[1].Position,
                    c = pts[2].Position,
                    d = end,
                };

            // Fallback for anything unexpected.
            default:
                DebugLogger.Write($"[NET COURSE]      BuildCurve: unhandled mode {e.Mode} with {pts.Length} pts — using straight");
                return NetUtils.StraightCurve(start, end);
        }
    }
}
