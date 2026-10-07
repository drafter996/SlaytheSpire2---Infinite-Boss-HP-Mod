using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace InfiniteBossHP.InfiniteBossHPCode;

// This file is loaded by MainFile.Initialize() through harmony.PatchAll(assembly).
internal static class InfiniteBossFight
{
    // StS2 v0.111.0 clamps monster maximum HP to this value itself.
    internal const int InfiniteHp = 999_999_999;

    internal sealed class PhaseState
    {
        internal bool InfinitePhase;
        internal bool AwaitingRecovery;
        internal int TransitionRound = -1;
        internal int BudgetRound = -1;
        internal int StartingHp;
        internal int HpLostThisRound;
        internal bool EndingShadowFight;
    }

    private static readonly ConditionalWeakTable<Creature, PhaseState> PhaseStates = new();
    private static readonly ConditionalWeakTable<Creature, ShadowDeathMarker> ShadowDeaths = new();
    private static readonly ConditionalWeakTable<IRunState, VictoryMarker> ConvertedRuns = new();
    private static readonly FieldInfo RespawnsField = AccessTools.Field(typeof(TestSubject), "_respawns");

    private sealed class VictoryMarker { }

    private sealed class ShadowDeathMarker
    {
        internal Creature? Boss;
    }

    internal static bool IsEndingShadowFight(Creature creature)
    {
        return PhaseStates.TryGetValue(creature, out var phase) && phase.EndingShadowFight;
    }

    internal static void MarkShadowDeath(Creature player, Creature boss)
    {
        ShadowDeaths.GetValue(player, _ => new ShadowDeathMarker()).Boss = boss;
    }

    internal static bool TryTakeShadowDeath(Creature player, out Creature? boss)
    {
        if (ShadowDeaths.TryGetValue(player, out var marker) && marker.Boss != null)
        {
            boss = marker.Boss;
            ShadowDeaths.Remove(player);
            return true;
        }

        boss = null;
        return false;
    }

    internal static void MarkShadowFightEnding(Creature boss)
    {
        GetState(boss).EndingShadowFight = true;
    }

    internal static void MarkConvertedRun(IRunState run)
    {
        ConvertedRuns.GetValue(run, _ => new VictoryMarker());
    }

    internal static bool WasConvertedRun(IRunState run)
    {
        return ConvertedRuns.TryGetValue(run, out _);
    }

    internal static PhaseState GetState(Creature creature)
    {
        return PhaseStates.GetValue(creature, _ => new PhaseState());
    }

    internal static bool IsTarget(Creature creature)
    {
        ICombatState? combat = creature.CombatState;
        if (combat == null || !creature.IsMonster)
            return false;

        IRunState run = combat.RunState;
        if (run.CurrentActIndex != 2 || run.CurrentRoom is not CombatRoom { RoomType: RoomType.Boss })
            return false;

        // On ascension 10 the second boss map point is the only eligible fight.
        var finalBossPoint = run.Map.SecondBossMapPoint ?? run.Map.BossMapPoint;
        if (finalBossPoint == null || !run.CurrentMapCoord.HasValue ||
            !run.CurrentMapCoord.Value.Equals(finalBossPoint.coord))
            return false;

        if (combat.Encounter is AeonglassBoss)
            return creature.Monster is Aeonglass;

        if (combat.Encounter is QueenBoss)
            return creature.Monster is Queen;

        if (combat.Encounter is TestSubjectBoss && creature.Monster is TestSubject subject)
            return (int)RespawnsField.GetValue(subject)! >= 2;

        return false;
    }

    internal static bool IsInfinite(Creature creature)
    {
        var phase = GetState(creature);
        if (phase.InfinitePhase)
            return true;

        // Reconstruct the marker after loading a combat save.
        if (IsTarget(creature) && creature.MaxHp == InfiniteHp &&
            creature.HpDisplay is HpDisplay.InfiniteWithNumbers or HpDisplay.InfiniteWithoutNumbers)
        {
            phase.InfinitePhase = true;
            if (creature.HpDisplay == HpDisplay.InfiniteWithoutNumbers)
            {
                // The transition marker survives a combat save. Its original
                // stun callback may not, so restore the shadow on load.
                phase.AwaitingRecovery = true;
                _ = CompleteRecovery(creature);
            }
            return true;
        }

        return false;
    }

    internal static bool TransitionedThisRound(Creature creature)
    {
        return PhaseStates.TryGetValue(creature, out var phase) &&
               phase.InfinitePhase && creature.CombatState?.RoundNumber == phase.TransitionRound;
    }

    internal static bool IsTransitionPending(Creature creature)
    {
        var wasPendingOnLoad = creature.MaxHp == InfiniteHp &&
            creature.HpDisplay == HpDisplay.InfiniteWithoutNumbers;
        IsInfinite(creature);
        return wasPendingOnLoad || GetState(creature).AwaitingRecovery;
    }

    internal static decimal RemainingTurnBudget(Creature creature)
    {
        var phase = GetState(creature);
        int round = creature.CombatState!.RoundNumber;
        if (phase.BudgetRound != round)
        {
            phase.BudgetRound = round;
            phase.StartingHp = creature.CurrentHp;
            phase.HpLostThisRound = 0;
        }

        // Same cumulative calculation as HardenedShellPower, with a cap derived
        // from the boss's HP when this round's first HP loss is attempted.
        return Math.Max(0, phase.StartingHp - 1 - phase.HpLostThisRound);
    }

    internal static void RecordHpLost(Creature creature, int hpBefore)
    {
        GetState(creature).HpLostThisRound += Math.Max(0, hpBefore - creature.CurrentHp);
    }

    internal static void EnterInfinitePhase(Creature creature)
    {
        var phase = GetState(creature);
        phase.InfinitePhase = true;
        phase.AwaitingRecovery = true;
        phase.TransitionRound = creature.CombatState!.RoundNumber;

        // SetMaxHpInternal changes no powers and leaves the current HP at 1.
        AccessTools.Method(typeof(Creature), "SetMaxHpInternal")
            .Invoke(creature, new object[] { (decimal)InfiniteHp });
        // A distinct display mode persists the not-yet-healed transition
        // across saves. The numeric infinite bar returns after recovery.
        creature.HpDisplay = HpDisplay.InfiniteWithoutNumbers;

        // Stun inserts one no-action move, then resumes the original move order.
        // Healing takes place in that inserted move, preserving every existing debuff.
        _ = CreatureCmd.Stun(creature, (_ignored) => CompleteRecovery(creature),
            string.Empty);
    }

    private static async Task CompleteRecovery(Creature creature)
    {
        if (creature.IsDead || creature.CombatState == null)
            return;

        await CreatureCmd.SetCurrentHp(creature, creature.MaxHp);
        if (creature.IsDead || creature.CombatState == null)
            return;

        GetState(creature).AwaitingRecovery = false;
        creature.HpDisplay = HpDisplay.InfiniteWithNumbers;
        ShadowDamageUi.Refresh(creature);
    }
}

[HarmonyPatch(typeof(Creature), "LoseHpInternal")]
internal static class BossDamagePatch
{
    private static void Prefix(Creature __instance, ref decimal __0, out int __state)
    {
        __state = __instance.CurrentHp;
        if (!InfiniteBossFight.IsTarget(__instance))
            return;

        // A scripted shadow disappearance must bypass the boss's HP floor.
        if (InfiniteBossFight.IsEndingShadowFight(__instance))
            return;

        if (InfiniteBossFight.TransitionedThisRound(__instance) ||
            InfiniteBossFight.IsTransitionPending(__instance))
        {
            __0 = 0m;
            return;
        }

        // In the shadow phase, filling the hidden HP budget defeats the boss.
        // This amount has already passed through block and damage modifiers;
        // letting the lethal HP loss through keeps the game's normal death,
        // encounter and victory handling (including Queen and Test Subject).
        if (InfiniteBossFight.IsInfinite(__instance) &&
            __instance.CurrentHp > 0 && __0 >= __instance.CurrentHp)
        {
            InfiniteBossFight.MarkShadowFightEnding(__instance);
            return;
        }

        // The cumulative limit follows the coral enemy's HardenedShellPower.
        // The second limit also protects the boss if HP changed during the turn.
        __0 = Math.Min(__0, Math.Min(InfiniteBossFight.RemainingTurnBudget(__instance),
            Math.Max(0, __instance.CurrentHp - 1)));
    }

    private static void Postfix(Creature __instance, int __state)
    {
        if (!InfiniteBossFight.IsTarget(__instance))
            return;

        if (InfiniteBossFight.IsEndingShadowFight(__instance))
            return;

        InfiniteBossFight.RecordHpLost(__instance, __state);
        if (!InfiniteBossFight.IsInfinite(__instance) && __instance.CurrentHp == 1)
        {
            InfiniteBossFight.EnterInfinitePhase(__instance);
        }
        ShadowDamageUi.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ShouldDie))]
internal static class ShadowPreventsFinalPlayerDeathPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ICombatState? __1, Creature __2,
        ref AbstractModel? __3, ref bool __result)
    {
        // Existing death prevention (including Fairy in a Bottle) wins first.
        if (!__result || __1 == null || !__2.IsPlayer || __2.CurrentHp > 0 ||
            __1.Players.Any(player => player.Creature.CurrentHp > 0))
            return;

        var shadow = __1.Enemies.FirstOrDefault(InfiniteBossFight.IsInfinite);
        if (shadow == null)
            return;

        InfiniteBossFight.MarkShadowDeath(__2, shadow);
        __3 = null;
        __result = false;
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPreventingDeath))]
internal static class ShadowRevivesPlayerAndEndsFightPatch
{
    private static bool Prefix(Creature __3, ref Task __result)
    {
        if (!InfiniteBossFight.TryTakeShadowDeath(__3, out var shadow) || shadow == null)
            return true;

        __result = ReviveAndDismissShadow(__3, shadow);
        return false;
    }

    private static async Task ReviveAndDismissShadow(Creature player, Creature shadow)
    {
        // Prevent the final player's death and bring them back at one HP.
        // Other dead players use the game's one-HP post-combat revival.
        await CreatureCmd.Heal(player, 1m, true);

        if (shadow.IsDead || shadow.CombatState == null)
            return;

        InfiniteBossFight.MarkConvertedRun(shadow.CombatState.RunState);
        InfiniteBossFight.MarkShadowFightEnding(shadow);
        await CreatureCmd.Kill(shadow, true);
    }
}

[HarmonyPatch]
internal static class BossLossBecomesVictoryPatch
{
    private static readonly FieldInfo TurnStateField = AccessTools.Field(typeof(CombatManager), "_turnState");
    private static readonly MethodInfo EndCombatMethod = AccessTools.Method(typeof(CombatManager),
        "EndCombatInternal", Type.EmptyTypes);

    private static IEnumerable<MethodBase> TargetMethods()
    {
        return AccessTools.GetDeclaredMethods(typeof(CombatManager))
            .Where(method => method.Name == "CheckWinCondition" &&
                             method.ReturnType == typeof(Task<bool>));
    }

    private static bool Prefix(CombatManager __instance, ref Task<bool> __result)
    {
        object? turnState = TurnStateField.GetValue(__instance);
        if (turnState == null)
            return true;

        var type = turnState.GetType();
        var pendingLoss = type.GetProperty("PendingLoss")!;
        if (pendingLoss.GetValue(turnState) == null)
            return true;

        var combat = (ICombatState)type.GetProperty("State")!.GetValue(turnState)!;
        if (!combat.Enemies.Any(InfiniteBossFight.IsInfinite))
            return true;

        __result = FinishAsVictory(__instance, turnState, pendingLoss);
        return false;
    }

    private static async Task<bool> FinishAsVictory(
        CombatManager manager, object turnState, PropertyInfo pendingLoss)
    {
        var combat = (ICombatState)turnState.GetType().GetProperty("State")!.GetValue(turnState)!;
        InfiniteBossFight.MarkConvertedRun(combat.RunState);
        pendingLoss.SetValue(turnState, null);
        await (Task)EndCombatMethod.Invoke(manager, null)!;
        return true;
    }
}

[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
internal static class ConvertedBossVictoryScreenPatch
{
    private static readonly FieldInfo ScreenRunStateField =
        AccessTools.Field(typeof(NGameOverScreen), "_runState");
    private static readonly PropertyInfo HistoryWinProperty =
        AccessTools.Property(typeof(RunHistory), "Win");

    private static void Prefix(NGameOverScreen __instance)
    {
        var runManager = RunManager.Instance;
        if (ScreenRunStateField.GetValue(__instance) is not IRunState run ||
            !InfiniteBossFight.WasConvertedRun(run))
            return;

        // The live screen can open while CurrentRoom is still the boss combat room.
        // In that case NGameOverScreen._Ready defaults to Win=false even though
        // EndCombatInternal has already recorded a victory. Give it the same
        // victory result before it calculates the banner and score.
        if (runManager.History == null)
            runManager.History = new RunHistory { Win = true };
        else
            HistoryWinProperty.SetValue(runManager.History, true);
    }
}
