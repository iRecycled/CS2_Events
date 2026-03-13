// [TEST ONLY] Remove or disable before shipping to production.
using System.Collections.Generic;
using CS2Hooks.Events;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays BudgetChangedEvents by calling CityServiceBudgetSystem.SetServiceBudget directly.
/// Used by TestHarness to verify the revert → re-apply round-trip.
/// </summary>
public class BudgetApplier : GameSystemBase
{
    private struct PendingChange
    {
        public Entity ServicePrefab;
        public int    Percentage;
        public int    FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingChange> _pending = new();

    /// <summary>Enqueue a budget change to be applied after <paramref name="delayFrames"/> frames.</summary>
    public static void Enqueue(Entity servicePrefab, int percentage, int delayFrames = 0)
        => _pending.Add(new PendingChange { ServicePrefab = servicePrefab, Percentage = percentage, FramesLeft = delayFrames });

    protected override void OnUpdate()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0)
            {
                _pending.RemoveAt(i);
                Apply(item.ServicePrefab, item.Percentage);
            }
            else
            {
                _pending[i] = item;
            }
        }
    }

    private void Apply(Entity servicePrefab, int percentage)
    {
        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            World.GetOrCreateSystemManaged<CityServiceBudgetSystem>().SetServiceBudget(servicePrefab, percentage);
            DebugLogger.Write($"[BUDGET APPLIER]  entity={servicePrefab.Index} → {percentage}%");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }
}
