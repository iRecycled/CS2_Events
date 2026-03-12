using Colossal.Mathematics;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2Hooks.Events;

// ---------------------------------------------------------------------------
// Shared position type
// ---------------------------------------------------------------------------

/// <summary>
/// A world-space point captured from a CS2 net-tool action (road, power line, pipe, etc.).
/// For roads/nets, multiple points define the bezier curve (start → [curve handle] → end).
/// </summary>
public readonly struct PlacementPoint
{
    /// <summary>Snapped world-space position (what actually gets built).</summary>
    public readonly float3 Position;
    /// <summary>Rotation at this point.</summary>
    public readonly quaternion Rotation;
    /// <summary>Vertical elevation offset above terrain.</summary>
    public readonly float Elevation;

    /// <summary>
    /// The existing node or edge entity this point snapped to, or
    /// <c>Entity.Null</c> if placed on bare terrain.
    /// <list type="bullet">
    ///   <item>Node entity  → endpoint connects to an existing intersection/junction.</item>
    ///   <item>Edge entity  → endpoint splits an existing segment mid-span.</item>
    ///   <item>Entity.Null  → fresh placement, no connection to existing network.</item>
    /// </list>
    /// Note: entity IDs are session-specific and not valid across save/load.
    /// </summary>
    public readonly Entity SnapEntity;

    /// <summary>
    /// When <see cref="SnapEntity"/> is a node: <c>(-1, -1)</c>.<br/>
    /// When <see cref="SnapEntity"/> is an edge: the element index within that edge
    /// (identifies which sub-segment was hit).
    /// <c>default</c> when <see cref="SnapEntity"/> is <c>Entity.Null</c>.
    /// </summary>
    public readonly int2 ElementIndex;

    public PlacementPoint(float3 position, quaternion rotation, float elevation,
                          Entity snapEntity, int2 elementIndex)
    {
        Position     = position;
        Rotation     = rotation;
        Elevation    = elevation;
        SnapEntity   = snapEntity;
        ElementIndex = elementIndex;
    }

    /// <summary>True if this point snapped to an existing node or edge.</summary>
    public bool IsSnapped => SnapEntity != Entity.Null;

    public override string ToString() =>
        $"({Position.x:F1}, {Position.y:F1}, {Position.z:F1}) elev={Elevation:F1}" +
        (IsSnapped ? $" snap={SnapEntity.Index}:{SnapEntity.Version} elem={ElementIndex}" : "");
}

// ---------------------------------------------------------------------------
// Road / net
// ---------------------------------------------------------------------------

/// <summary>
/// Fired after a road or net segment is successfully placed.
/// </summary>
/// <param name="Mode">The drawing mode used (Straight, SimpleCurve, etc.).</param>
/// <param name="Prefab">The road prefab that was placed, or null if unavailable.</param>
/// <param name="ControlPoints">
/// The ordered bezier control points of the placed segment.
/// Straight = 2 points, SimpleCurve = 3, ComplexCurve = 4.
/// </param>
public record RoadPlacedEvent(
    NetToolSystem.Mode Mode,
    NetPrefab?         Prefab,
    PlacementPoint[]   ControlPoints,
    Bezier4x3?         Curve = null
);

/// <summary>
/// Fired after an existing road segment is replaced/upgraded with a new type.
/// </summary>
/// <param name="Prefab">The new road prefab applied, or null if unavailable.</param>
/// <param name="ControlPoints">The control points of the upgraded segment.</param>
public record RoadUpgradedEvent(
    NetPrefab?       Prefab,
    PlacementPoint[] ControlPoints,
    Bezier4x3?       Curve = null
);

// ---------------------------------------------------------------------------
// Demolish
// ---------------------------------------------------------------------------

/// <summary>
/// Fired after the bulldoze tool commits a demolition.
/// </summary>
/// <param name="Mode">Whether main elements, sub-elements, or everything was bulldozed.</param>
/// <param name="Prefab">The prefab of the bulldozed object, or null if unavailable.</param>
/// <param name="Position">World-space position where the bulldoze was applied.</param>
public record ObjectDemolishedEvent(
    BulldozeToolSystem.Mode Mode,
    BulldozePrefab?         Prefab,
    float3                  Position
);

// ---------------------------------------------------------------------------
// Zoning
// ---------------------------------------------------------------------------

/// <summary>
/// Fired after a zone paint/fill/marquee action is committed.
/// </summary>
/// <param name="Mode">FloodFill, Marquee, or Paint.</param>
/// <param name="Prefab">The zone type prefab applied, or null if dezoning.</param>
/// <param name="StartPosition">Where the zone action began (first click/touch).</param>
/// <param name="EndPosition">Where the zone action ended (release point).</param>
/// <param name="MarqueeDirection">
/// Marquee mode only: the road-aligned forward direction of the block under the
/// start click (<c>Block.m_Direction</c>).  Used to reconstruct the oriented
/// marquee rectangle on replay.  Zero for all other modes.
/// </param>
/// <param name="PaintedCellPositions">
/// Paint mode only: world-space centre positions of every cell that was
/// painted/dezoned during this drag stroke.  Populated by intercepting
/// <c>ApplyZonesSystem</c> before it destroys the per-frame Temp block
/// entities.  Null for single-click Paint, FloodFill, and Marquee.
/// When non-null, <c>ZoneApplier</c> replays these exact cells rather than
/// approximating from start/end cursor endpoints.
/// </param>
public record ZoneChangedEvent(
    ZoneToolSystem.Mode Mode,
    ZonePrefab?         Prefab,
    float3              StartPosition,
    float3              EndPosition,
    float2              MarqueeDirection     = default,
    float3[]?           PaintedCellPositions = null
);

// ---------------------------------------------------------------------------
// Buildings & objects
// ---------------------------------------------------------------------------

/// <summary>
/// Fired after a building or prop is successfully placed.
/// </summary>
/// <param name="Mode">Create, Brush, Stamp, Line, etc.</param>
/// <param name="Prefab">The object prefab placed, or null if unavailable.</param>
/// <param name="Position">World-space placement position.</param>
/// <param name="Rotation">Placement rotation.</param>
public record BuildingPlacedEvent(
    ObjectToolSystem.Mode Mode,
    ObjectPrefab?         Prefab,
    float3                Position,
    quaternion            Rotation
);

/// <summary>
/// Fired after a building service upgrade (extension) is applied.
/// </summary>
/// <param name="TargetEntity">The building entity that was upgraded.</param>
/// <param name="Prefab">The upgrade/extension prefab applied, or null.</param>
/// <param name="Position">World-space position of the upgraded building.</param>
public record BuildingUpgradedEvent(
    Entity        TargetEntity,
    ObjectPrefab? Prefab,
    float3        Position
);

// ---------------------------------------------------------------------------
// City management (no coordinates — these are entity-based)
// ---------------------------------------------------------------------------

/// <summary>
/// Fired just before a service budget percentage is changed.
/// </summary>
/// <param name="ServicePrefab">The entity of the service whose budget changed.</param>
/// <param name="NewPercentage">The new budget percentage (0–200 typically).</param>
public record BudgetChangedEvent(
    Entity ServicePrefab,
    int    NewPercentage
);

/// <summary>
/// Fired just before a policy is enabled, disabled, or adjusted.
/// </summary>
/// <param name="Target">The entity the policy is applied to (city, district, route, etc.).</param>
/// <param name="Policy">The policy entity being modified.</param>
/// <param name="Active">Whether the policy is being turned on or off.</param>
/// <param name="Adjustment">The slider value, if this policy has one (0 if not applicable).</param>
public record PolicyChangedEvent(
    Entity Target,
    Entity Policy,
    bool   Active,
    float  Adjustment
);
