using System.Reflection;
using Colossal.Logging;
using CS2Hooks.Actions.Apply;
using CS2Hooks.Events;
using Game;
using Game.Modding;
using Game.Tools;
using HarmonyLib;

namespace CS2Hooks;

public class CS2HooksMod : IMod
{
    public static readonly ILog Log = LogManager.GetLogger(nameof(CS2Hooks))
        .SetShowsErrorsInUI(false);

    private Harmony? _harmony;

    public void OnLoad(UpdateSystem updateSystem)
    {
        Log.Info("CS2Hooks loading...");

        VerifyPatchTargets();

        _harmony = new Harmony("community.cs2hooks");
        _harmony.PatchAll(typeof(CS2HooksMod).Assembly);

        updateSystem.UpdateAt<BuildingApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<ZoneApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<NetCourseApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<EconomyDebounceSystem>(SystemUpdatePhase.PreTool);

        // [TEST ONLY] economy/policy appliers — remove before shipping
        updateSystem.UpdateAt<TaxApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<LoanApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<ServiceFeeApplier>(SystemUpdatePhase.PreTool);
        updateSystem.UpdateAt<BudgetApplier>(SystemUpdatePhase.UIUpdate);
        updateSystem.UpdateAt<PolicyApplier>(SystemUpdatePhase.UIUpdate);

        DebugLogger.Enable();
        TestHarness.Enable();

        Log.Info("CS2Hooks loaded — patches applied.");
    }

    public void OnDispose()
    {
        _harmony?.UnpatchAll("community.cs2hooks");
        Log.Info("CS2Hooks disposed.");
    }

    /// <summary>
    /// Validates that every private method we patch still exists.
    /// Logs an error (rather than crashing) so other mods aren't broken
    /// if a game update renames something.
    /// </summary>
    private static void VerifyPatchTargets()
    {
        const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance;

        Check(typeof(NetToolSystem),                    "Apply",       priv);
        Check(typeof(NetToolSystem),                    "OnUpdate",    priv);
        Check(typeof(NetToolSystem),                    "UpdateCourse",       priv);
        Check(typeof(NetToolSystem),                    "SnapControlPoints",  priv);
        Check(typeof(NetToolSystem),                    "FixControlPoints",   priv);
        Check(typeof(BulldozeToolSystem),               "Apply",              priv);
        Check(typeof(ZoneToolSystem),                   "Apply",       priv);
        Check(typeof(ZoneToolSystem),                   "Cancel",      priv);
        Check(typeof(ApplyZonesSystem),                 "OnUpdate",
              BindingFlags.NonPublic | BindingFlags.Instance);
        Check(typeof(ObjectToolSystem),                 "Apply",       priv);
        Check(typeof(UpgradeToolSystem),                "Apply",       priv);
        Check(typeof(Game.Simulation.CityServiceBudgetSystem), "SetServiceBudget",
              BindingFlags.Public | BindingFlags.Instance);
        Check(typeof(Game.UI.InGame.PoliciesUISystem),  "SetPolicy",
              BindingFlags.Public | BindingFlags.Instance);
    }

    private static void Check(System.Type type, string method, BindingFlags flags)
    {
        if (System.Array.Find(type.GetMethods(flags), m => m.Name == method) == null)
            Log.Error($"CS2Hooks: {type.Name}.{method} not found — " +
                      "a game update may have broken this patch!");
    }
}
