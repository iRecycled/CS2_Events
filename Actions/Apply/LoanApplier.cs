// [TEST ONLY] Remove or disable before shipping to production.
using System.Collections.Generic;
using CS2Hooks.Events;
using Game;
using Game.Tools;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays LoanChangedEvents by calling LoanSystem.ChangeLoan directly.
/// Used by TestHarness to verify the revert → re-apply round-trip.
/// </summary>
public class LoanApplier : GameSystemBase
{
    private struct PendingChange
    {
        public int Amount;
        public int FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingChange> _pending = new();

    /// <summary>Enqueue a loan change to be applied after <paramref name="delayFrames"/> frames.</summary>
    public static void Enqueue(int amount, int delayFrames = 0)
        => _pending.Add(new PendingChange { Amount = amount, FramesLeft = delayFrames });

    protected override void OnUpdate()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0)
            {
                _pending.RemoveAt(i);
                Apply(item.Amount);
            }
            else
            {
                _pending[i] = item;
            }
        }
    }

    private void Apply(int amount)
    {
        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            World.GetOrCreateSystemManaged<LoanSystem>().ChangeLoan(amount);
            DebugLogger.Write($"[LOAN APPLIER]    ChangeLoan({amount})");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }
}
