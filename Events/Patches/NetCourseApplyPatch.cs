using System.Reflection;
using CS2Hooks.Actions.Apply;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Triggers ApplyTool from within ToolOutputSystem.OnUpdate() — the correct phase context
/// where SafeCommandBufferSystem barriers are open. Called on the frame after
/// NetCourseApplier creates the definition entity (frame N+1), so GenerateNodes/GenerateEdges
/// have already flushed Temp entities into ECS and ApplyNetSystem can promote them.
///
/// CRITICAL: We must suppress ToolOutputSystem's normal body from calling ClearTool,
/// because ToolClearSystem adds Deleted to ALL Temp entities — including the ones
/// ApplyNetSystem just promoted. Both ECBs flush together via ToolOutputBarrier,
/// so the road would get Applied+Created+Updated AND Deleted simultaneously.
///
/// Fix: Set applyMode=None so the body does nothing. Call ApplyTool manually from Prefix.
/// Next frame, normal ClearTool resumes and clears any leftover preview Temp entities.
/// </summary>
[HarmonyPatch(typeof(ToolOutputSystem), "OnUpdate")]
static class NetCourseApplyPatch
{
    private static FieldInfo? _updateSysField;
    private static FieldInfo? _toolSysField;
    private static FieldInfo? _lastToolField;
    private static FieldInfo? _applyModeBackingField;

    static void Prefix(ToolOutputSystem __instance)
    {
        if (!NetCourseApplier.PendingApply && !RouteApplier.PendingApply) return;

        NetCourseApplier.PendingApply = false;
        RouteApplier.PendingApply     = false;

        // Count Temp+Edge entities so we can verify GenerateNodes/Edges ran in frame N.
        var em = NetCourseApplier.SharedEntityManager;
        var tempEdgeQuery = em.CreateEntityQuery(
            ComponentType.ReadOnly<Temp>(),
            ComponentType.ReadOnly<Edge>());
        int tempEdgeCount = tempEdgeQuery.CalculateEntityCount();
        tempEdgeQuery.Dispose();

        var tempNodeQuery = em.CreateEntityQuery(
            ComponentType.ReadOnly<Temp>(),
            ComponentType.ReadOnly<Node>());
        int tempNodeCount = tempNodeQuery.CalculateEntityCount();
        tempNodeQuery.Dispose();

        DebugLogger.Write($"[NET COURSE]      NetCourseApplyPatch — Temp+Edge={tempEdgeCount} Temp+Node={tempNodeCount} — calling Update(ApplyTool)");

        // Force-complete all pending jobs before calling Update(ApplyTool).
        // Without this, ApplyNetSystem.base.Dependency may not include ModificationBarrier1/2
        // job handles from frame N, causing HandleTempEntitiesJob to see 0 Temp entities.
        em.CompleteAllTrackedJobs();

        _updateSysField ??= typeof(ToolOutputSystem)
            .GetField("m_UpdateSystem", BindingFlags.NonPublic | BindingFlags.Instance);

        var updateSys = _updateSysField?.GetValue(__instance) as UpdateSystem;
        if (updateSys == null)
        {
            DebugLogger.Write("[NET COURSE]      NetCourseApplyPatch — could not get m_UpdateSystem!");
            return;
        }

        // Suppress ToolOutputSystem body: set applyMode=None so it doesn't call ClearTool.
        // ToolClearSystem adds Deleted to ALL Temp entities, which would destroy our
        // newly-promoted road before ToolOutputBarrier flushes the ECBs.
        //
        // ToolSystem.applyMode delegates to m_LastTool.applyMode (ToolBaseSystem).
        // ToolBaseSystem.applyMode has a protected setter → need reflection on the backing field.
        _toolSysField ??= typeof(ToolOutputSystem)
            .GetField("m_ToolSystem", BindingFlags.NonPublic | BindingFlags.Instance);
        var toolSys = _toolSysField?.GetValue(__instance) as ToolSystem;
        var savedApplyMode = toolSys?.applyMode ?? ApplyMode.None;

        if (toolSys != null)
            SetApplyMode(toolSys, ApplyMode.None);

        DebugLogger.Write($"[NET COURSE]      suppressed applyMode={savedApplyMode}→None to prevent ClearTool");

        updateSys.Update(SystemUpdatePhase.ApplyTool);
    }

    /// <summary>
    /// ToolSystem.applyMode → m_LastTool.applyMode (ToolBaseSystem, protected setter).
    /// Set via the auto-property backing field on the active tool.
    /// </summary>
    private static void SetApplyMode(ToolSystem toolSys, ApplyMode mode)
    {
        _lastToolField ??= typeof(ToolSystem)
            .GetField("m_LastTool", BindingFlags.NonPublic | BindingFlags.Instance);
        var lastTool = _lastToolField?.GetValue(toolSys) as ToolBaseSystem;
        if (lastTool == null) return;

        _applyModeBackingField ??= typeof(ToolBaseSystem)
            .GetField("<applyMode>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        _applyModeBackingField?.SetValue(lastTool, mode);
    }
}
