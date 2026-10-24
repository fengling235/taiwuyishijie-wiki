using System;
using System.Collections.Generic;
using Game;
using Game.BattleProperty;

namespace TaiwuDpsMeter;

public enum CharKind
{
    Unknown,
    Player,
    Squire,
    Summon,
    Enemy,
}

public class CharEntry
{
    public CharKind Kind;
    public string Name;
    /// <summary>侍从/召唤的主人 uid；null=无或未解析。注意 0 是合法 uid（实测玩家0），不能用 0 当"无"。</summary>
    public int? OwnerUid;
    /// <summary>玩家已选进化：功法族前缀(WSBF) → SkillUpgrades 行名(WSBF_E1)。null=非玩家或未采集。
    /// 分支技能体(E1/E2/E3)谁都会发射，只有这里才是「该玩家真正进化到哪」的权威。</summary>
    public Dictionary<string, string> EvoByFamily;
    public double ResolvedMs;
}

/// <summary>
/// 战斗角色 uid → 展示名/类别 解析（仅房主侧有效：BattleManager 是宿主模拟侧对象，客人端为空）。
/// 通过 BattleManager.GetBattleCharacterByUid 拿 IBattleCharacter，再 TryCast 分流：
///   玩家直接取名；侍从带 _ownerCharacterUid 可并主；召唤物只有 _playerCharacterIndex，
///   依赖平时解析玩家时顺手维护的 index→uid 反查表。缓存 30 秒（含解析失败的结果）。
/// 递归带 depth 上限，防环形归属栈溢出；解析层一旦异常则永久退化为 #uid 显示。
/// </summary>
internal sealed class CharacterRegistry
{
    private const double CacheTtlMs = 30_000;
    private const int MaxDepth = 2;

    /// <summary>槽位号(characterIndex) → 中文角色名 / Steam 玩家名。营地页 UpdateCampsite 自带 playerId，
    /// 战斗对象的 GetName() 只给内部拼音名（RanXinDu），中文显示名只能从这里来。</summary>
    private static readonly Dictionary<int, string> CnNameByIndex = new();
    private static readonly Dictionary<int, string> SteamByIndex = new();
    /// <summary>角色名 → Steam 玩家名（战斗内队友条回调补充；主键路走上面的按槽位表）。</summary>
    private static readonly Dictionary<string, string> SteamByCharName = new();

    /// <summary>营地页 roster 回调：按槽位记 中文角色名与 Steam 名（playerId=玩家槽位号）。</summary>
    public static void NoteMember(int playerId, string charName, string steamName)
    {
        try
        {
            if (playerId < 0 || playerId > 15) return;
            charName = charName?.Trim();
            if (!string.IsNullOrWhiteSpace(charName)) CnNameByIndex[playerId] = charName;
            if (!string.IsNullOrWhiteSpace(steamName) && !steamName.Contains('*')) SteamByIndex[playerId] = steamName.Trim();
            _instanceCache?._cache.Clear();
        }
        catch { /* 名字链路永远不抛 */ }
    }

    public static void NoteSteamName(string charName, string steamName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(charName) || string.IsNullOrWhiteSpace(steamName)) return;
            if (steamName.Contains('*')) return; // 隐私模式打码名不要
            charName = charName.Trim();
            steamName = steamName.Trim();
            // 冲突防御（2026-10-23 实测踩坑）：战斗内队友条 UpdateBattle 无 id 参数且不可靠——
            // 多人局实测把同一个 Steam 名（房主名"我不配"）填给了每个队友行 → 面板两行同名。
            // 规则：一个 Steam 名只允许属于一个角色名；要挂到第二个角色名=数据源污染，拒收。
            foreach (var v in SteamByCharName.Values)
                if (v == steamName) return;
            if (SteamByCharName.TryGetValue(charName, out var old) && old == steamName) return;
            SteamByCharName[charName] = steamName;
            // 新玩家名到达后让缓存重建，玩家行尽快换成「玩家名（角色名）」
            _instanceCache?._cache.Clear();
        }
        catch
        {
            // 名字链路永远不抛
        }
    }

    private readonly Dictionary<int, CharEntry> _cache = new();
    private readonly Dictionary<int, int> _indexToUid = new();
    private bool _resolverBroken;
    private readonly HashSet<int> _nameFailLogged = new(); // 中文名链失败诊断：每 uid 记一次

    private static CharacterRegistry _instanceCache;

    public CharacterRegistry()
    {
        _instanceCache = this;
    }

    public void Reset()
    {
        _cache.Clear();
        _indexToUid.Clear();
        CnNameByIndex.Clear();
        SteamByIndex.Clear();
        SteamByCharName.Clear();
    }

    public CharEntry Resolve(int uid) => Resolve(uid, 0);

    private CharEntry Resolve(int uid, int depth)
    {
        var now = DamageBus.NowMs;
        if (_cache.TryGetValue(uid, out var cached) && now - cached.ResolvedMs < CacheTtlMs)
            return cached;

        // demo 判定放缓存 miss 之后：热路径（缓存命中）零 config 读——Resolve 每事件都会路过这里

        var entry = ResolveCore(uid, depth);
        entry.ResolvedMs = now;
        _cache[uid] = entry;
        return entry;
    }

    private CharEntry ResolveCore(int uid, int depth)
    {
        var miss = new CharEntry { Kind = CharKind.Unknown, Name = "#" + uid };
        if (depth > MaxDepth || _resolverBroken) return miss;

        try
        {
            var battleManager = StartGame.BattleManager();
            if (battleManager == null) return miss;

            var character = battleManager.GetBattleCharacterByUid(uid);
            if (character == null) return miss;

            var player = character.TryCast<BattlePlayerCharacter>();
            if (player != null)
            {
                int idx = player._characterIndex;
                if (idx >= 0) _indexToUid[idx] = uid;

                // 中文名权威链（2026-10-20 错位修复）：走游戏自己的 characterIndex→成员换算
                // GameServerPlayerManager.GetGameClientPlayer → BattlePlayerData.currentCharacterName
                // （营地页那条是 playerId=RoomMember@0x19 房间编号，与 characterIndex 两套空间、
                // 不保证同序——四人局实测错位：官方喧泣无花的太始剑气被面板挂到据说下棋好行）。
                // 营地页表降级为 fallback（客人端/服务器侧不可用时）；Steam 名不走 BattleData()
                // （可能反序列化 bytes，不冒险），仍取营地页表。
                // 2026-10-23 诊断：多人局实测本链全退化（面板显示拼音）——失败原因按 uid 记一次日志定位
                string rawName = null;
                string failReason = null;
                try
                {
                    var gspm = StartGame.GameServerPlayerManager();
                    if (gspm == null) failReason = "gspm=null";
                    else
                    {
                        var member = gspm.GetGameClientPlayer(idx);
                        if (member == null) failReason = "member=null(idx=" + idx + ")";
                        else
                        {
                            var bpd = member._battlePlayerData;
                            if (bpd == null) failReason = "bpd=null";
                            else
                            {
                                rawName = SafeName(bpd.currentCharacterName);
                                if (rawName == null) failReason = "currentCharacterName=空";
                            }
                        }
                    }
                }
                catch (Exception e) { failReason = e.GetType().Name; /* 客人端服务器侧不可用 → 营地表 fallback */ }
                if (rawName == null && idx >= 0 && CnNameByIndex.TryGetValue(idx, out var cn))
                    rawName = SafeName(cn);
                if (rawName == null)
                {
                    rawName = SafeName(TryGetName(character)) ?? ("玩家#" + uid);
                    if (!_nameFailLogged.Contains(uid))
                    {
                        _nameFailLogged.Add(uid);
                        Plugin.Log.LogInfo($"[name][诊] uid={uid} idx={idx} 中文名链失败（{failReason ?? "?"}），显示拼音 {rawName}");
                    }
                }
                string steam = idx >= 0 && SteamByIndex.TryGetValue(idx, out var st) ? st : null;
                return new CharEntry { Kind = CharKind.Player, Name = ComposePlayerName(rawName, steam), OwnerUid = null, EvoByFamily = HarvestEvolutions(player) };
            }

            var squire = character.TryCast<BattleSquireCharacter>();
            if (squire != null)
            {
                // 名字用侍从自己的（多个侍从可区分）。归属优先走主人直接引用
                // （_battlePlayerCharacter@0x90，实测 2026-10-18 那局 _ownerCharacterUid 没对上），uid 字段兜底
                string own = SafeName(TryGetName(character)) ?? ("侍从#" + uid);
                int? owner = OwnerFromPlayerRef(squire._battlePlayerCharacter);
                if (owner == null && squire._ownerCharacterUid >= 0) owner = squire._ownerCharacterUid;
                if (squire._characterIndex >= 0) _indexToUid[squire._characterIndex] = uid;
                return new CharEntry { Kind = CharKind.Squire, Name = own + "(侍从)", OwnerUid = owner };
            }

            var summon = character.TryCast<BattleSummonCharacter>();
            if (summon != null)
            {
                int? owner = OwnerFromPlayerRef(summon._battlePlayerCharacter);
                if (owner == null && summon._playerCharacterIndex >= 0
                    && _indexToUid.TryGetValue(summon._playerCharacterIndex, out var o))
                    owner = o;
                string own = SafeName(TryGetName(character)) ?? ("召唤#" + uid);
                if (summon._characterIndex >= 0) _indexToUid[summon._characterIndex] = uid;
                return new CharEntry { Kind = CharKind.Summon, Name = own + "(召唤)", OwnerUid = owner };
            }

            if (character.TryCast<BattleEnemyCharacter>() != null)
                return new CharEntry { Kind = CharKind.Enemy, Name = SafeName(TryGetName(character)) ?? "敌人", OwnerUid = null };

            return miss;
        }
        catch (Exception e)
        {
            // 解析层崩一次就永久退化为 #uid 显示，避免每个事件都抛一次
            if (!_resolverBroken)
            {
                _resolverBroken = true;
                Plugin.Log.LogWarning($"CharacterRegistry 解析失败，退化为 #uid 显示：{e.GetType().Name}: {e.Message}");
            }
            return miss;
        }
    }

    /// <summary>采集玩家的已选进化：遍历其技能槽，slot 的功法族前缀 → _superSkillUpgrades 行名。
    /// 分支体(E1/E2/E3)所有玩家都会发射，只有槽上选中的进化才是该玩家的真实形态。</summary>
    private static Dictionary<string, string> HarvestEvolutions(BattlePlayerCharacter player)
    {
        try
        {
            var slots = player._battleSkillSlots;
            if (slots == null || slots.Count == 0) return null;
            Dictionary<string, string> map = null;
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot == null) continue;
                var slotRes = slot._skillSlotResource;
                var super = slot._superSkillUpgrades;
                if (slotRes == null || super == null || string.IsNullOrEmpty(slotRes.name) || string.IsNullOrEmpty(super.name)) continue;
                // Skill_WSBF → WSBF；只收 E 系进化行（WSBF_E1），U 系词条不进
                if (!super.name.Contains("_E")) continue;
                map ??= new Dictionary<string, string>();
                int us = slotRes.name.IndexOf('_');
                if (us > 0) map[slotRes.name.Substring(us + 1)] = super.name;
            }
            return map;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读敌人运行时信息（Boss 面板数据源）：名字/当前血/是否Boss。
    /// Boss 判定：HealthBarType≥3（四大真 Boss）或设计名含 Boss（Dummy_BigBoss 等模板 Boss 档位为空，2026-10-18 实测踩坑）。
    /// （_damageByCharacter 的 per-char 数组在 Boss 面板 v3 改两段式后已无消费者，2026-10-23 删掉了每次的整份拷贝。）</summary>
    public bool TryGetEnemyInfo(int uid, out string name, out float hp, out bool isBoss)
    {
        name = null; hp = -1f; isBoss = false;
        if (_resolverBroken) return false;
        try
        {
            var bm = StartGame.BattleManager();
            if (bm == null) return false;
            var c = bm.GetBattleCharacterByUid(uid);
            var enemy = c?.TryCast<BattleEnemyCharacter>();
            if (enemy == null) return false;

            hp = enemy._currentHp;
            var design = enemy._enemyCharacter;
            if (design != null)
            {
                // 2026-10-21 用户指定 Boss 面板只记录大 Boss（血枫/衣以侯档）：
                // HealthBarType≥4；小 Boss（=3）不进面板；类型为空才退回设计名含 Boss 的模板判定（木桩档位为空）
                isBoss = design.HealthBarType >= 4
                         || (design.HealthBarType == 0 && design.name != null && design.name.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0);
                name = EnemyDisplayName(design);
            }
            return name != null || hp >= 0f;
        }
        catch (Exception e)
        {
            // 只报一次，别刷屏；没这条日志的话这里坏了就是静默的（Boss 面板消失还查不到原因）
            if (!_enemyInfoErrLogged)
            {
                _enemyInfoErrLogged = true;
                Plugin.Log.LogWarning($"TryGetEnemyInfo 首次异常（后续静默）：{e.GetType().Name}: {e.Message}");
            }
            return false;
        }
    }

    private bool _enemyInfoErrLogged;

    private static string EnemyDisplayName(Excel.Generate.Character.EnemyCharacter design)
    {
        try
        {
            if (!string.IsNullOrEmpty(design.Prefab_Path))
            {
                var n = design.Prefab_Path.Trim();
                if (n.StartsWith("敌人_", StringComparison.Ordinal)) n = n.Substring(3);
                int b = n.IndexOf("_Boss", StringComparison.OrdinalIgnoreCase);
                if (b > 0) n = n.Substring(0, b);
                return n;
            }
            return design.name;
        }
        catch
        {
            return design.name;
        }
    }

    /// <summary>玩家显示名：Steam 玩家名（中文角色名）；没有 Steam 名时退角色名。</summary>
    private static string ComposePlayerName(string charName, string steamOverride = null)
    {
        string steam = steamOverride;
        if (steam == null) SteamByCharName.TryGetValue(charName, out steam);
        if (steam != null)
        {
            string s = steam.Length > 12 ? steam.Substring(0, 12) : steam;
            string c = charName.Length > 10 ? charName.Substring(0, 10) : charName;
            return s + "（" + c + "）";
        }
        return charName;
    }


    /// <summary>从随从持有的主人直接引用读 uid（BattleSquireCharacter/BattleSummonCharacter 均有 _battlePlayerCharacter@0x90）。</summary>
    private static int? OwnerFromPlayerRef(BattlePlayerCharacter player)
    {
        try
        {
            if (player == null) return null;
            int uid = player._characterUid;
            return uid >= 0 ? uid : (int?)null;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        return t.Length > 14 ? t.Substring(0, 14) : t;
    }

    private static string TryGetName(IBattleCharacter character)
    {
        try
        {
            return character?.GetName();
        }
        catch
        {
            return null;
        }
    }
}
