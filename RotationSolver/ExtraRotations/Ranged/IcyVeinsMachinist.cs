using Lumina.Excel.Sheets;
using System.ComponentModel;
using System.Diagnostics;

namespace RotationSolver.ExtraRotations.Ranged;

[Rotation("IcyVeins MCH Optimized", CombatType.PvE, GameVersion = "7.3")]
[SourceCode(Path = "main/ExtraRotations/Ranged/IcyVeinsMachinist.cs")]
[ExtraRotation]
public sealed class IcyVeinsMachinist : MachinistRotation
{
    #region Configuration Options
    
    [RotationConfig(CombatType.PvE, Name = "Use Countdown Action (Air Anchor)")]
    public bool UseCountdown { get; set; } = true;
    
    [RotationConfig(CombatType.PvE, Name = "Wait for Party Burst Windows")]
    [Description("Align burst with party buffs (Divination, Brotherhood, etc.)")]
    public bool WaitForPartyBurst { get; set; } = true;
    
    [RotationConfig(CombatType.PvE, Name = "Use 10xHB (Double Hypercharge)")]
    [Description("Use double Hypercharge for 2-minute bursts")]
    public bool UseTenXHB { get; set; } = true;
    
    [RotationConfig(CombatType.PvE, Name = "Potion Usage")]
    public PotionMode PotionUsage { get; set; } = PotionMode.BurstWindows;
    
    [RotationConfig(CombatType.PvE, Name = "Smart Battery Management")]
    [Description("Hold Queen for 90-100 battery to align with 2-min bursts")]
    public bool SmartBatteryManagement { get; set; } = true;
    
    [RotationConfig(CombatType.PvE, Name = "Reassemble Usage")]
    [Description("Which tools to use Reassemble on")]
    public ReassembleTarget ReassembleSetting { get; set; } = ReassembleTarget.ToolsOnly;
    
    [RotationConfig(CombatType.PvE, Name = "Burst Window Delay Tolerance")]
    [Range(0, 15, ConfigUnitType.None, 1)]
    [Description("How many seconds to delay burst for party alignment")]
    public float BurstDelayTolerance { get; set; } = 10f;
    
    public enum PotionMode : byte
    {
        [Description("Never use")] Never,
        [Description("Opener only")] OpenerOnly,
        [Description("Burst Windows (2-min)")] BurstWindows,
    }
    
    public enum ReassembleTarget : byte
    {
        [Description("Drill only")] DrillOnly,
        [Description("Drill and Air Anchor")] DrillAndAnchor,
        [Description("All Tools (Drill/Anchor/Chainsaw)")] ToolsOnly,
    }
    #endregion

    #region Party Burst Detection
    
    private static readonly HashSet<uint> PartyBurstStatusIds = new()
    {
        (uint)StatusID.Divination, (uint)StatusID.Brotherhood, (uint)StatusID.BattleLitany,
        (uint)StatusID.ArcaneCircle, (uint)StatusID.StarryMuse, (uint)StatusID.Embolden,
        (uint)StatusID.SearingLight, (uint)StatusID.BattleVoice, (uint)StatusID.TechnicalFinish,
        (uint)StatusID.RadiantFinale, (uint)StatusID.MeditativeBrotherhood,
    };

    private static readonly HashSet<uint> MedicatedStatusIds = new()
    {
        (uint)StatusID.Medicated, (uint)StatusID.Medicated_49,
    };

    public static bool IsPartyBurstActive => PartyMembers?.Any(m =>
        m?.StatusList?.Any(s => PartyBurstStatusIds.Contains(s.StatusId)) == true
    ) == true;
    
    public static bool IsPartyMedicated => PartyMembers?.Any(m =>
        m?.StatusList?.Any(s => MedicatedStatusIds.Contains(s.StatusId)) == true
    ) == true;
    #endregion

    #region Combat Timer & Burst State
    
    private static readonly Stopwatch CombatTimer = new();
    private static int _lastKnownMinute = -1;
    private static bool _tenXHBActive = false;
    private static int _hcCount = 0;
    private static DateTime _tenXHBStart = DateTime.MinValue;
    
    public static double CombatTime => CombatTimer.Elapsed.TotalSeconds;
    
    // 2-minute burst windows: 0:00-0:15, 2:00-2:15, 4:00-4:15...
    public static bool IsInTwoMinuteBurst
    {
        get
        {
            if (!InCombat) return false;
            var minute = (int)(CombatTime / 60);
            var secondInMinute = CombatTime % 60;
            // Every even minute (0, 2, 4...)
            return minute % 2 == 0 && secondInMinute < 15;
        }
    }
    
    // Pre-burst preparation (18s before 2-min mark)
    public static bool IsPreBurstPhase
    {
        get
        {
            if (!InCombat) return false;
            var nextBurst = Math.Ceiling(CombatTime / 120) * 120;
            var timeToBurst = nextBurst - CombatTime;
            return timeToBurst <= 18 && timeToBurst > 0;
        }
    }
    
    // 1-minute mini-burst (opener sonrası)
    public static bool IsInOneMinuteBurst
    {
        get
        {
            if (!InCombat) return false;
            var minute = (int)(CombatTime / 60);
            var secondInMinute = CombatTime % 60;
            return minute % 2 == 1 && secondInMinute < 12; // Odd minutes (1, 3, 5...)
        }
    }
    
    public bool ShouldWaitForParty => WaitForPartyBurst && IsPartyBurstActive;
    
    public bool IsBurstReady => !WaitForPartyBurst || IsPartyBurstActive || IsInTwoMinuteBurst;
    
    public static bool IsTenXHBActive => _tenXHBActive && 
        (DateTime.Now - _tenXHBStart).TotalSeconds < 20;
    #endregion

    #region Skill Holding Logic (Optimized)
    
    // Reassemble sakla mı?
    public bool ShouldHoldReassemble
    {
        get
        {
            // 2 charge varsa ve biri overcap olacaksa kullan
            if (ReassemblePvE.Cooldown.CurrentCharges == 2 && 
                ReassemblePvE.Cooldown.RecastTimeElapsed > 50)
                return false;
            
            // Pre-burst'ta 1+ charge varsa ve tool GCD geliyorsa sakla
            if (IsPreBurstPhase && ReassemblePvE.Cooldown.CurrentCharges >= 1)
                return true;
                
            return false;
        }
    }
    
    // Excavator sakla mı?
    public bool ShouldHoldExcavator
    {
        get
        {
            if (!IsPreBurstPhase) return false;
            
            // Eğer bu son charge ise ve burst <15s ise sakla
            var timeToBurst = Math.Ceiling(CombatTime / 120) * 120 - CombatTime;
            if (timeToBurst < 15 && HasExcavatorReady && 
                ChainSawPvE.Cooldown.CurrentCharges == 0)
                return true;
                
            return false;
        }
    }
    
    // Full Metal Field sakla mı?
    public bool ShouldHoldFMF
    {
        get
        {
            if (!HasFullMetalMachinist) return false;
            
            // FMF 30 saniye sürer, burst zamanlaması için sakla
            var timeToBurst = Math.Ceiling(CombatTime / 120) * 120 - CombatTime;
            if (timeToBurst < 12 && !IsInTwoMinuteBurst)
                return true;
                
            return false;
        }
    }
    
    // Drill charge yönetimi
    public bool ShouldUseDrillNow
    {
        get
        {
            // 2 charge varsa biri kullan (overcap önle)
            if (DrillPvE.Cooldown.CurrentCharges == 2)
                return true;
            
            // Pre-burst'ta 1 charge varsa kullanma (emergency)
            if (IsPreBurstPhase && DrillPvE.Cooldown.CurrentCharges == 1)
                return false;
                
            return true;
        }
    }
    #endregion

    #region Battery & Queen Management (Critical!)
    
    public bool ShouldSummonQueenNow
    {
        get
        {
            if (IsRobotActive || Battery < 50) return false;
            
            // Eğer hedef ölüyorsa hemen summon
            if (CurrentTarget?.IsDying() == true && CurrentTarget?.CurrentHp < 100000)
                return true;
            
            if (!SmartBatteryManagement) return true; // Manuel mod
            
            // 90-100 battery'de 2-dakika burst'a denk getir
            var timeToBurst = Math.Ceiling(CombatTime / 120) * 120 - CombatTime;
            
            if (Battery >= 90 && timeToBurst < 12)
                return true; // Burst'a denk getir
                
            if (Battery >= 100)
                return true; // Overcap önle
                
            if (!IsPreBurstPhase && Battery >= 50)
                return true; // Normal durumda kullan
                
            return false;
        }
    }
    
    // Queen overdrive - ölüm riski varsa
    public bool ShouldUseQueenOverdrive
    {
        get
        {
            if (!IsRobotActive) return false;
            
            // Hedef ölüyorsa veya uzaklaşıyorsa
            if (CurrentTarget?.IsDying() == true && CurrentTarget?.CurrentHp < 50000)
                return true;
                
            return false;
        }
    }
    #endregion

    #region Potion Logic
    
    public bool ShouldUsePotion
    {
        get
        {
            if (PotionUsage == PotionMode.Never) return false;
            if (PotionUsage == PotionMode.OpenerOnly && CombatTime > 20) return false;
            if (PotionUsage == PotionMode.BurstWindows && !IsInTwoMinuteBurst) return false;
            
            // Zaten potion var mı?
            if (Player?.StatusList?.Any(s => MedicatedStatusIds.Contains(s.StatusId)) == true)
                return false;
                
            return true;
        }
    }
    #endregion

    #region Combat Events
    
    public override void OnCombatStarted(IGameObject target)
    {
        base.OnCombatStarted(target);
        CombatTimer.Restart();
        _tenXHBActive = false;
        _hcCount = 0;
        _lastKnownMinute = 0;
    }
    
    public override void OnCombatEnded()
    {
        base.OnCombatEnded();
        CombatTimer.Stop();
        _tenXHBActive = false;
        _hcCount = 0;
    }
    #endregion

    #region Countdown & Opener
    
    protected override IAction? CountDownAction(float remainTime)
    {
        if (!UseCountdown) return base.CountDownAction(remainTime);
        
        IAction act;
        
        // -3s: Potion
        if (remainTime <= 3f && ShouldUsePotion && CombatTime < 5)
        {
            if (UseBurstMedicine(out act)) return act;
        }
        
        // -2s: Reassemble
        if (remainTime <= 2f && remainTime > 0.8f)
        {
            if (ReassemblePvE.CanUse(out act)) return act;
        }
        
        // -1.5s: Air Anchor (cast time ~1.5s)
        if (remainTime <= AirAnchorPvE.Info.CastTime + CountDownAhead && remainTime > 0.3f)
        {
            if (AirAnchorPvE.CanUse(out act)) return act;
        }
        
        return base.CountDownAction(remainTime);
    }
    #endregion

    #region Emergency Ability (Burst Setup)
    
    [RotationDesc]
    protected override bool EmergencyAbility(IAction nextGCD, out IAction? act)
    {
        // Potion (2-min burst)
        if (ShouldUsePotion && IsInTwoMinuteBurst && CombatTime > 30 &&
            (nextGCD.IsTheSameTo(true, AirAnchorPvE) || nextGCD.IsTheSameTo(true, ChainSawPvE)))
        {
            if (UseBurstMedicine(out act)) return true;
        }
        
        // Barrel Stabilizer - Pre-burst veya opener
        if (BarrelStabilizerPvE.CanUse(out act) && InCombat)
        {
            // Opener'da 3. GCD civarı
            if (CombatTime < 10) return true;
            
            // Pre-burst'ta
            if (IsPreBurstPhase && !HasHypercharged && !HasFullMetalMachinist)
                return true;
        }
        
        // Queen summon
        if (ShouldSummonQueenNow && AutomatonQueenPvE.CanUse(out act)) return true;
        if (ShouldUseQueenOverdrive && QueenOverdrivePvE.CanUse(out act)) return true;
        
        // 10xHB Wildfire başlatma (sadece 2-min burst'ta)
        if (UseTenXHB && IsInTwoMinuteBurst && !IsTenXHBActive && !WildfirePvE.Cooldown.IsCoolingDown)
        {
            if (nextGCD.IsTheSameTo(true, HeatedSplitShotPvE, HeatedSlugShotPvE, HeatedCleanShotPvE) ||
                nextGCD.IsTheSameTo(true, DrillPvE, AirAnchorPvE))
            {
                // 6 farklı attack için GCD hazır mı kontrolü
                if ((Heat >= 50 || HasHypercharged) && IsBurstReady)
                {
                    if (WildfirePvE.CanUse(out act))
                    {
                        _tenXHBActive = true;
                        _hcCount = 0;
                        _tenXHBStart = DateTime.Now;
                        return true;
                    }
                }
            }
        }
        
        // 1-min burst (single Hypercharge) - 10xHB yoksa
        if (!UseTenXHB && IsInOneMinuteBurst && !IsOverheated && !WildfirePvE.Cooldown.IsCoolingDown)
        {
            if ((Heat >= 50 || HasHypercharged) && nextGCD.IsTheSameTo(true, HeatedSplitShotPvE))
            {
                if (WildfirePvE.CanUse(out act)) return true;
            }
        }
        
        // Hypercharge yönetimi
        if (IsTenXHBActive && !IsOverheated && (Heat >= 50 || HasHypercharged))
        {
            // İlk HC
            if (_hcCount == 0)
            {
                if (HyperchargePvE.CanUse(out act))
                {
                    _hcCount = 1;
                    return true;
                }
            }
            // İkinci HC (10xHB)
            else if (_hcCount == 1 && OverheatedStacks == 0 && WildfirePvE.Cooldown.RecastTimeElapsed < 8)
            {
                if (HyperchargePvE.CanUse(out act))
                {
                    _hcCount = 2;
                    return true;
                }
            }
        }
        
        // Normal single Hypercharge (10xHB değilse)
        if (!IsTenXHBActive && !IsOverheated && (Heat >= 50 || HasHypercharged) && 
            !WildfirePvE.Cooldown.IsCoolingDown && WildfirePvE.Cooldown.RecastTimeElapsed > 5)
        {
            if (HyperchargePvE.CanUse(out act)) return true;
        }
        
        // Detonator
        if (DetonatorPvEReady && WildfirePvE.Cooldown.RecastTimeElapsed > 9.5f)
        {
            if (DetonatorPvE.CanUse(out act)) return true;
        }
        
        // 10xHB bitiş kontrolü
        if (_tenXHBActive && !IsOverheated && _hcCount >= 2)
        {
            _tenXHBActive = false;
            _hcCount = 0;
        }
        
        return base.EmergencyAbility(nextGCD, out act);
    }
    #endregion

    #region General Ability (Reassemble)
    
    [RotationDesc(ActionID.ReassemblePvE)]
    protected override bool GeneralAbility(IAction nextGCD, out IAction? act)
    {
        act = null;
        
        // Reassemble kullanımı - Tool'lara göre ayar
        if (!HasReassembled && !ShouldHoldReassemble)
        {
            var isValidTarget = ReassembleSetting switch
            {
                ReassembleTarget.DrillOnly => nextGCD.IsTheSameTo(true, DrillPvE),
                ReassembleTarget.DrillAndAnchor => nextGCD.IsTheSameTo(true, DrillPvE, AirAnchorPvE),
                ReassembleTarget.ToolsOnly => nextGCD.IsTheSameTo(true, DrillPvE, AirAnchorPvE, ChainSawPvE),
                _ => false
            };
            
            if (isValidTarget && ReassemblePvE.CanUse(out act)) return true;
        }
        
        return base.GeneralAbility(nextGCD, out act);
    }
    #endregion

    #region Attack Ability (oGCD Weaving)
    
    [RotationDesc(ActionID.GaussRoundPvE, ActionID.RicochetPvE, ActionID.DoubleCheckPvE, ActionID.CheckmatePvE)]
    protected override bool AttackAbility(IAction nextGCD, out IAction? act)
    {
        // Hypercharge içinde - her GCD arasına 2 oGCD
        if (IsOverheated)
        {
            // Öncelik: Double Check > Checkmate > Gauss Round > Ricochet
            if (DoubleCheckPvE.CanUse(out act, usedUp: true)) return true;
            if (CheckmatePvE.CanUse(out act, usedUp: true)) return true;
            if (GaussRoundPvE.CanUse(out act, usedUp: true)) return true;
            if (RicochetPvE.CanUse(out act, usedUp: true)) return true;
        }
        else
        {
            // Normal weaving - charge overcap kontrolü
            if (DoubleCheckPvE.Cooldown.CurrentCharges == DoubleCheckPvE.Cooldown.MaxCharges &&
                DoubleCheckPvE.CanUse(out act)) return true;
                
            if (CheckmatePvE.Cooldown.CurrentCharges == CheckmatePvE.Cooldown.MaxCharges &&
                CheckmatePvE.CanUse(out act)) return true;
                
            if (GaussRoundPvE.CanUse(out act)) return true;
            if (RicochetPvE.CanUse(out act)) return true;
        }
        
        return base.AttackAbility(nextGCD, out act);
    }
    #endregion

    #region GCD Logic
    
    protected override bool GeneralGCD(out IAction? act)
    {
        // Hedef ölüm kontrolü - beklemeyi bırak
        var targetDying = CurrentTarget?.IsDying() == true && CurrentTarget?.CurrentHp < 50000;
        
        // === TOOLS (Öncelik Sırası) ===
        
        // Air Anchor (Reassembled varsa öncelikli)
        if (AirAnchorPvE.CanUse(out act) && (HasReassembled || !ShouldHoldReassemble || targetDying))
        {
            if (targetDying || !IsPreBurstPhase || HasReassembled) return true;
        }
        
        // Drill (Charge yönetimi)
        if (DrillPvE.CanUse(out act) && (ShouldUseDrillNow || targetDying))
        {
            // Eğer Reassemble varsa Drill'e kullan (opener veya burst)
            if (HasReassembled || !IsPreBurstPhase || DrillPvE.Cooldown.CurrentCharges == 2) 
                return true;
        }
        
        // Chain Saw → Excavator
        if (ChainSawPvE.CanUse(out act))
        {
            if (targetDying || !ShouldHoldExcavator) return true;
        }
        
        if (ExcavatorPvE.CanUse(out act) && HasExcavatorReady && 
            (targetDying || !ShouldHoldExcavator))
        {
            return true;
        }
        
        // Full Metal Field (saklama kontrolü)
        if (FullMetalFieldPvE.CanUse(out act) && HasFullMetalMachinist &&
            (targetDying || !ShouldHoldFMF))
        {
            return true;
        }
        
        // === HYPERCHARGE GCD'leri ===
        if (IsOverheated)
        {
            // AoE: Auto Crossbow (3+ target)
            if (GetAoeCount(AutoCrossbowPvE) >= 3)
            {
                if (AutoCrossbowPvE.CanUse(out act)) return true;
            }
            
            // Single: Blazing Shot (90+) veya Heat Blast
            if (BlazingShotPvE.CanUse(out act)) return true;
            if (HeatBlastPvE.CanUse(out act)) return true;
        }
        
        // === FILLER ===
        
        // Heated Combo
        if (HeatedCleanShotPvE.CanUse(out act)) return true;
        if (HeatedSlugShotPvE.CanUse(out act)) return true;
        if (HeatedSplitShotPvE.CanUse(out act)) return true;
        
        // Low level fallback
        if (CleanShotPvE.CanUse(out act)) return true;
        if (SlugShotPvE.CanUse(out act)) return true;
        if (SplitShotPvE.CanUse(out act)) return true;
        
        return base.GeneralGCD(out act);
    }
    #endregion

    #region AoE Logic
    
    private static int GetAoeCount(IBaseAction action)
    {
        // Icy Veins: Spread Shot/Scattergun range kontrolü
        int count = 0;
        if (AllHostileTargets == null) return 0;
        
        foreach (var t in AllHostileTargets.Where(t => t.DistanceToPlayer() < action.Info.Range))
        {
            count = AllHostileTargets.Count(o => 
                Vector3.Distance(t.Position, o.Position) < action.Info.EffectRange + t.HitboxRadius);
        }
        return count;
    }
    
    protected override bool GeneralGCDForMoving(out IAction? act)
    {
        // Movement GCD'leri
        if (IsMoving)
        {
            // Scattergun (3+ target)
            if (GetAoeCount(ScattergunPvE) >= 3 && ScattergunPvE.CanUse(out act)) return true;
        }
        
        return base.GeneralGCDForMoving(out act);
    }
    #endregion

    #region Defense
    
    [RotationDesc(ActionID.TacticianPvE, ActionID.DismantlePvE)]
    protected override bool DefenseAreaAbility(IAction nextGCD, out IAction? act)
    {
        if (TacticianPvE.CanUse(out act)) return true;
        return base.DefenseAreaAbility(nextGCD, out act);
    }
    
    protected override bool DefenseSingleAbility(IAction nextGCD, out IAction? act)
    {
        if (DismantlePvE.CanUse(out act)) return true;
        return base.DefenseSingleAbility(nextGCD, out act);
    }
    #endregion

    #region Debug Display
    
    public unsafe override void DisplayRotationStatus()
    {
        ImGui.Text("=== IcyVeins MCH Optimized ===");
        ImGui.Text($"Combat: {CombatTime:F1}s | 2-Min Burst: {IsInTwoMinuteBurst}");
        ImGui.Text($"Pre-Burst: {IsPreBurstPhase} | 10xHB: {IsTenXHBActive}");
        ImGui.Text($"Battery: {Battery}/100 | Queen: {IsRobotActive}");
        ImGui.Text($"Heat: {Heat} | HC Stacks: {OverheatedStacks}");
        ImGui.Text($"Wildfire CD: {WildfirePvE.Cooldown.RecastTimeRemaining:F1}s");
        ImGui.Text($"Drill Charges: {DrillPvE.Cooldown.CurrentCharges}/2");
        ImGui.Text($"Hold Reassemble: {ShouldHoldReassemble} | Hold FMF: {ShouldHoldFMF}");
        
        base.DisplayRotationStatus();
    }
    #endregion
}