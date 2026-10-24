using System;
using System.Collections.Generic;
using Excel.Generate;
using Excel.Generate.Skills;
using Excel.Generate.SkillUpgrades;

namespace TaiwuDpsMeter;

/// <summary>
/// 触发类效果资源名 → 展示名。三级链：
/// 1) 核心异常/通用效果对照表（ZhongDuDamage→中毒·毒跳、ShenHuoBlastDamage→神火引爆…）；
/// 2) 功法词条效果（HZG_U4L1_Damage）：去 Damage 尾巴查 SkillUpgrades（HZG_U4L1→词条中文名），
///    再用前缀查功法表合成「黄竹歌·妨害联动」；
/// 3) 功法基础触发（HRXY_Damage）：前缀查功法表→「候人兮猗·触发」；都链不上给原始名。结果缓存。
/// </summary>
internal static class TriggerNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Core = new(StringComparer.Ordinal)
    {
        // 命名铁律（用户定案）：只用官方名称，绝不自造复合词。
        // 中毒/外伤/神火爆炸均为 Keywords 关键词表原文；无官方名的通用效果不给映射、显示原始名。
        ["ZhongDuDamage"] = "中毒",
        ["ShenHuoBlastDamage"] = "神火爆炸",
        ["LiuXieDamage"] = "外伤",
        ["Qingping1"] = "清平调",
    };

    public static string Get(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "触发/无功法";
        if (Cache.TryGetValue(rawName, out var cached)) return cached;

        string result = null;
        if (Core.TryGetValue(rawName, out var core)) result = core;
        if (result == null) result = SkillNames.SlotDisplayName(rawName); // 效果名本身是槽名（RanXinDu_L3_3_duwu=象幻弥真）
        if (result == null) result = FromElixir(rawName); // 丹药效果（DanYao_1012_2=青蛛蛊丸）
        if (result == null) result = FromEquipment(rawName); // 装备特效（Equip_QXLYJ_Special_1=七星龙渊剑（武器）…）
        if (result == null) result = FromUpgrades(rawName);
        if (result == null) result = FromGongfaPrefix(rawName);
        result ??= rawName;

        Cache[rawName] = result;
        return result;
    }

    /// <summary>装备特效效果行（Equip_QXLYJ_Special_1 / Equip_YYBZ_Special）：装备名（部位）开头；
    /// 剥序号尾巴后若恰是特效技能槽名，SlotDisplayName 已带装备前缀（七星龙渊剑（武器）鹰撮击），
    /// 否则只给装备名（部位）。</summary>
    private static string FromEquipment(string effectName)
    {
        try
        {
            if (!effectName.StartsWith("Equip_", StringComparison.Ordinal)) return null;
            string stripped = effectName;
            int p = stripped.LastIndexOf('_');
            if (p > 0 && p + 1 < stripped.Length && char.IsDigit(stripped[p + 1])) stripped = stripped.Substring(0, p);
            var viaSlot = SkillNames.SlotDisplayName(stripped); // 槽名命中=自带装备前缀版本
            if (!string.IsNullOrEmpty(viaSlot)) return viaSlot;
            return EquipmentNames.PrefixOf(stripped); // 非槽名（纯特效词条效果行）=只给装备名（部位）
        }
        catch
        {
            return null;
        }
    }

    /// <summary>丹药效果（DanYao_1012_2）：剥出丹药号查 Elixir 表 → 官方丹药名（青蛛蛊丸）。</summary>
    private static string FromElixir(string effectName)
    {
        try
        {
            const string prefix = "DanYao_";
            if (!effectName.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int start = prefix.Length, len = 0;
            while (start + len < effectName.Length && char.IsDigit(effectName[start + len])) len++;
            if (len == 0) return null;
            string num = effectName.Substring(start, len);

            var elixirs = DesignResource.ElixirElixirs;
            if (elixirs == null) return null;
            for (int i = 0; i < elixirs.Count; i++)
            {
                var e = elixirs[i];
                if (e == null || e.name != num || string.IsNullOrEmpty(e.ElixirName_TID)) continue;
                var tid = DesignResource.GetTIDElixirTID(e.ElixirName_TID);
                return !string.IsNullOrEmpty(tid?.CHS) ? tid.CHS : null;
            }
        }
        catch
        {
            // 表不可用走下一级
        }
        return null;
    }

    /// <summary>功法词条效果（XXX_U4L1_Damage / XXX_E2ExtraDamage 这类）：链 SkillUpgrades 词条名。</summary>
    private static string FromUpgrades(string effectName)
    {
        try
        {
            var upgrades = DesignResource.SkillUpgradesSkillUpgradess;
            var slots = DesignResource.SkillsSkillSlots;
            if (upgrades == null || slots == null) return null;

            string[] candidates =
            {
                effectName,
                TrimOnce(effectName, "Damage"),
                TrimOnce(effectName, "_Damage"),
                effectName.Replace("ExtraDamage", "").TrimEnd('_'),
            };

            foreach (var key in candidates)
            {
                if (string.IsNullOrEmpty(key) || key.IndexOf('_') <= 0) continue;
                for (int i = 0; i < upgrades.Count; i++)
                {
                    var u = upgrades[i];
                    if (u == null || u.name != key) continue;

                    string upName = UpgradeTidName(u) ?? key;
                    int us = key.IndexOf('_');
                    string gongfa = GongfaName(key.Substring(0, us), slots);
                    string label = StripLevel(upName);
                    return gongfa != null ? gongfa + "·" + label : label;
                }
            }
        }
        catch
        {
            // 表不可用走下一级
        }
        return null;
    }

    /// <summary>功法基础触发（XXX_Damage / XXX_BasicDamage / XXX_E1Damage）：前缀查功法表，只给官方功法名。</summary>
    private static string FromGongfaPrefix(string effectName)
    {
        try
        {
            int us = effectName.IndexOf('_');
            if (us <= 0) return null;
            var gongfa = GongfaName(effectName.Substring(0, us), DesignResource.SkillsSkillSlots);
            return gongfa; // 官方功法名，不加任何自造后缀
        }
        catch
        {
            return null;
        }
    }

    /// <summary>前缀（如 HZG）→ SkillSlot 的 Skill_HZG 行 → 中文名。</summary>
    private static string GongfaName(string prefix, Il2CppSystem.Collections.Generic.List<SkillSlot> slots)
    {
        if (slots == null) return null;
        string want = "Skill_" + prefix;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s != null && s.name == want)
                return SkillNames.ResolveTidPublic(s.NameTID);
        }
        return null;
    }

    private static string UpgradeTidName(SkillUpgrades u)
    {
        try
        {
            var tid = DesignResource.GetTIDSkillUpgradeTID(u.Upgrade_Name);
            return tid != null && !string.IsNullOrEmpty(tid.CHS) ? tid.CHS : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉「  等级N」尾巴（妨害联动  等级1 → 妨害联动）。</summary>
    private static string StripLevel(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int p = s.LastIndexOf("等级", StringComparison.Ordinal);
        return p > 0 ? s.Substring(0, p).Trim() : s;
    }

    private static string TrimOnce(string s, string tail) =>
        s != null && s.EndsWith(tail, StringComparison.Ordinal) && s.Length > tail.Length
            ? s.Substring(0, s.Length - tail.Length)
            : null;
}
