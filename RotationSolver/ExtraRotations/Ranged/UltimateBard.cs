using System.ComponentModel;
using RotationSolver.RebornRotations.Ranged;

namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Ultimate Bard", CombatType.PvE, GameVersion = "7.35",
    Description = "Optimized Bard rotation with Smart Burst Alignment and Custom Level 100 Opener.")]
[SourceCode(Path = "main/RebornRotations/Ranged/UltimateBard.cs")]
public sealed class UltimateBard : BardRotation
{
    #region Configuration

    [RotationConfig(CombatType.PvE, Name = "Smart Burst Alignment: Wait for Party Buffs")]
    [Description("If true, 2-minute bursts will wait for party buffs.")]
    public bool SmartBurstAlignment { get; set; } = true;

    private enum SongTiming
    {
        [Description("Standard (43-34-43)")] Standard,
        [Description("Strict 120s Loop (42-36-42)")] Strict120, 
        [Description("Custom")] Custom
    }

    [RotationConfig(CombatType.PvE, Name = "Song Timing Cycle")]
    private SongTiming SongTimings { get; set; } = SongTiming.Strict120;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Wanderer's Minuet Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    public float CustomWandTime { get; set; } = 42f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Mage's Ballad Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    public float CustomMageTime { get; set; } = 39f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Army's Paeon Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    public float CustomArmyTime { get; set; } = 36f;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in Opener")]
    public bool UsePotionOpener { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in 2-Minute Bursts")]
    public bool UsePotionBurst { get; set; } = true;

    #endregion

    #region State & Helpers

    // Song Timings Config
    private float WandTime => SongTimings switch
    {
        SongTiming.Standard => 43f,
        SongTiming.Strict120 => 42f,
        SongTiming.Custom => CustomWandTime,
        _ => 43f
    };

    private float MageTime => SongTimings switch
    {
        SongTiming.Standard => 34f,
        SongTiming.Strict120 => 39f, 
        SongTiming.Custom => CustomMageTime,
        _ => 34f
    };

    private float ArmyTime => SongTimings switch
    {
        SongTiming.Standard => 43f,
        SongTiming.Strict120 => 36f, 
        SongTiming.Custom => CustomArmyTime,
        _ => 43f
    };

    private bool InWanderers => Song == Song.Wanderer;
    private bool InMages => Song == Song.Mage;
    private bool InArmys => Song == Song.Army;
    private bool NoSong => Song == Song.None;

    // Advanced Weaving Logic
    private static float WeaponTotal => 2.5f; 
    private static float LateWeaveWindow => WeaponTotal * 0.5f;
    private static bool CanLateWeave => (WeaponRemain > 0) && (WeaponRemain <= LateWeaveWindow) && EnoughWeaveTime;
    private static bool EnoughWeaveTime => (WeaponRemain > AnimLock) && WeaponRemain > 0;
    private static float AnimLock => Math.Max(AnimationLock, WeaponTotal * 0.25f);

    // Raid Buff IDs for Smart Alignment (Adapted from Rabbs_BLM)
    private static readonly HashSet<uint> BurstStatusIds = new()
    {
        (uint)StatusID.Divination,
        (uint)StatusID.Brotherhood,
        (uint)StatusID.BattleLitany,
        (uint)StatusID.ArcaneCircle,
        (uint)StatusID.StarryMuse,
        (uint)StatusID.Embolden,
        (uint)StatusID.SearingLight,
        (uint)StatusID.BattleVoice,
        (uint)StatusID.TechnicalFinish,
        (uint)StatusID.RadiantFinale,
        (uint)StatusID.Devilment,
        (uint)StatusID.ChainStratagem
    };

    private static bool IsPartyBurst => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => BurstStatusIds.Contains(status.StatusId)) == true
    ) == true;

    private bool IsBurstReady
    {
        get
        {
            if (!SmartBurstAlignment) return true;
            if (CombatTime < 45f) return true;
            return IsPartyBurst;
        }
    }
    
    private bool InBurstMode => 
        (HasRagingStrikes && HasBattleVoice) || 
        (HasRagingStrikes && !BattleVoicePvE.EnoughLevel);

    private int CountEnemiesInRange(float range)
    {
        if (AllHostileTargets == null) return 0;
        return AllHostileTargets.Count(t => t.DistanceToPlayer() <= range);
    }

    // Opener State
    private int _openerStep = 0;
    private bool _inOpener = false;

    // Reset opener on combat end
    protected override void UpdateInfo()
    {
        if (!InCombat)
        {
            _openerStep = 0;
            _inOpener = false;
        }
        
        // Attempt to trigger opener at start
        if (InCombat && CombatTime < 5f && _openerStep == 0 && !IsDummy)
        {
            _inOpener = true;
        }

        base.UpdateInfo();
    }

    #endregion

    #region Countdown
    protected override IAction? CountDownAction(float remainTime)
    {
        if (UsePotionOpener && remainTime <= 2.0f && remainTime > 0.5f)
        {
            if (UseBurstMedicine(out var act)) return act;
        }
        return base.CountDownAction(remainTime);
    }
    #endregion

    #region Opener Implementation (Lv 100)
    
    private bool ExecuteOpenerGCD(out IAction? act)
    {
        act = null;
        if (!_inOpener) return false;

        // Step 0: Stormbite
        if (_openerStep == 0)
        {
             if (StormbitePvE.CanUse(out act)) return true;
             // If already applied (pre-pull?), advance
             if (CurrentTarget?.HasStatus(true, StatusID.Stormbite) == true) _openerStep++;
        }

        // Step 1: Caustic Bite
        if (_openerStep == 1)
        {
            if (CausticBitePvE.CanUse(out act)) return true;
            if (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == true) _openerStep++;
        }

        // Step 2: Burst Shot
        if (_openerStep == 2)
        {
            if (BurstShotPvE.CanUse(out act)) return true;
            // Advance logic is tricky for filler, assume next GCD
            if (IsLastGCD(ActionID.BurstShotPvE)) _openerStep++;
        }

        // Step 3: Burst Shot
        if (_openerStep == 3)
        {
            if (BurstShotPvE.CanUse(out act)) return true;
            if (IsLastGCD(ActionID.BurstShotPvE) && Player.HasStatus(true, StatusID.RagingStrikes)) _openerStep++;
        }

        // Step 4: Radiant Encore
        if (_openerStep == 4)
        {
            if (RadiantEncorePvE.CanUse(out act, skipComboCheck: true)) return true;
            // If we missed it (no coda?), failover to Refulgent
            _openerStep++;
        }

        // Step 5: Refulgent Arrow
        if (_openerStep == 5)
        {
            if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
            if (StraightShotPvE.CanUse(out act)) return true; // Fallback
             _openerStep++;
        }

        // Step 6: Resonant Arrow
        if (_openerStep == 6)
        {
            if (ResonantArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
             _openerStep++;
        }

        // Step 7: Refulgent Arrow
        if (_openerStep == 7)
        {
            if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
             _openerStep++;
        }

        // Step 8: Burst Shot
        if (_openerStep == 8)
        {
            if (BurstShotPvE.CanUse(out act)) return true;
             _openerStep++;
        }

        // Step 9: Iron Jaws
        if (_openerStep == 9)
        {
             if (IronJawsPvE.CanUse(out act)) return true;
             // End Opener
             _inOpener = false;
        }

        return false;
    }

    private bool ExecuteOpeneroGCD(out IAction? act)
    {
        act = null;
        if (!_inOpener) return false;

        // Note: oGCDs assume the previous GCD is running
        
        // After Stormbite: Wanderer -> Empyreal
        if (_openerStep == 0)
        {
             if (TheWanderersMinuetPvE.CanUse(out act)) return true;
             if (EmpyrealArrowPvE.CanUse(out act)) return true;
        }

        // After Caustic: Pot -> BV
        if (_openerStep == 1)
        {
            // Pot handled by general logic usually, but here:
            if (UsePotionOpener && !Player.HasStatus(true, StatusID.Medicated))
            {
                 if (UseBurstMedicine(out act)) return true;
            }
            if (BattleVoicePvE.CanUse(out act)) return true;
        }

        // After Burst Shot 1: Radiant Finale -> Raging Strikes
        if (_openerStep == 2)
        {
            if (RadiantFinalePvE.CanUse(out act)) return true;
            if (RagingStrikesPvE.CanUse(out act)) return true;
        }

        // After Burst Shot 2: Heartbreak -> (Next GCD is Radiant Encore)
        // Wait, User said: Burst Shot -> Heartbreak -> Radiant Encore -> Barrage
        // So after Burst Shot (Step 3), we use Heartbreak
        if (_openerStep == 3)
        {
            if (HeartbreakShotPvE.CanUse(out act, usedUp: true)) return true;
        }

        // After Radiant Encore (Step 4): Barrage
        if (_openerStep == 4)
        {
            if (BarragePvE.CanUse(out act)) return true;
        }

        // After Refulgent (Step 5): Sidewinder
        if (_openerStep == 5)
        {
            if (SidewinderPvE.CanUse(out act)) return true;
        }

        // After Resonant (Step 6): Empyreal
        if (_openerStep == 6)
        {
            if (EmpyrealArrowPvE.CanUse(out act)) return true;
        }

        // After Refulgent (Step 7): Heartbreak
        if (_openerStep == 7)
        {
             if (HeartbreakShotPvE.CanUse(out act, usedUp: true)) return true;
        }
        
        // Step 8 (Burst Shot): No Weave requested
        
        // Step 9 (Iron Jaws): Pitch Perfect
        if (_openerStep == 9)
        {
            if (PitchPerfectPvE.CanUse(out act)) return true;
        }

        return false;
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // 0. Opener
        if (ExecuteOpeneroGCD(out act)) return true;

        // 1. Esuna
        if (TheWardensPaeanPvE.CanUse(out act) && Player.HasStatus(true, StatusID.Doom)) return true;

        if (!EnoughWeaveTime) 
        {
            act = null;
            return false;
        }

        // 2. Pot
        if (UsePotionBurst && InBurstMode && !Player.HasStatus(true, StatusID.Medicated))
        {
             if (UseBurstMedicine(out act)) return true;
        }

        // 3. Pitch Perfect
        if (InWanderers)
        {
            if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act)) return true;
            if (Repertoire >= 2 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1) && PitchPerfectPvE.CanUse(out act)) return true;
            if (SongTime >= (WandTime - AnimLock) && Repertoire > 0 && PitchPerfectPvE.CanUse(out act)) return true;
        }

        // 4. Empyreal Arrow
        if (EmpyrealArrowPvE.CanUse(out act))
        {
            if (Repertoire == 3) return true;
            if (!InWanderers) return true;
            if (InWanderers)
            {
               if (CanLateWeave) return true;
               if (EmpyrealArrowPvE.Cooldown.CurrentCharges > 0.9f) return true;
            }
        }

        // 5. Heartbreak / Bloodletter
        bool isAoe = CountEnemiesInRange(5) >= 3;
        var blAction = isAoe ? RainOfDeathPvE : (HeartbreakShotPvE.EnoughLevel ? HeartbreakShotPvE : BloodletterPvE);
        
        // CRITICAL: Dump Heartbreak Shot during Burst
        if (InBurstMode && blAction.CanUse(out act, usedUp: true)) return true;

        if (blAction.CanUse(out act))
        {
            if (BloodletterPvE.Cooldown.CurrentCharges >= 2.8f) return true;
            if (InMages && BloodletterPvE.Cooldown.CurrentCharges >= 1f) return true;
            if (InBurstMode) return true;
        }
        
        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Song Cycle Management (Fixed Logic using SongTime)
        // Check if we need to switch songs based on ELAPSED TIME (SongTime)
        
        // 1. Switch to Wanderers Minuet
        // Condition: In Armys and time is up, OR No Song
        if (TheWanderersMinuetPvE.CanUse(out act))
        {
             if (InArmys && SongTime >= ArmyTime) return true;
             if (NoSong) return true;
        }

        // 2. Switch to Mages Ballad
        // Condition: In Wanderers and time is up
        if (MagesBalladPvE.CanUse(out act))
        {
            if (InWanderers && SongTime >= WandTime) return true;
        }

        // 3. Switch to Armys Paeon
        // Condition: In Mages and time is up
        if (ArmysPaeonPvE.CanUse(out act))
        {
            if (InMages && SongTime >= MageTime) return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (ExecuteOpeneroGCD(out act)) return true;

        if (RadiantFinalePvE.CanUse(out act))
        {
            if (IsBurstReady) return true;
        }

        if (BattleVoicePvE.CanUse(out act))
        {
            if (Player.HasStatus(true, StatusID.RadiantFinale) || (!RadiantFinalePvE.EnoughLevel && IsBurstReady)) return true;
        }

        if (RagingStrikesPvE.CanUse(out act))
        {
            bool buffsActive = Player.HasStatus(true, StatusID.RadiantFinale) || Player.HasStatus(true, StatusID.BattleVoice);
            if (buffsActive) return true;
            if (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel && IsBurstReady) return true;
        }

        if (BarragePvE.CanUse(out act))
        {
            if (Player.HasStatus(true, StatusID.RagingStrikes)) return true;
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(20)) return true;
        }

        if (SidewinderPvE.CanUse(out act))
        {
             if (InBurstMode) return true;
             if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // 0. Custom Opener
        if (ExecuteOpenerGCD(out act)) return true;

        // 1. Radiant Encore & Resonant Arrow (Highest Priority)
        // Ensure we don't miss these in bursts
        if (RadiantEncorePvE.CanUse(out act, skipComboCheck: true)) return true;
        if (ResonantArrowPvE.CanUse(out act, skipComboCheck: true)) return true;

        // 2. Iron Jaws Maintenance & Snapshotting
        bool dotsFalling = CurrentTarget?.WillStatusEndGCD(1, 0, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false;
        
        // Critical: Snapshot Raging Strikes
        bool ragingEnding = Player.HasStatus(true, StatusID.RagingStrikes) && Player.WillStatusEnd(3.0f, true, StatusID.RagingStrikes);
        
        if (IronJawsPvE.CanUse(out act))
        {
            if (dotsFalling) return true;
            if (ragingEnding) return true;
            
            // Burst Window Assurance: If we have full buffs, ensuring IJ is up logic?
            // Usually Raging Ending handles the snapshot.
        }

        // 3. Blast Arrow
        if (BlastArrowPvE.CanUse(out act))
        {
            if (Player.HasStatus(true, StatusID.RagingStrikes)) return true;
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(5)) return true;
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        // 4. Apex Arrow
        if (ApexArrowPvE.CanUse(out act))
        {
            if (SoulVoice == 100) return true;
            if (Player.HasStatus(true, StatusID.RagingStrikes) && SoulVoice >= 80) return true;
        }

        // 5. AoE
        if (ShadowbitePvE.CanUse(out act)) return true;
        if (WideVolleyPvE.CanUse(out act)) return true;
        if (LadonsbitePvE.CanUse(out act)) return true; 
        if (QuickNockPvE.CanUse(out act)) return true;

        // 6. DoT & Fillers
        bool isBoss = CurrentTarget?.IsBossFromIcon() ?? false;
        bool shouldDot = !OnlyDotBoss || isBoss;

        if (shouldDot)
        {
             if (StormbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Stormbite) == false)) return true;
             if (CausticBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
             
             if (!StormbitePvE.EnoughLevel && WindbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Windbite) == false)) return true;
             if (!CausticBitePvE.EnoughLevel && VenomousBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
        }
        
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
        if (StraightShotPvE.CanUse(out act)) return true;
        if (BurstShotPvE.CanUse(out act)) return true;
        if (HeavyShotPvE.CanUse(out act)) return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
