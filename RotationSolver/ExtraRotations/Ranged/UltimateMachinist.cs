using System.ComponentModel;
using Dalamud.Interface.Colors;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("Ultimate Machinist", CombatType.PvE, GameVersion = "7.4")]
[SourceCode(Path = "main/ExtraRotations/Ranged/UltimateMachinist.cs")]
[ExtraRotation]

public sealed class UltimateMachinist : MachinistRotation
{
    #region Config Options
    
    [RotationConfig(CombatType.PvE, Name = "Burst Mode")]
    public BurstMode BurstSetting { get; set; } = BurstMode.WithParty;

    [RotationConfig(CombatType.PvE, Name = "Potion Usage")]
    public PotionMode PotionSetting { get; set; } = PotionMode.Every2Min;

    [RotationConfig(CombatType.PvE, Name = "Pool Skills for Burst")]
    public bool PoolSkillsForBurst { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use 10x Heat Blast Optimization")]
    public bool Use10xHB { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Seconds before burst to start pooling")]
    [Range(10, 30, ConfigUnitType.Seconds, 1)]
    public float PoolingWindow { get; set; } = 15f;

    public enum BurstMode : byte
    {
        [Description("Solo (Every 2 min)")] Solo,
        [Description("With Party Buffs")] WithParty,
        [Description("Manual Only")] Manual
    }

    public enum PotionMode : byte
    {
        [Description("Never")] Never,
        [Description("Opener Only")] OpenerOnly,
        [Description("Every 2 Minutes")] Every2Min,
        [Description("With Party (when others use)")] WithParty
    }

    #endregion

    #region Burst Status IDs
    
    private static readonly HashSet<uint> BurstStatusIds =
    [
        (uint)StatusID.Divination,
        (uint)StatusID.Brotherhood,
        (uint)StatusID.BattleLitany,
        (uint)StatusID.ArcaneCircle,
        (uint)StatusID.StarryMuse,
        (uint)StatusID.Embolden,
        (uint)StatusID.SearingLight,
        (uint)StatusID.BattleVoice,
        (uint)StatusID.TechnicalFinish,
        (uint)StatusID.RadiantFinale
    ];

    #endregion

    #region Burst Timing Detection

    /// <summary>
    /// Check if any party member has burst buffs active
    /// </summary>
    public static bool IsPartyBursting => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => BurstStatusIds.Contains(status.StatusId)) == true
    ) == true;

    /// <summary>
    /// Check if any party member has Medicated (potion) status
    /// </summary>
    public static bool IsPartyMedicated => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => status.StatusId == (uint)StatusID.Medicated) == true
    ) == true;

    /// <summary>
    /// Check if we're in opener phase (first 15 seconds of combat)
    /// </summary>
    public bool IsInOpener => CombatTime > 0 && CombatTime < 15 && InCombat;

    /// <summary>
    /// Current position in 2-minute cycle (0-120 seconds)
    /// </summary>
    public float CyclePosition => CombatTime > 0 ? (CombatTime % 120f) : 0f;

    /// <summary>
    /// Check if we're in the 2-minute burst window (first 20 seconds of cycle)
    /// </summary>
    public bool IsIn2MinWindow => CombatTime > 0 && CyclePosition < 20f;

    /// <summary>
    /// Seconds until next 2-minute burst window
    /// </summary>
    public float SecondsUntilBurst => CyclePosition < 20f ? 0f : (120f - CyclePosition);

    /// <summary>
    /// Check if burst window is approaching (within pooling window)
    /// </summary>
    public bool IsBurstApproaching => SecondsUntilBurst <= PoolingWindow && SecondsUntilBurst > 0;

    /// <summary>
    /// Determine if we should burst based on settings
    /// </summary>
    public bool ShouldBurst => BurstSetting switch
    {
        BurstMode.WithParty => IsPartyBursting || IsIn2MinWindow,
        BurstMode.Solo => IsIn2MinWindow,
        BurstMode.Manual => false,
        _ => false
    };

    /// <summary>
    /// Check if this is the first 2-min burst (optimal for 10xHB with potion)
    /// </summary>
    public bool IsFirst2MinBurst => CombatTime >= 110 && CombatTime <= 140;

    #endregion

    #region Skill Pooling Logic

    /// <summary>
    /// Should hold Reassemble for burst? Keep at least 1 charge
    /// </summary>
    public bool ShouldHoldReassemble => PoolSkillsForBurst
        && IsBurstApproaching
        && ReassemblePvE.Cooldown.CurrentCharges >= 1
        && !ReassembleWillCap;

    /// <summary>
    /// Should hold Drill for burst? Keep 1 charge for burst
    /// </summary>
    public bool ShouldHoldDrill => PoolSkillsForBurst
        && IsBurstApproaching
        && DrillPvE.Cooldown.CurrentCharges < 2
        && !DrillWillCap;

    /// <summary>
    /// Should hold Chainsaw for burst?
    /// </summary>
    public bool ShouldHoldChainsaw => PoolSkillsForBurst
        && IsBurstApproaching
        && !HasExcavatorReady
        && ChainSawPvE.Cooldown.IsCoolingDown;

    /// <summary>
    /// Should hold Barrel Stabilizer for burst?
    /// </summary>
    public bool ShouldHoldBarrelStabilizer => PoolSkillsForBurst
        && IsBurstApproaching
        && !HasHypercharged
        && !HasFullMetalMachinist;

    #endregion

    #region Cap Prevention

    /// <summary>
    /// Reassemble about to cap (2 charges or 1 charge with less than 5s to next)
    /// </summary>
    public bool ReassembleWillCap => ReassemblePvE.Cooldown.CurrentCharges == 2
        || (ReassemblePvE.Cooldown.CurrentCharges == 1
            && ReassemblePvE.Cooldown.RecastTimeRemainOneCharge < 5f);

    /// <summary>
    /// Drill at max charges
    /// </summary>
    public bool DrillWillCap => DrillPvE.Cooldown.CurrentCharges == 2;

    /// <summary>
    /// Battery about to cap
    /// </summary>
    public bool BatteryWillCap => Battery >= 90;

    /// <summary>
    /// Heat about to cap (only use if not approaching burst)
    /// </summary>
    public bool HeatWillCap => Heat >= 95 && !IsBurstApproaching;

    #endregion

    #region Queen Timing

    /// <summary>
    /// Determine optimal Queen summon timing
    /// </summary>
    public bool ShouldSummonQueen
    {
        get
        {
            if (Battery < 50 || IsRobotActive) return false;

            // During burst with 100 battery - summon immediately
            if (ShouldBurst && Battery == 100) return true;

            // Approaching burst with 100 battery - summon now so Queen finishes during burst
            if (IsBurstApproaching && Battery >= 90) return true;

            // Cap prevention - always summon if about to cap
            if (BatteryWillCap) return true;

            // Between bursts: summon at 50-60 to avoid capping
            if (!IsBurstApproaching && !ShouldBurst && Battery >= 50 && Battery <= 70)
            {
                // Only if we won't have 100 for next burst
                return SecondsUntilBurst > 30;
            }

            return false;
        }
    }

    #endregion

    #region 10x Heat Blast Check

    /// <summary>
    /// Check if we can execute 10x Heat Blast rotation
    /// Requirements: FMF ready, tools aligned, enough heat
    /// </summary>
    public bool Can10xHB => Use10xHB
        && HasFullMetalMachinist
        && !BarrelStabilizerPvE.Cooldown.IsCoolingDown
        && (Heat >= 50 || HasHypercharged);

    /// <summary>
    /// Check if all tools are ready for burst
    /// </summary>
    public bool ToolsAligned => !AirAnchorPvE.Cooldown.IsCoolingDown
        && !ChainSawPvE.Cooldown.IsCoolingDown
        && DrillPvE.Cooldown.CurrentCharges >= 1;

    #endregion

    #region Potion Logic

    /// <summary>
    /// Determine if potion should be used
    /// </summary>
    public bool ShouldUsePotion
    {
        get
        {
            if (PotionSetting == PotionMode.Never) return false;

            // Already medicated
            if (StatusHelper.PlayerHasStatus(true, StatusID.Medicated)) return false;

            // Opener (first 5 seconds)
            if (IsInOpener && CombatTime > 2 && CombatTime < 8)
                return true;

            // Opener only mode - don't use after opener
            if (PotionSetting == PotionMode.OpenerOnly)
                return false;

            // Party sync mode
            if (PotionSetting == PotionMode.WithParty)
                return IsPartyMedicated && ShouldBurst;

            // Every 2 min mode - use at burst start with tools ready
            if (PotionSetting == PotionMode.Every2Min)
            {
                return ShouldBurst
                    && CyclePosition < 5f
                    && ToolsAligned;
            }

            return false;
        }
    }

    #endregion

    #region Hypercharge Logic

    /// <summary>
    /// Safe to use Hypercharge? Check tool cooldowns
    /// </summary>
    public bool CanSafelyHypercharge
    {
        get
        {
            if (IsOverheated) return false;
            if (Heat < 50 && !HasHypercharged) return false;

            // Check tools have > 8s cooldown remaining
            float drillCD = DrillPvE.Cooldown.RecastTimeRemainOneCharge;
            float airAnchorCD = AirAnchorPvE.Cooldown.RecastTimeRemainOneCharge;
            float chainsawCD = ChainSawPvE.Cooldown.RecastTimeRemainOneCharge;

            // All tools must have > 8s or be ready (we'll use them first)
            bool toolsSafe = (drillCD > 8f || drillCD == 0)
                && (airAnchorCD > 8f || airAnchorCD == 0)
                && (chainsawCD > 8f || chainsawCD == 0);

            // Don't hypercharge if approaching burst and should pool
            if (IsBurstApproaching && PoolSkillsForBurst && !ShouldBurst)
                return false;

            return toolsSafe || ShouldBurst;
        }
    }

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        // Pre-pull Reassemble
        if (remainTime < 5f && remainTime > 3f)
        {
            if (ReassemblePvE.CanUse(out var act)) return act;
        }

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region oGCD Logic

    [RotationDesc(ActionID.ReassemblePvE, ActionID.WildfirePvE, ActionID.BarrelStabilizerPvE,
        ActionID.HyperchargePvE, ActionID.GaussRoundPvE, ActionID.RicochetPvE)]
    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        act = null;

        // Potion usage
        if (ShouldUsePotion && InCombat && HasHostilesInRange)
        {
            if (UseBurstMedicine(out act)) return true;
        }

        // Reassemble before tools (Air Anchor > Chainsaw > Drill priority)
        if (!HasReassembled && InCombat)
        {
            bool nextIsTool = nextGCD.IsTheSameTo(true, AirAnchorPvE, ChainSawPvE, DrillPvE, ExcavatorPvE);
            
            if (nextIsTool)
            {
                // Use if bursting, or if cap prevention, or if not holding
                if (ShouldBurst || ReassembleWillCap || !ShouldHoldReassemble)
                {
                    if (ReassemblePvE.CanUse(out act, usedUp: ReassembleWillCap)) return true;
                }
            }
        }

        // Wildfire - use at start of burst with first Hypercharge
        if (ShouldBurst && HasHostilesInRange && !HasWildfire)
        {
            // Use Wildfire when we have heat for Hypercharge or during Overheated
            // Wildfire snapshots buffs when applied, so use early in burst
            if ((Heat >= 50 || HasHypercharged) && !IsOverheated)
            {
                if (WildfirePvE.CanUse(out act)) return true;
            }
        }

        // Barrel Stabilizer - use AFTER first Hypercharge in 10xHB
        // This gives us FMF (which we use during overheat) and free Hypercharge
        if (InCombat && HasHostilesInRange && !HasFullMetalMachinist)
        {
            // During burst: use after first Hypercharge (when overheated)
            if (ShouldBurst && IsOverheated && OverheatedStacks <= 3)
            {
                if (BarrelStabilizerPvE.CanUse(out act)) return true;
            }
            // Outside burst: use freely to avoid drift
            else if (!ShouldBurst && !ShouldHoldBarrelStabilizer)
            {
                if (BarrelStabilizerPvE.CanUse(out act)) return true;
            }
        }

        // Hypercharge - use before Barrel Stabilizer in 10xHB rotation
        if (CanSafelyHypercharge && HasHostilesInRange)
        {
            // During burst - prioritize using stored heat first
            if (ShouldBurst)
            {
                // First Hypercharge in burst (with Wildfire)
                if (Heat >= 50 && !HasHypercharged)
                {
                    if (HyperchargePvE.CanUse(out act)) return true;
                }
                // Second Hypercharge (free from Barrel Stabilizer)
                if (HasHypercharged)
                {
                    if (HyperchargePvE.CanUse(out act)) return true;
                }
            }
            // Outside burst - use if heat capping or free charge from BS
            else if (HeatWillCap || HasHypercharged)
            {
                if (HyperchargePvE.CanUse(out act)) return true;
            }
        }

        // Queen summon
        if (ShouldSummonQueen && HasHostilesInRange)
        {
            if (AutomatonQueenPvE.CanUse(out act)) return true;
        }

        // Gauss Round and Ricochet / Double Check and Checkmate
        // Use during Hypercharge, or to prevent capping charges
        if (HasHostilesInRange)
        {
            // Prioritize during overheat to get max weaves
            if (IsOverheated)
            {
                if (DoubleCheckPvE.CanUse(out act, usedUp: true)) return true;
                if (CheckmatePvE.CanUse(out act, usedUp: true)) return true;
                if (GaussRoundPvE.CanUse(out act, usedUp: true)) return true;
                if (RicochetPvE.CanUse(out act, usedUp: true)) return true;
            }
            else
            {
                // Outside overheat - use to prevent cap
                if (GaussRoundPvE.Cooldown.CurrentCharges >= 2)
                {
                    if (GaussRoundPvE.CanUse(out act)) return true;
                }
                if (RicochetPvE.Cooldown.CurrentCharges >= 2)
                {
                    if (RicochetPvE.CanUse(out act)) return true;
                }
                if (DoubleCheckPvE.Cooldown.CurrentCharges >= 2)
                {
                    if (DoubleCheckPvE.CanUse(out act)) return true;
                }
                if (CheckmatePvE.Cooldown.CurrentCharges >= 2)
                {
                    if (CheckmatePvE.CanUse(out act)) return true;
                }
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TacticianPvE, ActionID.DismantlePvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (TacticianPvE.CanUse(out act)) return true;
        if (DismantlePvE.CanUse(out act)) return true;
        return base.DefenseAreaAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        act = null;

        // During Overheat - Blazing Shot spam with FMF
        if (IsOverheated)
        {
            // Full Metal Field - AoE GCD that doesn't consume Hypercharge stacks!
            // Use this during Hypercharge for 10xHB optimization
            if (HasFullMetalMachinist)
            {
                if (FullMetalFieldPvE.CanUse(out act)) return true;
            }

            // Blazing Shot
            if (BlazingShotPvE.CanUse(out act)) return true;
        }

        // Excavator (proc from Chainsaw)
        if (HasExcavatorReady)
        {
            if (ExcavatorPvE.CanUse(out act)) return true;
        }

        // Full Metal Field outside of Hypercharge (if somehow still have buff)
        if (HasFullMetalMachinist && !IsOverheated)
        {
            if (FullMetalFieldPvE.CanUse(out act)) return true;
        }

        // Tool Priority during burst: Air Anchor > Chainsaw > Drill
        if (ShouldBurst || !IsBurstApproaching)
        {
            // Air Anchor - highest priority
            if (AirAnchorPvE.CanUse(out act)) return true;

            // Chainsaw
            if (!ShouldHoldChainsaw || ShouldBurst)
            {
                if (ChainSawPvE.CanUse(out act)) return true;
            }

            // Drill
            if (!ShouldHoldDrill || ShouldBurst || DrillWillCap)
            {
                if (DrillPvE.CanUse(out act)) return true;
            }
        }
        else
        {
            // During pooling - only use if capping
            if (DrillWillCap)
            {
                if (DrillPvE.CanUse(out act)) return true;
            }
        }

        // AoE rotation (3+ targets)
        if (SpreadShotPvE.CanUse(out act)) return true;
        if (ScattergunPvE.CanUse(out act)) return true;

        // Single target combo
        if (HeatedCleanShotPvE.CanUse(out act)) return true;
        if (HeatedSlugShotPvE.CanUse(out act)) return true;
        if (HeatedSplitShotPvE.CanUse(out act)) return true;

        // Fallback to base
        return base.GeneralGCD(out act);
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "=== Ultimate Machinist ===");
        ImGui.Text($"Combat Time: {CombatTime:F1}s");
        ImGui.Text($"Cycle Position: {CyclePosition:F1}s");
        ImGui.Text($"Seconds Until Burst: {SecondsUntilBurst:F1}s");
        ImGui.Separator();
        
        ImGui.TextColored(ImGuiColors.HealerGreen, "Burst Status:");
        ImGui.Text($"Is In 2Min Window: {IsIn2MinWindow}");
        ImGui.Text($"Is Burst Approaching: {IsBurstApproaching}");
        ImGui.Text($"Should Burst: {ShouldBurst}");
        ImGui.Text($"Is Party Bursting: {IsPartyBursting}");
        ImGui.Text($"Can 10xHB: {Can10xHB}");
        ImGui.Separator();
        
        ImGui.TextColored(ImGuiColors.DalamudOrange, "Pooling Status:");
        ImGui.Text($"Hold Reassemble: {ShouldHoldReassemble}");
        ImGui.Text($"Hold Drill: {ShouldHoldDrill}");
        ImGui.Text($"Hold Chainsaw: {ShouldHoldChainsaw}");
        ImGui.Text($"Hold Barrel Stab: {ShouldHoldBarrelStabilizer}");
        ImGui.Separator();
        
        ImGui.TextColored(ImGuiColors.DalamudRed, "Cap Prevention:");
        ImGui.Text($"Reassemble Will Cap: {ReassembleWillCap}");
        ImGui.Text($"Drill Will Cap: {DrillWillCap}");
        ImGui.Text($"Battery Will Cap: {BatteryWillCap}");
        ImGui.Text($"Heat Will Cap: {HeatWillCap}");
        ImGui.Separator();
        
        ImGui.TextColored(ImGuiColors.TankBlue, "Gauges:");
        ImGui.Text($"Heat: {Heat}");
        ImGui.Text($"Battery: {Battery}");
        ImGui.Text($"Is Overheated: {IsOverheated}");
        ImGui.Text($"Has Wildfire: {HasWildfire}");
        ImGui.Text($"Has FMF Ready: {HasFullMetalMachinist}");
        ImGui.Text($"Has Excavator Ready: {HasExcavatorReady}");
        
        base.DisplayRotationStatus();
    }

    #endregion
}
