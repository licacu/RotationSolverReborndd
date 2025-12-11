using Lumina.Excel.Sheets;
using System.ComponentModel;

namespace RotationSolver.RebornRotations.Ranged;

[Rotation("UltimateBard", CombatType.PvE, GameVersion = "7.35",
    Description = "Custom Bard rotation integrating Rabbs_BLM burst management.")]
[SourceCode(Path = "main/RebornRotations/Ranged/UltimateBard.cs")]
public sealed class UltimateBard : BardRotation
{
    #region Config Options

    [Range(1, 5, ConfigUnitType.Seconds, 0.1f)]
    [RotationConfig(CombatType.PvE, Name = "Buff Alignment Timer (Experimental, do not touch if you don't understand it)")]
    public float BuffAlignment { get; set; } = 1;

    [RotationConfig(CombatType.PvE, Name = "Attempt to assign Raging Strikes, Battle Voice, and Radiant Finale to specific ogcd slots (Experimental)")]
    public bool OGCDTimers { get; set; } = false;

    [RotationConfig(CombatType.PvE, Name = "Only use DOTs on targets with Boss Icon")]
    public bool DOTBoss { get; set; } = false;

    [Range(80, 100, ConfigUnitType.None, 5)]
    [RotationConfig(CombatType.PvE, Name = "Soul Voice Threshold for Apex Arrow")]
    public float SoulVoiceConfig { get; set; } = 100;

    [Range(1, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Wanderer's Minuet Uptime")]
    public float WANDTime { get; set; } = 43;

    [Range(0, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Mage's Ballad Uptime")]
    public float MAGETime { get; set; } = 43;

    [Range(0, 45, ConfigUnitType.Seconds, 1)]
    [RotationConfig(CombatType.PvE, Name = "Army's Paeon Uptime")]
    public float ARMYTime { get; set; } = 34;

    [RotationConfig(CombatType.PvE, Name = "First Song")]
    private Song FirstSong { get; set; } = Song.Wanderer;

    [RotationConfig(CombatType.PvE, Name = "Use Warden's Paean on other players")]
    public bool BRDEsuna { get; set; } = true;

    [RotationConfig(CombatType.PvE, Name = "Prevent the use of defense abilties during burst")]
    private bool BurstDefense { get; set; } = true;

    // --- BURST MANAGEMENT CONFIG (From Rabbs_BLM) ---
    [RotationConfig(CombatType.PvE, Name = "When to use Burst")]
    [Range(1, 5, ConfigUnitType.None, 1)]
    public BurstWhen When2Burst { get; set; } = BurstWhen.WithOthers;

    [RotationConfig(CombatType.PvE, Name = "Force Burst in Opener (Ignore Party Buffs)")]
    public bool ForceOpenerBurst { get; set; } = true;

    public enum BurstWhen : byte
    {
        [Description("Never (Self Managed)")] Never,
        [Description("Only to prevent Cap")] PreventCap,
        [Description("With others (checks if other people have party buffs")] WithOthers,
        [Description("Every Two Minutes (uses combat time so expect some error)")] Q2M,
        [Description("All day everyday")] Allday,
    }
    // ------------------------------------------------

    private float WANDRemainTime => 45 - WANDTime;
    private float MAGERemainTime => 45 - MAGETime;
    private float ARMYRemainTime => 45 - ARMYTime;

    private bool InBurstStatus => (!BattleVoicePvE.EnoughLevel && HasRagingStrikes)
        || (BattleVoicePvE.EnoughLevel && !RadiantFinalePvE.EnoughLevel && HasRagingStrikes && HasBattleVoice)
        || (MinstrelsCodaTrait.EnoughLevel && HasRagingStrikes && HasRadiantFinale && HasBattleVoice);

    #endregion

    #region Burst Management Logic (From Rabbs_BLM)
    private static readonly HashSet<uint> burstStatusIds =
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

    public static bool IsPartyBurst => PartyMembers?.Any(member =>
        member?.StatusList?.Any(status => burstStatusIds.Contains(status.StatusId)) == true
    ) == true;

    public bool IsBurstReady => 
           (ForceOpenerBurst && InCombat && CombatTime < 20) // Force Burst in Opener
        || (When2Burst == BurstWhen.WithOthers && IsPartyBurst) 
        || (When2Burst == BurstWhen.Q2M && IsWithinFirst15SecondsOfEvenMinute()) 
        || (When2Burst == BurstWhen.Allday)
        || (When2Burst == BurstWhen.PreventCap);

    // Helper for 2 minute window
    private bool IsWithinFirst15SecondsOfEvenMinute()
    {
        var time = CombatTime;
        // Logic: active for 0-15s, 120-135s, 240-255s...
        var cycle = time % 120;
        return cycle < 20; // Expanded to 20s to be safe
    }
    #endregion

    #region Countdown logic
    // Defines logic for actions to take during the countdown before combat starts.
    protected override IAction? CountDownAction(float remainTime)
    {
        // tincture needs to be used on -0.7s exactly
        if (remainTime <= 0.7f && UseBurstMedicine(out IAction? act))
        {
            return act;
        }
        return base.CountDownAction(remainTime);
    }
    #endregion

    #region oGCD Logic
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        if (Player.HasStatus(false, StatusID.Doom))
        {
            if (TheWardensPaeanPvE.CanUse(out act))
            {
                return true;
            }
        }

        if (nextGCD.IsTheSameTo(true, StraightShotPvE, VenomousBitePvE, WindbitePvE, IronJawsPvE))
        {
            return base.EmergencyAbility(nextGCD, out act);
        }
        else if (!RagingStrikesPvE.EnoughLevel || HasRagingStrikes)
        {
            if (((EmpyrealArrowPvE.Cooldown.IsCoolingDown && !EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD(1)) || !EmpyrealArrowPvE.EnoughLevel) && Repertoire != 3)
            {
                if (!Player.HasStatus(true, StatusID.HawksEye_3861) && BarragePvE.CanUse(out act))
                {
                    return true;
                }
            }
        }

        return base.EmergencyAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TheWardensPaeanPvE)]
    protected override bool DispelAbility(IAction nextGCD, out IAction? action)
    {
        if (BRDEsuna && TheWardensPaeanPvE.CanUse(out action))
        {
            return true;
        }
        return base.DispelAbility(nextGCD, out action);
    }

    [RotationDesc(ActionID.NaturesMinnePvE)]
    protected override bool HealSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (NaturesMinnePvE.CanUse(out act))
        {
            return true;
        }
        return base.HealSingleAbility(nextGCD, out act);
    }

    [RotationDesc(ActionID.TroubadourPvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if ((!BurstDefense || (BurstDefense && !InBurstStatus)) && TroubadourPvE.CanUse(out act))
        {
            return true;
        }
        return base.DefenseAreaAbility(nextGCD, out act);
    }

    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        if (TheWanderersMinuetPvE.CanUse(out act) && InCombat && !IsLastAbility(ActionID.ArmysPaeonPvE) && !IsLastAbility(ActionID.MagesBalladPvE))
        {
            if (SongEndAfter(ARMYRemainTime) && (Song != Song.None || Player.HasStatus(true, StatusID.ArmysEthos)))
            {
                return true;
            }
        }

        if (MagesBalladPvE.CanUse(out act) && InCombat && !IsLastAbility(ActionID.ArmysPaeonPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
        {
            if (Song == Song.Wanderer && SongEndAfter(WANDRemainTime) && (Repertoire == 0 || !HasHostilesInMaxRange))
            {
                return true;
            }

            if (Song == Song.Army && SongEndAfterGCD(2) && TheWanderersMinuetPvE.Cooldown.IsCoolingDown)
            {
                return true;
            }
        }

        if (ArmysPaeonPvE.CanUse(out act) && InCombat && !IsLastAbility(ActionID.MagesBalladPvE) && !IsLastAbility(ActionID.TheWanderersMinuetPvE))
        {
            if (TheWanderersMinuetPvE.EnoughLevel && SongEndAfter(MAGERemainTime) && Song == Song.Mage)
            {
                return true;
            }

            if (TheWanderersMinuetPvE.EnoughLevel && SongEndAfter(2) && MagesBalladPvE.Cooldown.IsCoolingDown && Song == Song.Wanderer)
            {
                return true;
            }

            if (!TheWanderersMinuetPvE.EnoughLevel && SongEndAfter(2))
            {
                return true;
            }
        }

        if (Song == Song.None && InCombat)
        {
            switch (FirstSong)
            {
                case Song.Wanderer:
                    if (TheWanderersMinuetPvE.CanUse(out act))
                    {
                        return true;
                    }

                    break;

                case Song.Army:
                    if (ArmysPaeonPvE.CanUse(out act))
                    {
                        return true;
                    }

                    break;

                case Song.Mage:
                    if (MagesBalladPvE.CanUse(out act))
                    {
                        return true;
                    }

                    break;
            }
            if (TheWanderersMinuetPvE.CanUse(out act))
            {
                return true;
            }

            if (MagesBalladPvE.CanUse(out act))
            {
                return true;
            }

            if (ArmysPaeonPvE.CanUse(out act))
            {
                return true;
            }
        }

        return base.GeneralAbility(nextGCD, out act);
    }

    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // MODIFIED: Wrapped burst entry with IsBurstReady check
        if (IsBurstReady || !MagesBalladPvE.EnoughLevel) // Always burst if low level or if Burst is Ready
        {
            if (IsBurst && Song != Song.None && MagesBalladPvE.EnoughLevel)
            {
                if (((!RadiantFinalePvE.EnoughLevel && !RagingStrikesPvE.Cooldown.IsCoolingDown)
                        || (RadiantFinalePvE.EnoughLevel && !RadiantFinalePvE.Cooldown.IsCoolingDown && RagingStrikesPvE.EnoughLevel && (!RagingStrikesPvE.Cooldown.IsCoolingDown || RagingStrikesPvE.Cooldown.WillHaveOneCharge(BuffAlignment))))
                        && CurrentTarget?.HasStatus(true, StatusID.Windbite, StatusID.Stormbite) == true && CurrentTarget?.HasStatus(true, StatusID.VenomousBite, StatusID.CausticBite) == true && BattleVoicePvE.CanUse(out act))
                {
                    return true;
                }

                if (Player.HasStatus(true, StatusID.BattleVoice) && RadiantFinalePvE.CanUse(out act))
                {
                    return true;
                }

                if (((RadiantFinalePvE.EnoughLevel && HasRadiantFinale && HasBattleVoice)
                    || (!RadiantFinalePvE.EnoughLevel && BattleVoicePvE.EnoughLevel && HasBattleVoice)
                    || (!RadiantFinalePvE.EnoughLevel && !BattleVoicePvE.EnoughLevel))
                    && RagingStrikesPvE.CanUse(out act))
                {
                    return true;
                }
            }
            else if (!MagesBalladPvE.EnoughLevel)
            {
                if (!StraightShotPvE.EnoughLevel && RagingStrikesPvE.CanUse(out act))
                {
                    return true;
                }

                if (nextGCD.IsTheSameTo(true, StraightShotPvE) && RagingStrikesPvE.CanUse(out act))
                {
                    return true;
                }
            }
        }

        if (RadiantFinalePvE.EnoughLevel && RadiantFinalePvE.Cooldown.IsCoolingDown && BattleVoicePvE.EnoughLevel && !BattleVoicePvE.Cooldown.IsCoolingDown)
        {
            return base.AttackAbility(nextGCD, out act);
        }

        if ((RagingStrikesPvE.Cooldown.IsCoolingDown || !RagingStrikesPvE.Cooldown.WillHaveOneCharge(15)) && Song != Song.None && EmpyrealArrowPvE.CanUse(out act))
        {
            return true;
        }

        if (PitchPerfectPvE.CanUse(out act, skipAoeCheck: true, skipComboCheck: true))
        {
            if (SongEndAfter(3) && Repertoire > 0)
            {
                return true;
            }

            if (Repertoire == 3)
            {
                return true;
            }

            if (Repertoire == 2 && EmpyrealArrowPvE.Cooldown.WillHaveOneChargeGCD() && RadiantFinalePvE.Cooldown.IsCoolingDown)
            {
                return true;
            }
        }

        if (SidewinderPvE.EnoughLevel)
        {
            if ((BattleVoicePvE.Cooldown.IsCoolingDown && !BattleVoicePvE.Cooldown.WillHaveOneCharge(10))
            && (!RadiantFinalePvE.EnoughLevel || (RadiantFinalePvE.EnoughLevel && RadiantFinalePvE.Cooldown.IsCoolingDown && !RadiantFinalePvE.Cooldown.WillHaveOneCharge(10)))
            && RagingStrikesPvE.Cooldown.IsCoolingDown)
            {
                if (SidewinderPvE.CanUse(out act))
                {
                    return true;
                }
            }
        }

        // Bloodletter Overcap protection
        if (BloodletterPvE.Cooldown.WillHaveXCharges(BloodletterMax, 3f))
        {
            if (RainOfDeathPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }

            if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }

            if (BloodletterPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }
        }

        // Prevents Bloodletter bumpcapping when MAGE is the song due to Repetoire procs
        if (BloodletterPvE.Cooldown.WillHaveXCharges(BloodletterMax, 7.5f) && Song == Song.Mage)
        {
            if (RainOfDeathPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }

            if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }

            if (BloodletterPvE.CanUse(out act, usedUp: true))
            {
                return true;
            }
        }

        if (BetterBloodletterLogic(out act))
        {
            return true;
        }

        return base.AttackAbility(nextGCD, out act);
    }
    #endregion

    #region GCD Logic
    protected override bool GeneralGCD(out IAction? act)
    {
        if (IronJawsPvE.CanUse(out act))
        {
            return true;
        }

        if (IronJawsPvE.CanUse(out act, skipStatusProvideCheck: true) && (IronJawsPvE.Target.Target?.WillStatusEnd(30, true, IronJawsPvE.Setting.TargetStatusProvide ?? []) ?? false))
        {
            if (Player.HasStatus(true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes) && Player.WillStatusEndGCD(1, 1, true, StatusID.BattleVoice, StatusID.RadiantFinale, StatusID.RagingStrikes))
            {
                return true;
            }
        }

        if (ResonantArrowPvE.CanUse(out act))
        {
            return true;
        }

        if (CanUseApexArrow(out act))
        {
            return true;
        }

        if (RadiantEncorePvE.CanUse(out act, skipComboCheck: true))
        {
            if (InBurstStatus)
            {
                return true;
            }
        }

        if (BlastArrowPvE.CanUse(out act))
        {
            if (!Player.HasStatus(true, StatusID.RagingStrikes))
            {
                return true;
            }

            if (Player.HasStatus(true, StatusID.RagingStrikes) && BarragePvE.Cooldown.IsCoolingDown)
            {
                return true;
            }

            if (BlastArrowPvE.Target.Target?.WillStatusEndGCD(1, 0.5f, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false)
            {
                return false;
            }
        }

        //aoe
        if (ShadowbitePvE.CanUse(out act))
        {
            return true;
        }

        if (WideVolleyPvE.CanUse(out act))
        {
            return true;
        }

        if (LadonsbitePvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
        {
            return true;
        }

        if (QuickNockPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
        {
            return true;
        }

        if (StormbitePvE.EnoughLevel)
        {
            if (StormbitePvE.CanUse(out act))
            {
                if ((DOTBoss && StormbitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
                {
                    if (StormbitePvE.Target.Target.HasStatus(true, StatusID.Stormbite) == false)
                    {
                        return true;
                    }
                }
            }
        }
        if (CausticBitePvE.EnoughLevel)
        {
            if (CausticBitePvE.CanUse(out act))
            {
                if ((DOTBoss && CausticBitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
                {
                    if (CausticBitePvE.Target.Target.HasStatus(true, StatusID.VenomousBite) == false)
                    {
                        return true;
                    }
                }
            }
        }

        if (!StormbitePvE.EnoughLevel)
        {
            if (WindbitePvE.CanUse(out act))
            {
                if ((DOTBoss && WindbitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
                {
                    if (IronJawsPvE.EnoughLevel && WindbitePvE.Target.Target.HasStatus(true, StatusID.Windbite) == false)
                    {
                        return true;
                    }
                    if (!IronJawsPvE.EnoughLevel)
                    {
                        return true;
                    }
                }
            }
        }
        if (!CausticBitePvE.EnoughLevel)
        {
            if (VenomousBitePvE.CanUse(out act))
            {
                if ((DOTBoss && VenomousBitePvE.Target.Target.IsBossFromIcon()) || !DOTBoss)
                {
                    if (IronJawsPvE.EnoughLevel && VenomousBitePvE.Target.Target.HasStatus(true, StatusID.CausticBite) == false)
                    {
                        return true;
                    }
                    if (!IronJawsPvE.EnoughLevel)
                    {
                        return true;
                    }
                }
            }
        }

        if (RefulgentArrowPvE.CanUse(out act, skipComboCheck: true))
        {
            return true;
        }

        if (!RefulgentArrowPvE.Info.EnoughLevelAndQuest() && StraightShotPvE.CanUse(out act))
        {
            return true;
        }

        if (BurstShotPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
        {
            return true;
        }

        if (HeavyShotPvE.CanUse(out act) && !HasHawksEye && !HasBarrage)
        {
            return true;
        }

        return base.GeneralGCD(out act);
    }
    #endregion

    #region Extra Methods
    private bool CanUseApexArrow(out IAction act)
    {
        if (!ApexArrowPvE.CanUse(out act))
        {
            return false;
        }

        if (QuickNockPvE.CanUse(out _) && SoulVoice == SoulVoiceConfig)
        {
            return true;
        }

        if (LadonsbitePvE.CanUse(out _) && SoulVoice == SoulVoiceConfig)
        {
            return true;
        }

        if (CurrentTarget?.WillStatusEndGCD(1, 1, true, StatusID.Windbite, StatusID.Stormbite, StatusID.VenomousBite, StatusID.CausticBite) ?? false)
        {
            return false;
        }

        if (Song == Song.Wanderer && SoulVoice >= 80 && !HasRagingStrikes)
        {
            return false;
        }

        if (SoulVoice == SoulVoiceConfig && BattleVoicePvE.Cooldown.WillHaveOneCharge(25))
        {
            return false;
        }

        if (SoulVoice >= 80 && HasRagingStrikes && Player.WillStatusEnd(10, false, StatusID.RagingStrikes))
        {
            return true;
        }

        if (SoulVoice == SoulVoiceConfig && HasRagingStrikes && HasBattleVoice)
        {
            return true;
        }

        if (Song == Song.Mage && SoulVoice >= 80 && SongEndAfter(22) && SongEndAfter(18))
        {
            return true;
        }

        if (!HasRagingStrikes && SoulVoice == SoulVoiceConfig)
        {
            return true;
        }

        return false;
    }

    private bool BetterBloodletterLogic(out IAction? act)
    {

        if (HeartbreakShotPvE.CanUse(out act, usedUp: true))
        {
            if (InBurstStatus)
            {
                return true;
            }
        }

        if (RainOfDeathPvE.CanUse(out act, usedUp: true))
        {
            if (InBurstStatus)
            {
                return true;
            }
        }

        if (BloodletterPvE.CanUse(out act, usedUp: true))
        {
            if (InBurstStatus)
            {
                return true;
            }
        }
        return false;
    }
    #endregion
}
