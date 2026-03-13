using Game.Tools;
using HarmonyLib;

namespace CS2Hooks.Events.Patches;

/// <summary>
/// Patches LoanSystem.ChangeLoan(int) to record a debounced loan change.
/// Routes through EconomyDebounceSystem so rapid successive calls are batched
/// into a single LoanChangedEvent fired after 1 second of inactivity.
/// </summary>
[HarmonyPatch(typeof(LoanSystem), nameof(LoanSystem.ChangeLoan))]
static class LoanPatch
{
    static void Prefix(LoanSystem __instance, int amount)
    {
        int oldAmount = __instance.CurrentLoan.m_Amount;
        EconomyDebounceSystem.RecordLoanChange(oldAmount, amount);
    }
}
