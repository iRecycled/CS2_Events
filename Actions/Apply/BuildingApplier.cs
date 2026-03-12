using CS2Hooks.Events;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Receives BuildingPlacedEvent data and re-creates the building by injecting
/// Temp entities into the ECS world. ApplyObjectsSystem picks them up on the
/// next ApplyTool phase and promotes them to permanent entities.
///
/// Minimum Temp entity for a building:
///   Game.Objects.Object   — marker component (empty struct)
///   Game.Objects.Transform — world position + rotation
///   Game.Prefabs.PrefabRef — which prefab to instantiate
///   Game.Tools.Temp        — m_Original=Entity.Null, m_Flags=0 → "create new"
/// </summary>
public class BuildingApplier : GameSystemBase
{
    private static readonly Queue<BuildingPlacedEvent> _pending = new();

    // EntityManager.CreateEntity(EntityArchetype) internally uses ReadOnlySpan<ComponentType>
    // which Unity compiled against its own mscorlib — not resolvable at netstandard2.1 compile
    // time (CS7069). Cache the MethodInfo and invoke at runtime when Unity's mscorlib is live.
    private static readonly MethodInfo _createEntityByArchetype =
        typeof(EntityManager).GetMethod("CreateEntity", new[] { typeof(EntityArchetype) })!;

    private PrefabSystem _prefabSystem = null!;

    /// <summary>Enqueue a building to be placed on the next update tick.</summary>
    public static void Enqueue(BuildingPlacedEvent e) => _pending.Enqueue(e);

    protected override void OnCreate()
    {
        base.OnCreate();
        _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
    }

    protected override void OnUpdate()
    {
        while (_pending.TryDequeue(out var e))
            Apply(e);
    }

    private void Apply(BuildingPlacedEvent e)
    {
        if (e.Prefab == null)
        {
            DebugLogger.Write("[APPLIER]         prefab is null, skipping");
            return;
        }

        Entity prefabEntity;
        try
        {
            prefabEntity = _prefabSystem.GetEntity(e.Prefab);
            DebugLogger.Write($"[APPLIER]         resolved '{e.Prefab.name}' → Entity({prefabEntity.Index},{prefabEntity.Version})");
        }
        catch (System.Exception ex)
        {
            DebugLogger.Write($"[APPLIER]         GetEntity threw for '{e.Prefab.name}': {ex.Message}");
            return;
        }

        if (prefabEntity == Entity.Null)
        {
            DebugLogger.Write($"[APPLIER]         '{e.Prefab.name}' resolved to Entity.Null, skipping");
            return;
        }

        // ObjectData.m_Archetype encodes every component the object type needs.
        // CreateEntity(archetype) + SetComponentData is how the game spawns objects directly
        // (same pattern as TreeSpawnSystem and ObjectEmergeSystem in the decompiled source).
        // We invoke via reflection because CreateEntity(EntityArchetype) uses ReadOnlySpan<>
        // from Unity's mscorlib — which can't be resolved at netstandard2.1 compile time.
        var objectData = EntityManager.GetComponentData<ObjectData>(prefabEntity);
        var em = EntityManager; // copy struct so we can box it for reflection
        var entity = (Entity)_createEntityByArchetype.Invoke(em, new object[] { objectData.m_Archetype })!;
        EntityManager.SetComponentData(entity, new PrefabRef(prefabEntity));
        EntityManager.SetComponentData(entity, new Transform(e.Position, e.Rotation));

        DebugLogger.Write($"[APPLIER]         spawned Entity({entity.Index}) for '{e.Prefab.name}' at {e.Position}");
    }
}
