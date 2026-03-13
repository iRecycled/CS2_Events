// [TEST ONLY] Remove or disable before shipping to production.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CS2Hooks.Events;
using Game;
using Game.Economy;
using Game.Simulation;
using Game.UI.InGame;

namespace CS2Hooks.Actions.Apply;

/// <summary>
/// Replays TaxRateChangedEvents and ResourceTaxRateChangedEvents by routing through
/// TaxationUISystem private methods (via reflection) so that both the TaxSystem value
/// AND the UI bindings are refreshed in one call.
///
/// Force-trigger area rate: create CS2Hooks_taxtest.txt with "Residential:15".
/// Force-trigger resource rate: create CS2Hooks_taxtest.txt with "Residential:1:16"
///   (AreaType:Resource:Rate — Resource is job level for Residential, resource index for others).
/// Example from terminal:
///   echo "Residential:15" > "/c/Users/$USER/AppData/LocalLow/Colossal Order/Cities Skylines II/CS2Hooks_taxtest.txt"
/// </summary>
public class TaxApplier : GameSystemBase
{
    private struct PendingAreaChange
    {
        public TaxAreaType AreaType;
        public int         Rate;
        public int         FramesLeft;
    }

    private struct PendingResourceChange
    {
        public TaxAreaType AreaType;
        public int         Resource;
        public int         Rate;
        public int         FramesLeft;
    }

    internal static bool IsApplying { get; private set; }

    private static readonly List<PendingAreaChange>     _pendingArea     = new();
    private static readonly List<PendingResourceChange> _pendingResource = new();

    // TaxationUISystem.SetAreaTaxRate(int areaType, int rate)
    private static MethodInfo? _setAreaTaxRate;
    // TaxationUISystem.SetResourceTaxRate(int resource, int areaType, int rate)
    private static MethodInfo? _setResourceTaxRate;

    private static string _triggerPath = string.Empty;
    private int _pollCountdown;

    protected override void OnCreate()
    {
        base.OnCreate();
        _triggerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"..\LocalLow\Colossal Order\Cities Skylines II\CS2Hooks_taxtest.txt"
        );

        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;
        _setAreaTaxRate     = typeof(TaxationUISystem).GetMethod("SetAreaTaxRate",     priv);
        _setResourceTaxRate = typeof(TaxationUISystem).GetMethod("SetResourceTaxRate", priv);

        if (_setAreaTaxRate == null)
            DebugLogger.Write("[TAX APPLIER]     WARNING: TaxationUISystem.SetAreaTaxRate not found");
        if (_setResourceTaxRate == null)
            DebugLogger.Write("[TAX APPLIER]     WARNING: TaxationUISystem.SetResourceTaxRate not found");
    }

    /// <summary>Enqueue an area-level tax rate change.</summary>
    public static void Enqueue(TaxAreaType areaType, int rate, int delayFrames = 0)
        => _pendingArea.Add(new PendingAreaChange { AreaType = areaType, Rate = rate, FramesLeft = delayFrames });

    /// <summary>Enqueue a resource/job-level tax rate change.</summary>
    public static void EnqueueResource(TaxAreaType areaType, int resource, int rate, int delayFrames = 0)
        => _pendingResource.Add(new PendingResourceChange { AreaType = areaType, Resource = resource, Rate = rate, FramesLeft = delayFrames });

    protected override void OnUpdate()
    {
        if (--_pollCountdown <= 0)
        {
            _pollCountdown = 60;
            PollTriggerFile();
        }

        for (int i = _pendingArea.Count - 1; i >= 0; i--)
        {
            var item = _pendingArea[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0) { _pendingArea.RemoveAt(i);     ApplyArea(item.AreaType, item.Rate); }
            else                      { _pendingArea[i] = item; }
        }

        for (int i = _pendingResource.Count - 1; i >= 0; i--)
        {
            var item = _pendingResource[i];
            item.FramesLeft--;
            if (item.FramesLeft <= 0) { _pendingResource.RemoveAt(i); ApplyResource(item.AreaType, item.Resource, item.Rate); }
            else                      { _pendingResource[i] = item; }
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
            if (!Enum.TryParse<TaxAreaType>(parts[0].Trim(), out var areaType)) return;

            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var rate))
            {
                DebugLogger.Write($"[TAX APPLIER]     trigger file: area {areaType} → {rate}%");
                Enqueue(areaType, rate, delayFrames: 0);
            }
            else if (parts.Length == 3 && int.TryParse(parts[1].Trim(), out var resource) && int.TryParse(parts[2].Trim(), out rate))
            {
                DebugLogger.Write($"[TAX APPLIER]     trigger file: resource {areaType}:{resource} → {rate}%");
                EnqueueResource(areaType, resource, rate, delayFrames: 0);
            }
        }
        catch { /* never crash the game */ }
    }

    private void ApplyArea(TaxAreaType areaType, int rate)
    {
        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            var taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
            int before    = taxSystem.GetTaxRate(areaType);

            if (_setAreaTaxRate != null)
            {
                var uiSystem = World.GetOrCreateSystemManaged<TaxationUISystem>();
                _setAreaTaxRate.Invoke(uiSystem, new object[] { (int)areaType, rate });
            }
            else
            {
                taxSystem.Readers.Complete();
                taxSystem.SetTaxRate(areaType, rate);
            }

            int after = taxSystem.GetTaxRate(areaType);
            DebugLogger.Write($"[TAX APPLIER]     area {areaType} {before}% → {rate}% (confirmed {after}%)");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }

    private void ApplyResource(TaxAreaType areaType, int resource, int rate)
    {
        IsApplying = true;
        EconomyDebounceSystem.SuppressRecording = true;
        try
        {
            if (_setResourceTaxRate != null)
            {
                // SetResourceTaxRate(int resource, int areaType, int rate) — updates TaxSystem + all UI bindings
                var uiSystem = World.GetOrCreateSystemManaged<TaxationUISystem>();
                _setResourceTaxRate.Invoke(uiSystem, new object[] { resource, (int)areaType, rate });
            }
            else
            {
                // Fallback: call TaxSystem directly (UI won't refresh until panel is reopened)
                var taxSystem = World.GetOrCreateSystemManaged<TaxSystem>();
                taxSystem.Readers.Complete();
                switch (areaType)
                {
                    case TaxAreaType.Residential: taxSystem.SetResidentialTaxRate(resource, rate);                                  break;
                    case TaxAreaType.Commercial:  taxSystem.SetCommercialTaxRate(EconomyUtils.GetResource(resource), rate);      break;
                    case TaxAreaType.Industrial:  taxSystem.SetIndustrialTaxRate(EconomyUtils.GetResource(resource), rate);      break;
                    case TaxAreaType.Office:      taxSystem.SetOfficeTaxRate(EconomyUtils.GetResource(resource), rate);          break;
                }
            }

            DebugLogger.Write($"[TAX APPLIER]     resource {areaType}:{resource} → {rate}%");
        }
        finally
        {
            EconomyDebounceSystem.SuppressRecording = false;
            IsApplying = false;
        }
    }
}
