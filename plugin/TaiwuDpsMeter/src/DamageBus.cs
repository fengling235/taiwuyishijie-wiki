using System;
using System.Collections.Concurrent;
using System.Threading;

namespace TaiwuDpsMeter;

public enum EventKind
{
    /// <summary>房主侧 BattleEffect.Execute Postfix：完整归属（攻击者/受击者/伤害/暴击/击杀）。</summary>
    ExecuteHit,

    /// <summary>客户端 BulletManager.EnqueuePCreateEffect Prefix：飘字广播（受击者视角，无攻击者）。</summary>
    Packet,

    /// <summary>BattleRecordManager.AddDamage Postfix：游戏官方战报口径（角色序号, 技能序号, 伤害）。</summary>
    Record,

    /// <summary>BattleRecordManager.AddKillCount Postfix：官方击杀计数（结算画面同源）。
    /// 实测定案：BattleEffectResult.DeadCharacterUid 每次命中都非零，不能当击杀标记用。</summary>
    Kill,

    /// <summary>Type=10 立即结算（中毒/神火/外伤）的触发计数（2026-10-24）：EffectName=归属键
    /// （功法族前缀或效果原名），SkillIndex=分类（SettlementLedger.Poison/Fire/Bleed/Other）。</summary>
    Settlement,
}

/// <summary>三路 hook 共用的事件信封。所有字段一次性拷贝，禁止在 hook 线程里摸托管代理之外的东西。</summary>
public struct DamageEvent
{
    public EventKind Kind;
    public long TimestampMs;

    // ExecuteHit
    public int AttackerUid;
    public int TargetUid;
    public int SkillIndex;
    /// <summary>ExecuteHit 专用：skill&lt;0（触发类）时效果资源名（如 ZhongDuDamage），用于触发伤害细分。</summary>
    public string EffectName;

    // Packet（PCreateEffect 原始字段；CharacterId 语义为受击者）
    public ushort PacketTargetId;
    public ushort PacketEffectIndex;
    public uint PacketCurHp;
    public uint PacketMaxHp;

    // Record
    public int RecordCharacterId;
    public int RecordSkillId;

    // 共用
    public float Damage;
    public bool IsCritical;
    public bool IsKill;

    /// <summary>ExecuteHit 专用：本跳暴击 roll 喂给 RandomProbability 的概率原值（可>1=溢出 100% 封顶；
    /// 定案 Execute 内 0xED1B02 是唯一 roll 点）。-1=窗口内没采到（无暴击资格等）。</summary>
    public float CritChance;
}

/// <summary>
/// hook 线程 → 主线程 的事件通道 + 熔断器。
/// HarmonyX/IL2CPP 下 Prefix/Postfix 抛异常可能吞掉原方法调用，所以每个 hook 体都全量兜底；
/// 连续/累计错误超过阈值后置 Disabled，三个 hook 直接短路，宁可没数据也不能玩坏游戏。
/// </summary>
public static class DamageBus
{
    private const int ErrorBudget = 50;

    private static readonly ConcurrentQueue<DamageEvent> Queue = new();
    private static int _errors;
    private static int _published;
    private static bool _tripLogged;

    public static volatile bool Disabled;
    /// <summary>用户级停采开关（DPS✗ 按钮/F7）：true=停止入队，热 hook 空转；调参类工具不受影响。</summary>
    public static volatile bool CollectionPaused;
    public static long PublishedCount => _published;

    public static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    public static void Reset()
    {
        Queue.Clear();
        _errors = 0;
        _published = 0;
        Disabled = false;
        _tripLogged = false;
    }

    public static bool TryPublish(in DamageEvent e)
    {
        if (Disabled || CollectionPaused) return false;
        try
        {
            Queue.Enqueue(e);
            Interlocked.Increment(ref _published);
            return true;
        }
        catch (Exception ex)
        {
            ReportError("TryPublish", ex);
            return false;
        }
    }

    public static bool TryDequeue(out DamageEvent e) => Queue.TryDequeue(out e);

    /// <summary>当前积压深度（[perf] 诊断用；ConcurrentQueue.Count 为 O(1)）。</summary>
    public static int QueueDepth => Queue.Count;

    public static void ReportError(string where, Exception ex)
    {
        if (Disabled) return;
        var n = Interlocked.Increment(ref _errors);
        if (n <= 5 || n % 20 == 0)
            Plugin.Log?.LogWarning($"[{where}] 第 {n} 次异常：{ex.GetType().Name}: {ex.Message}");
        if (n >= ErrorBudget && !Disabled)
        {
            Disabled = true;
            if (!_tripLogged)
            {
                _tripLogged = true;
                Plugin.Log?.LogError($"[{where}] 累计 {n} 次异常，熔断：所有伤害 hook 已停用（游戏不受影响，面板将停止更新）。");
            }
        }
    }
}
