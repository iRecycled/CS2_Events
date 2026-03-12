using System;
using CS2Hooks.Events;

namespace CS2Hooks;

/// <summary>
/// The public API for CS2Hooks. Subscribe to any of these events from your mod.
///
/// Example:
///   CS2Hooks.EventBus.OnRoadPlaced += e => MyLogger.Log($"Road placed: {e.Prefab?.name}");
/// </summary>
public static class EventBus
{
    // -------------------------------------------------------------------------
    // Road & net events
    // -------------------------------------------------------------------------

    /// <summary>Fired when a road or net segment is successfully placed.</summary>
    public static event Action<RoadPlacedEvent>? OnRoadPlaced;

    /// <summary>
    /// Fired when a road segment is upgraded/replaced with a different type.
    /// (NetToolSystem in Replace mode.)
    /// </summary>
    public static event Action<RoadUpgradedEvent>? OnRoadUpgraded;

    // -------------------------------------------------------------------------
    // Demolish
    // -------------------------------------------------------------------------

    /// <summary>Fired when the bulldoze tool commits a demolition.</summary>
    public static event Action<ObjectDemolishedEvent>? OnObjectDemolished;

    // -------------------------------------------------------------------------
    // Zoning
    // -------------------------------------------------------------------------

    /// <summary>Fired when a zone paint/fill/marquee action is committed.</summary>
    public static event Action<ZoneChangedEvent>? OnZoneChanged;

    // -------------------------------------------------------------------------
    // Buildings & objects
    // -------------------------------------------------------------------------

    /// <summary>Fired when a building or prop is placed.</summary>
    public static event Action<BuildingPlacedEvent>? OnBuildingPlaced;

    /// <summary>
    /// Fired when a building service upgrade (extension) is applied.
    /// (UpgradeToolSystem — distinct from road upgrades.)
    /// </summary>
    public static event Action<BuildingUpgradedEvent>? OnBuildingUpgraded;

    // -------------------------------------------------------------------------
    // City management
    // -------------------------------------------------------------------------

    /// <summary>Fired when the player changes a service budget percentage.</summary>
    public static event Action<BudgetChangedEvent>? OnBudgetChanged;

    /// <summary>Fired when the player enables, disables, or adjusts a policy.</summary>
    public static event Action<PolicyChangedEvent>? OnPolicyChanged;

    // -------------------------------------------------------------------------
    // Internal fire methods — only Patch classes should call these.
    // -------------------------------------------------------------------------

    internal static void FireRoadPlaced(RoadPlacedEvent e)        => OnRoadPlaced?.Invoke(e);
    internal static void FireRoadUpgraded(RoadUpgradedEvent e)    => OnRoadUpgraded?.Invoke(e);
    internal static void FireDemolished(ObjectDemolishedEvent e)  => OnObjectDemolished?.Invoke(e);
    internal static void FireZoneChanged(ZoneChangedEvent e)      => OnZoneChanged?.Invoke(e);
    internal static void FireBuildingPlaced(BuildingPlacedEvent e)   => OnBuildingPlaced?.Invoke(e);
    internal static void FireBuildingUpgraded(BuildingUpgradedEvent e) => OnBuildingUpgraded?.Invoke(e);
    internal static void FireBudgetChanged(BudgetChangedEvent e)  => OnBudgetChanged?.Invoke(e);
    internal static void FirePolicyChanged(PolicyChangedEvent e)  => OnPolicyChanged?.Invoke(e);
}
