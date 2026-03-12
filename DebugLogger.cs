using System;
using System.IO;
using System.Linq;

namespace CS2Hooks;

/// <summary>
/// Subscribes to all CS2Hooks events and writes them to a log file
/// that can be tailed in a terminal while the game is running.
///
/// Log location: %AppData%\..\LocalLow\Colossal Order\Cities Skylines II\CS2Hooks.log
///
/// Tail it (bash/VSCode terminal):
///   tail -f "/c/Users/$USER/AppData/LocalLow/Colossal Order/Cities Skylines II/CS2Hooks.log"
/// </summary>
internal static class DebugLogger
{
    private static string _logPath = string.Empty;

    internal static void Enable()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"..\LocalLow\Colossal Order\Cities Skylines II\CS2Hooks.log"
        );

        File.WriteAllText(_logPath,
            $"=== CS2Hooks Debug Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");

        EventBus.OnRoadPlaced += e =>
        {
            var pts = string.Join(" → ", e.ControlPoints.Select(p => p.ToString()));
            Write($"[ROAD PLACED]     Mode={e.Mode,-14} Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Points: {pts}");
        };

        EventBus.OnRoadUpgraded += e =>
        {
            var pts = string.Join(" → ", e.ControlPoints.Select(p => p.ToString()));
            Write($"[ROAD UPGRADED]   Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Points: {pts}");
        };

        EventBus.OnObjectDemolished += e =>
            Write($"[DEMOLISHED]      Mode={e.Mode,-14} Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Pos: {e.Position}");

        EventBus.OnZoneChanged += e =>
            Write($"[ZONE CHANGED]    Mode={e.Mode,-14} Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Start: {e.StartPosition}  End: {e.EndPosition}");

        EventBus.OnBuildingPlaced += e =>
            Write($"[BUILDING PLACED] Mode={e.Mode,-14} Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Pos: {e.Position}");

        EventBus.OnBuildingUpgraded += e =>
            Write($"[BLDG UPGRADED]   Target={e.TargetEntity.Index}  Prefab={e.Prefab?.name ?? "null"}{Environment.NewLine}" +
                  $"                  Pos: {e.Position}");

        EventBus.OnBudgetChanged += e =>
            Write($"[BUDGET CHANGED]  Service={e.ServicePrefab.Index} → {e.NewPercentage}%");

        EventBus.OnPolicyChanged += e =>
            Write($"[POLICY CHANGED]  Target={e.Target.Index} Policy={e.Policy.Index} " +
                  $"Active={e.Active} Adj={e.Adjustment}");

        CS2HooksMod.Log.Info($"CS2Hooks: debug log → {_logPath}");
    }

    internal static void Write(string message)
    {
        try
        {
            File.AppendAllText(_logPath,
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging crash the game
        }
    }
}
