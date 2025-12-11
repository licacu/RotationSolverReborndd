using System.ComponentModel;
using RotationSolver.Basic.Data; // StatusID için gerekli
using RotationSolver.RebornRotations.Ranged;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("Ultimate Bard", CombatType.PvE, GameVersion = "7.35",
    Description = "Optimized Bard rotation with Smart Burst Alignment. Maintains strict song cycles while aligning bursts with party buffs.")]
public sealed class UltimateBard : BardRotation
{
    #region Configuration

    [RotationConfig(CombatType.PvE, Name = "Smart Burst Alignment: Wait for Party Buffs")]
    [Description("If true, 2-minute bursts (Raging/Battle Voice) will wait for at least one major party buff (e.g. Divination, Litany) to be present before activating, unless it's the Opener.")]
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
    private float CustomWandTime { get; set; } = 42f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Mage's Ballad Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    private float CustomMageTime { get; set; } = 39f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Army's Paeon Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    private float CustomArmyTime { get; set; } = 36f;

    [RotationConfig(CombatType.PvE, Name = "Only use DOTs on targets with Boss Icon")]
    public bool OnlyDotBoss { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in Opener")]
    public bool UsePotionOpener { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in 2-Minute Bursts")]
    public bool UsePotionBurst { get; set; } = true;

    #endregion

    #region State & Helpers

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

    private float WandRemainTime => 45f - WandTime;
    private float MageRemainTime => 45f - MageTime;
    private float ArmyRemainTime => 45f - ArmyTime;

    private bool InWanderers => Song == Song.Wanderer;
    private bool InMages => Song == Song.Mage;
    private bool InArmys => Song == Song.Army;
    private bool NoSong => Song == Song.None;

    // Advanced Weaving Logic
    // 'new' keyword added to hide inherited member warning
    private new static float WeaponTotal => 2.5f; 
    private static float LateWeaveWindow => WeaponTotal * 0.5f;
    private static bool CanLateWeave => (WeaponRemain > 0) && (WeaponRemain <= LateWeaveWindow) && EnoughWeaveTime;
    private static bool EnoughWeaveTime => (WeaponRemain > AnimLock) && WeaponRemain > 0;
    private static float AnimLock => 0.6f; 

    // FIXED: Added (uint) casts to solve the error
    private static readonly uint[] PartyBuffs = new uint[]
    {
        (uint)StatusID.Divination,
        (uint)StatusID.BattleLitany,
        (uint)StatusID.Brotherhood,
        (uint)StatusID.TechnicalFinish,
        (uint)StatusID.ArcaneCircle,
        (uint)StatusID.ChainStratagem,
        (uint)StatusID.SearingLight,
        (uint)StatusID.Embolden,
        (uint)StatusID.Devilment,
        (uint)StatusID.StarryMuse 
    };

    private bool HasRaidBuff()
    {
        if (!SmartBurstAlignment) return true;
        if (CombatTime < 30f) return true;
        return Player.HasStatus(true, PartyBuffs);
    }
    
    private bool InBurst => 
        (HasRagingStrikes && HasBattleVoice) || 
        (RagingStrikesPvE.Cooldown.IsCoolingDown && BattleVoicePvE.Cooldown.IsCoolingDown && 
         !RagingStrikesPvE.Cooldown.WillHaveOneCharge(5) && !BattleVoicePvE.Cooldown.WillHaveOneCharge(5));

    private bool IsFirstCycle => CombatTime < 120f;

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        if (UsePotionOpener && remainTime <= 1.5f && remainTime > 0.5f)
        {
            if (UseBurstMedicine(out var act)) return act;
        }
        return base.CountDownAction(remainTime);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Potion Logic Fix
        if (UsePotionBurst && InBurst && ((CombatTime > 300 && CombatTime < 600) || !IsFirstCycle)) 
        {
            if (UseBurstMedicine(out act)) return true;
        }

        if (TheWardensPaeanPvE.CanUse(out act) && Player.HasStatus(true, StatusID.Doom))
        {
            return true;
        }

        if (!EnoughWeaveTime) 
        {
            act = null;
            return false;
        }

        if (EmpyrealArrowPvE.CanUse(out act))
        {
            if (Repertoire == 3) return true;
            if (!InWanderers) return true;
            if (InWanderers)
            {
                if (CanLateWeave) return true;
                if (Repertoire < 3) return true; 
            }
        }

        if (InWanderers)
        {
            if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act)) return true;
            if (Repertoire >= 2 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1) && PitchPerfectPvE.CanUse(out act)) return true;
            if (SongEndAfter(WandRemainTime) && SongTime < (WandRemainTime - AnimLock) && Repertoire > 0 && PitchPerfectPvE.CanUse(out act)) return true;
        }

        bool isAoe = Player.GetNearbyEnemies(5).Count() >= 3;
        var blAction = isAoe ? RainOfDeathPvE : BloodletterPvE;
        var blCharges = BloodletterPvE.Cooldown.CurrentCharges;

        if (blAction.CanUse(out act))
        {
            if (blCharges >= 2.8f) return true;
            if (InMages && blCharges >= 1f) return true;
            if (InBurst) return true;
        }
        
        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        if (TheWanderersMinuetPvE.CanUse(out act))
        {
             if (InArmys && SongEndAfter(ArmyRemainTime)) return true;
             if (NoSong) return true;
        }

        if (MagesBalladPvE.CanUse(out act))
        {
            if (InWanderers && SongEndAfter(WandRemainTime)) return true; 
        }

        if (ArmysPaeonPvE.CanUse(out act))
        {
            if (InMages && SongEndAfter(MageRemainTime)) return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        if (RadiantFinalePvE.CanUse(out act))
        {
            if (HasRaidBuff()) return true;
        }

        if (BattleVoicePvE.CanUse(out act))
        {
            if (Player.HasStatus(true, StatusID.RadiantFinale) || !RadiantFinalePvE.EnoughLevel)
            {
                 if (HasRaidBuff()) return true;
            }
        }

        if (RagingStrikesPvE.CanUse(out act))
        {
            if (HasRaidBuff())
            {
                 if (Player.HasStatus(true, StatusID.RadiantFinale) || Player.HasStatus(true, StatusID.BattleVoice)) return true;
                 if (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel) return true;
            }
        }

        if (BarragePvE.CanUse(out act))
        {
            if (Player.HasStatus(true, StatusID.RagingStrikes) || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(20))
            {
                 return true;
            }
        }

        if (SidewinderPvE.CanUse(out act))
        {
            if (InBurst || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        bool dotsFalling = CurrentTarget?.WillStatusEndGCD(1, 0, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false;
        bool ragingEnding = Player.HasStatus(true, StatusID.RagingStrikes) && Player.WillStatusEndGCD(1, 0, true, StatusID.RagingStrikes);
        
        if (IronJawsPvE.CanUse(out act))
        {
            if (dotsFalling) return true;
            if (ragingEnding) return true;
        }

        if (BlastArrowPvE.CanUse(out act)) return true;

        if (ApexArrowPvE.CanUse(out act))
        {
            if (SoulVoice == 100) return true;
            if (SoulVoice >= 80 && Player.HasStatus(true, StatusID.RagingStrikes)) return true; 
        }

        if (ShadowbitePvE.CanUse(out act)) return true; 
        if (WideVolleyPvE.CanUse(out act)) return true;
        if (LadonsbitePvE.CanUse(out act)) return true;
        if (QuickNockPvE.CanUse(out act)) return true;

        if (OnlyDotBoss && !(CurrentTarget?.IsBossFromIcon() ?? false) && !IsDummy)
        {
            // Skip DoTs
        }
        else
        {
             if (StormbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Stormbite) == false)) return true;
             if (CausticBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
        }
        
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
        if (StraightShotPvE.CanUse(out act)) return true; 
        
        if (BurstShotPvE.CanUse(out act)) return true;
        
        return base.GeneralGCD(out act);
    }

    #endregion
}
