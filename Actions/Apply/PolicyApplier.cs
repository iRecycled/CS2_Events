// [TEST ONLY] Remove or disable before shipping to production.
using System.Collections.Generic;
using Game;
using Game.Policies;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using Unity.Entities;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays PolicyChangedEvents. The backend data is updated via direct buffer
/// write + SetPolicy (for game side effects). The UI is refreshed by briefly
/// clearing the entity selection and restoring it the next frame — this forces
/// React to remount the info panel and read fresh binding values.
///
/// For city policies pass Entity.Null as target (same convention as the event).
/// </summary>
public class PolicyApplier : GameSystemBase
{
    private struct PendingChange
    {
        public Entity Target;     // Entity.Null → city policy
        public Entity Policy;
        public bool   Active;
        public float  Adjustment;
        public int    FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingChange> _pending = new();
    private bool _loggedOnce;
    private CitySystem _citySystem = null!;
    private ToolSystem _toolSystem = null!;
    private SelectedInfoUISystem _infoSystem = null!;

    // Two-phase reselect state: clear selection this frame, restore next frame.
    private Entity _reselectEntity = Entity.Null;
    private bool   _pendingRestore;

    /// <summary>True if there's already a pending revert/re-apply queued for this target+policy pair.</summary>
    public static bool HasPending(Entity target, Entity policy)
        => _pending.Exists(p => p.Target == target && p.Policy == policy);

    /// <summary>Enqueue a policy change to be applied after <paramref name="delayFrames"/> frames.</summary>
    public static void Enqueue(Entity target, Entity policy, bool active, float adjustment, int delayFrames = 0)
        => _pending.Add(new PendingChange
        {
            Target     = target,
            Policy     = policy,
            Active     = active,
            Adjustment = adjustment,
            FramesLeft = delayFrames,
        });

    protected override void OnCreate()
    {
        base.OnCreate();
        _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
        _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
        _infoSystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
    }

    protected override void OnUpdate()
    {
        if (!_loggedOnce)
        {
            _loggedOnce = true;
            DebugLogger.Write("[POLICY APPLIER] system running at UIUpdate — ready");
        }

        // Phase 2 of reselect: restore the entity that was cleared last frame.
        // SelectedInfoUISystem sees m_SelectedEntity changed → React remounts panel
        // with fresh binding data (reads current buffer = new policy value).
        if (_pendingRestore)
        {
            _pendingRestore = false;
            if (_reselectEntity != Entity.Null && EntityManager.Exists(_reselectEntity))
            {
                _toolSystem.selected = _reselectEntity;
                DebugLogger.Write($"[POLICY APPLIER] reselect phase 2: restored entity {_reselectEntity.Index}");
            }
            _reselectEntity = Entity.Null;
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0)
            {
                _pending.RemoveAt(i);
                Apply(item.Target, item.Policy, item.Active, item.Adjustment);
            }
            else
            {
                _pending[i] = item;
            }
        }
    }

    private void Apply(Entity target, Entity policy, bool active, float adjustment)
    {
        var em = EntityManager;

        // Resolve the actual entity for city policies (Entity.Null is our sentinel).
        Entity bufferEntity = target == Entity.Null ? _citySystem.City : target;

        if (!em.Exists(bufferEntity) || !em.HasBuffer<Policy>(bufferEntity))
        {
            DebugLogger.Write($"[POLICY APPLIER] SKIPPED — entity {bufferEntity.Index} invalid or has no Policy buffer");
            return;
        }

        Entity sel = _infoSystem.selectedEntity;
        DebugLogger.Write($"[POLICY APPLIER] apply: target={bufferEntity.Index} policy={policy.Index} active={active} adj={adjustment:F2}  selectedEntity={sel.Index}:{sel.Version}");

        IsApplying = true;
        try
        {
            // ── Step 1: Direct buffer write — immediate, same-frame effect ──
            var buf = em.GetBuffer<Policy>(bufferEntity);
            var flags = active ? PolicyFlags.Active : 0;
            bool found = false;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].m_Policy == policy)
                {
                    buf[i] = new Policy(policy, flags, adjustment);
                    found = true;
                    break;
                }
            }
            if (!found && active)
                buf.Add(new Policy(policy, flags, adjustment));

            DebugLogger.Write($"[POLICY APPLIER] buffer write: adj={adjustment:F2} found={found}");

            // ── Step 2: SetPolicy for game side effects (RouteModifier, telemetry etc.) ──
            var uiSystem = World.GetOrCreateSystemManaged<PoliciesUISystem>();
            if (target == Entity.Null)
                uiSystem.SetCityPolicy(policy, active, adjustment);
            else
                uiSystem.SetPolicy(target, policy, active, adjustment);

            // ── Step 3: Force panel remount via 2-frame reselect ──
            // The React slider is an uncontrolled component — it ignores binding
            // pushes once the user has interacted with it. The only way to make it
            // read the new value is to unmount and remount the entire info panel,
            // which happens when the selected entity changes.
            //
            // Phase 1 (this frame): clear selection → panel disappears.
            // Phase 2 (next frame): restore entity → panel remounts and reads
            //   the buffer value we wrote in Step 1.
            if (_reselectEntity == Entity.Null)  // don't stomp an in-progress reselect
            {
                _reselectEntity = bufferEntity;
                _pendingRestore = true;
                _toolSystem.selected = Entity.Null;
                DebugLogger.Write($"[POLICY APPLIER] reselect phase 1: cleared selection (will restore {bufferEntity.Index} next frame)");

                // Arm diagnostics to trace the next OnProcess call.
                Events.Patches.DiagPatch.PolicyDiagFrames = 6;
            }
        }
        finally
        {
            IsApplying = false;
        }
    }
}
