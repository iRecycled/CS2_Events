using System.Reflection;
using Game.Tools;
using HarmonyLib;
using Unity.Mathematics;

namespace CS2Hooks.Events.Patches;

[HarmonyPatch(typeof(BulldozeToolSystem), "Apply")]
static class BulldozeApplyPatch
{
    static readonly FieldInfo? LastRaycastField = typeof(BulldozeToolSystem)
        .GetField("m_LastRaycastPoint", BindingFlags.NonPublic | BindingFlags.Instance);

    static BulldozeToolSystem.Mode _mode;
    static float3                  _position;

    static void Prefix(BulldozeToolSystem __instance)
    {
        _mode = __instance.actualMode;

        if (LastRaycastField?.GetValue(__instance) is ControlPoint cp)
            _position = cp.m_Position;
        else
            _position = default;
    }

    static void Postfix(BulldozeToolSystem __instance)
    {
        if (__instance.applyMode != ApplyMode.Apply)
            return;

        EventBus.FireDemolished(new ObjectDemolishedEvent(
            _mode,
            __instance.prefab,
            _position
        ));
    }
}
