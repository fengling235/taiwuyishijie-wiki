using System;
using System.Collections.Generic;
using System.Linq;

namespace TaiwuDpsMeter;

/// <summary>
/// 主线程账本：消费三路事件流，产出渲染快照。
/// 口径约定（重要）：
///  - ExecuteHit（房主）是唯一带归属的口径，玩家榜/击杀/暴击全部来自它；
///  - Packet 的 Damage 只在客人模式计入全队总伤（房主模式会与 ExecuteHit 重复记账，禁止叠加），
///    两种模式下 Packet 的 CurrentHp 都用于「全队实扣/溢出估算」（按受击者 telescoping 求差，遇回升视为治疗并重置基线）；
///  - Record（官方战报）单独累计，仅用于对账显示。
/// </summary>
internal sealed class Ledger
{
    /// <summary>
    /// 滚动暴击窗口：只存 (时刻, 是否暴击) 环。锚点由调用方给「该行自己最后一跳的时刻」——
    /// 与 DPS 冻结同一原则：伤害一停窗口跟着冻结（打完 boss 面板不再变），窗口内没数据就是全程没跳。
    /// </summary>
    internal sealed class CritRing
    {
        private readonly List<long> _times = new();
        private readonly List<bool> _crits = new();
        private const int Cap = 256;

        internal void Add(long t, bool crit)
        {
            _times.Add(t);
            _crits.Add(crit);
            if (_times.Count > Cap) { _times.RemoveRange(0, _times.Count - Cap); _crits.RemoveRange(0, _crits.Count - Cap); }
        }

        /// <summary>anchorMs 一般取本行 LastMs：窗口 = [anchor − windowMs, anchor]。倒序索引遍历，无分配。</summary>
        internal (long Hits, long Crits) Window(double anchorMs, double windowMs)
        {
            long h = 0, c = 0;
            for (int i = _times.Count - 1; i >= 0; i--)
            {
                if (anchorMs - _times[i] > windowMs) break;
                h++;
                if (_crits[i]) c++;
            }
            return (h, c);
        }
    }

    /// <summary>
    /// 滚动窗口伤害账本（「近N秒DPS」列）：30 个 1 秒桶环形复用，写入按当前秒落桶（过期桶清零），
    /// 读取只累计窗口内的桶。写读均 O(1)/O(桶数) 无分配；窗口秒数取 Plugin.CfgBurstWindow（5~30）。
    /// </summary>
    internal sealed class RollingWindow
    {
        private const int Buckets = 30;
        private readonly double[] _sum = new double[Buckets];
        private readonly long[] _sec = new long[Buckets];

        internal void Add(double nowMs, double dmg)
        {
            long cur = (long)(nowMs / 1000.0);
            int slot = (int)(cur % Buckets);
            if (_sec[slot] != cur) { _sum[slot] = 0; _sec[slot] = cur; }
            _sum[slot] += dmg;
        }

        /// <summary>窗口内伤害总和（秒粒度）。</summary>
        internal double Sum(double nowMs, int windowSec)
        {
            long cur = (long)(nowMs / 1000.0);
            double s = 0;
            for (int i = 0; i < Buckets; i++)
                if (cur - _sec[i] < windowSec) s += _sum[i];
            return s;
        }

        internal void Clear() => Array.Clear(_sum, 0, _sum.Length);
    }

    internal sealed class SkillStats
    {
        public int SkillIndex;
        public double Damage;
        public long Hits;
        public long Crits;
        public double FirstMs = -1; // 本行首跳（功法行 DPS 分母用：活动窗口）
        public double LastMs;
        public readonly CritRing Ring = new();
        public readonly RollingWindow Burst = new();
    }

    /// <summary>触发类伤害细分（skill=-1 按效果资源名分桶，如 ZhongDuDamage/ShenHuoBlastDamage）。</summary>
    internal sealed class TriggerStats
    {
        public string Name;
        public double Damage;
        public long Hits;
        public long Crits;
        public double FirstMs = -1;
        public double LastMs;
        public readonly CritRing Ring = new();
        public readonly RollingWindow Burst = new();
    }

    internal sealed class ActorStats
    {
        public int Uid;
        public double TotalDamage;
        public double SegmentDamage;
        public double SegmentStartMs = -1;
        public double LastEventMs;
        public double FirstEventMs = -1; // 整场首跳（技能行 DPS 的共用分母：Details!/FFLogs 口径=技能伤害÷战斗时长）
        public long Hits;
        public long Crits;
        public long Kills;
        public double ClosedSegDps;
        public bool SegmentOpen;
        /// <summary>首次解析成功时的名字快照：玩家离场后 Registry 会退化成 #uid，用它保住显示名。</summary>
        public string DisplayName;
        /// <summary>解析成功快照（类别+主人）：回大厅后 Registry 全灭，靠它保住树形归属（灵侍不掉出主人名下）。</summary>
        public CharKind KindSnap;
        public int? OwnerUidSnap;
        public bool SnapValid;
        public readonly Dictionary<int, SkillStats> BySkill = new();
        public readonly Dictionary<string, TriggerStats> ByTrigger = new();
        public readonly CritRing Ring = new();
        public readonly RollingWindow Burst = new();

        public SkillStats GetOrCreateSkill(int skillIndex)
        {
            if (!BySkill.TryGetValue(skillIndex, out var s))
            {
                s = new SkillStats { SkillIndex = skillIndex };
                BySkill[skillIndex] = s;
            }
            return s;
        }

        public List<SkillStats> SortedSkills()
            => BySkill.Values.OrderByDescending(s => s.Damage).ToList();
    }

    /// <summary>按攻击者的对 Boss 伤害（玩家排行段用：Details! 的 boss segment 主视图=玩家排行）。</summary>
    internal sealed class AttackerAgg
    {
        public double Damage;
        public double FirstMs = -1;
        public double LastMs;
        public readonly CritRing Ring = new();
    }

    /// <summary>按受击目标聚合的功法/异常伤害（Boss 面板用：跨玩家全队合计）。</summary>
    internal sealed class BossAgg
    {
        // 无敌/离场判定阈值：对 Boss 的伤害断流超过 3 秒视为转阶段/无敌/消失窗口
        // （4 人局打 Boss 命中间隙亚秒级；Boss 无敌期团队的命中走 IsWuDi 保底 1 点，被 hook 门控丢弃，同样表现为断流）
        private const double IdleGapMs = 3000;

        public readonly Dictionary<int, SkillStats> BySkill = new();
        public readonly Dictionary<string, TriggerStats> ByTrigger = new();
        public readonly Dictionary<int, AttackerAgg> ByAttacker = new();
        public double Total;
        public double FirstMs = -1; // 对该 Boss 的首跳（战斗时间起点，WCL「遭遇时长」口径）
        public double LastMs;      // 末跳（战斗时间终点——Boss 死于 DoT 时即死亡时刻）
        public double IdleMs;      // 断流间隙累计（WCL「停机时间」口径：无敌/转阶段/消失）

        public void Add(double t, int attackerUid, int skillIdx, string effectName, double dmg, bool crit)
        {
            Total += dmg;
            if (FirstMs < 0) FirstMs = t;
            else if (t - LastMs > IdleGapMs) IdleMs += t - LastMs;
            LastMs = t;

            if (!ByAttacker.TryGetValue(attackerUid, out var aa))
            {
                aa = new AttackerAgg();
                ByAttacker[attackerUid] = aa;
            }
            aa.Damage += dmg;
            if (aa.FirstMs < 0) aa.FirstMs = t;
            aa.LastMs = t;
            aa.Ring.Add((long)t, crit);

            if (skillIdx >= 0)
            {
                if (!BySkill.TryGetValue(skillIdx, out var s))
                {
                    s = new SkillStats { SkillIndex = skillIdx };
                    BySkill[skillIdx] = s;
                }
                s.Damage += dmg; s.Hits++;
                if (crit) s.Crits++;
                if (s.FirstMs < 0) s.FirstMs = t;
                s.LastMs = t;
                s.Ring.Add((long)t, crit);
            }
            else if (!string.IsNullOrEmpty(effectName))
            {
                if (!ByTrigger.TryGetValue(effectName, out var tr))
                {
                    tr = new TriggerStats { Name = effectName };
                    ByTrigger[effectName] = tr;
                }
                tr.Damage += dmg; tr.Hits++;
                if (crit) tr.Crits++;
                if (tr.FirstMs < 0) tr.FirstMs = t;
                tr.LastMs = t;
                tr.Ring.Add((long)t, crit);
            }
        }
    }

    internal sealed class TargetStats
    {
        public int Key;
        public double Damage;
        public long Hits;
        public uint LastHp;
        public uint MaxHp;
        public bool HasHp;
        public double Healed;
    }

    private readonly Dictionary<int, ActorStats> _actors = new();
    // 房主侧按 targetUid（Execute 流）；客人侧按 PacketTargetId —— 两套键空间分开存
    private readonly Dictionary<int, TargetStats> _hostTargets = new();
    private readonly Dictionary<int, TargetStats> _guestTargets = new();
    private readonly Dictionary<int, double> _recordByChar = new();
    // 对账仪表化：官方流与 Execute 流各按技能序号累计（诊断「同名牌行对不上」到底差在哪个技能上）
    private readonly Dictionary<int, double> _recordBySkill = new();
    private readonly Dictionary<int, double> _executeBySkill = new();
    // Boss 面板：按受击目标的功法/异常聚合。先对所有受击目标记账，每帧探测后把非 Boss 的剪掉
    // （这样 Boss 出手前几跳也不会丢；chaff 目标活不过一个渲染周期）
    private readonly Dictionary<int, BossAgg> _bossByTarget = new();

    private double _executeTeamDamage;
    private double _guestTeamDamage;
    // 全队伤害滚动窗（趋势图/峰值用）：房主走 Execute 流、客人走飘字流，与总伤同口径（两路互斥不会双记）
    private readonly RollingWindow _teamBurst = new();
    private double _packetTeamActual;
    private double _enemyOutDamage;
    private double _recordTotal;
    private double _recordDropped;

    public double ExecuteTeamDamage => _executeTeamDamage;
    public double GuestTeamDamage => _guestTeamDamage;
    public double PacketTeamActual => _packetTeamActual;
    public double RecordTotal => _recordTotal;
    /// <summary>AddDamage 被调用但内部丢弃的量（characterId≥4：侍从/召唤/敌方/友方 NPC）——参数层可见、官方不落账。</summary>
    public double RecordDropped => _recordDropped;
    public double EnemyOutDamage => _enemyOutDamage;
    /// <summary>全队滚动窗窗口内伤害和（房主=Execute 流 / 客人=飘字流）。</summary>
    public double TeamBurstSum(double nowMs, int windowSec) => _teamBurst.Sum(nowMs, windowSec);
    public IReadOnlyDictionary<int, double> RecordByChar => _recordByChar;
    public IReadOnlyDictionary<int, double> ExecuteBySkill => _executeBySkill;
    public IReadOnlyDictionary<int, double> RecordBySkill => _recordBySkill;
    public IReadOnlyDictionary<int, BossAgg> BossByTarget => _bossByTarget;

    public void Consume(in DamageEvent e, CharacterRegistry registry, bool isHost, bool aggregateSquire, double idleMs)
    {
        switch (e.Kind)
        {
            case EventKind.ExecuteHit: OnExecuteHit(e, registry, aggregateSquire, idleMs); break;
            case EventKind.Packet: OnPacket(e, isHost); break;
            case EventKind.Record: OnRecord(e); break;
            case EventKind.Kill: OnKill(e, registry, aggregateSquire); break;
        }
    }

    private void OnExecuteHit(in DamageEvent e, CharacterRegistry registry, bool aggregateSquire, double idleMs)
    {
        var entry = registry.Resolve(e.AttackerUid);

        if (entry.Kind == CharKind.Enemy)
        {
            // 敌方攻击者=对我方的伤害，不进玩家榜，单独累计备查
            _enemyOutDamage += e.Damage;
            return;
        }

        if (entry.Kind == CharKind.Unknown)
        {
            // 解析失败的攻击者：打到玩家/侍从身上的按敌方输出计（承受）；
            // 其余一律丢弃——绝不给 Unknown 建玩家行（回营地后冒出的 #1/#5/#8 幽灵行就是这条漏的）
            var targetEntry = registry.Resolve(e.TargetUid);
            if (targetEntry.Kind == CharKind.Player || targetEntry.Kind == CharKind.Squire || targetEntry.Kind == CharKind.Summon)
                _enemyOutDamage += e.Damage;
            return;
        }

        int keyUid = e.AttackerUid;
        if (aggregateSquire && (entry.Kind == CharKind.Squire || entry.Kind == CharKind.Summon) && entry.OwnerUid.HasValue)
            keyUid = entry.OwnerUid.Value;

        var a = GetOrCreateActor(keyUid);
        if (entry.Kind != CharKind.Unknown && entry.Kind != CharKind.Enemy)
        {
            // 活解析期间持续刷新快照（名字/类别/主人）——大厅里 Registry 退化后靠这份快照保住显示与归属
            a.SnapValid = true;
            a.KindSnap = entry.Kind;
            a.OwnerUidSnap = entry.OwnerUid;
            a.DisplayName ??= entry.Name;
        }
        double now = e.TimestampMs;
        a.TotalDamage += e.Damage;
        a.Burst.Add(now, e.Damage);
        a.Hits++;
        if (e.IsCritical) a.Crits++;
        a.Ring.Add((long)now, e.IsCritical);

        var sk = a.GetOrCreateSkill(e.SkillIndex);
        sk.Damage += e.Damage;
        sk.Burst.Add(now, e.Damage);
        sk.Hits++;
        if (e.IsCritical) sk.Crits++;
        if (sk.FirstMs < 0) sk.FirstMs = now;
        sk.LastMs = now;
        sk.Ring.Add((long)now, e.IsCritical);
        if (e.SkillIndex >= 0)
            _executeBySkill[e.SkillIndex] = (_executeBySkill.TryGetValue(e.SkillIndex, out var es) ? es : 0) + e.Damage;

        // 触发类细分：skill=-1 时按效果资源名（毒跳/神火引爆/流血/功法词条效果…）分桶
        if (e.SkillIndex < 0 && !string.IsNullOrEmpty(e.EffectName))
        {
            if (!a.ByTrigger.TryGetValue(e.EffectName, out var tr))
            {
                tr = new TriggerStats { Name = e.EffectName };
                a.ByTrigger[e.EffectName] = tr;
            }
            tr.Damage += e.Damage;
            tr.Burst.Add(now, e.Damage);
            tr.Hits++;
            if (e.IsCritical) tr.Crits++;
            if (tr.FirstMs < 0) tr.FirstMs = now;
            tr.LastMs = now;
            tr.Ring.Add((long)now, e.IsCritical);
        }

        if (!a.SegmentOpen || now - a.LastEventMs > idleMs)
        {
            a.SegmentOpen = true;
            a.SegmentStartMs = now;
            a.SegmentDamage = 0;
            a.ClosedSegDps = 0;
        }
        if (a.FirstEventMs < 0) a.FirstEventMs = now;
        a.SegmentDamage += e.Damage;
        a.LastEventMs = now;

        _executeTeamDamage += e.Damage;
        _teamBurst.Add(now, e.Damage);

        var t = GetOrCreateTarget(_hostTargets, e.TargetUid);
        t.Damage += e.Damage;
        t.Hits++;

        // Boss 面板聚合：只记玩家/侍从/召唤打出的（敌方/Unknown 已在上面 return 掉）。
        // 是否真是 Boss 由探测决定，先记后剪（PruneBossAgg）。
        if (!_bossByTarget.TryGetValue(e.TargetUid, out var bagg))
        {
            bagg = new BossAgg();
            _bossByTarget[e.TargetUid] = bagg;
        }
        bagg.Add(now, keyUid, e.SkillIndex, e.EffectName, e.Damage, e.IsCritical);
    }

    private void OnPacket(in DamageEvent e, bool isHost)
    {
        // 房主模式不消费飘字流：Execute 流才是权威账目，且飘字含「敌方打玩家」、
        // 目标序号空间与玩家序号可能碰撞导致无法过滤（Debug 日志仍会记录 [pkt] 供冒烟比对）
        if (isHost) return;

        var t = GetOrCreateTarget(_guestTargets, e.PacketTargetId);
        if (t.HasHp)
        {
            if (e.PacketCurHp > t.LastHp)
            {
                // 血量回升=治疗事件不在流里，重置基线且不计实扣
                t.Healed += e.PacketCurHp - t.LastHp;
            }
            else
            {
                _packetTeamActual += t.LastHp - e.PacketCurHp;
            }
        }
        t.HasHp = true;
        t.LastHp = e.PacketCurHp;
        if (e.PacketMaxHp > t.MaxHp) t.MaxHp = e.PacketMaxHp;

        // 客人模式：飘字流是唯一伤害来源（无归属，只有全队合计与按受击者）
        _guestTeamDamage += e.Damage;
        _teamBurst.Add(e.TimestampMs, e.Damage);
        t.Damage += e.Damage;
        t.Hits++;
    }

    private void OnRecord(in DamageEvent e)
    {
        _recordByChar.TryGetValue(e.RecordCharacterId, out var cur);
        _recordByChar[e.RecordCharacterId] = cur + e.Damage;
        // AddDamage 内部实锤（0xF0A344：cmp ebx,4 jge ret）：characterId≥4 的调用直接丢弃——
        // 只有玩家槽 0-3 真正落账，侍从/召唤/敌方/友方 NPC 是参数级空调用。
        // 对账口径只把 0-3 计入「官方真实落账」，被丢的单独累计备查。
        if (e.RecordCharacterId is < 0 or >= 4)
        {
            _recordDropped += e.Damage;
            return;
        }
        _recordTotal += e.Damage;
        if (e.RecordSkillId >= 0)
        {
            _recordBySkill.TryGetValue(e.RecordSkillId, out var rs);
            _recordBySkill[e.RecordSkillId] = rs + e.Damage;
        }
    }

    private void OnKill(in DamageEvent e, CharacterRegistry registry, bool aggregateSquire)
    {
        // 白名单 Player/Squire/Summon 才建行（敌方/解析失败来源不进玩家榜）；
        // 处决流（Type25）只有击杀没有伤害事件，也要能建出 0 伤行
        var entry = registry.Resolve(e.RecordCharacterId);
        if (entry.Kind != CharKind.Player && entry.Kind != CharKind.Squire && entry.Kind != CharKind.Summon) return;

        int keyUid = e.RecordCharacterId;
        if (aggregateSquire && (entry.Kind == CharKind.Squire || entry.Kind == CharKind.Summon) && entry.OwnerUid.HasValue)
            keyUid = entry.OwnerUid.Value;

        var ka = GetOrCreateActor(keyUid);
        ka.SnapValid = true;
        ka.KindSnap = entry.Kind;
        ka.OwnerUidSnap = entry.OwnerUid;
        ka.DisplayName ??= entry.Name;
        ka.Kills++;
    }

    public void CloseIdleSegments(double nowMs, double idleMs)
    {
        foreach (var a in _actors.Values)
        {
            if (a.SegmentOpen && nowMs - a.LastEventMs > idleMs)
            {
                double elapsedSec = Math.Max((a.LastEventMs - a.SegmentStartMs) / 1000.0, 1.0);
                a.ClosedSegDps = a.SegmentDamage / elapsedSec;
                a.SegmentDamage = 0;
                a.SegmentOpen = false;
            }
        }
    }

    public double CurrentDps(ActorStats a, double nowMs)
    {
        // 开段期间分母用「最后一跳时刻」而非当前时刻：伤害一停 DPS 立即冻结
        // （打完 boss 不再随闲置时间衰减），收段时存下的值与此一致
        if (a.SegmentOpen)
            return a.SegmentDamage / Math.Max((a.LastEventMs - a.SegmentStartMs) / 1000.0, 1.0);
        return a.ClosedSegDps;
    }

    public List<ActorStats> SortedActors()
        => _actors.Values.OrderByDescending(a => a.TotalDamage).ThenBy(a => a.Uid).ToList();

    public List<TargetStats> SortedHostTargets(int take = 12)
        => _hostTargets.Values.OrderByDescending(t => t.Damage).Take(take).ToList();

    public List<TargetStats> SortedGuestTargets()
        => _guestTargets.Values.OrderByDescending(t => t.Damage).Take(12).ToList();

    public void Reset()
    {
        _actors.Clear();
        _hostTargets.Clear();
        _guestTargets.Clear();
        _recordByChar.Clear();
        _recordBySkill.Clear();
        _executeBySkill.Clear();
        _bossByTarget.Clear();
        _executeTeamDamage = 0;
        _guestTeamDamage = 0;
        _packetTeamActual = 0;
        _enemyOutDamage = 0;
        _recordTotal = 0;
        _recordDropped = 0;
        _teamBurst.Clear();
    }

    /// <summary>
    /// 剪枝：非 Boss 的目标聚合，仅在「超过 60 秒没挨打」后丢弃。
    /// 旧版按伤害榜前12保留的方案有缺陷——Boss 从首跳到挤进榜单前会被反复剪掉重建，
    /// 首跳时间/无敌间隙/前期统计全部丢失（实测战斗时间显示 0:00）。Boss 战命中间隙远小于 60 秒，
    /// 按不活跃剪枝能保住 Boss 从第一跳起的完整账；chaff 小怪死后 60 秒被清，内存有界。
    /// keepForever（探测确认过的 Boss uid）永不剪。
    /// </summary>
    public void PruneBossAgg(double nowMs, HashSet<int> keepForever)
    {
        if (keepForever == null) keepForever = new HashSet<int>();
        List<int> drop = null;
        foreach (var kv in _bossByTarget)
        {
            if (keepForever.Contains(kv.Key)) continue;
            if (nowMs - kv.Value.LastMs <= 60_000.0) continue;
            (drop ??= new List<int>()).Add(kv.Key);
        }
        if (drop != null)
            foreach (var uid in drop) _bossByTarget.Remove(uid);
    }

    private ActorStats GetOrCreateActor(int uid)
    {
        if (!_actors.TryGetValue(uid, out var a))
        {
            a = new ActorStats { Uid = uid };
            _actors[uid] = a;
        }
        return a;
    }

    // 按目标字典的有界化（2026-10-23）：消费端只取伤害 Top12（目标视图）/Top20（Boss 探测），
    // 但字典本身只增不减——uid 不复用的长局会无界膨胀，且 SortedHostTargets 每次渲染全排序。
    // 超过 512 条时修剪到伤害 Top 256（远高于榜单需求，绝不丢可上榜目标）；建新目标时检查，频率=击杀率
    private const int TargetTrimThreshold = 512;
    private const int TargetKeep = 256;
    private static int _trimLogged;

    private static void TrimTargets(Dictionary<int, TargetStats> dict)
    {
        if (dict.Count <= TargetTrimThreshold) return;
        var dmgs = new double[dict.Count];
        int i = 0;
        foreach (var t in dict.Values) dmgs[i++] = t.Damage;
        Array.Sort(dmgs);
        double threshold = dmgs[dmgs.Length - TargetKeep];
        List<int> drop = null;
        foreach (var kv in dict)
            if (kv.Value.Damage < threshold) (drop ??= new List<int>()).Add(kv.Key);
        if (drop == null) return;
        foreach (var k in drop) dict.Remove(k);
        if (_trimLogged < 5)
        {
            _trimLogged++;
            Plugin.Log?.LogInfo($"[prune] 按目标聚合 {dict.Count + drop.Count} → {dict.Count} 条（保留伤害 Top {TargetKeep}，榜单 Top12/20 不受影响）");
        }
    }

    private static TargetStats GetOrCreateTarget(Dictionary<int, TargetStats> dict, int key)
    {
        if (!dict.TryGetValue(key, out var t))
        {
            TrimTargets(dict);
            t = new TargetStats { Key = key };
            dict[key] = t;
        }
        return t;
    }
}
