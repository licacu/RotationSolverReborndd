using System.ComponentModel;
using RotationSolver.RebornRotations.Ranged;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("Ultimate Bard", CombatType.PvE, GameVersion = "7.35",
    Description = "Optimized Bard rotation with Smart Burst Alignment. Maintains strict song cycles while aligning bursts with party buffs.")]
[SourceCode(Path = "main/ExtraRotations/Ranged/UltimateBard.cs")]
[ExtraRotation]
public sealed class UltimateBard : BardRotation
{
    #region Configuration

    [RotationConfig(CombatType.PvE, Name = "Smart Burst Alignment: Wait for Party Buffs")]
    [Description("If true, 2-minute bursts (Raging/Battle Voice) will wait for at least one major party buff (e.g. Divination, Litany) to be present before activating, unless it's the Opener.")]
    private bool SmartBurstAlignment { get; set; } = true;

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
    private bool OnlyDotBoss { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in Opener")]
    private bool UsePotionOpener { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Potion: Use in 2-Minute Bursts")]
    private bool UsePotionBurst { get; set; } = true;

    #endregion

    #region State & Helpers

    // Song Timings Dictionary
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
        SongTiming.Strict120 => 39f, // Extended Mage for 120s alignment
        SongTiming.Custom => CustomMageTime,
        _ => 34f
    };

    private float ArmyTime => SongTimings switch
    {
        SongTiming.Standard => 43f,
        SongTiming.Strict120 => 36f, // Reduced Army 
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

    // Advanced Weaving Logic from Churin
    private static float WeaponTotal => 2.5f; // Base GCD, ideally fetched from client but safe default
    private static float LateWeaveWindow => WeaponTotal * 0.5f;
    private static bool CanLateWeave => (WeaponRemain > 0) && (WeaponRemain <= LateWeaveWindow) && EnoughWeaveTime;
    private static bool EnoughWeaveTime => (WeaponRemain > AnimLock) && WeaponRemain > 0;
    private static float AnimLock => 0.6f; // Safe assumption

    // Raid Buff IDs for Smart Alignment
    // Note: These IDs must match the game's Status IDs.
    private static readonly uint[] PartyBuffs = new uint[]
    {
        StatusID.Divination,
        StatusID.BattleLitany,
        StatusID.Brotherhood,
        StatusID.TechnicalFinish,
        StatusID.ArcaneCircle,
        StatusID.ChainStratagem,
        StatusID.SearingLight,
        StatusID.Embolden,
        StatusID.Devilment,
        StatusID.StarryMuse 
    };

    private bool HasRaidBuff()
    {
        // If Smart Alignment is off, we always "have" the buff to proceed.
        if (!SmartBurstAlignment) return true;

        // In opener (combat time < 30s), we always proceed.
        if (CombatTime < 30f) return true;

        // Check if player has any of the major raid buffs
        return Player.HasStatus(true, PartyBuffs);
    }
    
    // Check if we are in a burst window state
    private bool InBurst => 
        (HasRagingStrikes && HasBattleVoice) || 
        // Or if we are preparing for it and cooldowns are ready
        (RagingStrikesPvE.Cooldown.IsCoolingDown && BattleVoicePvE.Cooldown.IsCoolingDown && 
         !RagingStrikesPvE.Cooldown.WillHaveOneCharge(5) && !BattleVoicePvE.Cooldown.WillHaveOneCharge(5));

    private bool IsFirstCycle => CombatTime < 120f;

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        // Potion at -1.5s approx to cover opener
        if (UsePotionOpener && remainTime <= 1.5f && remainTime > 0.5f)
        {
            if (UseBurstMedicine(out var act)) return act;
        }

        // Pre-cast action usually handled by base, but we can enforce Tincture
        return base.CountDownAction(remainTime);
    }

    #endregion

    #region oGCD Logic

    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // 1. Esuna / Cleanse
        if (TheWardensPaeanPvE.CanUse(out act) && Player.HasStatus(true, StatusID.Doom))
        {
            return true;
        }

        // 2. Weaving Check
        if (!EnoughWeaveTime) 
        {
            act = null;
            return false;
        }

        // 3. Empyreal Arrow - Keep on CD, but align with Repertoire
        if (EmpyrealArrowPvE.CanUse(out act))
        {
            // If we are about to cap Repertoire (3 stacks), use it immediately
            if (Repertoire == 3) return true;

            // In Mages/Armys, just send it
            if (!InWanderers) return true;

            // In Wanderers, try to late weave if possible to maximize proc chance
            if (InWanderers)
            {
                if (CanLateWeave) return true;
                // If we can't late weave but it's ready, do we hold? 
                // Generally better to use than drift, unless pitch perfect is full.
                if (Repertoire < 3) return true; 
            }
        }

        // 4. Pitch Perfect (Wanderer's Minuet)
        if (InWanderers)
        {
            // Use at 3 stacks
            if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act)) return true;

            // Use at 2 stacks if Empyreal is about to give a stack (avoid overflow)
            if (Repertoire >= 2 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1) && PitchPerfectPvE.CanUse(out act)) return true;

            // Use remaining stacks before song ends
            if (SongEndAfter(WandRemainTime) && SongTime < (WandRemainTime - AnimLock) && Repertoire > 0 && PitchPerfectPvE.CanUse(out act)) return true;
        }

        // 5. Bloodletter / Rain of Death
        // Prevent overcap (3 charges)
        // If in Mages, we get lots of resets, so dump more aggressively
        bool isAoe = Player.GetNearbyEnemies(5).Count() >= 3;
        var blAction = isAoe ? RainOfDeathPvE : BloodletterPvE;
        var blCharges = BloodletterPvE.Cooldown.CurrentCharges;

        if (blAction.CanUse(out act))
        {
            // If max charges, use
            if (blCharges >= 2.8f) return true;

            // In Mages Ballad, dump to avoid capping from procs
            if (InMages && blCharges >= 1f) return true;

            // In Burst, dump for damage
            if (InBurst) return true;
        }
        
        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // 1. Song Cycle Management
        // Wanderers -> Mages -> Armys
        
        // Switch to Wanderers
        if (TheWanderersMinuetPvE.CanUse(out act))
        {
            // If in Armys and time is up
             if (InArmys && SongEndAfter(ArmyRemainTime)) return true;
             // Use if no song
             if (NoSong) return true;
        }

        // Switch to Mages
        if (MagesBalladPvE.CanUse(out act))
        {
            // If in Wanderers and time is up
            if (InWanderers && SongEndAfter(WandRemainTime))
            {
                // Wait for late weave window to get last Pitch Perfect potentially?
                // Just ensuring we don't cut it off prematurely without using PP is handled in Emergency
                return true; 
            }
        }

        // Switch to Armys
        if (ArmysPaeonPvE.CanUse(out act))
        {
            // If in Mages and time is up
            if (InMages && SongEndAfter(MageRemainTime)) return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // 1. Radiant Finale (Highest Priority Buffer)
        // We generally want to use this first in the burst window
        if (RadiantFinalePvE.CanUse(out act))
        {
            // Only use if we are in the "Burst Ready" state
            // Check Smart Alignment: Do we have buffs? OR is it our opener?
            if (HasRaidBuff())
            {
                // Ensure we have coda if possible (should happen naturally)
                return true;
            }
        }

        // 2. Battle Voice
        if (BattleVoicePvE.CanUse(out act))
        {
            // Align with Radiant Finale if possible
            if (Player.HasStatus(true, StatusID.RadiantFinale) || !RadiantFinalePvE.EnoughLevel)
            {
                 // Smart Alignment check largely covered by Radiant, 
                 // but if RF is on CD/not ready, check again.
                 if (HasRaidBuff()) return true;
            }
        }

        // 3. Raging Strikes
        if (RagingStrikesPvE.CanUse(out act))
        {
            // Standard: Use before Iron Jaws refresh
            // We want this to cover the burst.
            // If we have RF/BV, use it.
            if (HasRaidBuff())
            {
                 // If we have RF/BV active or they are coming up
                 if (Player.HasStatus(true, StatusID.RadiantFinale) || Player.HasStatus(true, StatusID.BattleVoice)) return true;
                 
                 // Fallback for lower levels
                 if (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel) return true;
            }
        }

        // 4. Barrage
        // Always pair with Refulgent or Shadowbite
        if (BarragePvE.CanUse(out act))
        {
            // Only use if we have Raging Strikes active (Snapshotting)
            // Or if Raging Strikes is not ready for a long time (don't drift Barrage too much)
            if (Player.HasStatus(true, StatusID.RagingStrikes) || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(20))
            {
                 return true;
            }
        }

        // 5. Sidewinder
        if (SidewinderPvE.CanUse(out act))
        {
            // Use under buffs
            if (InBurst || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // 1. Iron Jaws (DoT Maintenance)
        // Refresh if DoTs are about to fall off (< 3s)
        bool dotsFalling = CurrentTarget?.WillStatusEndGCD(1, 0, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false;
        
        // Also refresh if we have fully buffed Raging Strikes (Snapshotting)
        // We want to catch Raging + Raid Buffs just before Raging falls off
        bool ragingEnding = Player.HasStatus(true, StatusID.RagingStrikes) && Player.WillStatusEndGCD(1, 0, true, StatusID.RagingStrikes);
        
        if (IronJawsPvE.CanUse(out act))
        {
            // Standard refresh
            if (dotsFalling) return true;

            // Snapshot refresh
            if (ragingEnding) return true;
        }

        // 2. Apex Arrow / Blast Arrow
        // Use Blast Arrow if ready
        if (BlastArrowPvE.CanUse(out act))
        {
            // Try to use under buffs if possible, but don't lose the proc (10s duration)
            // If buffs are up, send it. If buffs are 50s away, send it.
            return true;
        }

        // Use Apex Arrow if Gauge is high
        if (ApexArrowPvE.CanUse(out act))
        {
            if (SoulVoice == 100) return true;
            if (SoulVoice >= 80 && Player.HasStatus(true, StatusID.RagingStrikes)) return true; // Dump during buffs
        }

        // 3. AoE
        if (ShadowbitePvE.CanUse(out act)) return true; // Only procs with Barrage or Proc
        if (WideVolleyPvE.CanUse(out act)) return true;
        if (LadonsbitePvE.CanUse(out act)) return true;
        if (QuickNockPvE.CanUse(out act)) return true;

        // 4. Single Target
        // DoT application logic
        if (OnlyDotBoss && !(CurrentTarget?.IsBossFromIcon() ?? false) && !IsDummy)
        {
            // Skip DoTs if not boss and config is set
        }
        else
        {
             if (StormbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Stormbite) == false)) return true;
             if (CausticBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
        }
        
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
        if (StraightShotPvE.CanUse(out act)) return true; // Fallback
        
        // Filler
        if (BurstShotPvE.CanUse(out act)) return true;
        
        return base.GeneralGCD(out act);
    }

    #endregion
}


