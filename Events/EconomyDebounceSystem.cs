using System;
using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace CS2Hooks.Events;

/// <summary>
/// Sits between all economy patches and the EventBus.
/// Batches rapid changes (e.g. slider drags) and fires a single consolidated event
/// per key after 1 second of inactivity.
///
/// Replaces TaxDebounceSystem and extends the same pattern to loans and service fees.
/// </summary>
public class EconomyDebounceSystem : GameSystemBase
{
    /// <summary>
    /// Set to true by any economy applier while it is writing programmatically,
    /// so patches do not feed replayed values back into the debounce queues.
    /// </summary>
    internal static bool SuppressRecording { get; set; }

    private static readonly TimeSpan DebounceTime = TimeSpan.FromSeconds(1);

    // Key = TaxAreaType; fires TaxRateChangedEvent
    private static readonly Debounce<TaxAreaType, int> _areaTax = new(DebounceTime,
        (area, old, @new) => EventBus.FireTaxRateChanged(
            new TaxRateChangedEvent(area, old, @new)));

    // Key = (TaxAreaType, resource index); fires ResourceTaxRateChangedEvent
    private static readonly Debounce<(TaxAreaType, int), int> _resourceTax = new(DebounceTime,
        (key, old, @new) => EventBus.FireResourceTaxRateChanged(
            new ResourceTaxRateChangedEvent(key.Item1, key.Item2, old, @new)));

    // Key = PlayerResource; fires ServiceFeeChangedEvent
    private static readonly Debounce<PlayerResource, float> _serviceFee = new(DebounceTime,
        (resource, old, @new) => EventBus.FireServiceFeeChanged(
            new ServiceFeeChangedEvent(resource, old, @new)));

    // Key = 0 (singleton slot — only one city loan); fires LoanChangedEvent
    private static readonly Debounce<int, int> _loan = new(DebounceTime,
        (_, old, @new) => EventBus.FireLoanChanged(
            new LoanChangedEvent(old, @new)));

    // Key = service Entity; fires BudgetChangedEvent
    private static readonly Debounce<Entity, int> _budget = new(DebounceTime,
        (entity, old, @new) => EventBus.FireBudgetChanged(
            new BudgetChangedEvent(entity, old, @new)));

    internal static void RecordAreaTaxChange(TaxAreaType area, int old, int @new)
    {
        if (SuppressRecording) return;
        _areaTax.Record(area, old, @new);
    }

    internal static void RecordResourceTaxChange(TaxAreaType area, int resource, int old, int @new)
    {
        if (SuppressRecording) return;
        _resourceTax.Record((area, resource), old, @new);
    }

    internal static void RecordServiceFeeChange(PlayerResource resource, float old, float @new)
    {
        if (SuppressRecording) return;
        _serviceFee.Record(resource, old, @new);
    }

    internal static void RecordLoanChange(int old, int @new)
    {
        if (SuppressRecording) return;
        _loan.Record(0, old, @new);
    }

    internal static void RecordBudgetChange(Entity entity, int old, int @new)
    {
        if (SuppressRecording) return;
        _budget.Record(entity, old, @new);
    }

    protected override void OnUpdate()
    {
        _areaTax.Flush();
        _resourceTax.Flush();
        _serviceFee.Flush();
        _loan.Flush();
        _budget.Flush();
    }
}
