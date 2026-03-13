using System.Reflection;
using Game;
using Game.Common;
using Game.Net;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.UI.InGame;
using HarmonyLib;
using Unity.Entities;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Temporary diagnostic patches — log the ordering of the road placement pipeline
/// systems so we can determine which SystemUpdatePhase each runs in.
///
/// Two counters:
///   NetApplierPatch.DiagFrames     — set to 10 by the two-frame replay (Phase 2).
///   DiagPatch.PipelineDiagFrames   — set to 30 by NetToolApplyDiagPatch on a real placement.
///   DiagPatch.PolicyDiagFrames     — set to 10 by PolicyApplier when it fires.
///
/// Remove or disable before shipping.
/// </summary>
static class DiagPatch
{
    /// <summary>Set to N to log the road pipeline for N more ToolUpdate iterations.</summary>
    public static int PipelineDiagFrames;

    /// <summary>Set to N to log the policy pipeline for N more frames after PolicyApplier fires.</summary>
    public static int PolicyDiagFrames;
}

// ── ModifiedSystem — processes [Event, Modify] → updates Policy buffers ───
[HarmonyPatch(typeof(ModifiedSystem), "OnUpdate")]
static class ModifiedSystemDiagPatch
{
    private static EntityQuery _localQuery;
    private static bool _queryReady;

    static void Prefix(ModifiedSystem __instance)
    {
        if (DiagPatch.PolicyDiagFrames <= 0) return;

        // Build a query once using this system's EntityManager so we can count entities.
        if (!_queryReady)
        {
            _localQuery = __instance.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<Event>(),
                ComponentType.ReadOnly<Modify>());
            _queryReady = true;
        }

        int count = _localQuery.CalculateEntityCount();
        DebugLogger.Write($"[POLICY DIAG] ModifiedSystem OnUpdate  pending Modify events={count}");
        DiagPatch.PolicyDiagFrames--;
    }
}

// ── Triggered by a real user road placement ───────────────────────────────
[HarmonyPatch(typeof(NetToolSystem), "Apply")]
static class NetToolApplyDiagPatch
{
    static void Postfix(NetToolSystem __instance)
    {
        if (__instance.applyMode != ApplyMode.Apply) return;
        DiagPatch.PipelineDiagFrames = 30;
        DebugLogger.Write("[PIPELINE] === Road placed by user — logging pipeline for 30 iterations ===");
    }
}

// ── Generate systems (key question: which phase do these run in?) ─────────
[HarmonyPatch(typeof(GenerateNodesSystem), "OnUpdate")]
static class GenNodesDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] GenerateNodesSystem OnUpdate");
    }
}

[HarmonyPatch(typeof(GenerateEdgesSystem), "OnUpdate")]
static class GenEdgesDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] GenerateEdgesSystem OnUpdate");
    }
}

[HarmonyPatch(typeof(CourseSplitSystem), "OnUpdate")]
static class CourseSplitDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] CourseSplitSystem OnUpdate");
    }
}

// ── ToolOutputSystem — reads applyMode, triggers ApplyTool phase ──────────
[HarmonyPatch(typeof(ToolOutputSystem), "OnUpdate")]
static class ToolOutputDiagPatch
{
    private static FieldInfo? _toolSysField;

    static void Postfix(ToolOutputSystem __instance)
    {
        bool pipeline = DiagPatch.PipelineDiagFrames > 0;
        bool replay   = NetApplierPatch.DiagFrames   > 0;
        if (!pipeline && !replay) return;

        _toolSysField ??= typeof(ToolOutputSystem)
            .GetField("m_ToolSystem", BindingFlags.NonPublic | BindingFlags.Instance);

        var ts = _toolSysField?.GetValue(__instance) as ToolSystem;
        DebugLogger.Write($"[PIPELINE] ToolOutputSystem OnUpdate  applyMode={ts?.applyMode}");

        // Decrement here so all systems in this iteration share the same window.
        if (pipeline) DiagPatch.PipelineDiagFrames--;
        if (replay)   NetApplierPatch.DiagFrames--;
    }
}

// ── ApplyNetSystem — promotes Temp → permanent ────────────────────────────
[HarmonyPatch(typeof(ApplyNetSystem), "OnUpdate")]
static class ApplyNetDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] ApplyNetSystem OnUpdate FIRED");
    }
}

// ── Game.Prefabs.NetCompositionSystem — prefab composition loader (Modification4) ─
[HarmonyPatch(typeof(NetCompositionSystem), "OnUpdate")]
static class NetCompositionDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] Game.Prefabs.NetCompositionSystem OnUpdate (Modification4)");
    }
}

// ── GenerateAggregatesSystem — creates road name labels (Modification1) ───
[HarmonyPatch(typeof(GenerateAggregatesSystem), "OnUpdate")]
static class GenAggregatesDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] GenerateAggregatesSystem OnUpdate (Modification1)");
    }
}

// ── Game.Net.GeometrySystem — computes EdgeGeometry/NodeGeometry (Modification4) ─
[HarmonyPatch(typeof(GeometrySystem), "OnUpdate")]
static class GeometryDiagPatch
{
    private static EntityQuery _updatedEdgesQuery;
    private static bool _queryCreated;

    static void Prefix(GeometrySystem __instance)
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;

        if (!_queryCreated)
        {
            _updatedEdgesQuery = __instance.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<EdgeGeometry>(),
                ComponentType.ReadOnly<Updated>());
            _queryCreated = true;
        }

        int count = _updatedEdgesQuery.CalculateEntityCount();
        DebugLogger.Write($"[PIPELINE] Game.Net.GeometrySystem OnUpdate (Modification4)  updatedEdges={count}");
    }
}

// ── LaneSystem — generates lane entities (Modification4) ─────────────────
[HarmonyPatch(typeof(LaneSystem), "OnUpdate")]
static class LaneDiagPatch
{
    static void Prefix()
    {
        if (DiagPatch.PipelineDiagFrames <= 0 && NetApplierPatch.DiagFrames <= 0) return;
        DebugLogger.Write("[PIPELINE] LaneSystem OnUpdate (Modification4)");
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// TicketPriceSection diagnostics — trace the UI binding chain to find the break
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>Set to N to log TicketPriceSection for N more frames.</summary>
[HarmonyPatch(typeof(TicketPriceSection), "OnProcess")]
static class TicketPriceOnProcessDiag
{
    static void Postfix(TicketPriceSection __instance)
    {
        if (DiagPatch.PolicyDiagFrames <= 0) return;
        // Read the sliderData property via reflection to see what value was captured.
        var prop = typeof(TicketPriceSection).GetProperty("sliderData",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null)
        {
            var val = prop.GetValue(__instance);
            float sliderValue = -1f;
            if (val != null)
            {
                var valField = val.GetType().GetField("m_Value",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (valField != null)
                    sliderValue = (float)valField.GetValue(val)!;
            }
            DebugLogger.Write($"[POLICY DIAG] TicketPriceSection.OnProcess FIRED  m_Value={sliderValue:F2}");
        }
        else
        {
            DebugLogger.Write("[POLICY DIAG] TicketPriceSection.OnProcess FIRED  (could not read sliderData)");
        }
    }
}

[HarmonyPatch(typeof(TicketPriceSection), "OnWriteProperties")]
static class TicketPriceWriteDiag
{
    static void Prefix()
    {
        if (DiagPatch.PolicyDiagFrames <= 0) return;
        DebugLogger.Write("[POLICY DIAG] TicketPriceSection.OnWriteProperties called");
    }
}

[HarmonyPatch(typeof(SelectedInfoUISystem), "OnUpdate")]
static class SelectedInfoOnUpdateDiag
{
    static void Prefix(SelectedInfoUISystem __instance)
    {
        if (DiagPatch.PolicyDiagFrames <= 0) return;
        var em = __instance.EntityManager;
        var sel = __instance.selectedEntity;
        bool hasUpdated = sel != Entity.Null && em.Exists(sel) && em.HasComponent<Updated>(sel);
        DebugLogger.Write($"[POLICY DIAG] SelectedInfoUISystem.OnUpdate  sel={sel.Index}:{sel.Version} hasUpdated={hasUpdated}");
        DiagPatch.PolicyDiagFrames--;
    }
}
