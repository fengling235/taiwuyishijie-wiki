using System;
using System.Collections.Generic;
using Excel.Generate;
using Game;
using UnityEngine;
using Game.BattleProperty;
using Game.Enemy;
using Game.InteractiveItem;
using Excel.Generate.InGameProgress;
using Game.Bullet;
using Game.Net.Protocol;
using Game.UI;
using Game.UI.MulPlayerPage;
using HarmonyLib;

namespace TaiwuDpsMeter;

/// <summary>
/// 心跳：StartGame（根单例 MonoBehaviour，从启动活到退出）Update 的 Postfix 驱动
/// MeterController.Tick——主线程、每帧。绕开 ClassInjector（本游戏运行时下注入会 NRE）。
/// </summary>
[HarmonyPatch(typeof(StartGame), nameof(StartGame.Update))]
internal static class StartGame_Update_Patch
{
    private static void Postfix()
    {
        try
        {
            MeterController.Instance.Tick();
        }
        catch (Exception e)
        {
            DamageBus.ReportError("StartGame.Update", e);
        }
    }
}

/// <summary>
/// 房主侧主数据源：BattleEffect::Execute @0xEAC010 的 Postfix。
/// 实例字段（运行时定案，对象池复用，字段名前带下划线的都是私有字段、由 interop 属性直读）：
///   _effectResource(Type@0x2C)  _createCharacterUid@0x18  _targetCharacterUid@0x1C
///   _damageValue@0x5C           _isCritical@0x60          _battleEffectResult.IsWuDi@0x5E / DeadCharacterUid@0x40
/// 记账门槛：Type==4（造成伤害）且 _damageValue&gt;0 且未被无敌/闪避位（IsWuDi）跳过。
/// 治疗（Type12 负伤害）与即死（Type25 不写 _damageValue）天然被 Type 门挡掉。
/// </summary>
[HarmonyPatch(typeof(BattleEffect), nameof(BattleEffect.Execute))]
internal static class BattleEffect_Execute_Patch
{

    private static void Postfix(BattleEffect __instance)
    {
        try
        {
            if (DamageBus.Disabled || DamageBus.CollectionPaused || __instance == null) return;

            var resource = __instance._effectResource;
            if (resource == null) return;

            // Type=10 立即结算（中毒/神火/外伤）：只计数不走伤害记账（结算伤害由内部逐层结算产生，
            // 归属见 SettlementLedger 注释）。计数口径=Execute 发生=词条 roll 命中
            if (resource.Type == 10)
            {
                string sn = null;
                try { sn = resource.name; } catch { }
                if (!string.IsNullOrEmpty(sn))
                {
                    var (key, kind) = SettlementLedger.Resolve(sn);
                    if (key != null)
                        DamageBus.TryPublish(new DamageEvent
                        {
                            Kind = EventKind.Settlement,
                            TimestampMs = DamageBus.NowMs,
                            AttackerUid = __instance._createCharacterUid,
                            TargetUid = __instance._targetCharacterUid,
                            EffectName = key,
                            SkillIndex = kind,
                        });
                }
                return;
            }
            if (resource.Type != 4) return;

            var result = __instance._battleEffectResult;
            if (result != null && result.IsWuDi) return;

            float damage = __instance._damageValue;
            if (damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage)) return;

            int skillIdx = __instance._createSkillIndex;
            // 过滤三件套（蟋蟀/真龙陨石/清平调=炸弹音爆）与技能号解耦（2026-10-24 返修）：
            // 原来只在 skillIdx<0 时查效果名——游戏 10-23 补丁后蟋蟀伤害可能经词条/装备链发射并
            // 继承父技能号（带号绕过过滤直接记进功法行，用户实测"蛐蛐伤害算进无暇七绝剑"）。
            // 现在无条件读效果名比对；带号被拦时打取证日志（下局日志实锤该路径）
            string effectName = null;
            try { effectName = resource.name; } catch { /* 无名效果按无名算 */ }
            if (effectName == "Cricket_Damage" || effectName == "Dragon_Meteor_Damage" || effectName == "Qingping1")
            {
                return;
            }
            if (skillIdx >= 0) effectName = null; // 触发桶分桶只用 skill<0 的名字，带号的清掉不走 ByTrigger


            DamageBus.TryPublish(new DamageEvent
            {
                Kind = EventKind.ExecuteHit,
                TimestampMs = DamageBus.NowMs,
                AttackerUid = __instance._createCharacterUid,
                TargetUid = __instance._targetCharacterUid,
                SkillIndex = skillIdx,
                EffectName = effectName,
                Damage = damage,
                IsCritical = __instance._isCritical,
            });
        }
        catch (Exception e)
        {
            DamageBus.ReportError("BattleEffect.Execute", e);
        }
    }
}

/// <summary>
/// Type=10 立即结算的按功法计数（2026-10-24 需求：「什么功法触发了多少次中毒/外伤结算」——
/// 如血雨浴八荒 U3 词条 10% 额外外伤、九死离魂手 U4 词条 15% 神火爆炸+50% 外伤）。
/// 机制（design 库+定案）：词条 buff 挂 Trigger（概率 roll 命中）→ 直接执行 Type=10 结算效果行——
/// 结算目标 buff tag：6=中毒 7=神火 8=外伤；效果名带功法族前缀（JSLHS_U4U1BasicLauncher_2=九死·外伤
/// 结算、XYYBH_U3L1=血雨·外伤结算）→ 名字首段命中功法槽前缀集=归属功法（与 SkillNames.ComputeFamily
/// 的族键同源，都是槽名 Skill_X 去前缀）；非功法族（Equip_/DanYao_/Boss/角色名）保留原名计数（[dot] 日志）。
/// 计数口径=Execute 发生（roll 命中即计）：九死系触发条件自带 RequireTargetHasBuff（ShenHuo/LiuXie）
/// 不会空计；血雨系无 buff 要求，目标无外伤时是空结算也计 1 次（词条文本语义就是「触发」）。
/// 写入只发生在 TickCore 消费线程（_trigCrit 同款单线程模式），hook 侧只读解析缓存。
/// </summary>
internal static class SettlementLedger
{
    internal const int Poison = 0, Fire = 1, Bleed = 2, Other = 3;
    private static readonly string[] KindTags = { "毒", "爆", "外", "他" };

    /// <summary>每归属键的计数：累计分类计数 + 各分类近窗触发次数（30×1s 桶，与近N秒 DPS 同款）。</summary>
    internal sealed class Counts
    {
        public readonly int[] Kinds = new int[4];
        public readonly Ledger.RollingWindow[] Recent = { new(), new(), new(), new() };
    }

    /// <summary>uid → 归属键 → 计数。归属键=功法族前缀（JSLHS）或效果原名（Equip_LHJQ_Special_1）。</summary>
    internal static readonly Dictionary<int, Dictionary<string, Counts>> By = new();

    private static Dictionary<string, (string Key, int Kind)> _resolve;
    private static bool _built;

    internal static void Add(int uid, string key, int kind, double tsMs)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!By.TryGetValue(uid, out var m)) { m = new Dictionary<string, Counts>(); By[uid] = m; }
        if (!m.TryGetValue(key, out var c)) { c = new Counts(); m[key] = c; }
        c.Kinds[kind]++;
        c.Recent[kind].Add(tsMs, 1);
    }

    /// <summary>功法族行的显示后缀（" 毒N 爆N 外N"，全零返回 null）——RenderTree 功法行名字列用。</summary>
    internal static string SuffixFor(int uid, string family)
    {
        if (By.TryGetValue(uid, out var m) && m.TryGetValue(family, out var c))
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 3; i++)
                if (c.Kinds[i] > 0) sb.Append(' ').Append(KindTags[i]).Append(c.Kinds[i]);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        return null;
    }

    /// <summary>近 winSec 秒该归属键的结算触发明细（"毒3 爆1 外4" 只列非零类，全零返回 null）——
    /// 面板第 8 列，与近N秒 DPS 同窗口配置（用户点名要和累计后缀同样格式）。</summary>
    internal static string RecentSuffixFor(int uid, string family, double nowMs, int winSec)
    {
        if (!By.TryGetValue(uid, out var m) || !m.TryGetValue(family, out var c)) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            int n = (int)c.Recent[i].Sum(nowMs, winSec);
            if (n > 0) sb.Append(KindTags[i]).Append(n).Append(' ');
        }
        if (sb.Length > 0) sb.Length--; // 去尾空格
        return sb.Length > 0 ? sb.ToString() : null;
    }

    internal static void Reset() => By.Clear();

    /// <summary>效果名 → (归属键, 分类)。懒建一次：Type=10 行 + 功法槽前缀集。</summary>
    internal static (string Key, int Kind) Resolve(string effectName)
    {
        if (!_built) { _built = true; Build(); }
        return _resolve != null && effectName != null && _resolve.TryGetValue(effectName, out var r) ? r : (null, Other);
    }

    private static void Build()
    {
        try
        {
            // 功法槽前缀集：槽名去 "Skill_" 前缀（Skill_JSLHS→JSLHS），与 ComputeFamily 族键一致
            var prefixes = new HashSet<string>();
            var slots = Excel.Generate.DesignResource.SkillsSkillSlots;
            if (slots != null)
                for (int i = 0; i < slots.Count; i++)
                {
                    string n = slots[i]?.name;
                    if (!string.IsNullOrEmpty(n) && n.StartsWith("Skill_") && n.Length > 6) prefixes.Add(n.Substring(6));
                }

            var map = new Dictionary<string, (string, int)>(64);
            var effects = Excel.Generate.DesignResource.EffectEffects;
            if (effects != null)
                for (int i = 0; i < effects.Count; i++)
                {
                    var ef = effects[i];
                    if (ef == null || ef.Type != 10) continue;
                    string n = ef.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    map[n] = (AttrKey(n, prefixes), KindOf(ef, n));
                }
            _resolve = map;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Settlement.Build", e);
        }
    }

    /// <summary>归属：名字首段命中功法槽前缀集=功法族键；否则保留原名（装备/丹药/Boss 结算走 [dot] 日志）。</summary>
    private static string AttrKey(string name, HashSet<string> prefixes)
    {
        int p = name.IndexOf('_');
        if (p > 0 && prefixes.Contains(name.Substring(0, p))) return name.Substring(0, p);
        return name;
    }

    /// <summary>分类：TargetBuff_Name（ZhongDu/LiuXie/ShenHuo）→ TargetBuff_Tags(6/7/8) →
    /// SelectByIncludeBuffTag[0] → 名字启发（FXG_ShenHuoTrigger 这类无 tag 行兜底）。</summary>
    private static int KindOf(Excel.Generate.Effect.Effect ef, string name)
    {
        try
        {
            var bn = ef.TargetBuff_Name;
            if (bn != null)
                foreach (var b in bn)
                {
                    if (string.IsNullOrEmpty(b)) continue;
                    if (b.Contains("ZhongDu")) return Poison;
                    if (b.Contains("LiuXie")) return Bleed;
                    if (b.Contains("ShenHuo")) return Fire;
                }
            switch (ef.TargetBuff_Tags)
            {
                case 6: return Poison;
                case 7: return Fire;
                case 8: return Bleed;
            }
            var sbt = ef.SelectByIncludeBuffTag;
            if (sbt != null && sbt.Length > 0)
            {
                switch (sbt[0])
                {
                    case 6: return Poison;
                    case 7: return Fire;
                    case 8: return Bleed;
                }
            }
            if (name.Contains("ShenHuo")) return Fire;
            if (name.IndexOf("ZhongDu", StringComparison.OrdinalIgnoreCase) >= 0) return Poison;
            if (name.Contains("LiuXie")) return Bleed;
            return Other;
        }
        catch { return Other; }
    }
}

/// <summary>
/// 客户端侧数据源：BulletManager::EnqueuePCreateEffect @0xDDAB50 的 Prefix。
/// 这是网络收包后、飘字渲染前的最早拦截点——晚于此的渲染层可能因目标不可见而丢弃事件，
/// 所以屏幕外队友的伤害在这里一定可见。PCreateEffect.CharacterId 是受击者（无攻击者字段）。
/// 此处只做字段拷贝并入队，任何异常都必须吞掉：Prefix 抛异常会丢包导致游戏内飘字缺失。
/// </summary>
[HarmonyPatch(typeof(BulletManager), nameof(BulletManager.EnqueuePCreateEffect))]
internal static class BulletManager_EnqueuePCreateEffect_Patch
{
    private static void Prefix(ref PCreateEffect createEffect)
    {
        try
        {
            if (DamageBus.Disabled || DamageBus.CollectionPaused) return;

            DamageBus.TryPublish(new DamageEvent
            {
                Kind = EventKind.Packet,
                TimestampMs = DamageBus.NowMs,
                PacketTargetId = createEffect.CharacterId,
                PacketEffectIndex = createEffect.EffectIndex,
                PacketCurHp = createEffect.CurrentHp,
                PacketMaxHp = createEffect.MaxHp,
                Damage = createEffect.Damage,
                IsCritical = createEffect.IsCritical != 0,
            });
        }
        catch (Exception e)
        {
            DamageBus.ReportError("EnqueuePCreateEffect", e);
        }
    }
}

/// <summary>
/// 对账基准：BattleRecordManager::AddDamage(int characterId, int skillId, float damage) @0xF0A2F0 的 Postfix。
/// 这是游戏自己结算画面用的累计口径（房主侧），用来验证我们 Execute 流的账目是否对得上官方数字。
/// </summary>
[HarmonyPatch(typeof(BattleRecordManager), nameof(BattleRecordManager.AddDamage))]
internal static class BattleRecordManager_AddDamage_Patch
{
    private static void Postfix(int characterId, int skillId, float damage)
    {
        try
        {
            if (DamageBus.Disabled || DamageBus.CollectionPaused) return; // F7 停采期间零构造（与 Execute/飘字两 hook 一致）

            DamageBus.TryPublish(new DamageEvent
            {
                Kind = EventKind.Record,
                TimestampMs = DamageBus.NowMs,
                RecordCharacterId = characterId,
                RecordSkillId = skillId,
                Damage = damage,
            });
        }
        catch (Exception e)
        {
            DamageBus.ReportError("BattleRecordManager.AddDamage", e);
        }
    }
}

/// <summary>
/// 击杀计数：BattleRecordManager::AddKillCount(int characterId, int selectionTargetType) @0xF0A400 的 Postfix。
/// 结算画面击杀数同源。实测定案：不能用 DeadCharacterUid 判击杀（每次命中都非零）。
/// </summary>
[HarmonyPatch(typeof(BattleRecordManager), nameof(BattleRecordManager.AddKillCount))]
internal static class BattleRecordManager_AddKillCount_Patch
{
    private static void Postfix(int characterId, int selectionTargetType)
    {
        try
        {
            if (DamageBus.Disabled || DamageBus.CollectionPaused) return; // F7 停采期间零构造

            DamageBus.TryPublish(new DamageEvent
            {
                Kind = EventKind.Kill,
                TimestampMs = DamageBus.NowMs,
                RecordCharacterId = characterId,
            });
        }
        catch (Exception e)
        {
            DamageBus.ReportError("BattleRecordManager.AddKillCount", e);
        }
    }
}

// ---------------------------------------------------------------- Steam 玩家名截获
// 三个 UI 入口都带「playerName(Steam名) + characterName(角色名)」成对参数，喂给
// CharacterRegistry 合成「玩家名（角色名）」。营地页开局前就有，结算页兜底。

[HarmonyPatch(typeof(UITeammateInfo), nameof(UITeammateInfo.UpdateBattle))]
internal static class UITeammateInfo_UpdateBattle_Patch
{
    private static void Prefix(string playerName, string characterName)
    {
        try { CharacterRegistry.NoteSteamName(characterName, playerName); }
        catch { /* 名字链路永远不抛 */ }
    }
}

[HarmonyPatch(typeof(UITeammateInfo), nameof(UITeammateInfo.UpdateCampsite))]
internal static class UITeammateInfo_UpdateCampsite_Patch
{
    // 营地 roster 自带 playerId——中文角色名唯一可靠的来源（战斗对象 GetName() 只有拼音）。
    // ⚠ 定案（0xB330A2 调用点）：playerId=RoomMember@0x19（房间成员编号），与战斗槽位
    // characterIndex 是两套独立编号（游戏配 member↔character 走 BattlePlayerData 对象链，
    // 不按编号匹配）——顺序错开时名字会挂错行（2026-10-20 四人局实锤疑似：太始剑气·冲
    // 官方记喧泣无花、面板挂据说下棋好）。[name] 去重日志用于下局实锤两套编号的对应。
    private static readonly HashSet<string> _loggedNames = new();

    private static void Prefix(string playerName, string characterName, int readyStatus, bool isHost, int playerId)
    {
        try
        {
            CharacterRegistry.NoteMember(playerId, characterName, playerName);
            string key = playerId + "|" + characterName + "|" + playerName;
        }
        catch { /* 名字链路永远不抛 */ }
    }
}

// 注：不再 hook UIPlayerStatTip.SetPlayerStat（async 状态机方法，打补丁有破坏风险；
// 营地页 + 战斗队友条两个入口已足够拿到所有玩家名）

/// <summary>
/// 难度旋钮读缓存：ResetEnemyState 每帧被场上每只怪各调一次（高怪量局每帧可达 200 次），
/// ConfigEntry.Value（BepInEx 带锁读）与曲线计算按次收费会吃帧——全部旋钮 250ms 刷新一次，
/// 对手动旋钮与曲线（+0.15~0.4/分钟 ≈ 每 250ms 变 0.001）精度损失无感。
/// 全部关闭时调用方只剩一次 int 比较即早退（实测长局高倍率卡顿的主源头就是这条路径）。
/// </summary>
internal static class TuningCache
{
    private const int RefreshMs = 250;
    private static int _at;

    internal static bool ModsEnabled = true; // 公开版恒 true（模式开关是完整版功能，保留字段省掉全部调用点的条件编译）
    internal static bool DummyGuardOn = true;                                 // 木桩防斩杀（CheckAndDie 前拉满血）
    // 2026-10-23 热路径收尾：以下配置原本在每帧/每事件/每怪路径直读 ConfigEntry.Value（带锁），
    // 与 2026-10-20 修掉的 ResetEnemyState 同款问题——统一收进本缓存
    internal static bool DummyOn;                                            // 木桩模式（ResetEnemyState 每帧×每怪）

    /// <summary>写路径专用：清节拍后立即刷新——按钮/直输改配置后同帧生效，不等 250ms
    /// （木桩开关后同帧的怪物 Initialize 要读到新 DummyOn，250ms 延迟会漏标记）。</summary>
    internal static void ForceRefresh() { _at = 0; Refresh(); }


    internal static void Refresh()
    {
        int now = Environment.TickCount;
        if (now - _at < RefreshMs) return; // 开机首 250ms 用默认值（全关），随后即正确
        _at = now;
        DummyOn = Plugin.CfgDummyMode?.Value == true;
        DummyGuardOn = Plugin.CfgDummyAntiExecute?.Value != false;
    }
}

/// <summary>
/// 中途加 Boss：StartGame.LevelFlowManager().monsterManager.AnalyzeEnemy(Boss 规则)。
/// 定案（0xE46800）：AnalyzeEnemy = 容量检查（场上敌数 ≥0xC8=200 直接不刷）→ 按规则
/// Rate 走 GetBonusNum（数量乘区，Boss 规则 Rate=1）→ new EnemySpawner().Initialize(rule, n)。
/// 4 只 Boss 各有专属刷怪规则（design 库 InGameProgress_EnemySpawnRule：SpawnOnce=1/MaxNum=1），
/// 每按一次「+召Boss」精确刷一只（数量=按的次数）；EnemySpawner_Initialize_Patch 已豁免
/// Boss 规则不受怪量倍率放大。Boss 探测（HealthBarType≥3）自动接管面板显示。
/// </summary>
internal static class BossSummoner
{

    /// <summary>Boss 专属规则名集合——怪量放大/改写的豁免名单（含插件自造的木桩规则）。
    /// 公开版只有木桩（难度旋钮/Boss 召唤是完整版功能）。</summary>
    internal static readonly HashSet<string> RuleNames = new()
    { "TaiwuDummy" };

}

/// <summary>
/// 木桩模式：停刷怪 + 清场上怪 + 召唤官方木桩怪（design 库自带 Dummy_Normal：MaxHP=99999999、
/// MoveSpeed=0、EXPDrop=0、Prefab=敌人_祀刃——静止站桩零经验，官方测伤道具）。
/// 木桩 uid 在 Initialize Postfix 标记；ResetEnemyState Postfix 每帧把木桩血拉满（防超高额伤害
/// 打穿 9999 万）。刷怪停摆原理：所有规则 Rate=0（GetBonusNum@0xE46FA7 对 Rate≤0 跳过下限保护
/// → 0 只）；恢复走 _orig 快照。关闭时清掉木桩并恢复规则。
/// </summary>
internal static class DummyController
{
    private const string DummyEnemyName = "Dummy_Normal";
    private static readonly HashSet<int> _dummyUids = new();

    /// <summary>uid 是否木桩（CheckAndDie 防斩杀拦截用，跨类访问）。</summary>
    internal static bool IsDummy(int uid) => _dummyUids.Contains(uid);

    // 读 TuningCache 缓存（2026-10-23）：本属性在 ResetEnemyState 每帧×每怪路径被调两次，
    // 直读 ConfigEntry 会重演 2026-10-20 的卡顿；开关按钮路径用 ForceRefresh 同帧生效
    internal static bool On => TuningCache.DummyOn;

    internal static void NoteSpawn(BattleEnemyCharacter enemy)
    {
        try
        {
            if (!On || enemy == null) return;
            if (enemy._enemyCharacter?.name == DummyEnemyName)
                _dummyUids.Add(enemy._characterUid);
        }
        catch { /* 标记失败无碍 */ }
    }

    /// <summary>每帧血量拉满（挂在 ResetEnemyState Postfix，开关前置保证关闭时零开销）。</summary>
    internal static void KeepFullHp(BattleEnemyCharacter enemy)
    {
        try
        {
            if (!On || enemy == null) return;
            if (_dummyUids.Contains(enemy._characterUid))
                enemy._currentHp = enemy.GetMaxHP();
        }
        catch { /* 拉满失败无碍 */ }
    }

    /// <summary>切换木桩模式。返回 null=成功；否则为原因。</summary>
    internal static string SetMode(bool on)
    {
        try
        {
            if (!TuningCache.ModsEnabled) return "总开关在原始模式，木桩停用";
            var lfm = StartGame.LevelFlowManager();
            if (lfm == null) return "不在关卡内";
            if (on)
            {
                EnemySpawner_Initialize_Patch.StopAllSpawn(lfm.monsterManager);
                int killed = KillAllEnemies();
                string err = SummonDummy(lfm.monsterManager);
                Plugin.Log.LogInfo($"[dummy] 木桩模式开启：停刷 {EnemySpawner_Initialize_Patch.KnownRuleCount} 条规则+清空在场刷怪器预算，清场 {killed} 只，{(err == null ? "木桩已召唤" : "召唤失败：" + err)}");
                return err;
            }
            EnemySpawner_Initialize_Patch.RestoreAllSpawn();
            _dummyUids.Clear();
            int removed = KillAllEnemies();
            Plugin.Log.LogInfo($"[dummy] 木桩模式关闭：刷怪规则已恢复，清场 {removed} 只（含木桩）");
            return null;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Dummy.SetMode", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>遍历角色 uid 空间秒杀所有敌人（LossHP(极大值) 走官方清场同款通道，含正常掉落结算）。</summary>
    internal static int KillAllEnemies()
    {
        int n = 0;
        var bm = StartGame.BattleManager();
        if (bm == null) return 0;
        for (int uid = 16; uid < 800; uid++)
        {
            try
            {
                var c = bm.GetBattleCharacterByUid(uid);
                var enemy = c?.TryCast<BattleEnemyCharacter>();
                if (enemy == null) continue;
                enemy.LossHP(1e18f);
                n++;
            }
            catch { /* 个别 uid 解析失败跳过 */ }
        }
        return n;
    }

    /// <summary>构造木桩专属刷怪规则（字段抄 Boss 规则 L1H1_CM，距离拉近方便打）交给 MonsterManager。
    /// Rate=count：GetBonusNum 会算 count×(1+数量加成)，Interval=999 防重复。</summary>
    private static string SummonDummy(Game.Scripts.LevelTimeLine.MonsterManager mm)
    {
        try
        {
            if (mm == null) return "MonsterManager 不可用";
            int count = Math.Clamp(Plugin.CfgDummyCount?.Value ?? 1, 1, 16);
            var rule = new Excel.Generate.InGameProgress.EnemySpawnRule();
            rule.name = "TaiwuDummy";
            rule.SpawnEnemy_Name = DummyEnemyName;
            rule.SpawnEnemy_SpawnOnce = 1;
            rule.SpawnEnemy_MaxNum = count;
            rule.SpawnEnemy_Interval = 999f;
            rule.SpawnEnemy_Rate = count;
            rule.SpawnEnemy_MinDistance = 6f;
            rule.SpawnEnemy_MaxDistance = 10f;
            rule.IsHideProgressBar = 1;
            mm.AnalyzeEnemy(rule);
            return null;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Dummy.Summon", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>刷新木桩：杀掉全部现有木桩，重新召唤 N 只——全新敌人身上零 buff 零损血（测纯净基线用）。</summary>
    internal static string Refresh()
    {
        try
        {
            if (!On) return "木桩模式未开启";
            var lfm = StartGame.LevelFlowManager();
            if (lfm == null) return "不在关卡内";
            var bm = StartGame.BattleManager();
            int killed = 0;
            var uids = new List<int>(_dummyUids);
            foreach (var uid in uids)
            {
                try
                {
                    var e = bm?.GetBattleCharacterByUid(uid)?.TryCast<BattleEnemyCharacter>();
                    if (e != null && e._currentHp > 0) { e.LossHP(1e18f); killed++; }
                }
                catch { }
            }
            _dummyUids.Clear();
            string err = SummonDummy(lfm.monsterManager);
            Plugin.Log.LogInfo($"[dummy] 木桩已刷新：清掉 {killed} 只旧桩（连带其身上的 buff/损血），重召 {Math.Clamp(Plugin.CfgDummyCount?.Value ?? 1, 1, 16)} 只全新");
            return err;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Dummy.Refresh", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>木桩模式运行中调整数量（右键木桩键输入后调用）：多退（LossHP 秒多余）少补（追加召唤）。</summary>
    internal static string AdjustCount(int target)
    {
        try
        {
            if (!On) return "木桩模式未开启";
            var lfm = StartGame.LevelFlowManager();
            if (lfm == null) return "不在关卡内";
            target = Math.Clamp(target, 1, 16);
            var bm = StartGame.BattleManager();
            int alive = 0;
            var dead = new List<int>();
            foreach (var uid in _dummyUids)
            {
                try
                {
                    var c = bm != null ? bm.GetBattleCharacterByUid(uid) : null;
                    var e = c?.TryCast<BattleEnemyCharacter>();
                    if (e != null && e._currentHp > 0) alive++;
                    else dead.Add(uid);
                }
                catch { dead.Add(uid); }
            }
            foreach (var d in dead) _dummyUids.Remove(d);
            Plugin.Log.LogInfo($"[dummy] 木桩数量 {alive} → {target}");
            if (alive < target)
            {
                // 补召差额（Rate=差额 一次刷齐）
                var rule = new Excel.Generate.InGameProgress.EnemySpawnRule();
                rule.name = "TaiwuDummy";
                rule.SpawnEnemy_Name = DummyEnemyName;
                rule.SpawnEnemy_SpawnOnce = 1;
                rule.SpawnEnemy_MaxNum = target - alive;
                rule.SpawnEnemy_Interval = 999f;
                rule.SpawnEnemy_Rate = target - alive;
                rule.SpawnEnemy_MinDistance = 6f;
                rule.SpawnEnemy_MaxDistance = 10f;
                rule.IsHideProgressBar = 1;
                lfm.monsterManager?.AnalyzeEnemy(rule);
            }
            else if (alive > target)
            {
                int toKill = alive - target;
                foreach (var uid in _dummyUids)
                {
                    if (toKill <= 0) break;
                    try
                    {
                        var e = bm?.GetBattleCharacterByUid(uid)?.TryCast<BattleEnemyCharacter>();
                        if (e != null && e._currentHp > 0) { e.LossHP(1e18f); toKill--; _dummyUids.Remove(uid); }
                    }
                    catch { }
                }
            }
            Plugin.CfgDummyCount.Value = target;
            try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
            return null;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Dummy.Adjust", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }
}

/// <summary>
/// BD 构筑一键装配：读 wiki「BD 构筑台」（#/planner）导出的构筑 JSON（BepInEx/plugins/TaiwuDpsMeter.bd.json），
/// 把 skills[]（{id:槽名, evo:进化词条id, lv:[五分支等级]}）装配到本地玩家：
/// ChangeSkillSlot(官方编辑器通道，尾部自带 RefreshPBattleDataSkillSlots 网络同步) 换技能体
/// （进化体=族_E1Skill / 基础体=族_BasicSkill）→ SetSkillSlotNormalUpgrades 点分支词条
/// （lv[b] 展开为 U{b+1}L{1..lv} 全链，贴近正常升级流程的列表状态）→ SetSuperSkillUpgrade 选进化。
/// 词条/进化 id 与 wiki 数据同源（SkillUpgrades 表 name=HZG_U1L1/HZG_E1，直接同名）。
/// 只装功法（装备/天赋/丹药是另一套系统，不做）。资源校验（参悟点等）全绕过。
/// </summary>
internal static class BuildLoader
{
    private class BdSkill { public string id { get; set; } public string evo { get; set; } public int[] lv { get; set; } }
    private class BdBuild { public string name { get; set; } public List<BdSkill> skills { get; set; } }

    /// <summary>最近一次装配的结果摘要（面板 toast 显示用）。</summary>
    internal static string LastSummary = "";

    internal static string ApplyFromFile()
    {
        try
        {
            string path = System.IO.Path.Combine(BepInEx.Paths.PluginPath, "TaiwuDpsMeter.bd.json");
            if (!System.IO.File.Exists(path)) return $"找不到 {path}（或右键「装配BD」直接读剪贴板）";
            return ApplyFromText(System.IO.File.ReadAllText(path), $"文件 {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Build.ApplyFile", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>右键「装配BD」：直接读系统剪贴板（wiki 构筑台复制 JSON → 游戏内右键即装）。</summary>
    internal static string ApplyFromClipboard()
    {
        try
        {
            string text = UnityEngine.GUIUtility.systemCopyBuffer;
            if (string.IsNullOrWhiteSpace(text)) return "剪贴板是空的（先在 wiki 构筑台复制 JSON）";
            return ApplyFromText(text, "剪贴板");
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Build.ApplyClip", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }

    internal static string ApplyFromText(string json, string source)
    {
        try
        {
            var bd = System.Text.Json.JsonSerializer.Deserialize<BdBuild>(json ?? "");
            if (bd?.skills == null || bd.skills.Count == 0) return $"{source} 的 JSON 里没有 skills";
            if (bd.skills.Count > 6) return $"skills 有 {bd.skills.Count} 门，超过 6 槽";

            var bm = StartGame.BattleManager();
            if (bm == null) return "不在战斗内";
            var gspm = StartGame.GameServerPlayerManager();
            var player = bm.GetBattlePlayerCharacter(gspm != null ? gspm.LocalPlayerCharacterIndex() : 0);
            if (player == null) return "本地玩家不可用";

            int done = 0;
            for (int i = 0; i < bd.skills.Count; i++)
            {
                var s = bd.skills[i];
                if (string.IsNullOrEmpty(s?.id)) continue;
                string family = s.id.StartsWith("Skill_") ? s.id["Skill_".Length..] : s.id;
                string body = string.IsNullOrEmpty(s.evo) ? family + "_BasicSkill" : s.evo + "Skill";
                if (Excel.Generate.DesignResource.GetSkillsSkillSlot(s.id) == null)
                {
                    Plugin.Log.LogWarning($"[build] 槽位{i}：{s.id} 查不到 SkillSlot 行，跳过");
                    continue;
                }
                if (Excel.Generate.DesignResource.GetSkillsSkill(body) == null)
                {
                    Plugin.Log.LogWarning($"[build] 槽位{i}：技能体 {body} 查不到，跳过");
                    continue;
                }
                // 覆盖装配清理：旧功法词条/进化挂的 buff 载体在玩家身上，整槽替换不会摘——
                // 先按旧技能的三个名字（技能体/槽名/族名）各摘一次，防旧 BD 词条效果残留
                try
                {
                    var oldSlots = player._battleSkillSlots;
                    if (i < oldSlots.Count)
                    {
                        var oldSlot = oldSlots[i];
                        string oldBody = oldSlot?._skillResource?.name;       // 如 HZG_E1Skill
                        string oldSlotRes = oldSlot?._skillSlotResource?.name; // 如 Skill_HZG
                        if (!string.IsNullOrEmpty(oldBody)) player.RemoveBuffBySkill(oldBody);
                        if (!string.IsNullOrEmpty(oldSlotRes)) player.RemoveBuffBySkill(oldSlotRes);
                        if (!string.IsNullOrEmpty(oldSlotRes) && oldSlotRes.StartsWith("Skill_"))
                            player.RemoveBuffBySkill(oldSlotRes["Skill_".Length..]); // 族名 HZG
                    }
                }
                catch { /* 清理失败不阻断装配 */ }

                player.ChangeSkillSlot(s.id, body, i);

                var slots = player._battleSkillSlots;
                if (i >= slots.Count)
                {
                    Plugin.Log.LogWarning($"[build] 槽位{i}（{family}）：槽列表只有 {slots.Count} 个——第 {i + 1} 槽未解锁（等级不够？先点「升100级」再装配）。功法本体已追加，词条/进化被跳过");
                    continue;
                }
                var slot = slots[i];
                if (slot == null) continue;

                // 等级不足时 ChangeSkillSlot 追加的新槽缺内部字段（_battlePlayerCharacter 等），
                // SkillSlotUpgrade 挂 buff 会静默失败（第 6 槽没分支的实测根因候选）——补上
                try
                {
                    if (slot._battlePlayerCharacter == null) slot._battlePlayerCharacter = player;
                }
                catch { /* 字段不可写就算了 */ }

                // 分支词条：lv[b]（0~3）展开为 U{b+1}L{1..lv}，逐条走官方升级应用函数
                // BattleSkillSlot.SkillSlotUpgrade(运行时列表位置, 词条名)——内部自己挂 Add_Buffs、
                // 写词条列表（直设列表绕过 buff 挂载会让行为型词条失效，焚血功实测）。
                // 位置=SkillUpgradesSkillUpgradess 运行时列表下标（Avaliable=0 已被剔除，不能拿 sqlite Index）。
                int upsBefore = -1, upsAfter = -1;
                try { upsBefore = slot.GetSkillSlotNormalUpgrades()?.Count ?? -1; } catch { }
                // 重复装配幂等：先清旧词条再重挂（升级后重按一次装配BD 不叠加）
                try { slot.SetSkillSlotNormalUpgrades(new Il2CppSystem.Collections.Generic.List<Excel.Generate.SkillUpgrades.SkillUpgrades>()); } catch { }
                if (s.lv != null && s.lv.Length > 0)
                {
                    var all = Excel.Generate.DesignResource.SkillUpgradesSkillUpgradess;
                    var idxMap = new Dictionary<string, int>();
                    for (int k = 0; k < all.Count; k++)
                    {
                        var nm = all[k]?.name;
                        if (nm != null) idxMap.TryAdd(nm, k);
                    }
                    for (int b = 0; b < Math.Min(5, s.lv.Length); b++)
                    {
                        int lv = Math.Clamp(s.lv[b], 0, 3);
                        for (int l = 1; l <= lv; l++)
                        {
                            string uname = $"{family}_U{b + 1}L{l}";
                            if (idxMap.TryGetValue(uname, out var uidx)) slot.SkillSlotUpgrade(uidx, uname);
                            else Plugin.Log.LogWarning($"[build] 槽位{i}：词条 {uname} 运行时表查不到，跳过");
                        }
                    }
                }
                try { upsAfter = slot.GetSkillSlotNormalUpgrades()?.Count ?? -1; } catch { }
                Plugin.Log.LogInfo($"[build] 槽位{i}（{family}）：词条 {upsBefore}→{upsAfter}{(string.IsNullOrEmpty(s.evo) ? "" : "，进化=" + s.evo)}");

                // 进化：词条 id（HZG_E1）直接查 SkillUpgrades
                if (!string.IsNullOrEmpty(s.evo))
                {
                    var evoUp = Excel.Generate.DesignResource.GetSkillUpgradesSkillUpgrades(s.evo);
                    if (evoUp != null) slot.SetSuperSkillUpgrade(evoUp);
                    else Plugin.Log.LogWarning($"[build] 槽位{i}：进化 {s.evo} 查不到，保持未进化");
                }
                done++;
            }

            // 全部装完后统一重刷技能槽网络镜像（UI/客户端读 PBattleDataSkillSlots）：ChangeSkillSlot
            // 尾部刷过一次（功法名进了镜像），但词条/进化是之后挂的——不补刷最后一层，第 6 槽
            // （低等级装配的追加槽）镜像词条缺失、UI 不显示分支（实测主嫌疑）
            try
            {
                var refresh = AccessTools.Method(typeof(BattlePlayerCharacter), "RefreshPBattleDataSkillSlots");
                refresh?.Invoke(player, null);
                Plugin.Log.LogInfo("[build] 已重刷技能槽镜像（词条/进化最后同步一次）");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[build] 镜像刷新失败（词条数据仍在槽对象上，实战应生效）：{e.GetType().Name}: {e.Message}");
            }

            Plugin.Log.LogInfo($"[build] 构筑「{bd.name ?? "?"}」（{source}）装配完成：{done}/{bd.skills.Count} 门功法（槽位0起、词条全链、进化直设）");
            LastSummary = $"✓ {bd.name ?? "构筑"} {done}/{bd.skills.Count} 门";
            return null;
        }
        catch (System.Text.Json.JsonException e)
        {
            LastSummary = "✗ JSON 无效";
            return $"{source} 不是有效 JSON：{e.Message}";
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Build.Apply", e);
            return e.GetType().Name + ": " + e.Message;
        }
    }
}

/// <summary>
/// 难度调节（持久层）：BattleEnemyCharacter::ResetEnemyState 的 Postfix。
/// 定案（0xEC22B0）：该方法把设计基值 _enemyStat 逐字段覆盖到传入的 stat（MaxHP@0x34 等），
/// 且 BattleEnemyCharacter.Update 每帧都调它重置 _currentStat/_currentStatNoDepend——
/// 一次性缩放（比如只挂 Initialize）下一帧就被打回基值（实测 ×10 后怪仍几百血）。
/// 挂在重置完成后把倍率乘回去：每帧幂等，Buff/灾祸的后续累加建立在放大后的基值上。
/// </summary>
[HarmonyPatch(typeof(BattleEnemyCharacter), "ResetEnemyState")]
internal static class BattleEnemyCharacter_ResetEnemyState_Patch
{
    private static void Postfix(object enemyStat, BattleEnemyCharacter __instance)
    {
        try
        {
            TuningCache.Refresh(); // 250ms 缓存：每帧×每怪的调用只剩 int 比较即早退
            if (DummyController.On) DummyController.KeepFullHp(__instance); // 公开版：只保木桩满血
        }
        catch (Exception e)
        {
            DamageBus.ReportError("EnemyHp.ResetStat", e);
        }
    }
}

/// <summary>
/// 难度调节（出生血量）：Initialize 的 Postfix 只把出生满血拉到放大后的满血
/// （MaxHP 由 ResetEnemyState 的 Postfix 持久放大，这里只乘 _currentHp 初值）+ 每档前3只验证日志。
/// 房主侧生效（敌人属性由房主模拟）；对倍率生效后生成的怪起效。
/// </summary>
[HarmonyPatch(typeof(BattleEnemyCharacter), nameof(BattleEnemyCharacter.Initialize))]
internal static class BattleEnemyCharacter_Initialize_Patch
{
    private static void Postfix(BattleEnemyCharacter __instance)
    {
        try
        {
            TuningCache.Refresh();
            DummyController.NoteSpawn(__instance);
        }
        catch (Exception e)
        {
            DamageBus.ReportError("EnemyHp.Initialize", e);
        }
    }

}

/// <summary>
/// 难度调节（出怪数量/速度）：EnemySpawner::Initialize 的 Pre/Postfix。
/// 定案：MonsterSpawnTask::OnUpdate 每帧读共享规则 EnemySpawnRule 的 SpawnEnemy_Interval(@0x4C)
/// 计时，到点调 MonsterManager.AnalyzeEnemy（读 MaxNum@0x44/Rate@0x54 补怪）——间隔与上限都是
/// 从规则对象现读的，改对象即活生效。规则是 DesignResource 共享行，做快照+换倍率全量恢复重写防叠加；
/// 单波总量 RemainNumber/spawnNum 在 Postfix 里按怪量倍率放大（单次刷的规则不走 tick，靠这个）。
/// </summary>
[HarmonyPatch(typeof(EnemySpawner), nameof(EnemySpawner.Initialize))]
internal static class EnemySpawner_Initialize_Patch
{
    private static readonly Dictionary<string, (int MaxNum, float Interval, int Rate)> _orig = new();
    private static readonly Dictionary<string, EnemySpawnRule> _rules = new();

    private static void Prefix(EnemySpawnRule enemySpawnRule)
    {
        try
        {
            // 公开版：无倍率改写——只登记规则快照 + 木桩模式停摆（Rate=0 走 GetBonusNum 的 jle 分支=0 只）
            if (enemySpawnRule == null || string.IsNullOrEmpty(enemySpawnRule.name)) return;
            if (BossSummoner.RuleNames.Contains(enemySpawnRule.name)) return;
            if (!_orig.ContainsKey(enemySpawnRule.name))
                _orig[enemySpawnRule.name] = (enemySpawnRule.SpawnEnemy_MaxNum, enemySpawnRule.SpawnEnemy_Interval, enemySpawnRule.SpawnEnemy_Rate);
            _rules[enemySpawnRule.name] = enemySpawnRule;
            if (TuningCache.DummyOn && !BossSummoner.RuleNames.Contains(enemySpawnRule.name))
            {
                enemySpawnRule.SpawnEnemy_Rate = 0;
                enemySpawnRule.SpawnEnemy_MaxNum = 0;
            }
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Spawn.Rule", e);
        }
    }

    private static void Postfix(EnemySpawner __instance, int spawnNum, EnemySpawnRule enemySpawnRule)
    {
        try
        {
            // 公开版：木桩模式下清在途预算（RuleNames 豁免照旧），无倍率放大
            if (enemySpawnRule != null && BossSummoner.RuleNames.Contains(enemySpawnRule.name)) return;
            if (TuningCache.DummyOn && __instance != null) __instance.RemainNumber = 0;
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Spawn.Remain", e);
        }
    }

    /// <summary>木桩模式停刷：所有已见规则 Rate=0（GetBonusNum 对 Rate≤0 跳过下限→0 只，底层 0xE46FA7 jle 分支）+ MaxNum=0，
    /// 并清空 MonsterManager 在场全部刷怪器的存量预算（RemainNumber）——规则停摆只挡"之后补怪"，
    /// 已拿到数量的在途 spawner 会把存量放完（实测漏怪来源），预算清零才是彻底停。</summary>
    internal static void StopAllSpawn(Game.Scripts.LevelTimeLine.MonsterManager mm)
    {
        foreach (var kv in _rules)
        {
            if (BossSummoner.RuleNames.Contains(kv.Key)) continue;
            kv.Value.SpawnEnemy_Rate = 0;
            kv.Value.SpawnEnemy_MaxNum = 0;
        }
        try
        {
            var spawners = mm?._enemySpawners;
            if (spawners != null)
                for (int i = 0; i < spawners.Count; i++)
                {
                    var sp = spawners[i];
                    if (sp != null && sp.EnemyName != "Dummy_Normal") sp.RemainNumber = 0;
                }
        }
        catch (Exception e)
        {
            DamageBus.ReportError("Spawn.ClearRemain", e);
        }
    }

    /// <summary>木桩模式关闭：按快照恢复全部规则（不恢复倍率改写——那由下次 Initialize 的倍率变更逻辑接管）。</summary>
    internal static void RestoreAllSpawn()
    {
        foreach (var kv in _rules)
        {
            if (!_orig.TryGetValue(kv.Key, out var o)) continue;
            if (BossSummoner.RuleNames.Contains(kv.Key)) continue;
            kv.Value.SpawnEnemy_MaxNum = o.MaxNum;
            kv.Value.SpawnEnemy_Interval = o.Interval;
            kv.Value.SpawnEnemy_Rate = o.Rate;
        }
    }

    internal static int KnownRuleCount => _rules.Count;

}

/// <summary>
/// 难度调节（精英 roll）：EnemySpawner::CreateEnemy 的 Pre/Postfix——生成单只怪时按配置概率
/// 把规则名临时换成精英变体（Enemy_X → Enemy_Elite_X，设计表成对存在约10种，覆盖常用波次怪），
/// 生成完恢复原名（规则是共享行，不恢复会污染后续）。无精英变体的怪（查表不到）跳过不 roll。
/// </summary>
[HarmonyPatch(typeof(EnemySpawner), "CreateEnemy")]
/// <summary>
/// 木桩防斩杀（2026-10-24）：斩杀类效果（Type=25 处决族：SWS_ZhanSha/自爆/大春牛到期）能绕过
/// 每帧满血保险直接弄死木桩（处决当帧清血+死亡判定，KeepFullHp 下一帧才拉回来不及）。
/// 拦截点=CheckAndDie（死亡判定的唯一汇聚口：血量≤0 才走死亡流程，BattleManager 每帧巡检调用）——
/// Prefix 把木桩血拉满，原方法开头「血量&gt;0 直接返回」自然早退，零侵入。
/// 只保护木桩名单（_dummyUids）；正常怪被斩杀照常生效。木桩关闭时名单已清、恒放行。
/// </summary>
[HarmonyPatch(typeof(BattleEnemyCharacter), nameof(BattleEnemyCharacter.CheckAndDie))]
internal static class BattleEnemyCharacter_CheckAndDie_Patch
{
    private static void Prefix(BattleEnemyCharacter __instance)
    {
        try
        {
            if (!TuningCache.DummyGuardOn || !TuningCache.ModsEnabled) return;
            if (__instance == null || !DummyController.On) return;
            if (DummyController.IsDummy(__instance._characterUid))
                __instance._currentHp = __instance.GetMaxHP(); // 拉满血：原方法血>0 自然返回=斩杀无效
        }
        catch { /* 保护失败放行原生流程 */ }
    }
}

/// <summary>
/// 掉落归零的兜底拦截（路径无关）：金币/装备球的服务端发送口直接短路。
/// 决策点在哪不重要——所有掉落最终都要走 InteractiveManager.SendPCreateGoldBall/
/// SendPCreateEquipmentBall/SendPPlayerDropEquipment 广播给客户端，Prefix 返回 false 即不生成。
/// 与 DropPackId 清空（ApplyDropToggle）双保险；注意会连祭坛/宝箱类金币一起断（「爆率0」语义）。
/// 装备两口在掉落开着时还做稀度过滤（DropFilter，用户语义：roll 照常、稀度不够不出货）。
/// </summary>