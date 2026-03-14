# CS2Hooks — Cities Skylines 2 Event Framework

A Harmony-based mod framework for **Cities Skylines 2** that intercepts private game methods and exposes clean C# events. Other mods can subscribe to these events to react to or replay player actions — without having to understand CS2's internal ECS pipeline.

---

## Use Case

CS2's gameplay systems are built on Unity DOTS/ECS with private internal state. There is no built-in callback system for actions like placing a road, painting a zone, or changing a tax rate. CS2Hooks solves this by:

1. **Patching** the relevant private/internal methods with Harmony Prefix/Postfix hooks.
2. **Capturing** the action data (prefab, positions, values) before the game clears its own state.
3. **Firing** typed C# events on a static `EventBus` that any mod in the same AppDomain can subscribe to.
4. **Replaying** actions via applier systems that reconstruct the game pipeline — proven via round-trip test harnesses.

The primary audience is mod authors who want to build features like:
- **Undo/redo** systems
- **Blueprints / copy-paste**
- **Macro recording and playback**
- **Automation mods** that respond to player actions
- **Analytics or telemetry** for city-builder assistants

---

## Subscribing to Events

```csharp
// In your mod's OnLoad or system OnCreate:
EventBus.OnRoadPlaced += e =>
{
    Debug.Log($"Road placed: {e.Prefab?.name} mode={e.Mode}");
};

EventBus.OnBuildingPlaced += e =>
{
    Debug.Log($"Building placed: {e.Prefab?.name} at {e.Position}");
};
```

All events are `static event Action<T>` on `CS2Hooks.Events.EventBus`. Events fire on the main thread during the ECS update loop — keep handlers short.

---

## Completed Events

### Construction Tools

| Event | Fired When | Key Data |
|-------|-----------|----------|
| `OnRoadPlaced` | Road/net segment committed | Prefab, Mode (Straight/Curve/Complex), control points (position, rotation, elevation, snap entity), Bezier curve |
| `OnRoadUpgraded` | Existing road replaced with new type | Prefab, control points, Bezier curve |
| `OnObjectDemolished` | Bulldoze tool commits | Mode (Main/Sub/All), prefab, position |
| `OnZoneChanged` | Zone painted, flood-filled, or marquee-selected | Mode, zone prefab (null = dezone), start/end positions, marquee direction, per-cell positions for paint strokes |
| `OnBuildingPlaced` | Building or prop placed | Mode, prefab, world position, rotation |
| `OnBuildingUpgraded` | Service upgrade/extension applied to building | Target entity, upgrade prefab, position |
| `OnTransportLinePlaced` | Stop confirmed or route loop closed | Prefab, transport type (Bus/Train/Tram/Subway/Ship/Airplane), stop positions, snap entities, `IsComplete` flag, `IsModification` flag |
| `OnTransportLineToggled` | Transport line activated or deactivated | Route entity, policy entity, active state |

### Economy & City Management

| Event | Fired When | Key Data |
|-------|-----------|----------|
| `OnBudgetChanged` | Service budget percentage changed | Service prefab entity, old %, new % |
| `OnPolicyChanged` | Policy toggled or slider adjusted (routes, districts, city) | Target entity, policy entity, old/new active state, old/new adjustment value |
| `OnTaxRateChanged` | Area-level tax changed (Residential/Commercial/Industrial/Office) | Area type, old rate, new rate |
| `OnResourceTaxRateChanged` | Per-resource or per-job-level tax changed | Area type, resource/job index, old rate, new rate |
| `OnLoanChanged` | Loan taken or repaid | Old amount, new amount |
| `OnServiceFeeChanged` | Service fee changed (electricity, water, healthcare, etc.) | Resource type, old fee, new fee |

---

## Completed Appliers (Round-Trip Proven)

Appliers replay captured events back into the game using the same ECS pipelines CS2 uses internally. All are `GameSystemBase` instances running at `SystemUpdatePhase.PreTool`.

| Applier | What It Replays | Approach |
|---------|----------------|----------|
| `BuildingApplier` | `BuildingPlacedEvent` | Direct ECS entity creation via `ObjectData.m_Archetype` |
| `ZoneApplier` | `ZoneChangedEvent` | Direct cell-level ECS writes using zone block structures |
| `NetCourseApplier` | `RoadPlacedEvent` | 2-frame definition-entity injection → `GenerateNodes/Edges` → `ApplyNetSystem` |
| `RouteApplier` | `TransportLinePlacedEvent` | Find + delete existing route, then 2-frame `CreationDefinition + WaypointDefinition` injection |
| `BudgetApplier` | `BudgetChangedEvent` | Direct call to `CityServiceBudgetSystem.SetServiceBudget` |
| `TaxApplier` | `TaxRateChangedEvent`, `ResourceTaxRateChangedEvent` | Direct call to `TaxSystem` methods |
| `LoanApplier` | `LoanChangedEvent` | Direct call to `CityFinanceSystem` loan method |
| `ServiceFeeApplier` | `ServiceFeeChangedEvent` | Direct call to `ServiceFeeSystem` |
| `PolicyApplier` | `PolicyChangedEvent`, `TransportLineToggledEvent` | Direct call to `PoliciesUISystem.SetPolicy` |

### Key Technical Detail: 2-Frame Net/Route Pipeline

CS2's road and route placement is a 3-stage ECS pipeline:
1. **Frame N (PreTool):** Create definition entity (`CreationDefinition + NetCourse/WaypointDefinition + Updated`).
2. **Frame N (ToolUpdate):** `GenerateNodesSystem` + `GenerateEdgesSystem`/`GenerateRoutesSystem` convert definition → `Temp` entities via ECB. `ToolOutputBarrier` flushes.
3. **Frame N+1 (PreTool):** Destroy definition. Set `PendingApply = true`.
4. **Frame N+1 (ToolOutputSystem):** `NetCourseApplyPatch` intercepts, suppresses normal `applyMode` to prevent `ToolClearSystem` from deleting the newly promoted entities, then calls `UpdateSystem.Update(ApplyTool)` → `ApplyNetSystem`/`ApplyRoutesSystem` promotes Temp → permanent.

---

## What Still Needs to Be Added

The following CS2 player actions do not yet have events or appliers:

### Infrastructure Placement
- **Power lines** — `NetToolSystem` is already patched for roads; power lines use the same system but need filtering by prefab type to distinguish from roads
- **Water pipes** — same net-tool pipeline, needs separate event type
- **Other net types** — fences, pedestrian paths placed with `NetToolSystem`
- **Area/district painting** — `DistrictToolSystem.Apply` (creating/renaming/deleting districts)
- **Park/attraction boundaries** — `AreaToolSystem` for park area designation

### Transport
- **Transport line stop moved/deleted** — currently `IsModification=true` fires on `RouteToolSystem.State.Modify` but stop-deletion mid-edit is not separately tracked
- **Transport line deleted** — the bulldoze event fires but there's no dedicated `TransportLineDeletedEvent` with route metadata
- **Transport line color/name changed** — UI-level mutations via `TransportLineUISystem`
- **Vehicle deployment changes** — adding/removing vehicles assigned to a line

### Building Management
- **Building abandoned / condemned** — game-driven state changes on building entities
- **Building demolished by game** (fire, aging, etc.) — distinct from player bulldoze
- **Lot upgrade** — auto-upgrade tier changes on residential/commercial/industrial lots

### City-Level Policies & Services
- **City policies toggled** — `SetCityPolicy` is partially covered by `PolicyChangedEvent` with `Target=Entity.Null`, but city-level policies are not filtered/named distinctly
- **Service building enabled/disabled** — the on/off toggle on service buildings (fire station, police, etc.)
- **Emergency/disaster response** — disaster declarations and responses
- **Outside connections** — adding/modifying cargo/passenger outside connections

### Economy
- **City milestones** — milestone reached events
- **Bankruptcy/financial events** — automatic loan triggers
- **Grant/subsidy received** — milestone reward tracking

### Terrain & Environment
- **Terrain sculpting** — `TerrainToolSystem` (raise, lower, smooth, level)
- **Tree/vegetation placement** — `ObjectToolSystem` brush mode for trees (currently `OnBuildingPlaced` fires but there's no dedicated tree event type)
- **Water level changes** — `WaterToolSystem`

### UI / Meta
- **Save game triggered** — hook into `AutoSaveSystem` or save flow
- **Game speed changed** — pause, 1x, 2x, 3x
- **Camera significant movement** — for spatial-aware mods

---

## Architecture Overview

```
CS2_Events/
├── Mod.cs                          — IMod entry, Harmony init + patch verification
├── CS2Hooks.cs                     — static EventBus (public API for subscribers)
├── Events/
│   ├── Events.cs                   — typed event record definitions
│   ├── Debounce.cs                 — debounce helper for high-frequency slider events
│   ├── EconomyDebounceSystem.cs    — batches economy events to avoid per-tick spam
│   └── Patches/
│       ├── NetToolPatch.cs         — road placement + upgrade events
│       ├── NetApplierPatch.cs      — road replay helper (legacy)
│       ├── NetCourseApplyPatch.cs  — triggers ApplyTool for road + route replay
│       ├── BulldozePatch.cs        — demolish/bulldoze events
│       ├── ZonePatch.cs            — zone paint/fill/marquee events
│       ├── ObjectToolPatch.cs      — building/prop placement events
│       ├── UpgradeToolPatch.cs     — building service upgrade events
│       ├── BudgetPatch.cs          — service budget events
│       ├── TaxPatch.cs             — tax rate events
│       ├── LoanPatch.cs            — loan events
│       ├── ServiceFeePatch.cs      — service fee events
│       ├── PolicyPatch.cs          — policy events + transport line toggle
│       ├── RouteToolPatch.cs       — transport line placement events
│       └── DiagPatch.cs            — diagnostic logging for pipeline debugging
└── Actions/
    └── Apply/
        ├── BuildingApplier.cs      — replays BuildingPlacedEvent
        ├── ZoneApplier.cs          — replays ZoneChangedEvent
        ├── ZonePaintCapture.cs     — accumulates Paint drag cell positions
        ├── NetCourseApplier.cs     — replays RoadPlacedEvent (2-frame pipeline)
        ├── RouteApplier.cs         — replays TransportLinePlacedEvent (2-frame pipeline)
        ├── BuildingApplier.cs      — replays BuildingPlacedEvent
        ├── BudgetApplier.cs        — replays BudgetChangedEvent
        ├── TaxApplier.cs           — replays TaxRateChangedEvent
        ├── LoanApplier.cs          — replays LoanChangedEvent
        ├── ServiceFeeApplier.cs    — replays ServiceFeeChangedEvent
        └── PolicyApplier.cs        — replays PolicyChangedEvent / TransportLineToggledEvent
```

---

## Building

Requires the CS2 managed DLLs at `D:\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed\` (referenced with `<Private>false</Private>` — not bundled).

```
dotnet build
```

Output DLL goes to the standard build output directory. Copy to your CS2 mods folder to load in-game.

- Target: `netstandard2.1`, C# 11
- Harmony: `Lib.Harmony 2.3.3` (bundled with output)
- Harmony mod ID: `community.cs2hooks`
