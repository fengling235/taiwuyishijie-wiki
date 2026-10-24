using System;
using System.Collections.Generic;
using Excel.Generate;
using Excel.Generate.Skills;

namespace TaiwuDpsMeter;

/// <summary>
/// 技能槽位 Index → 展示名。三级链路：
/// 1) DesignResource.SkillsSkillSlots（玩家功法表，109 行）→ NameTID → GetTIDSkillTID().CHS 中文名；
/// 2) DesignResource.SkillsSkills（全技能表，258 行，含进化技能体/灵侍/召唤子技能）——
///    进化体（&lt;前缀&gt;_E&lt;N&gt;Skill，如 TSS_E1Skill）先查 SkillUpgrades 进化表拿全名（天蛇索·毒），
///    链不上再经 SkillSlot_Name 回功法表拿基础名并追加 ·E 序号，再不行（XF_Skill3 这类）给内部名；
/// 3) 退 "技N"。skill&lt;0 一律归"触发/无功法"。结果缓存。
/// </summary>
internal static class SkillNames
{
    private static readonly Dictionary<int, string> Cache = new();
    private static bool _broken;

    public static string Get(int skillIndex)
    {
        if (skillIndex < 0) return "触发/无功法";
        if (Cache.TryGetValue(skillIndex, out var cached)) return cached;

        string result = null;
        if (!_broken)
        {
            try
            {
                // 查询顺序与游戏官方解析器一致（GetBattleSkillSlot(int) 拿 id 比对 Skills_Skill.Index）：
                // 必须全技能表优先——功法槽表(109行,索引0-108)与全技能表(258行)撞号，
                // 先查槽表会把进化体劫持成无关功法（实锤：51=五圣鞭法·毒被槽表51=烬羽归顶掉）
                var skills = DesignResource.SkillsSkills;

                Skill full = null;
                if (skills != null)
                    for (int i = 0; i < skills.Count; i++)
                    {
                        var s = skills[i];
                        if (s != null && s.Index == skillIndex) { full = s; break; }
                    }

                if (full != null)
                {
                    // 进化技能体（TSS_E1Skill）：真名在 SkillUpgrades 进化表（天蛇索·毒）
                    result = ResolveEvolution(full.name);

                    if (result == null)
                    {
                        // 技能体自身名字恰是槽名时优先按名直查（Skill_TagStat_13=黄竹歌、RanXinDu_L3_3_duwu=象幻弥真）
                        result = SlotDisplayName(full.name);

                        if (result == null && !string.IsNullOrEmpty(full.SkillSlot_Name))
                        {
                            // 经 SkillSlot_Name 回功法/灵侍槽拿中文名
                            var slots0 = DesignResource.SkillsSkillSlots;
                            if (slots0 != null)
                                for (int i = 0; i < slots0.Count; i++)
                                {
                                    var s = slots0[i];
                                    if (s != null && s.name == full.SkillSlot_Name)
                                    {
                                        result = ResolveTid(s.NameTID);
                                        // 装备槽技能体（YCH_Special_Skill→Equip_YCH_Special_Skill）：
                                        // 显示「鹰撮环（手镯）鹰撮击」——装备特效无游戏内名称，面板标归属
                                        var ep = EquipmentNames.PrefixOf(full.SkillSlot_Name);
                                        if (ep != null) result = ep + result;
                                        break;
                                    }
                                }
                        }

                        if (result == null)
                            result = string.IsNullOrWhiteSpace(full.name) ? null : full.name;
                    }
                }
                else
                {
                    // 全技能表没有的 id 才回功法槽表（兜底，正常战斗流不会走到）
                    var slots = DesignResource.SkillsSkillSlots;
                    if (slots != null)
                        for (int i = 0; i < slots.Count; i++)
                        {
                            var s = slots[i];
                            if (s != null && s.Index == skillIndex)
                            {
                                result = ResolveTid(s.NameTID) ?? s.name;
                                break;
                            }
                        }
                }
            }
            catch
            {
                // 表不可用（时机过早/结构变化），本局全部退回内部名
                _broken = true;
            }
        }

        result ??= "技" + skillIndex;
        result = result?.Trim();
        if (string.IsNullOrEmpty(result)) result = "技" + skillIndex;
        Cache[skillIndex] = result;
        return result;
    }

    /// <summary>进化技能体名（GSTX_E1Skill 这类）→ SkillUpgrades 进化表全名（姑射踏雪·延）。查不到返回 null。</summary>
    private static string ResolveEvolution(string internalName)
    {
        if (string.IsNullOrEmpty(internalName) || !internalName.EndsWith("Skill", StringComparison.Ordinal)) return null;
        string core = internalName.Substring(0, internalName.Length - 5); // GSTX_E1
        int p = core.LastIndexOf("_E", StringComparison.Ordinal);
        if (p <= 0) return null;
        string digits = core.Substring(p + 2);
        if (digits.Length == 0 || digits.Length > 2) return null;
        for (int i = 0; i < digits.Length; i++)
            if (digits[i] < '0' || digits[i] > '9') return null;

        try
        {
            var list = DesignResource.SkillUpgradesSkillUpgradess;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var u = list[i];
                if (u != null && u.name == core)
                {
                    var tid = DesignResource.GetTIDSkillUpgradeTID(u.Upgrade_Name);
                    return tid != null && !string.IsNullOrEmpty(tid.CHS) ? tid.CHS.Trim() : null;
                }
            }
        }
        catch
        {
            // 进化表不可用就走兜底链
        }
        return null;
    }

    /// <summary>从内部名解析进化档位（GSTX_E3Skill → 3）；无 _E 前缀段则返回 0。</summary>
    private static int EvSuffix(string internalName)
    {
        if (string.IsNullOrEmpty(internalName)) return 0;
        int p = internalName.IndexOf("_E", StringComparison.Ordinal);
        if (p < 0) return 0;
        int q = p + 2, v = 0;
        while (q < internalName.Length && internalName[q] >= '0' && internalName[q] <= '9')
        {
            v = v * 10 + (internalName[q] - '0');
            q++;
        }
        bool end = q >= internalName.Length || internalName[q] == '_' || internalName[q] == 'S';
        return (q > p + 2 && end) ? v : 0;
    }

    /// <summary>清洗设计表中文名里的富文本标签（熊掌击等名字自带 &lt;color&gt; 包裹，关富文本时会显示成乱码）。</summary>
    internal static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int lt;
        while ((lt = s.IndexOf('<')) >= 0)
        {
            int gt = s.IndexOf('>', lt + 1);
            if (gt < 0) break;
            s = s.Substring(0, lt) + s.Substring(gt + 1);
        }
        return s.Trim();
    }

    private static string ResolveTid(string nameTid)
    {
        if (string.IsNullOrEmpty(nameTid)) return null;
        try
        {
            var tid = DesignResource.GetTIDSkillTID(nameTid);
            return tid != null && !string.IsNullOrEmpty(tid.CHS) ? Clean(tid.CHS) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给 TriggerNames 复用的 TID 解析（同 ResolveTid）。</summary>
    internal static string ResolveTidPublic(string nameTid) => ResolveTid(nameTid);

    private static readonly Dictionary<string, string> EvoLabelCache = new(StringComparer.Ordinal);

    /// <summary>进化行名（WSBF_E1）→ 全名（五圣鞭法·毒）。给「玩家已选进化」标签用。</summary>
    internal static string EvolutionLabel(string upgradeRowName)
    {
        if (string.IsNullOrEmpty(upgradeRowName)) return null;
        lock (EvoLabelCache)
        {
            if (EvoLabelCache.TryGetValue(upgradeRowName, out var c)) return c;
        }
        string result = null;
        try
        {
            var ups = DesignResource.SkillUpgradesSkillUpgradess;
            if (ups != null)
                for (int i = 0; i < ups.Count; i++)
                {
                    var u = ups[i];
                    if (u == null || u.name != upgradeRowName) continue;
                    var tid = DesignResource.GetTIDSkillUpgradeTID(u.Upgrade_Name);
                    result = tid != null && !string.IsNullOrEmpty(tid.CHS) ? tid.CHS.Trim() : null;
                    break;
                }
        }
        catch { /* 表不可用返回 null，调用方退基础名 */ }
        lock (EvoLabelCache) { EvoLabelCache[upgradeRowName] = result; }
        return result;
    }

    /// <summary>
    /// 技能的功法族信息，用于「升级前后合并」：同一功法槽的基础体/进化体/XDD 变体归一族
    /// （WSBF_BasicSkill 与 WSBF_E1Skill 同族），显示名取出现过的最高进化档全名。
    /// 返回 (族键, 显示名, 进化档)；无族（灵侍/召唤独立技）族键为 "#索引" 每技独立。
    /// </summary>
    internal static (string Family, string Label, int Evo) FamilyOf(int skillIndex)
    {
        if (skillIndex < 0) return (null, null, 0);
        lock (FamilyCache)
        {
            if (FamilyCache.TryGetValue(skillIndex, out var cached)) return cached;
        }

        var info = ComputeFamily(skillIndex);
        lock (FamilyCache) { FamilyCache[skillIndex] = info; }
        return info;
    }

    private static readonly Dictionary<int, (string, string, int)> FamilyCache = new();

    private static (string Family, string Label, int Evo) ComputeFamily(int skillIndex)
    {
        try
        {
            var skills = DesignResource.SkillsSkills;
            if (skills != null)
                for (int i = 0; i < skills.Count; i++)
                {
                    var s = skills[i];
                    if (s == null || s.Index != skillIndex) continue;

                    string slotRef = s.SkillSlot_Name;
                    if (!string.IsNullOrEmpty(slotRef) && !string.IsNullOrEmpty(s.name))
                    {
                        string n = s.name;
                        int p = n.IndexOf('_');
                        if (slotRef.StartsWith("Skill_") && p > 0)
                        {
                            // 玩家功法族：族=前缀(WSBF)，标签=基础功法名，进化由 EvoByFamily 覆盖
                            string prefix = n.Substring(0, p);
                            return (prefix, BaseName(prefix) ?? Get(skillIndex), EvSuffix(n));
                        }
                        // 灵侍/装备等子技能：族=槽名（HuLing_Skill_3 把 196-199 并成一行），标签=槽中文名
                        return (slotRef, Get(skillIndex), 0);
                    }
                    return ("#" + skillIndex, Get(skillIndex), 0);
                }
        }
        catch { /* 表不可用按独立技处理 */ }
        return ("#" + skillIndex, Get(skillIndex), 0);
    }

    private static readonly Dictionary<string, string> BaseNameCache = new(StringComparer.Ordinal);

    /// <summary>槽表行名精确匹配 → 中文名（RanXinDu_L3_3_duwu=槽90=象幻弥真）。给触发行解析器补槽表这一跳。</summary>
    internal static string SlotDisplayName(string rowName)
    {
        var raw = SlotDisplayNameRaw(rowName);
        if (raw == null) return null;
        // 装备槽（Equip_YCH_Special_Skill）：加装备归属前缀（鹰撮环（手镯）鹰撮击）——
        // 游戏内装备特效无名称，玩家看不出面板上的「鹰撮击」是哪件装备的（2026-10-24 需求）
        var prefix = EquipmentNames.PrefixOf(rowName);
        return prefix != null ? prefix + raw : raw;
    }

    /// <summary>SlotDisplayName 的原始值（不带装备前缀）——TriggerNames 合成装备行时用。</summary>
    internal static string SlotDisplayNameRaw(string rowName)
    {
        if (string.IsNullOrEmpty(rowName)) return null;
        lock (BaseNameCache)
        {
            if (BaseNameCache.TryGetValue("S:" + rowName, out var c)) return c;
        }
        string result = null;
        try
        {
            var slots = DesignResource.SkillsSkillSlots;
            if (slots != null)
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (s != null && s.name == rowName) { result = ResolveTid(s.NameTID); break; }
                }
        }
        catch { /* 表不可用返回 null */ }
        lock (BaseNameCache) { BaseNameCache["S:" + rowName] = result; }
        return result;
    }

    /// <summary>族前缀(WSBF) → 基础功法名(五圣鞭法)。</summary>
    private static string BaseName(string prefix)
    {
        lock (BaseNameCache)
        {
            if (BaseNameCache.TryGetValue(prefix, out var c)) return c;
        }
        string result = null;
        try
        {
            var slots = DesignResource.SkillsSkillSlots;
            if (slots != null)
            {
                string want = "Skill_" + prefix;
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    if (s != null && s.name == want) { result = ResolveTid(s.NameTID); break; }
                }
            }
        }
        catch { /* 退 Get() 的结果 */ }
        lock (BaseNameCache) { BaseNameCache[prefix] = result; }
        return result;
    }
}

/// <summary>
/// 装备名（部位）前缀（2026-10-24 需求：游戏内装备特效没有名称，面板标归属）：
/// Equip_ 槽名/特效效果名 → 「鹰撮环（手镯）」「七星龙渊剑（武器）」；非装备源返回 null。
/// 懒建一次：EquipmentBase 全表 → 中文名（TID_EquipTID）+ 部位（Equip_Slot{1-9}=冠饰/护甲/…），
/// 登记键=装备名自身 + _Special_Skill/_Special/_Skill 槽名变体 + Fixed_Traits 特效词条名。
/// </summary>
internal static class EquipmentNames
{
    private static Dictionary<string, string> _map;
    private static bool _built;

    internal static string PrefixOf(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("Equip_", StringComparison.Ordinal)) return null;
        if (!_built) Build();
        if (_map == null) return null;
        if (_map.TryGetValue(name, out var v)) return v;
        // 带序号尾巴的效果行（Equip_QXLYJ_Special_1）剥掉再查一次
        int p = name.LastIndexOf('_');
        if (p > 0 && p + 1 < name.Length && char.IsDigit(name[p + 1])
            && _map.TryGetValue(name.Substring(0, p), out v)) return v;
        return null;
    }

    private static void Build()
    {
        try
        {
            var bases = DesignResource.EquipmentEquipmentBases;
            if (bases == null || bases.Count == 0) return; // design 表未就绪：不置 _built，下次调用重试
            var map = new Dictionary<string, string>(256);
            for (int i = 0; i < bases.Count; i++)
            {
                var b = bases[i];
                if (b == null || string.IsNullOrEmpty(b.name)) continue;
                string chs = null;
                try { chs = SkillNames.Clean(DesignResource.GetTIDEquipTID(b.EquipmentNameTID)?.CHS); } catch { }
                if (string.IsNullOrEmpty(chs)) continue;
                string part = null;
                try { part = SkillNames.Clean(DesignResource.GetTIDEquipTID("Equip_Slot" + b.Equip_Slot)?.CHS); } catch { }
                string label = string.IsNullOrEmpty(part) ? chs : $"{chs}（{part}）";
                map[b.name] = label;
                map[b.name + "_Special_Skill"] = label;
                map[b.name + "_Special"] = label;
                map[b.name + "_Skill"] = label;
                var ft = b.Fixed_Traits;
                if (ft != null)
                    foreach (var t in ft)
                        if (!string.IsNullOrEmpty(t) && !map.ContainsKey(t)) map[t] = label; // 特效词条名（Equip_YYBZ_Special）
            }
            _map = map;
            _built = true;
            Plugin.Log.LogInfo($"[equip] 装备显示名索引就绪：{map.Count} 键");
        }
        catch (Exception e)
        {
            DamageBus.ReportError("EquipNames.Build", e);
        }
    }
}
