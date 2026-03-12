using CS2Hooks.Events;
using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays road / net placement by creating Node + Edge entities directly in ECS,
/// bypassing the tool system entirely (Option B).
///
/// Endpoint resolution — three-tier strategy per endpoint:
///   Tier 1 — SnapEntity: if the original captured node entity is still alive (same
///             session), use it directly.
///   Tier 2 — Node position search: scan permanent nodes within 0.5 m.
///             Catches exact-position matches when the original node was re-created
///             at the same spot.
///   Tier 3 — Edge split: find the nearest permanent edge within 2 m and split it
///             at the closest point using De Casteljau subdivision.  This handles
///             T-intersections where the endpoint snapped to a mid-edge position and
///             the T-junction node was later deleted (e.g. when the road was bulldozed
///             and the through-road segments were merged back).
///   Tier 4 — Create new node: no nearby network found; place a fresh dead-end node.
///
/// MVP limitations — tracked in memory/road-applier-limitations.md:
///   • Curved roads (SimpleCurve / ComplexCurve) are approximated as straight.
///   • Edge-split curve geometry uses linear approximation (De Casteljau on the
///     straight bezier), which is exact for straight roads.
///   • Road crossing detection not supported.
///   • Elevation (tunnels / bridges) not handled.
/// </summary>
public class RoadApplier : GameSystemBase
{
    private static readonly Queue<RoadPlacedEvent> _pending = new();

    private static readonly MethodInfo _createEntityByArchetype =
        typeof(EntityManager).GetMethod("CreateEntity", new[] { typeof(EntityArchetype) })!;

    private PrefabSystem _prefabSystem = null!;
    private EntityQuery  _nodeQuery;
    private EntityQuery  _edgeQuery;

    // Tier 2: exact-position node snap (same location or sub-centimetre offset).
    private const float NODE_TIGHT_SNAP_SQ = 0.5f * 0.5f;

    // Tier 3: edge-split — how close the endpoint must be to a road edge centre-line.
    private const float EDGE_SNAP_DIST_SQ = 2.0f * 2.0f;

    // Minimum t along an edge to split at (avoids near-zero-length sub-segments).
    private const float SPLIT_T_MIN = 0.05f;
    private const float SPLIT_T_MAX = 0.95f;

    public static void Enqueue(RoadPlacedEvent e) => _pending.Enqueue(e);

    protected override void OnCreate()
    {
        base.OnCreate();
        _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

        _nodeQuery = GetEntityQuery(new EntityQueryDesc
        {
            All  = new[] { ComponentType.ReadOnly<Node>() },
            None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
        });

        _edgeQuery = GetEntityQuery(new EntityQueryDesc
        {
            All  = new[]
            {
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Curve>(),
                ComponentType.ReadOnly<PrefabRef>(),
            },
            None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
        });
    }

    protected override void OnUpdate()
    {
        while (_pending.TryDequeue(out var e))
            Apply(e);
    }

    // ─────────────────────────────────────────────────────────────────────────
    private void Apply(RoadPlacedEvent e)
    {
        if (e.Prefab == null || e.ControlPoints.Length < 2)
        {
            DebugLogger.Write("[ROAD APPLIER]    skipped — null prefab or < 2 control points");
            return;
        }

        Entity prefabEntity;
        try   { prefabEntity = _prefabSystem.GetEntity(e.Prefab); }
        catch (Exception ex)
        {
            DebugLogger.Write($"[ROAD APPLIER]    GetEntity threw for '{e.Prefab.name}': {ex.Message}");
            return;
        }

        if (prefabEntity == Entity.Null)
        {
            DebugLogger.Write($"[ROAD APPLIER]    '{e.Prefab.name}' → Entity.Null, skipping");
            return;
        }

        var netData = EntityManager.GetComponentData<NetData>(prefabEntity);

        // Bezier geometry — MVP: straight cubic bezier from first to last control point.
        float3 startPos = e.ControlPoints[0].Position;
        float3 endPos   = e.ControlPoints[e.ControlPoints.Length - 1].Position;

        if (e.Mode != NetToolSystem.Mode.Straight)
            DebugLogger.Write($"[ROAD APPLIER]    mode={e.Mode} approximated as Straight (MVP)");

        var bezier = new Bezier4x3
        {
            a = startPos,
            b = math.lerp(startPos, endPos, 1f / 3f),
            c = math.lerp(startPos, endPos, 2f / 3f),
            d = endPos,
        };
        float length = MathUtils.Length(bezier);

        try
        {
            // Snapshot node and edge state before any structural changes this frame.
            var nodeEntities = _nodeQuery.ToEntityArray(Allocator.Temp);
            var nodeData     = _nodeQuery.ToComponentDataArray<Node>(Allocator.Temp);
            var edgeEntities = _edgeQuery.ToEntityArray(Allocator.Temp);
            var edgeCurves   = _edgeQuery.ToComponentDataArray<Curve>(Allocator.Temp);
            var edgeEdges    = _edgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            var edgePrefabs  = _edgeQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);

            var startNode = FindOrCreateNode(
                netData.m_NodeArchetype, prefabEntity,
                e.ControlPoints[0],
                nodeEntities, nodeData,
                edgeEntities, edgeCurves, edgeEdges, edgePrefabs);

            var endNode = FindOrCreateNode(
                netData.m_NodeArchetype, prefabEntity,
                e.ControlPoints[e.ControlPoints.Length - 1],
                nodeEntities, nodeData,
                edgeEntities, edgeCurves, edgeEdges, edgePrefabs);

            nodeEntities.Dispose();
            nodeData.Dispose();
            edgeEntities.Dispose();
            edgeCurves.Dispose();
            edgeEdges.Dispose();
            edgePrefabs.Dispose();

            // Rebuild bezier from the nodes' ACTUAL positions, not the captured
            // PlacementPoint positions.  FindOrCreateNode may have returned a node
            // at a slightly different position (e.g. an existing node, or a split-
            // edge midpoint); using the real positions ensures the bezier endpoints
            // exactly match the node positions and avoids floating-road artifacts.
            float3 actualStart = EntityManager.GetComponentData<Node>(startNode).m_Position;
            float3 actualEnd   = EntityManager.GetComponentData<Node>(endNode).m_Position;
            bezier = new Bezier4x3
            {
                a = actualStart,
                b = math.lerp(actualStart, actualEnd, 1f / 3f),
                c = math.lerp(actualStart, actualEnd, 2f / 3f),
                d = actualEnd,
            };
            length = MathUtils.Length(bezier);

            // Create edge entity.
            var em         = EntityManager;
            var edgeEntity = (Entity)_createEntityByArchetype
                .Invoke(em, new object[] { netData.m_EdgeArchetype })!;

            EntityManager.SetComponentData(edgeEntity, new PrefabRef(prefabEntity));
            EntityManager.SetComponentData(edgeEntity, new Edge { m_Start = startNode, m_End = endNode });
            EntityManager.SetComponentData(edgeEntity, new Curve { m_Bezier = bezier, m_Length = length });

            if (EntityManager.HasComponent<Transform>(edgeEntity))
            {
                float3 mid = MathUtils.Position(bezier, 0.5f);
                float3 fwd = math.normalizesafe(endPos - startPos, math.forward());
                EntityManager.SetComponentData(edgeEntity,
                    new Transform(mid, quaternion.LookRotationSafe(fwd, math.up())));
            }

            EntityManager.GetBuffer<ConnectedNode>(edgeEntity).Add(new ConnectedNode(startNode, 0f));
            EntityManager.GetBuffer<ConnectedNode>(edgeEntity).Add(new ConnectedNode(endNode,   1f));
            EntityManager.GetBuffer<ConnectedEdge>(startNode).Add(new ConnectedEdge(edgeEntity));
            EntityManager.GetBuffer<ConnectedEdge>(endNode  ).Add(new ConnectedEdge(edgeEntity));

            EntityManager.AddComponentData(edgeEntity, default(Applied));

            DebugLogger.Write(
                $"[ROAD APPLIER]    spawned '{e.Prefab.name}' mode={e.Mode}" +
                $" len={length:F1}m edge={edgeEntity.Index}" +
                $" s={startNode.Index} e={endNode.Index}");
        }
        catch (Exception ex)
        {
            DebugLogger.Write($"[ROAD APPLIER]    exception: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    private Entity FindOrCreateNode(
        EntityArchetype archetype, Entity prefabEntity,
        in PlacementPoint pt,
        NativeArray<Entity>  nodeEntities, NativeArray<Node>    nodeData,
        NativeArray<Entity>  edgeEntities, NativeArray<Curve>   edgeCurves,
        NativeArray<Edge>    edgeEdges,    NativeArray<PrefabRef> edgePrefabs)
    {
        // ── Tier 1: reuse original snap entity (same session) ─────────────────
        // SnapEntity is the node (or edge) that the tool originally snapped to.
        // For endpoint-to-endpoint connections the entity is a Node and likely
        // still alive; use it directly.
        var snap = pt.SnapEntity;
        if (snap != Entity.Null
            && EntityManager.Exists(snap)
            && EntityManager.HasComponent<Node>(snap)
            && !EntityManager.HasComponent<Deleted>(snap)
            && !EntityManager.HasComponent<Temp>(snap))
        {
            DebugLogger.Write($"[ROAD APPLIER]      tier1 snap node {snap.Index}");
            MarkNodeUpdated(snap);
            return snap;
        }

        // ── Tier 2: exact position match — node within 0.5 m ─────────────────
        // Handles cases where the original node was deleted and a new one was
        // re-created at the same world position (e.g. after a save/load).
        {
            float  bestSq = NODE_TIGHT_SNAP_SQ;
            Entity found  = Entity.Null;
            for (int i = 0; i < nodeData.Length; i++)
            {
                float d = math.distancesq(nodeData[i].m_Position, pt.Position);
                if (d < bestSq) { bestSq = d; found = nodeEntities[i]; }
            }
            if (found != Entity.Null)
            {
                DebugLogger.Write(
                    $"[ROAD APPLIER]      tier2 node {found.Index} dist={math.sqrt(bestSq):F2}m");
                MarkNodeUpdated(found);
                return found;
            }
        }

        // ── Tier 3: edge split — T-intersection ───────────────────────────────
        // SnapEntity may have pointed to an Edge entity (mid-road snap), and the
        // resulting T-junction node was later deleted when the side road was
        // bulldozed and the through-road segments were merged back.
        // Find the closest existing road edge and split it at the nearest point.
        {
            float  bestSq   = EDGE_SNAP_DIST_SQ;
            Entity bestEdge = Entity.Null;
            float  bestT    = 0.5f;

            for (int i = 0; i < edgeCurves.Length; i++)
            {
                // Skip edges that were just split (marked Deleted) by the start-node pass.
                if (EntityManager.HasComponent<Deleted>(edgeEntities[i])) continue;

                // Linear projection along a→d (exact for straight beziers, approximate for curves).
                float3 a  = edgeCurves[i].m_Bezier.a;
                float3 d  = edgeCurves[i].m_Bezier.d;
                float3 ab = d - a;
                float  len2 = math.lengthsq(ab);
                if (len2 < 0.01f) continue;

                float  t       = math.clamp(math.dot(pt.Position - a, ab) / len2, SPLIT_T_MIN, SPLIT_T_MAX);
                float3 closest = a + ab * t;
                float  distSq  = math.distancesq(closest, pt.Position);

                if (distSq < bestSq)
                {
                    bestSq   = distSq;
                    bestEdge = edgeEntities[i];
                    bestT    = t;
                }
            }

            if (bestEdge != Entity.Null)
            {
                DebugLogger.Write(
                    $"[ROAD APPLIER]      tier3 splitting edge {bestEdge.Index}" +
                    $" t={bestT:F2} dist={math.sqrt(bestSq):F2}m");

                var splitNode = SplitEdge(bestEdge, bestT);
                if (splitNode != Entity.Null) return splitNode;
            }
        }

        // ── Tier 4: create a new dead-end node ────────────────────────────────
        var em   = EntityManager;
        var node = (Entity)_createEntityByArchetype.Invoke(em, new object[] { archetype })!;

        EntityManager.SetComponentData(node, new PrefabRef(prefabEntity));
        EntityManager.SetComponentData(node, new Node { m_Position = pt.Position, m_Rotation = pt.Rotation });

        if (EntityManager.HasComponent<Transform>(node))
            EntityManager.SetComponentData(node, new Transform(pt.Position, pt.Rotation));

        EntityManager.AddComponentData(node, default(Applied));
        DebugLogger.Write($"[ROAD APPLIER]      tier4 new node {node.Index} at {pt.Position}");
        return node;
    }

    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Splits <paramref name="originalEdge"/> at parameter <paramref name="t"/> using
    /// De Casteljau subdivision, replacing it with two sub-edges and a new midpoint node.
    /// Returns the new midpoint node, or Entity.Null on failure.
    /// </summary>
    private Entity SplitEdge(Entity originalEdge, float t)
    {
        try
        {
            // Guard: another split in the same frame may have already deleted this edge.
            if (EntityManager.HasComponent<Deleted>(originalEdge))
            {
                DebugLogger.Write("[ROAD APPLIER]      SplitEdge: edge already deleted, skipping");
                return Entity.Null;
            }

            var edgeComp  = EntityManager.GetComponentData<Edge>(originalEdge);
            var curveComp = EntityManager.GetComponentData<Curve>(originalEdge);
            var prefabRef = EntityManager.GetComponentData<PrefabRef>(originalEdge);

            Entity startNode = edgeComp.m_Start;
            Entity endNode   = edgeComp.m_End;
            var    full      = curveComp.m_Bezier;

            // Resolve the through road's prefab so the sub-edges use its archetype.
            Entity throughPrefab = prefabRef.m_Prefab;
            if (!EntityManager.HasComponent<NetData>(throughPrefab))
            {
                DebugLogger.Write("[ROAD APPLIER]      SplitEdge: through road prefab has no NetData");
                return Entity.Null;
            }
            var throughNetData = EntityManager.GetComponentData<NetData>(throughPrefab);

            // De Casteljau split at t.
            float3 p0 = math.lerp(full.a, full.b, t);
            float3 p1 = math.lerp(full.b, full.c, t);
            float3 p2 = math.lerp(full.c, full.d, t);
            float3 q0 = math.lerp(p0, p1, t);
            float3 q1 = math.lerp(p1, p2, t);
            float3 m  = math.lerp(q0, q1, t);   // split point on the curve

            var firstBezier  = new Bezier4x3 { a = full.a, b = p0, c = q0, d = m };
            var secondBezier = new Bezier4x3 { a = m,      b = q1, c = p2, d = full.d };

            var em = EntityManager;

            // ── New midpoint node ─────────────────────────────────────────────
            var midNode = (Entity)_createEntityByArchetype
                .Invoke(em, new object[] { throughNetData.m_NodeArchetype })!;
            EntityManager.SetComponentData(midNode, new PrefabRef(throughPrefab));
            EntityManager.SetComponentData(midNode, new Node { m_Position = m, m_Rotation = quaternion.identity });
            if (EntityManager.HasComponent<Transform>(midNode))
                EntityManager.SetComponentData(midNode, new Transform(m, quaternion.identity));
            EntityManager.AddComponentData(midNode, default(Applied));

            // ── First sub-edge (startNode → midNode) ─────────────────────────
            var firstEdge = (Entity)_createEntityByArchetype
                .Invoke(em, new object[] { throughNetData.m_EdgeArchetype })!;
            EntityManager.SetComponentData(firstEdge, new PrefabRef(throughPrefab));
            EntityManager.SetComponentData(firstEdge, new Edge { m_Start = startNode, m_End = midNode });
            EntityManager.SetComponentData(firstEdge,
                new Curve { m_Bezier = firstBezier, m_Length = MathUtils.Length(firstBezier) });
            if (EntityManager.HasComponent<Transform>(firstEdge))
            {
                float3 mid1 = MathUtils.Position(firstBezier, 0.5f);
                float3 fwd1 = math.normalizesafe(m - full.a, math.forward());
                EntityManager.SetComponentData(firstEdge,
                    new Transform(mid1, quaternion.LookRotationSafe(fwd1, math.up())));
            }
            EntityManager.GetBuffer<ConnectedNode>(firstEdge).Add(new ConnectedNode(startNode, 0f));
            EntityManager.GetBuffer<ConnectedNode>(firstEdge).Add(new ConnectedNode(midNode,   1f));
            EntityManager.AddComponentData(firstEdge, default(Applied));

            // ── Second sub-edge (midNode → endNode) ──────────────────────────
            var secondEdge = (Entity)_createEntityByArchetype
                .Invoke(em, new object[] { throughNetData.m_EdgeArchetype })!;
            EntityManager.SetComponentData(secondEdge, new PrefabRef(throughPrefab));
            EntityManager.SetComponentData(secondEdge, new Edge { m_Start = midNode, m_End = endNode });
            EntityManager.SetComponentData(secondEdge,
                new Curve { m_Bezier = secondBezier, m_Length = MathUtils.Length(secondBezier) });
            if (EntityManager.HasComponent<Transform>(secondEdge))
            {
                float3 mid2 = MathUtils.Position(secondBezier, 0.5f);
                float3 fwd2 = math.normalizesafe(full.d - m, math.forward());
                EntityManager.SetComponentData(secondEdge,
                    new Transform(mid2, quaternion.LookRotationSafe(fwd2, math.up())));
            }
            EntityManager.GetBuffer<ConnectedNode>(secondEdge).Add(new ConnectedNode(midNode,  0f));
            EntityManager.GetBuffer<ConnectedNode>(secondEdge).Add(new ConnectedNode(endNode,  1f));
            EntityManager.AddComponentData(secondEdge, default(Applied));

            // ── Wire midNode's ConnectedEdge ──────────────────────────────────
            EntityManager.GetBuffer<ConnectedEdge>(midNode).Add(new ConnectedEdge(firstEdge));
            EntityManager.GetBuffer<ConnectedEdge>(midNode).Add(new ConnectedEdge(secondEdge));

            // ── Update startNode and endNode: replace original edge with sub-edge ─
            ReplaceConnectedEdge(startNode, originalEdge, firstEdge);
            ReplaceConnectedEdge(endNode,   originalEdge, secondEdge);
            MarkNodeUpdated(startNode);
            MarkNodeUpdated(endNode);

            // ── Delete original edge ──────────────────────────────────────────
            EntityManager.AddComponentData(originalEdge, default(Deleted));

            DebugLogger.Write(
                $"[ROAD APPLIER]      split done mid={midNode.Index}" +
                $" first={firstEdge.Index} second={secondEdge.Index}");
            return midNode;
        }
        catch (Exception ex)
        {
            DebugLogger.Write($"[ROAD APPLIER]      SplitEdge exception: {ex.Message}");
            return Entity.Null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    private void ReplaceConnectedEdge(Entity node, Entity oldEdge, Entity newEdge)
    {
        if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return;
        var buf = EntityManager.GetBuffer<ConnectedEdge>(node);
        for (int i = 0; i < buf.Length; i++)
        {
            if (buf[i].m_Edge == oldEdge)
            {
                buf[i] = new ConnectedEdge(newEdge);
                return;
            }
        }
        // Original edge not found in buffer (shouldn't happen, but add defensively).
        buf.Add(new ConnectedEdge(newEdge));
    }

    private void MarkNodeUpdated(Entity node)
    {
        if (!EntityManager.HasComponent<Updated>(node))
            EntityManager.AddComponentData(node, default(Updated));
        if (!EntityManager.HasComponent<Applied>(node))
            EntityManager.AddComponentData(node, default(Applied));
    }
}
