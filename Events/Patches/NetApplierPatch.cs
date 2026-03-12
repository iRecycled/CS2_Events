using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Common;
using Game.Tools;
using HarmonyLib;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Replays <see cref="RoadPlacedEvent"/> data using a two-frame approach that mirrors
/// how the real NetToolSystem.Apply() pipeline works:
///
///   Frame N  (Phase 1 — "preview"):
///     • Sets m_Prefab / m_Mode / m_RandomSeed / m_ControlPoints from the event.
///     • Calls SnapControlPoints + FixControlPoints + UpdateCourse.
///       UpdateCourse queues ECB commands into ToolOutputBarrier (creates Temp entities).
///     • Sets m_State = State.Applying so that frame N+1's Update() keeps the cursor
///       frozen and does NOT call DestroyDefinitions / UpdateCourse again.
///     • Sets _waitingForFlush = true.
///
///   End of frame N: ToolOutputBarrier flushes — Temp entities now live in ECS.
///
///   Frame N+1  (Phase 2 — "commit"):
///     • Sets applyMode = Apply  (visible to ToolOutputSystem, which runs after us).
///     • Resets m_State = Default and clears control points.
///     • ToolOutputSystem sees Apply → triggers ApplyTool phase.
///     • ApplyNetSystem finds the Temp entities from Phase 1 → road is committed.
/// </summary>
[HarmonyPatch(typeof(NetToolSystem), "OnUpdate")]
static class NetApplierPatch
{
    private static readonly Queue<RoadPlacedEvent> _pending = new();

    // Two-frame state
    private static bool _waitingForFlush;

    // Reflection resolved lazily on first use.
    private static bool          _resolved;
    private static FieldInfo?    _prefabField;
    private static FieldInfo?    _modeField;
    private static FieldInfo?    _randomSeedField;
    private static MethodInfo?   _updateCourse;
    private static MethodInfo?   _setApplyMode;
    private static FieldInfo?    _stateField;
    private static FieldInfo?    _applyStartField;
    private static MethodInfo?   _snapControlPoints;
    private static MethodInfo?   _fixControlPoints;
    private static object?       _applyingStateValue; // boxed State.Applying
    private static object?       _defaultStateValue;  // boxed State.Default

    // Diagnostic frame counter — enable for N frames after Phase 2 fires.
    internal static int DiagFrames;

    public static void Enqueue(RoadPlacedEvent e) => _pending.Enqueue(e);

    static void Postfix(NetToolSystem __instance, ref JobHandle __result)
    {
        // ── PHASE 2: commit the Temp entities created in Phase 1 ──────────────
        if (_waitingForFlush)
        {
            DebugLogger.Write($"[NET APPLIER]     PHASE2 — applyMode={__instance.applyMode}");

            if (__instance.applyMode == ApplyMode.Apply)
            {
                // The game's own Apply() already committed this frame (e.g. user
                // released the mouse button) — the road is being placed naturally.
                _waitingForFlush = false;
                var l2 = __instance.GetControlPoints(out var ld2);
                ld2.Complete();
                l2.Clear();
                DebugLogger.Write("[NET APPLIER]     PHASE2 — natural commit detected, clearing state");
                return;
            }

            if (_setApplyMode == null || _stateField == null || _defaultStateValue == null)
            {
                _waitingForFlush = false;
                return;
            }

            // Set applyMode = Apply so ToolOutputSystem (which runs after us in
            // the same ToolUpdate frame) triggers SystemUpdatePhase.ApplyTool.
            _setApplyMode.Invoke(__instance, new object[] { ApplyMode.Apply });

            // Reset tool state so normal placement can resume next frame.
            _stateField.SetValue(__instance, _defaultStateValue);

            var clearList = __instance.GetControlPoints(out var clearDeps);
            clearDeps.Complete();
            clearList.Clear();

            _waitingForFlush = false;
            DiagFrames = 10; // log ToolOutputSystem + ApplyNetSystem for 10 frames

            DebugLogger.Write("[NET APPLIER]     PHASE2 done — applyMode=Apply set, awaiting ApplyTool");
            return;
        }

        // ── PHASE 1: create Temp entities for the replay ─────────────────────
        if (_pending.Count == 0) return;

        if (!_resolved) ResolveReflection();

        if (_prefabField   == null || _modeField      == null ||
            _randomSeedField == null || _updateCourse == null || _setApplyMode  == null ||
            _stateField    == null || _applyStartField == null ||
            _snapControlPoints == null || _fixControlPoints == null ||
            _applyingStateValue == null || _defaultStateValue == null)
        {
            _pending.Clear();
            return;
        }

        // Don't replay on a frame where a real placement is being committed.
        if (__instance.applyMode == ApplyMode.Apply) return;

        var e = _pending.Dequeue();

        if (e.Prefab == null)
        {
            DebugLogger.Write("[NET APPLIER]     PHASE1 skipped — prefab is null");
            return;
        }

        try
        {
            _prefabField.SetValue(__instance, e.Prefab);
            _modeField.SetValue(__instance, e.Mode);
            _randomSeedField.SetValue(__instance, RandomSeed.Next());

            // Populate control points (use Entity.Null for snap entities so the
            // game snaps by world-position proximity — safe after demolition).
            var list = __instance.GetControlPoints(out var cpDeps);
            cpDeps.Complete();
            list.Clear();
            foreach (var pt in e.ControlPoints)
                list.Add(new ControlPoint
                {
                    m_Position       = pt.Position,
                    m_HitPosition    = pt.Position,
                    m_Rotation       = pt.Rotation,
                    m_Elevation      = pt.Elevation,
                    m_OriginalEntity = Unity.Entities.Entity.Null,
                    m_ElementIndex   = pt.ElementIndex,
                });

            // Snap + fix (mirrors the real Apply() commit path).
            var snapDep = (JobHandle)_snapControlPoints.Invoke(
                __instance, new object[] { __result, false })!;
            var fixDep  = (JobHandle)_fixControlPoints.Invoke(
                __instance, new object[] { snapDep })!;
            fixDep.Complete(); // wait so list[last] reflects snapped position

            // Store the snapped end point as m_ApplyStartPoint.  In State.Applying,
            // Update() freezes the cursor to m_ApplyStartPoint, which prevents
            // DestroyDefinitions / UpdateCourse from running on frame N+1
            // (provided snapping at that position is deterministic).
            if (list.Length > 0)
                _applyStartField.SetValue(__instance, list[list.Length - 1]);

            // Queue Temp entity creation into ToolOutputBarrier.
            // Complete() ensures the ECB commands are queued before we set State.
            __result = (JobHandle)_updateCourse.Invoke(
                __instance, new object[] { fixDep, false })!;
            __result.Complete();

            // Freeze the tool so Update() on frame N+1 does not call
            // DestroyDefinitions (which would delete the just-queued entities).
            _stateField.SetValue(__instance, _applyingStateValue);

            _waitingForFlush = true;

            DebugLogger.Write(
                $"[NET APPLIER]     PHASE1 '{e.Prefab.name}'" +
                $" mode={e.Mode} pts={e.ControlPoints.Length}" +
                $" end={list[list.Length - 1].m_Position}");
        }
        catch (Exception ex)
        {
            DebugLogger.Write($"[NET APPLIER]     exception: {ex.Message}\n{ex.StackTrace}");
            _waitingForFlush = false;
        }
    }

    private static void ResolveReflection()
    {
        _resolved = true;
        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;

        _prefabField      = Warn(typeof(NetToolSystem).GetField("m_Prefab",      priv), "m_Prefab");
        _modeField        = Warn(typeof(NetToolSystem).GetField("m_Mode",        priv), "m_Mode");
        _randomSeedField  = Warn(typeof(NetToolSystem).GetField("m_RandomSeed",  priv), "m_RandomSeed");
        _stateField       = Warn(typeof(NetToolSystem).GetField("m_State",       priv), "m_State");
        _applyStartField  = Warn(typeof(NetToolSystem).GetField("m_ApplyStartPoint", priv), "m_ApplyStartPoint");

        _updateCourse     = Warn(typeof(NetToolSystem).GetMethod("UpdateCourse",     priv), "UpdateCourse");
        _snapControlPoints = Warn(typeof(NetToolSystem).GetMethod("SnapControlPoints", priv), "SnapControlPoints");
        _fixControlPoints  = Warn(typeof(NetToolSystem).GetMethod("FixControlPoints",  priv), "FixControlPoints");

        var prop = typeof(ToolBaseSystem).GetProperty("applyMode",
            BindingFlags.Public | BindingFlags.Instance);
        if (prop == null)
            DebugLogger.Write("[NET APPLIER]     reflection FAILED: ToolBaseSystem.applyMode property not found");
        else
            _setApplyMode = Warn(prop.GetSetMethod(nonPublic: true), "applyMode setter");

        // Resolve the private State enum values.
        var stateType = typeof(NetToolSystem).GetNestedType("State",
            BindingFlags.NonPublic);
        if (stateType == null)
        {
            DebugLogger.Write("[NET APPLIER]     reflection FAILED: NetToolSystem.State enum not found");
        }
        else
        {
            try
            {
                _applyingStateValue = Enum.Parse(stateType, "Applying");
                _defaultStateValue  = Enum.Parse(stateType, "Default");
            }
            catch (Exception ex)
            {
                DebugLogger.Write($"[NET APPLIER]     reflection FAILED parsing State enum: {ex.Message}");
            }
        }

        bool ok = _prefabField != null && _modeField != null && _randomSeedField != null &&
                  _stateField  != null && _applyStartField != null &&
                  _updateCourse != null && _snapControlPoints != null && _fixControlPoints != null &&
                  _setApplyMode != null && _applyingStateValue != null && _defaultStateValue != null;
        if (ok)
            DebugLogger.Write("[NET APPLIER]     reflection resolved OK");
    }

    private static FieldInfo? Warn(FieldInfo? f, string name)
    {
        if (f == null) DebugLogger.Write($"[NET APPLIER]     reflection FAILED: {name} not found");
        return f;
    }

    private static MethodInfo? Warn(MethodInfo? m, string name)
    {
        if (m == null) DebugLogger.Write($"[NET APPLIER]     reflection FAILED: {name} not found");
        return m;
    }
}
