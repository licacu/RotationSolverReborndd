using System.ComponentModel;
using RotationSolver.RebornRotations.Ranged;

namespace RotationSolver.RebornRotations.Ranged;

[Rotation("Ultimate Bard", CombatType.PvE, GameVersion = "7.35",
    Description = "Optimized Bard rotation with Smart Burst Alignment. Maintains strict song cycles while aligning bursts with party buffs.")]
[SourceCode(Path = "main/RebornRotations/Ranged/UltimateBard.cs")]
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
    public float CustomWandTime { get; set; } = 42f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Mage's Ballad Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    public float CustomMageTime { get; set; } = 39f;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Custom Army's Paeon Uptime", Parent = nameof(SongTimings), ParentValue = SongTiming.Custom)]
    public float CustomArmyTime { get; set; } = 36f;

    [RotationConfig(CombatType.PvE, Name = "Only use DOTs on targets with Boss Icon")]
    public bool OnlyDotBoss { get; set; } = true;

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

    private float WandRemainTime => 45f - WandTime;
    private float MageRemainTime => 45f - MageTime;
    private float ArmyRemainTime => 45f - ArmyTime;

    private bool InWanderers => Song == Song.Wanderer;
    private bool InMages => Song == Song.Mage;
    private bool InArmys => Song == Song.Army;
    private bool NoSong => Song == Song.None;

    // Advanced Weaving Logic (Adapted from ChurinBRD)
    private static float LateWeaveWindow => WeaponTotal * 0.5f;
    private static bool CanLateWeave => (WeaponRemain > 0) && (WeaponRemain <= LateWeaveWindow) && EnoughWeaveTime;
    private static bool EnoughWeaveTime => (WeaponRemain > AnimLock) && WeaponRemain > 0;
    private static float AnimLock => Math.Max(AnimationLock, WeaponTotal * 0.25f); // Safer dynamic lock 

    // Raid Buff IDs for Smart Alignment
    // Common 120s/60s raid buffs
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

    private bool ShouldBurst()
    {
        // 1. If not enabled, always go
        if (!SmartBurstAlignment) return true;

        // 2. Opener Rule (< 45s)
        // If we are in the start of the fight, just go.
        if (CombatTime < 45f) return true;

        // 3. Raid Buff Check
        // If external buffs are present, we go.
        if (Player.HasStatus(true, PartyBuffs)) return true;

        // Otherwise hold
        return false;
    }
    
    // Check if we are currently IN a burst window state (buffs active)
    private bool InBurstMode => 
        (HasRagingStrikes && HasBattleVoice) || 
        (HasRagingStrikes && !BattleVoicePvE.EnoughLevel);

    #endregion

    #region Countdown

    protected override IAction? CountDownAction(float remainTime)
    {
        // Potion at -1.5s approx to cover opener
        if (UsePotionOpener && remainTime <= 1.2f && remainTime > 0.5f)
        {
            if (UseBurstMedicine(out var act)) return act;
        }

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

        // 3. Potion Logic (Mid-Fight)
        // Use potion if we are entering burst (Raging Strikes just went up or is about to)
        if (UsePotionBurst && InBurstMode && !Player.HasStatus(true, StatusID.Medicated))
        {
             if (UseBurstMedicine(out act)) return true;
        }

        // 4. Pitch Perfect (Wanderer's Minuet)
        if (InWanderers)
        {
            // Use at 3 stacks
            if (Repertoire == 3 && PitchPerfectPvE.CanUse(out act)) return true;

            // Avoid Empyreal Overcap (2 stacks + Empyreal incoming)
            if (Repertoire >= 2 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1) && PitchPerfectPvE.CanUse(out act)) return true;

            // Use remaining stacks before song ends
            if (SongEndAfter(WandRemainTime) && SongTime < (WandRemainTime - AnimLock) && Repertoire > 0 && PitchPerfectPvE.CanUse(out act)) return true;
        }

        // 5. Empyreal Arrow
        if (EmpyrealArrowPvE.CanUse(out act))
        {
            // Repertoire cap check
            if (Repertoire == 3) return true;

            // In Mages/Armys, always use to cycle
            if (!InWanderers) return true;

            // In Wanderers, prioritize Late Weave for procs, but don't drift heavily
            if (InWanderers)
            {
               if (CanLateWeave) return true;
               // If we held it too long, just use it to avoid drift
               if (EmpyrealArrowPvE.Cooldown.CurrentCharges > 0.9f) return true;
            }
        }

        // 6. Bloodletter / Rain of Death
        // Prevent overcap (3 charges)
        bool isAoe = Player.GetNearbyEnemies(5).Count() >= 3;
        var blAction = isAoe ? RainOfDeathPvE : BloodletterPvE;
        var blCharges = BloodletterPvE.Cooldown.CurrentCharges;

        if (blAction.CanUse(out act))
        {
            // If near max charges, use
            if (blCharges >= 2.8f) return true;

            // In Mages Ballad, dump frequently to avoid proc overcap
            if (InMages && blCharges >= 1f) return true;

            // In Burst, dump for damage
            if (InBurstMode) return true;
        }
        
        return base.EmergencyAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        // Song Cycle Management: Wanderers -> Mages -> Armys
        
        // 1. Switch to Wanderers Minuet
        // Condition: In Armys and time is up, OR No Song
        if (TheWanderersMinuetPvE.CanUse(out act))
        {
             if (InArmys && SongEndAfter(ArmyRemainTime)) return true;
             if (NoSong) return true;
        }

        // 2. Switch to Mages Ballad
        // Condition: In Wanderers and time is up
        if (MagesBalladPvE.CanUse(out act))
        {
            if (InWanderers && SongEndAfter(WandRemainTime))
            {
                // Can verify we aren't cutting off a Pitch Perfect here, but EmergencyAbility handles that.
                // Try to late weave to catch last tick if possible
                 return true; 
            }
        }

        // 3. Switch to Armys Paeon
        // Condition: In Mages and time is up
        if (ArmysPaeonPvE.CanUse(out act))
        {
            if (InMages && SongEndAfter(MageRemainTime)) return true;
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // 1. Radiant Finale (Highest Priority Buffer)
        // Check Smart Alignment
        if (RadiantFinalePvE.CanUse(out act))
        {
            if (ShouldBurst()) return true;
        }

        // 2. Battle Voice
        if (BattleVoicePvE.CanUse(out act))
        {
            // If Radiant Finale is up, OR we don't have Radiant Finale but ShouldBurst is true
            if (Player.HasStatus(true, StatusID.RadiantFinale) || (!RadiantFinalePvE.EnoughLevel && ShouldBurst()))
            {
                 return true;
            }
        }

        // 3. Raging Strikes
        if (RagingStrikesPvE.CanUse(out act))
        {
            // If we have other buffs, or strict burst alignment allows and we are initiating
            bool buffsActive = Player.HasStatus(true, StatusID.RadiantFinale) || Player.HasStatus(true, StatusID.BattleVoice);
            
            if (buffsActive) return true;

            // Fallback: If we are low level or solo, respect ShouldBurst
            if (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel && ShouldBurst()) return true;
        }

        // 4. Barrage
        if (BarragePvE.CanUse(out act))
        {
            // Snapshotting: Use with Raging active
            if (Player.HasStatus(true, StatusID.RagingStrikes)) return true;

            // Anti-Drift: If Raging is far away (> 20s), just use it
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(20)) return true;
        }

        // 5. Sidewinder
        if (SidewinderPvE.CanUse(out act))
        {
            // Align with Burst if possible
             if (InBurstMode) return true;
             
             // Anti-Drift
             if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }

    #endregion

    #region GCD Logic

    protected override bool GeneralGCD(out IAction? act)
    {
        // 1. Iron Jaws Maintenance & Snapshotting
        bool dotsFalling = CurrentTarget?.WillStatusEndGCD(1, 0, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false;
        
        // Critical: Snapshot Raging Strikes
        // If Raging Strikes is up but ending in < 2.5s (next GCD), refresh dots
        bool ragingEnding = Player.HasStatus(true, StatusID.RagingStrikes) && Player.WillStatusEnd(2.5f, true, StatusID.RagingStrikes);
        
        if (IronJawsPvE.CanUse(out act))
        {
            if (dotsFalling) return true;
            if (ragingEnding) return true;
        }

        // 2. Blast Arrow
        if (BlastArrowPvE.CanUse(out act))
        {
            // Logic: 
            // 1. Use immediately if in Raging Strikes
            if (Player.HasStatus(true, StatusID.RagingStrikes)) return true;

            // 2. If proc is expiring (< 3s), use it so we don't lose it
            // Note: Blast Arrow buff ID is often internal or managed by job gauge, checking action usage is safer.
            // But we typically want to save it for burst.
            // If burst is effectively ready (Raging Strikes CD < 5s), hold.
            // Otherwise use.
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(5)) return true;

            // If we are holding for burst, do NOT return true here (fall through).
            // But we must ensure we don't lose it.
            // Hard to check exact proc remaining time without status ID (StatusID.BlastArrowReady?), assuming simple logic:
            // If Raging CD is long (> 10s), just use.
            if (!RagingStrikesPvE.Cooldown.WillHaveOneCharge(10)) return true;
        }

        // 3. Apex Arrow
        if (ApexArrowPvE.CanUse(out act))
        {
            // Max Gauge = Overcap risk -> Use
            if (SoulVoice == 100) return true;

            // In Burst -> Use for damage
            if (Player.HasStatus(true, StatusID.RagingStrikes)) 
            {
                if (SoulVoice >= 80) return true;
            }

            // Otherwise, pool gauge (return false to fall through to fillers)
            // But if we are very far from burst (> 30s) and high gauge (> 80), maybe usage is okay?
            // Churin logic prefers holding for 2 minutes unless overcap.
        }

        // 4. AoE (Barrage / Procs)
        if (ShadowbitePvE.CanUse(out act)) return true;
        if (WideVolleyPvE.CanUse(out act)) return true;
        if (LadonsbitePvE.CanUse(out act)) return true; 
        if (QuickNockPvE.CanUse(out act)) return true;

        // 5. Single Target & DoT Application
        // Check DoT Boss rules
        bool isBoss = CurrentTarget?.IsBossFromIcon() ?? false;
        bool shouldDot = !OnlyDotBoss || isBoss;

        if (shouldDot)
        {
             if (StormbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Stormbite) == false)) return true;
             if (CausticBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
             
             // Low Level fallback
             if (!StormbitePvE.EnoughLevel && WindbitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.Windbite) == false)) return true;
             if (!CausticBitePvE.EnoughLevel && VenomousBitePvE.CanUse(out act) && (CurrentTarget?.HasStatus(true, StatusID.VenomousBite) == false)) return true;
        }
        
        // 6. Fillers
        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true)) return true;
        if (StraightShotPvE.CanUse(out act)) return true;
        if (BurstShotPvE.CanUse(out act)) return true;
        if (HeavyShotPvE.CanUse(out act)) return true;

        return base.GeneralGCD(out act);
    }

    #endregion
}
