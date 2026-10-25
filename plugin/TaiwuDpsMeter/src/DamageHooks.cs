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

    // 暴击率峰值采样（2026-10-25 用户问「功法暴击超过 100% 多少」）：
    // 定案 BattleEffect.Execute 全函数只有 0xED1B02 一处 RandomProbability（紧接写 _isCritical@0x60；
    // 其余 roll——校验器/BattleTrigger/掉落/CheckAndDie/穿透——都在队列侧 Execute 返回之后或 LossHP 死亡
    // 链里），所以 Type=4 窗口内第一次 RandomProbability 调用即暴击 roll；p 是 Critical_Type 清零
    // 门控后的最终值（毒跳=1 会喂 0，如实反映「不吃暴击」）。深度计数防嵌套 Execute 误清采样槽。
    internal static int CritWatchDepth;
    internal static float PendingCritChance = -1f;
    internal static bool CritRollTaken;

    private static bool IsDamageEffect(BattleEffect e)
    {
        try { return e?._effectResource?.Type == 4; } catch { return false; }
    }

    private static void Prefix(BattleEffect __instance)
    {
        if (IsDamageEffect(__instance))
        {
            if (CritWatchDepth == 0) { CritRollTaken = false; PendingCritChance = -1f; }
            CritWatchDepth++;
        }
    }

    private static void Finalizer(BattleEffect __instance)
    {
        if (CritWatchDepth > 0 && IsDamageEffect(__instance)) CritWatchDepth--;
    }

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
                CritChance = CritRollTaken ? PendingCritChance : -1f, // 深度窗口内的暴击 roll 原值（可>1）
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
/// 暴击 roll 概率采样（2026-10-25 主功能 + 神火取证保留）：Execute Type=4 窗口内第一次
/// 调用=暴击 roll（定案唯一 roll 点，见 BattleEffect_Execute_Patch 采样注释），
/// p 原值进 DamageEvent.CritChance 供面板统计「暴击率峰值/溢出」。神火取证日志仅全量版。
/// </summary>
[HarmonyPatch(typeof(Game.Utils.GameTools), nameof(Game.Utils.GameTools.RandomProbability))]
internal static class GameTools_RandomProbability_Patch
{

    private static void Prefix(float __0)
    {
        // 采样分支无条件（公开版也有暴击峰值）：只写静态槽，零分配
        if (BattleEffect_Execute_Patch.CritWatchDepth > 0 && !BattleEffect_Execute_Patch.CritRollTaken)
        {
            BattleEffect_Execute_Patch.CritRollTaken = true;
            BattleEffect_Execute_Patch.PendingCritChance = __0;
        }
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




