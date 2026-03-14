using System.Collections.Generic;
using CS2Hooks.Events;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays TransportLinePlacedEvents (IsComplete=true): deletes the created route then
/// rebuilds it using the same 2-frame pipeline as NetCourseApplier.
///
///   Phase FindRoute  — wait a few frames, then locate the permanent route entity.
///   Phase WaitDelete — countdown before deleting.
///   Phase Delete     — destroy route + child entities.
///   Phase Inject     — (Frame N) create CreationDefinition + WaypointDefinition + Updated.
///   Phase Apply      — (Frame N+1) destroy definition, set PendingApply=true so
///                      NetCourseApplyPatch triggers UpdateSystem.Update(ApplyTool).
/// </summary>
public class RouteApplier : GameSystemBase
{
    private enum Phase { FindRoute, WaitDelete, Delete, Inject, Apply }

    private struct PendingLine
    {
        public Entity   PrefabEntity;
        public float3[] Positions;
        public Entity[] SnapEntities;
        public Entity   RouteEntity;
        public Entity   DefEntity;
        public Phase    Phase;
        public int      FramesLeft;
    }

    /// <summary>
    /// Set in Phase.Apply (PreTool); read by NetCourseApplyPatch (ToolOutputSystem.OnUpdate)
    /// which calls UpdateSystem.Update(ApplyTool) → ApplyRoutesSystem promotes Temp entities.
    /// </summary>
    public static bool PendingApply;

    private static readonly List<PendingLine> _pending = new();
    private static int _deleteAfter   = 60;
    private static int _recreateAfter = 60;

    public static void Enqueue(TransportLinePlacedEvent e, int deleteAfterFrames = 60, int recreateAfterFrames = 60)
    {
        if (e.WaypointPositions.Length == 0) return;
        _deleteAfter   = deleteAfterFrames;
        _recreateAfter = recreateAfterFrames;
        _pending.Add(new PendingLine
        {
            PrefabEntity = e.PrefabEntity,
            Positions    = e.WaypointPositions,
            SnapEntities = e.WaypointSnapEntities,
            RouteEntity  = Entity.Null,
            DefEntity    = Entity.Null,
            Phase        = Phase.FindRoute,
            FramesLeft   = 5,
        });
    }

    protected override void OnCreate()
    {
        base.OnCreate();
        World.GetOrCreateSystemManaged<UpdateSystem>().UpdateAt<RouteApplier>(SystemUpdatePhase.PreTool);
    }

    protected override void OnUpdate()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            switch (item.Phase)
            {
                case Phase.FindRoute:
                    if (--item.FramesLeft > 0) { _pending[i] = item; continue; }
                    item.RouteEntity = FindNewestRoute(item.PrefabEntity);
                    if (item.RouteEntity == Entity.Null)
                    {
                        DebugLogger.Write("[ROUTE APPLIER] route not found yet — retrying");
                        item.FramesLeft = 5;
                        _pending[i]     = item;
                        continue;
                    }
                    DebugLogger.Write($"[ROUTE APPLIER] found route {item.RouteEntity.Index} — deleting in {_deleteAfter} frames");
                    item.Phase      = Phase.WaitDelete;
                    item.FramesLeft = _deleteAfter;
                    _pending[i]     = item;
                    break;

                case Phase.WaitDelete:
                    if (--item.FramesLeft > 0) { _pending[i] = item; continue; }
                    item.Phase  = Phase.Delete;
                    _pending[i] = item;
                    break;

                case Phase.Delete:
                    DeleteRoute(item.RouteEntity);
                    item.Phase      = Phase.Inject;
                    item.FramesLeft = _recreateAfter;
                    _pending[i]     = item;
                    break;

                case Phase.Inject:
                    if (--item.FramesLeft > 0) { _pending[i] = item; continue; }
                    item.DefEntity = InjectDefinition(item.PrefabEntity, item.Positions, item.SnapEntities);
                    item.Phase     = Phase.Apply;
                    _pending[i]    = item;
                    break;

                // Frame N+1: destroy definition entity, signal NetCourseApplyPatch to fire ApplyTool.
                case Phase.Apply:
                    if (item.DefEntity != Entity.Null && EntityManager.Exists(item.DefEntity))
                        EntityManager.DestroyEntity(item.DefEntity);
                    PendingApply = true;
                    DebugLogger.Write("[ROUTE APPLIER] def destroyed, PendingApply=true — waiting for ApplyRoutesSystem");
                    _pending.RemoveAt(i);
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────

    private Entity FindNewestRoute(Entity prefabEntity)
    {
        var query = EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<Route>(),
            ComponentType.ReadOnly<PrefabRef>(),
            ComponentType.Exclude<Temp>());
        var entities = query.ToEntityArray(Allocator.TempJob);
        query.Dispose();

        Entity best = Entity.Null;
        int bestIdx = -1;
        for (int i = 0; i < entities.Length; i++)
        {
            var e = entities[i];
            if (EntityManager.GetComponentData<PrefabRef>(e).m_Prefab == prefabEntity && e.Index > bestIdx)
            {
                best    = e;
                bestIdx = e.Index;
            }
        }
        entities.Dispose();
        return best;
    }

    private void DeleteRoute(Entity route)
    {
        if (!EntityManager.Exists(route)) return;

        if (EntityManager.HasBuffer<RouteWaypoint>(route))
        {
            var buf = EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);
            for (int i = 0; i < buf.Length; i++)
                if (EntityManager.Exists(buf[i].m_Waypoint))
                    EntityManager.DestroyEntity(buf[i].m_Waypoint);
        }

        if (EntityManager.HasBuffer<RouteSegment>(route))
        {
            var buf = EntityManager.GetBuffer<RouteSegment>(route, isReadOnly: true);
            for (int i = 0; i < buf.Length; i++)
                if (EntityManager.Exists(buf[i].m_Segment))
                    EntityManager.DestroyEntity(buf[i].m_Segment);
        }

        EntityManager.DestroyEntity(route);
        DebugLogger.Write($"[ROUTE APPLIER] deleted route {route.Index}");
    }

    private Entity InjectDefinition(Entity prefabEntity, float3[] positions, Entity[] snapEntities)
    {
        var def = EntityManager.CreateEntity();
        EntityManager.AddComponentData(def, new CreationDefinition { m_Prefab = prefabEntity });

        var buf = EntityManager.AddBuffer<WaypointDefinition>(def);
        for (int i = 0; i < positions.Length; i++)
        {
            buf.Add(new WaypointDefinition
            {
                m_Position   = positions[i],
                m_Connection = snapEntities[i],
                m_Original   = Entity.Null,
            });
        }

        EntityManager.AddComponent<Updated>(def);
        DebugLogger.Write($"[ROUTE APPLIER] injected definition ({positions.Length} waypoints) — GenerateRoutesSystem will run this ToolUpdate");
        return def;
    }
}
