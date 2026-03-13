using Game.Economy;
using Game.Simulation;
using HarmonyLib;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches TaxSystem.SetTaxRate(TaxAreaType, int) to fire OnTaxRateChanged.
/// Captures the current rate before the change so OldRate is available.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.SetTaxRate))]
static class TaxAreaRatePatch
{
    static void Prefix(TaxSystem __instance, TaxAreaType areaType, int rate)
    {
        int oldRate = __instance.GetTaxRate(areaType);
        EconomyDebounceSystem.RecordAreaTaxChange(areaType, oldRate, rate);
    }
}

/// <summary>
/// Patches the TaxSystem.TaxRate property setter to fire OnTaxRateChanged
/// when the player adjusts the overall city tax rate slider.
/// Fires once per area type since the setter cascades to all four areas.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.TaxRate), MethodType.Setter)]
static class TaxOverallRatePatch
{
    static void Prefix(TaxSystem __instance, int value)
    {
        int oldRate = __instance.TaxRate;
        EconomyDebounceSystem.RecordAreaTaxChange(TaxAreaType.Residential, oldRate, value);
        EconomyDebounceSystem.RecordAreaTaxChange(TaxAreaType.Commercial,  oldRate, value);
        EconomyDebounceSystem.RecordAreaTaxChange(TaxAreaType.Industrial,  oldRate, value);
        EconomyDebounceSystem.RecordAreaTaxChange(TaxAreaType.Office,      oldRate, value);
    }
}

/// <summary>
/// Patches TaxSystem.SetResidentialTaxRate(int, int) to fire OnResourceTaxRateChanged.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.SetResidentialTaxRate))]
static class ResidentialTaxPatch
{
    static void Prefix(TaxSystem __instance, int jobLevel, int rate)
    {
        int oldRate = __instance.GetResidentialTaxRate(jobLevel);
        EconomyDebounceSystem.RecordResourceTaxChange(TaxAreaType.Residential, jobLevel, oldRate, rate);
    }
}

/// <summary>
/// Patches TaxSystem.SetCommercialTaxRate(Resource, int) to fire OnResourceTaxRateChanged.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.SetCommercialTaxRate))]
static class CommercialTaxPatch
{
    static void Prefix(TaxSystem __instance, Resource resource, int rate)
    {
        int oldRate = __instance.GetCommercialTaxRate(resource);
        EconomyDebounceSystem.RecordResourceTaxChange(TaxAreaType.Commercial, EconomyUtils.GetResourceIndex(resource), oldRate, rate);
    }
}

/// <summary>
/// Patches TaxSystem.SetIndustrialTaxRate(Resource, int) to fire OnResourceTaxRateChanged.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.SetIndustrialTaxRate))]
static class IndustrialTaxPatch
{
    static void Prefix(TaxSystem __instance, Resource resource, int rate)
    {
        int oldRate = __instance.GetIndustrialTaxRate(resource);
        EconomyDebounceSystem.RecordResourceTaxChange(TaxAreaType.Industrial, EconomyUtils.GetResourceIndex(resource), oldRate, rate);
    }
}

/// <summary>
/// Patches TaxSystem.SetOfficeTaxRate(Resource, int) to fire OnResourceTaxRateChanged.
/// </summary>
[HarmonyPatch(typeof(TaxSystem), nameof(TaxSystem.SetOfficeTaxRate))]
static class OfficeTaxPatch
{
    static void Prefix(TaxSystem __instance, Resource resource, int rate)
    {
        int oldRate = __instance.GetOfficeTaxRate(resource);
        EconomyDebounceSystem.RecordResourceTaxChange(TaxAreaType.Office, EconomyUtils.GetResourceIndex(resource), oldRate, rate);
    }
}
