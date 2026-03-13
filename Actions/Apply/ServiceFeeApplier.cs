// [TEST ONLY] Remove or disable before shipping to production.
using System.Collections.Generic;
using CS2Hooks.Events;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays ServiceFeeChangedEvents by calling ServiceFeeSystem.SetFee directly.
/// Used by TestHarness to verify the revert → re-apply round-trip.
/// </summary>
public class ServiceFeeApplier : GameSystemBase
{
    private struct PendingChange
    {
        public PlayerResource Resource;
        public float          Fee;
        public int            FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingChange> _pending = new();

    private CitySystem _citySystem = null!;

    /// <summary>Enqueue a service fee change to be applied after <paramref name="delayFrames"/> frames.</summary>
    public static void Enqueue(PlayerResource resource, float fee, int delayFrames = 0)
        => _pending.Add(new PendingChange { Resource = resource, Fee = fee, FramesLeft = delayFrames });

    protected override void OnCreate()
    {
        base.OnCreate();
        _citySystem = World.GetOrCreateSystemManaged<CitySystem>();
    }

    protected override void OnUpdate()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0)
            {
                _pending.RemoveAt(i);
                Apply(item.Resource, item.Fee);
            }
            else
            {
                _pending[i] = item;
            }
        }
    }

    private void Apply(PlayerResource resource, float fee)
    {
        if (!EntityManager.HasComponent<ServiceFee>(_citySystem.City))
            return;

        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            var buffer = EntityManager.GetBuffer<ServiceFee>(_citySystem.City);
            ServiceFeeSystem.SetFee(resource, buffer, fee);
            DebugLogger.Write($"[FEE APPLIER]     {resource} → {fee:F2}");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }
}
