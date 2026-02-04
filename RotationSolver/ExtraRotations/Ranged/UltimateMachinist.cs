using System.ComponentModel;
using Dalamud.Interface.Colors;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("Ultimate Machinist", CombatType.PvE, GameVersion = "7.4")]
[SourceCode(Path = "main/ExtraRotations/Ranged/UltimateMachinist.cs")]
[ExtraRotation]

public sealed class UltimateMachinist : MachinistRotation
{
    #region Config Options

    [RotationConfig(CombatType.PvE, Name = "Use burst medicine in opener")]
    private bool OpenerBurstMeds { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Use Bioblaster while moving")]
    private bool BioMove { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Only use Wildfire on Boss targets")]
    private bool WildfireBoss { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Restrict mitigations to not overlap")]
    private bool MitOverlap { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Use AirAnchor at 1 second remaining on countdown")]
    private bool AirAnchorCountdown { get; set; } = false;

    // === NEW CONFIG OPTIONS ===
    [RotationConfig(CombatType.PvE, Name = "Burst Mode")]
    public BurstMode BurstSetting { get; set; } = BurstMode.WithParty;

    [RotationConfig(CombatType.PvE, Name = "Potion Usage (4:30 cooldown)")]
    public PotionMode PotionSetting { get; set; } = PotionMode.OpenerAnd6Min;

    [RotationConfig(CombatType.PvE, Name = "Pool Skills for Burst")]
    public bool PoolSkillsForBurst { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Seconds before burst to start pooling")]
    [Range(10, 30, ConfigUnitType.Seconds, 1)]
    public float PoolingWindow { get; set; } = 15f;

    public enum BurstMode : byte
    {
        [Description("Use RSR Default (IsBurst)")] Default,
        [Description("Solo (Every 2 min)")] Solo,
        [Description("With Party Buffs")] WithParty
    }

    public enum PotionMode : byte
    {
        [Description("Never")] Never,
        [Description("Opener Only")] OpenerOnly,
        [Description("Opener + 6min burst (4:30 CD)")] OpenerAnd6Min,
        [Description("With Party (when others use)")] WithParty
    }

    #endregion

    #region Party Burst Detection

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

    public static bool IsPartyBursting => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => BurstStatusIds.Contains(status.StatusId)) == true
    ) == true;

    public static bool IsPartyMedicated => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => status.StatusId == (uint)StatusID.Medicated) == true
    ) == true;

    /// <summary>
    /// Enhanced burst check that considers party buffs
    /// </summary>
    public bool ShouldBurst => BurstSetting switch
    {
        BurstMode.Default => IsBurst,
        BurstMode.Solo => IsIn2MinWindow,
        BurstMode.WithParty => IsPartyBursting || IsIn2MinWindow,
        _ => IsBurst
    };

    public float CyclePosition => CombatTime > 0 ? (CombatTime % 120f) : 0f;
    public bool IsIn2MinWindow => CombatTime > 0 && CyclePosition < 20f;
    public float SecondsUntilBurst => CyclePosition < 20f ? 0f : (120f - CyclePosition);
    public bool IsBurstApproaching => SecondsUntilBurst <= PoolingWindow && SecondsUntilBurst > 0;

    #endregion

    #region Skill Pooling Logic

    // IMPORTANT: Pooling should NEVER cause GCD drift!
    // Only pool Reassemble (it's oGCD), never pool tool GCDs

    /// <summary>
    /// Should hold Reassemble for burst? Only if burst is very close (5s) and not capping
    /// </summary>
    public bool ShouldHoldReassemble => PoolSkillsForBurst
        && SecondsUntilBurst <= 5f  // Only very close to burst
        && SecondsUntilBurst > 0
        && ReassemblePvE.Cooldown.CurrentCharges == 1  // Only 1 charge
        && ReassemblePvE.Cooldown.RecastTimeRemainOneCharge > 10f; // Not about to cap

    // REMOVED: Drill/Chainsaw pooling - causes GCD drift!
    // MCH tools should ALWAYS be used on cooldown

    /// <summary>
    /// Should hold Barrel Stabilizer? Only if burst is imminent
    /// </summary>
    public bool ShouldHoldBarrelStabilizer => PoolSkillsForBurst
        && SecondsUntilBurst <= 5f
        && SecondsUntilBurst > 0
        && !HasHypercharged
        && !HasFullMetalMachinist;

    #endregion

    #region Potion Logic

    /// <summary>
    /// Potion has 4:30 (270s) cooldown, so use at opener and 6min burst
    /// </summary>
    public bool ShouldUsePotion
    {
        get
        {
            if (PotionSetting == PotionMode.Never) return false;
            if (StatusHelper.PlayerHasStatus(true, StatusID.Medicated)) return false;

            // Party sync mode
            if (PotionSetting == PotionMode.WithParty)
                return IsPartyMedicated && ShouldBurst;

            // Opener (first 15 seconds)
            if (CombatTime > 0 && CombatTime < 15)
                return true;

            // Opener only mode
            if (PotionSetting == PotionMode.OpenerOnly)
                return false;

            // 6min burst (4:30 CD means: opener, then 6min, then 10:30min, etc.)
            // So we use at: 0, 360s (6min), 720s (12min)...
            if (PotionSetting == PotionMode.OpenerAnd6Min)
            {
                // 6 minute windows: 360-380s, 720-740s, etc.
                float combatMod = CombatTime % 360f;
                return combatMod < 20f && CombatTime >= 350f && ShouldBurst;
            }

            return false;
        }
    }

    #endregion

    #region Queen Tracking (from MCH_Reborn)

    private readonly (byte from, byte to, int step)[] _stepPairs =
    [
        (0, 60, 0),
        (60, 90, 1),
        (90, 100, 2),
        (100, 50, 3),
        (50, 60, 4),
        (60, 100, 5),
        (100, 50, 6),
        (50, 70, 7),
        (70, 100, 8),
        (100, 50, 9),
        (50, 80, 10),
        (70, 100, 11),
        (100, 50, 12),
        (50, 60, 13)
    ];

    private int _currentStep = 0;
    private bool foundStepPair = false;
    private byte _lastTrackedSummonBatteryPower = 0;

    private void UpdateFoundStepPair()
    {
        if (_currentStep < _stepPairs.Length)
        {
            var (from, to, _) = _stepPairs[_currentStep];
            foundStepPair = (LastSummonBatteryPower == from && Battery == to);
        }
        else
        {
            foundStepPair = false;
        }
    }

    public void UpdateQueenStep()
    {
        if (_lastTrackedSummonBatteryPower != LastSummonBatteryPower)
        {
            _lastTrackedSummonBatteryPower = LastSummonBatteryPower;
            _currentStep++;
        }
    }

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        if (AirAnchorCountdown && remainTime < 1f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out IAction? act))
        {
            return act;
        }

        if (!AirAnchorCountdown && remainTime < 0.1f && AirAnchorPvE.EnoughLevel && AirAnchorPvE.CanUse(out act))
        {
            return act;
        }

        if (remainTime < 4.75f && ReassemblePvE.CanUse(out act))
        {
            return act;
        }

        // Potion in countdown
        if (AirAnchorCountdown && ShouldBurst && OpenerBurstMeds && remainTime <= 1.5f && UseBurstMedicine(out act))
        {
            return act;
        }

        if (!AirAnchorCountdown && ShouldBurst && OpenerBurstMeds && remainTime <= 1f && UseBurstMedicine(out act))
        {
            return act;
        }

        return base.CountDownAction(remainTime);
    }

    #endregion

    #region Emergency Ability

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (InCombat)
        {
            UpdateQueenStep();
            UpdateFoundStepPair();
        }

        if (HyperchargePvE.EnoughLevel)
        {
            if (!WildfirePvE.EnoughLevel)
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }
            }
            if (!FullMetalFieldPvE.EnoughLevel && (HasWildfire || (WildfirePvE.Cooldown.IsCoolingDown && Battery == 100)))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }
            }
            if (HasWildfire && IsLastAction(false, FullMetalFieldPvE))
            {
                if (HyperchargePvE.CanUse(out act, skipTTKCheck: true))
                {
                    return true;
                }
            }
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    #endregion

    #region oGCD Logic

    [RotationDesc(ActionID.TacticianPvE, ActionID.DismantlePvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (IsOverheated || HasWildfire || HasFullMetalMachinist)
        {
            return base.DefenseAreaAbility(nextGCD, out act);
        }

        if (TacticianPvE.CanUse(out act))
        {
            return true;
        }

        if (!MitOverlap || (MitOverlap && !StatusHelper.PlayerHasStatus(true, StatusID.Tactician_1951)))
        {
            if (DismantlePvE.CanUse(out act))
            {
                return true;
            }
        }

        return base.DefenseAreaAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Skip if just used Wildfire (let FMF come first)
        if (FullMetalFieldPvE.EnoughLevel && HasFullMetalMachinist && IsLastAction(false, WildfirePvE))
        {
            return base.AttackAbility(nextGCD, out act);
        }

        // === POTION (NEW) ===
        if (ShouldUsePotion && InCombat && HasHostilesInRange)
        {
            if (UseBurstMedicine(out act)) return true;
        }

        // Reassemble Logic (with pooling)
        bool isReassembleUsable =
            ReassemblePvE.Cooldown.CurrentCharges > 0 && !HasReassembled &&
            (nextGCD.IsTheSameTo(true, [ChainSawPvE, ExcavatorPvE])
            || (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, SpreadShotPvE) && ((IBaseAction)nextGCD).Target.AffectedTargets.Length >= (SpreadShotMasteryTrait.EnoughLevel ? 4 : 5))
            || nextGCD.IsTheSameTo(false, [AirAnchorPvE])
            || (!ChainSawPvE.EnoughLevel && nextGCD.IsTheSameTo(true, DrillPvE))
            || (!DrillPvE.EnoughLevel && nextGCD.IsTheSameTo(true, CleanShotPvE))
            || (!CleanShotPvE.EnoughLevel && nextGCD.IsTheSameTo(false, HotShotPvE)));

        // Modified: Don't use if holding for burst (unless capping)
        if (isReassembleUsable && (!ShouldHoldReassemble || ShouldBurst || ReassemblePvE.Cooldown.CurrentCharges == 2))
        {
            if (ReassemblePvE.CanUse(out act, usedUp: true))
            {
                return true;
            }
        }

        // Start Ricochet/Gauss cooldowns rolling
        if (!RicochetPvE.Cooldown.IsCoolingDown)
        {
            if (CheckmatePvE.EnoughLevel && CheckmatePvE.CanUse(out act))
            {
                return true;
            }
            if (!CheckmatePvE.EnoughLevel && RicochetPvE.CanUse(out act))
            {
                return true;
            }
        }
        if (!GaussRoundPvE.Cooldown.IsCoolingDown)
        {
            if (DoubleCheckPvE.EnoughLevel && DoubleCheckPvE.CanUse(out act))
            {
                return true;
            }
            if (!DoubleCheckPvE.EnoughLevel && GaussRoundPvE.CanUse(out act))
            {
                return true;
            }
        }

        // Barrel Stabilizer (with pooling)
        if (ShouldBurst || !ShouldHoldBarrelStabilizer)
        {
            if (BarrelStabilizerPvE.CanUse(out act))
            {
                return true;
            }
        }

        bool LowLevelHyperCheck = !AutoCrossbowPvE.EnoughLevel && SpreadShotPvE.CanUse(out _);

        // Wildfire logic
        if (ShouldBurst)
        {
            if (FullMetalFieldPvE.EnoughLevel)
            {
                if (Heat >= 50 || HasHypercharged)
                {
                    if (WeaponRemain < (GCDTime(1) / 2) && nextGCD.IsTheSameTo(false, FullMetalFieldPvE))
                    {
                        if (WildfirePvE.CanUse(out act))
                        {
                            if ((WildfirePvE.Target.Target.IsBossFromIcon() && WildfireBoss) || !WildfireBoss)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            if (!FullMetalFieldPvE.EnoughLevel)
            {
                if ((Heat >= 50 || HasHypercharged) && ToolChargeSoon(out _) && !LowLevelHyperCheck)
                {
                    if (WeaponRemain < (GCDTime(1) / 2))
                    {
                        if (WildfirePvE.CanUse(out act))
                        {
                            if ((WildfirePvE.Target.Target.IsBossFromIcon() && WildfireBoss) || !WildfireBoss)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        // Queen summon
        if (UseQueen(out act, nextGCD))
        {
            return true;
        }

        // Hypercharge outside of burst (cap prevention)
        if (!LowLevelHyperCheck && !HasReassembled && (!WildfirePvE.Cooldown.WillHaveOneCharge(30) || (Heat == 100)))
        {
            if (!(LiveComboTime <= 9f && LiveComboTime > 0f) && ToolChargeSoon(out act))
            {
                return true;
            }
        }

        // Gauss/Ricochet weaving
        var whichToUse = RicochetPvE.EnoughLevel switch
        {
            true when RicochetPvE.Cooldown.RecastTimeElapsed > GaussRoundPvE.Cooldown.RecastTimeElapsed => "Ricochet",
            true when GaussRoundPvE.Cooldown.RecastTimeElapsed > RicochetPvE.Cooldown.RecastTimeElapsed => "GaussRound",
            true => "Ricochet",
            _ => "GaussRound"
        };

        if (!FullMetalFieldPvE.EnoughLevel || (FullMetalFieldPvE.EnoughLevel && !nextGCD.IsTheSameTo(false, FullMetalFieldPvE)))
        {
            switch (whichToUse)
            {
                case "Ricochet":
                    if (CheckmatePvE.EnoughLevel && CheckmatePvE.CanUse(out act, usedUp: ShouldBurst || IsOverheated))
                    {
                        return true;
                    }
                    if (!CheckmatePvE.EnoughLevel && RicochetPvE.CanUse(out act, usedUp: ShouldBurst || IsOverheated))
                    {
                        return true;
                    }
                    break;
                case "GaussRound":
                    if (DoubleCheckPvE.EnoughLevel && DoubleCheckPvE.CanUse(out act, usedUp: ShouldBurst || IsOverheated))
                    {
                        return true;
                    }
                    if (!DoubleCheckPvE.EnoughLevel && GaussRoundPvE.CanUse(out act, usedUp: ShouldBurst || IsOverheated))
                    {
                        return true;
                    }
                    break;
            }
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // Combo protection (from MCH_Reborn)
        if (IsLastComboAction(true, SlugShotPvE) && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
        {
            if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
            {
                return true;
            }
            if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
            {
                return true;
            }
        }

        if (IsLastComboAction(true, SplitShotPvE) && LiveComboTime >= GCDTime(1) && LiveComboTime <= GCDTime(2) && !IsOverheated)
        {
            if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
            {
                return true;
            }
            if (!HeatedSlugShotPvE.Info.EnoughLevelAndQuest() && SlugShotPvE.CanUse(out act))
            {
                return true;
            }
        }

        // Overheated AoE
        if (AutoCrossbowPvE.CanUse(out act))
        {
            return true;
        }

        // Overheated ST
        if (BlazingShotPvE.EnoughLevel && BlazingShotPvE.CanUse(out act))
        {
            return true;
        }
        if (!BlazingShotPvE.EnoughLevel && HeatBlastPvE.CanUse(out act))
        {
            return true;
        }

        if (IsLastAction(false, HyperchargePvE) && HeatBlastPvE.EnoughLevel)
        {
            return base.GeneralGCD(out act);
        }

        // Bioblaster
        if ((BioMove || (!IsMoving && !BioMove)) && BioblasterPvE.CanUse(out act, usedUp: true))
        {
            return true;
        }

        // Air Anchor
        if (HotShotMasteryTrait.EnoughLevel && AirAnchorPvE.CanUse(out act))
        {
            return true;
        }

        // Drill - ALWAYS use on cooldown, never hold (causes drift)
        // for opener: only use the first charge of Drill after AirAnchor when there are two
        if (DrillPvE.CanUse(out act, usedUp: false))
        {
            return true;
        }

        if (!HotShotMasteryTrait.EnoughLevel && HotShotPvE.CanUse(out act))
        {
            return true;
        }

        // Chainsaw - ALWAYS use on cooldown
        if (ChainSawPvE.CanUse(out act))
        {
            return true;
        }

        // Excavator
        if (ExcavatorPvE.CanUse(out act))
        {
            return true;
        }

        // Full Metal Field
        if (!AirAnchorPvE.CanUse(out _) && !ChainSawPvE.CanUse(out _) && !ExcavatorPvE.CanUse(out _) && !HasExcavatorReady
            && !IsLastGCD(false, ChainSawPvE) && DrillPvE.Cooldown.CurrentCharges < 2 && (!WildfirePvE.Cooldown.IsCoolingDown || IsLastAction(false, WildfirePvE)))
        {
            if (FullMetalFieldPvE.CanUse(out act))
            {
                return true;
            }
        }

        // Second Drill charge
        if (DrillPvE.CanUse(out act, usedUp: true))
        {
            return true;
        }

        // FMF expiring
        if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.FullMetalMachinist))
        {
            if (FullMetalFieldPvE.CanUse(out act))
            {
                return true;
            }
        }

        // Excavator expiring
        if (StatusHelper.PlayerWillStatusEnd(3, true, StatusID.ExcavatorReady))
        {
            if (ExcavatorPvE.CanUse(out act))
            {
                return true;
            }
        }

        // AoE
        if (!IsOverheated)
        {
            if (ScattergunPvE.EnoughLevel)
            {
                if (ScattergunPvE.CanUse(out act))
                {
                    return true;
                }
            }
            if (!ScattergunPvE.EnoughLevel)
            {
                if (SpreadShotPvE.CanUse(out act))
                {
                    return true;
                }
            }
        }

        // ST Combo
        if (HeatedCleanShotPvE.EnoughLevel && HeatedCleanShotPvE.CanUse(out act))
        {
            return true;
        }
        if (!HeatedCleanShotPvE.EnoughLevel && CleanShotPvE.CanUse(out act))
        {
            return true;
        }
        if (HeatedSlugShotPvE.EnoughLevel && HeatedSlugShotPvE.CanUse(out act))
        {
            return true;
        }
        if (!HeatedSlugShotPvE.Info.EnoughLevelAndQuest() && SlugShotPvE.CanUse(out act))
        {
            return true;
        }
        if (HeatedSplitShotPvE.EnoughLevel && HeatedSplitShotPvE.CanUse(out act))
        {
            return true;
        }
        if (!HeatedSplitShotPvE.Info.EnoughLevelAndQuest() && SplitShotPvE.CanUse(out act))
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }

    #endregion

    #region Helper Methods

    private bool ToolChargeSoon(out IAction? act)
    {
        float REST_TIME = 8f;
        if
            (!SpreadShotPvE.CanUse(out _)
            &&
            ((AirAnchorPvE.EnoughLevel && AirAnchorPvE.Cooldown.WillHaveOneCharge(REST_TIME))
            ||
            (!AirAnchorPvE.EnoughLevel && HotShotPvE.EnoughLevel && HotShotPvE.Cooldown.WillHaveOneCharge(REST_TIME))
            ||
            (DrillPvE.EnoughLevel && DrillPvE.Cooldown.WillHaveXCharges(DrillPvE.Cooldown.MaxCharges, REST_TIME))
            ||
            (ChainSawPvE.EnoughLevel && ChainSawPvE.Cooldown.WillHaveOneCharge(REST_TIME))))
        {
            act = null;
            return false;
        }
        else
        {
            return HyperchargePvE.CanUse(out act, skipTTKCheck: true);
        }
    }

    private bool UseQueen(out IAction? act, IAction nextGCD)
    {
        act = null;
        if (!InCombat || IsRobotActive)
            return false;

        // Opener
        if (Battery == 60 && IsLastGCD(false, ExcavatorPvE) && CombatTime < 15)
        {
            if (AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
        }

        // Step pair matching (from MCH_Reborn)
        if (foundStepPair)
        {
            if (AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
        }

        // Overcap protection
        if ((nextGCD.IsTheSameTo(false, CleanShotPvE, HeatedCleanShotPvE) && Battery > 90)
            || (nextGCD.IsTheSameTo(false, HotShotPvE, AirAnchorPvE, ChainSawPvE, ExcavatorPvE) && Battery > 80))
        {
            if (AutomatonQueenPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
            if (!AutomatonQueenPvE.EnoughLevel && RookAutoturretPvE.CanUse(out act, skipTTKCheck: true))
            {
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Debug Display

    public override void DisplayRotationStatus()
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "=== Ultimate Machinist ===");
        ImGui.Text($"QueenStep: {_currentStep}");
        ImGui.Text($"Step Pair Found: {foundStepPair}");
        ImGui.Separator();

        ImGui.TextColored(ImGuiColors.HealerGreen, "Burst Status:");
        ImGui.Text($"Combat Time: {CombatTime:F1}s");
        ImGui.Text($"Should Burst: {ShouldBurst}");
        ImGui.Text($"Is Party Bursting: {IsPartyBursting}");
        ImGui.Text($"Is In 2Min Window: {IsIn2MinWindow}");
        ImGui.Separator();

        ImGui.TextColored(ImGuiColors.DalamudOrange, "Pooling (oGCD only):");
        ImGui.Text($"Seconds Until Burst: {SecondsUntilBurst:F0}s");
        ImGui.Text($"Hold Reassemble: {ShouldHoldReassemble}");
        ImGui.Text($"Hold Barrel Stab: {ShouldHoldBarrelStabilizer}");
        ImGui.Separator();

        ImGui.TextColored(ImGuiColors.TankBlue, "Potion:");
        ImGui.Text($"Should Use Potion: {ShouldUsePotion}");
        ImGui.Text($"Party Medicated: {IsPartyMedicated}");

        base.DisplayRotationStatus();
    }

    #endregion
}
