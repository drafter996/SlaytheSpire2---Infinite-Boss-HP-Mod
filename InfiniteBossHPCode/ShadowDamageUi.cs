using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace InfiniteBossHP.InfiniteBossHPCode;

// A visual-only power icon. It is never added to Creature.Powers, so it cannot
// change Strength, be cleansed, or trigger another mod's power hooks.
internal static class ShadowDamageUi
{
    private const string TitleKey = "INFINITE_BOSS_HP_SHADOW_DAMAGE.title";
    private const string DescriptionKey = "INFINITE_BOSS_HP_SHADOW_DAMAGE.description";

    private sealed class UiState
    {
        internal required NPowerContainer Container;
        internal NPower? Icon;
        internal PowerModel? Model;
    }

    private sealed class TooltipMarker { }

    private static readonly ConditionalWeakTable<Creature, UiState> UiStates = new();
    private static readonly ConditionalWeakTable<PowerModel, TooltipMarker> TooltipModels = new();
    private static readonly FieldInfo AmountField = AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly PropertyInfo OwnerProperty = AccessTools.Property(typeof(PowerModel), "Owner");
    private static readonly MethodInfo AmountChangedMethod =
        AccessTools.Method(typeof(PowerModel), "InvokeDisplayAmountChanged");
    private static readonly FieldInfo CreatureField = AccessTools.Field(typeof(NPowerContainer), "_creature");
    private static readonly FieldInfo PowerNodesField = AccessTools.Field(typeof(NPowerContainer), "_powerNodes");
    private static readonly MethodInfo UpdatePositionsMethod =
        AccessTools.Method(typeof(NPowerContainer), "UpdatePositions");

    internal static void Register(NPowerContainer container, Creature creature)
    {
        // Multi-stage bosses become eligible after their UI container is bound.
        if (!creature.IsMonster)
            return;

        var state = UiStates.GetValue(creature, _ => new UiState { Container = container });
        state.Container = container;
        Refresh(creature);
    }

    internal static void Refresh(Creature creature)
    {
        if (!UiStates.TryGetValue(creature, out var state) ||
            !GodotObject.IsInstanceValid(state.Container))
            return;

        if (creature.IsDead || creature.CombatState == null ||
            InfiniteBossFight.IsEndingShadowFight(creature))
        {
            Hide(state);
            return;
        }

        // At the transition the shadow has 1 HP until its stun restores it.
        // Do not show a nearly-full counter during that interlude.
        if (!InfiniteBossFight.IsInfinite(creature) ||
            InfiniteBossFight.IsTransitionPending(creature))
            return;

        if (state.Icon == null || !GodotObject.IsInstanceValid(state.Icon))
            Create(state, creature);

        if (state.Model == null)
            return;

        int damage = Math.Clamp(creature.MaxHp - creature.CurrentHp, 0, InfiniteBossFight.InfiniteHp);
        if ((int)AmountField.GetValue(state.Model)! != damage)
        {
            // This model is visual only. SetAmount would emit gameplay power
            // changes, so update its display field and event directly.
            AmountField.SetValue(state.Model, damage);
            AmountChangedMethod.Invoke(state.Model, null);
        }
    }

    private static void Create(UiState state, Creature creature)
    {
        var model = ModelDb.Power<StrengthPower>().ToMutable();
        OwnerProperty.GetSetMethod(true)!.Invoke(model, new object[] { creature });
        TooltipModels.GetValue(model, _ => new TooltipMarker());

        var icon = NPower.Create(model);
        icon.Container = state.Container;
        state.Model = model;
        state.Icon = icon;
        state.Container.AddChild(icon);
        LayoutIcon(state);
        UpdatePositionsMethod.Invoke(state.Container, null);
    }

    private static void Hide(UiState state)
    {
        if (state.Icon != null && GodotObject.IsInstanceValid(state.Icon))
            state.Icon.QueueFree();
        state.Icon = null;
        state.Model = null;
        if (GodotObject.IsInstanceValid(state.Container))
            UpdatePositionsMethod.Invoke(state.Container, null);
    }

    private static void LayoutIcon(UiState state)
    {
        if (state.Icon == null)
            return;
        state.Icon.Position = new Vector2(
            Math.Max(0, (state.Container.Size.X - state.Icon.Size.X) / 2), 0);
    }

    internal static void LayoutNormalPowers(NPowerContainer container)
    {
        var creature = (Creature?)CreatureField.GetValue(container);
        if (creature == null || !UiStates.TryGetValue(creature, out var state) ||
            state.Icon == null || !GodotObject.IsInstanceValid(state.Icon))
            return;

        LayoutIcon(state);
        float rowHeight = Math.Max(state.Icon.Size.Y, 48f);
        foreach (NPower power in (IEnumerable)PowerNodesField.GetValue(container)!)
            power.Position += new Vector2(0, rowHeight);
    }

    internal static bool IsTooltipModel(PowerModel model) =>
        TooltipModels.TryGetValue(model, out _);

    internal static IEnumerable<IHoverTip> GetTooltip(PowerModel model) =>
        new IHoverTip[]
        {
            new HoverTip(new LocString("powers", TitleKey),
                new LocString("powers", DescriptionKey), model.Icon)
        };
}

[HarmonyPatch(typeof(NPowerContainer), nameof(NPowerContainer.SetCreature))]
internal static class ShadowDamageUiAttachPatch
{
    private static void Postfix(NPowerContainer __instance, Creature __0) =>
        ShadowDamageUi.Register(__instance, __0);
}

[HarmonyPatch(typeof(NPowerContainer), "UpdatePositions")]
internal static class ShadowDamageUiLayoutPatch
{
    private static void Postfix(NPowerContainer __instance) =>
        ShadowDamageUi.LayoutNormalPowers(__instance);
}

[HarmonyPatch(typeof(PowerModel), "get_HoverTips")]
internal static class ShadowDamageUiTooltipPatch
{
    private static void Postfix(PowerModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (ShadowDamageUi.IsTooltipModel(__instance))
            __result = ShadowDamageUi.GetTooltip(__instance);
    }
}

[HarmonyPatch(typeof(Creature), "SetCurrentHpInternal")]
internal static class ShadowDamageUiHealingPatch
{
    private static void Postfix(Creature __instance) => ShadowDamageUi.Refresh(__instance);
}
