// [TEST ONLY] Remove or disable before shipping to production.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CS2Hooks.Events;
using Game;
using Game.Simulation;
using Game.UI.InGame;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays TaxRateChangedEvents by routing through TaxationUISystem.SetAreaTaxRate
/// (via reflection) so that both the TaxSystem value AND the UI bindings are refreshed.
/// Used by TestHarness to verify the revert → re-apply round-trip.
///
/// Force-trigger: create the file CS2Hooks_taxtest.txt in the same folder as CS2Hooks.log.
/// Contents: "Residential:15" (AreaType:Rate). The applier reads it, applies the rate, deletes the file.
/// Example from terminal:
///   echo "Residential:15" > "/c/Users/$USER/AppData/LocalLow/Colossal Order/Cities Skylines II/CS2Hooks_taxtest.txt"
/// </summary>
public class TaxApplier : GameSystemBase
{
    private struct PendingChange
    {
        public TaxAreaType AreaType;
        public int         Rate;
        public int         FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingChange> _pending = new();

    // Cached reflection — TaxationUISystem.SetAreaTaxRate(int areaType, int rate)
    // This sets m_TaxRates AND triggers the UI binding updates, matching the game's own UI path.
    private static MethodInfo? _setAreaTaxRate;

    private static string _triggerPath = string.Empty;
    private int _pollCountdown;

    protected override void OnCreate()
    {
        base.OnCreate();
        _triggerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"..\LocalLow\Colossal Order\Cities Skylines II\CS2Hooks_taxtest.txt"
        );
        _setAreaTaxRate = typeof(TaxationUISystem).GetMethod(
            "SetAreaTaxRate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (_setAreaTaxRate == null)
            DebugLogger.Write("[TAX APPLIER]     WARNING: TaxationUISystem.SetAreaTaxRate not found via reflection");
    }

    /// <summary>Enqueue a tax rate change to be applied after <paramref name="delayFrames"/> frames.</summary>
    public static void Enqueue(TaxAreaType areaType, int rate, int delayFrames = 0)
        => _pending.Add(new PendingChange { AreaType = areaType, Rate = rate, FramesLeft = delayFrames });

    protected override void OnUpdate()
    {
        // Poll the trigger file every ~60 frames to keep overhead minimal
        if (--_pollCountdown <= 0)
        {
            _pollCountdown = 60;
            PollTriggerFile();
        }

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var item = _pending[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0)
            {
                _pending.RemoveAt(i);
                Apply(item.AreaType, item.Rate);
            }
            else
            {
                _pending[i] = item;
            }
        }
    }

    private void PollTriggerFile()
    {
        try
        {
            if (!File.Exists(_triggerPath)) return;

            var line = File.ReadAllText(_triggerPath).Trim();
            File.Delete(_triggerPath);

            var parts = line.Split(':');
            if (parts.Length != 2) return;
            if (!Enum.TryParse<TaxAreaType>(parts[0].Trim(), out var areaType)) return;
            if (!int.TryParse(parts[1].Trim(), out var rate)) return;

            DebugLogger.Write($"[TAX APPLIER]     trigger file: forcing {areaType} → {rate}%");
            Enqueue(areaType, rate, delayFrames: 0);
        }
        catch { /* never crash the game */ }
    }

    private void Apply(TaxAreaType areaType, int rate)
    {
        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            var taxSystem  = World.GetOrCreateSystemManaged<TaxSystem>();
            int before = taxSystem.GetTaxRate(areaType);

            if (_setAreaTaxRate != null)
            {
                // Goes through TaxationUISystem which completes readers, sets the rate,
                // and pushes m_AreaTaxRates / m_ResourceTaxRates / m_AreaResourceTaxRanges updates.
                var uiSystem = World.GetOrCreateSystemManaged<TaxationUISystem>();
                _setAreaTaxRate.Invoke(uiSystem, new object[] { (int)areaType, rate });
            }
            else
            {
                // Fallback: set directly (UI won't refresh until panel is reopened)
                taxSystem.Readers.Complete();
                taxSystem.SetTaxRate(areaType, rate);
            }

            int after = taxSystem.GetTaxRate(areaType);
            DebugLogger.Write($"[TAX APPLIER]     {areaType} {before}% → {rate}% (confirmed {after}%)");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }
}
