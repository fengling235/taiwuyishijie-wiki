using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TaiwuDpsMeter;

public partial class MeterController
{
    // ---------------------------------------------------------------- 渲染

    private void Render(double now)
    {
        if (!_uiReady) return;

        bool show = Plugin.CfgShowPanel.Value && _visible && !_dpsOff;
        bool uiAlive = Plugin.CfgShowPanel.Value && _visible;
        _panel.SetActive(show);
        if (_toast != null && _toast.gameObject.activeSelf && DamageBus.NowMs > _toastUntilMs)
            _toast.gameObject.SetActive(false);
        // Boss 探测/剪枝在面板隐藏时也要跑（UpdateBossPanel 内部自判可见性），
        // 否则隐藏期间按目标聚合无人清理会无限增长；统计关闭时一并停（无事件累积，无需剪枝），
        // 但要把 Boss 面板显式藏掉——跳过更新=没人管它，按 F7 瞬间在屏上的 Boss 面板会冻结残留
        if (!_dpsOff)
            UpdateBossPanel(now);
        else if (_bossPanel != null && _bossPanel.activeSelf)
            _bossPanel.SetActive(false);

        // 工具箱：只跟总显隐走、不随 DPS 开关藏（关统计后仍可调怪物属性——2026-10-21 用户反馈）；
        // 未拖拽过（X<0）时贴主面板右侧（主面板隐藏时 rect 值仍在，公式照常）；高度随当前 tab 行数收缩
        if (_toolboxPanel != null)
        {
            bool tshow = uiAlive && _toolboxOpen;
            if (_toolboxPanel.activeSelf != tshow) _toolboxPanel.SetActive(tshow);
            if (tshow)
            {
                if ((Plugin.CfgToolboxX?.Value ?? -1f) < 0f)
                    _toolboxRect.anchoredPosition = ClampPos(
                        new Vector2(_panelRect.anchoredPosition.x + PanelW * _uiScale + 12f, -_panelRect.anchoredPosition.y),
                        TbW * _uiScale, 100f);
                int rows = 1;
                foreach (var e in TbLayout)
                    if (e.Tab == _toolboxTab && e.Row + 1 > rows) rows = e.Row + 1;
                float tbh = 66f + (rows - 1) * 32f;
                if (Math.Abs(_toolboxRect.sizeDelta.y - tbh) > 0.5f)
                    _toolboxRect.sizeDelta = new Vector2(TbW, tbh);
            }
        }
        UpdateButtons(); // 工具箱按钮可见性/状态每帧都要刷——DPS 关闭、主面板隐藏时也不能停（tab 切换靠它）
        if (!show) return;

        string mode = _hostMode ? "房主 · 按人" : "客人 · 全队(无归属,含敌方)";
        if (_view == 1) mode = _hostMode ? "房主 · 按目标" : "客人 · 按目标";
        mode += _treeExpanded ? $" · {_treeKeyLabel}收起" : $" · {_treeKeyLabel}展开";
        _modeText.text = mode;

        string summary;
        if (_hostMode)
        {
            summary = $"全队 {Fmt(_ledger.ExecuteTeamDamage)}";
            if (_ledger.EnemyOutDamage > 0)
                summary += $"   承受 {Fmt(_ledger.EnemyOutDamage)}";
        }
        else
        {
            double actual = _ledger.PacketTeamActual;
            summary = $"全队(含敌方) {Fmt(_ledger.GuestTeamDamage)}   实扣(含敌方) {Fmt(actual)}";
            if (_ledger.GuestTeamDamage > 0 && actual > 0 && actual < _ledger.GuestTeamDamage)
                summary += $"   溢出 {(1 - actual / _ledger.GuestTeamDamage) * 100:F1}%";
        }
        _summaryText.text = summary;
        if (!_inputMode && _teamPeakDps > 0)
            _summaryText.text += $"   峰值 {Fmt(_teamPeakDps)}/s";
        if (_inputMode)
            _summaryText.text = $"✏ 输入 {TuneLabel(_inputTargetAction)}：{_inputBuf}_   （回车=生效 · Esc=取消 · 退格删 · 减号切正负）";

        int burstWin = Math.Clamp(Plugin.CfgBurstWindow?.Value ?? 10, 5, 30);
        SetHeader(_view == 0
            ? new[] { "名字", "总伤", "DPS", "占比", "暴击", "击杀", $"近{burstWin}秒", $"近{burstWin}秒结算" }
            : new[] { "目标", "承伤", "血量", "", "", "", "", "" });
        // 峰值 = 全队近N秒 DPS 的历史最大（250ms 渲染粒度；清空/F9 归零）。爆发上限看它，不看均值
        double teamBurstDps = _ledger.TeamBurstSum(now, burstWin) / burstWin;
        if (teamBurstDps > _teamPeakDps) _teamPeakDps = teamBurstDps;

        int rowIdx = 0;
        int hidden = 0;
        for (int i = 0; i < _rowFills.Count; i++)
            _rowFills[i].gameObject.SetActive(false); // 每帧重画：先全灭，被 SetBar 点亮的行才有条
        Array.Fill(_rowMarkerUid, -1);
        if (_view == 0)
        {
            if (!_hostMode)
            {
                SetRow(rowIdx++, "客人模式：飘字广播不含攻击者，仅全队合计", "", "", "", "", "");
            }
            else if (_ledger.SortedActors().Count == 0)
            {
                SetRow(rowIdx++, "等待战斗数据…（仅房主/单机有按人明细）", "", "", "", "", "");
            }
            else
            {
                rowIdx = RenderTree(now, rowIdx, out hidden);
            }
        }
            else
            {
                var targets = _hostMode ? _ledger.SortedHostTargets() : _ledger.SortedGuestTargets();
                double maxD = targets.Count > 0 ? targets[0].Damage : 0;
                foreach (var t in targets)
                {
                    if (rowIdx >= RowCount) break;
                    string name = _hostMode ? _registry.Resolve(t.Key).Name : ("#" + t.Key);
                    string hp = t.HasHp ? $"{t.LastHp}/{t.MaxHp}" : "-";
                    SetBar(rowIdx, BarTarget, maxD > 0 ? t.Damage / maxD : 0);
                    SetRow(rowIdx++, name, Fmt(t.Damage), hp, "", "", "");
                }
            }

        for (int i = rowIdx; i < RowCount; i++) SetRow(i, "", "", "", "", "", "");

        bool footerShown = hidden > 0;
        _footerText.gameObject.SetActive(footerShown);
        if (hidden > 0)
            _footerText.text = $"…还有 {hidden} 行未显示（{_treeKeyLabel} 收起可看全）";

        // 面板高度随实际行数收缩：用几行画多高，未用行的底条隐藏（不再垫 24 行空底）
        int used = Math.Max(rowIdx, 1);
        float newPanelH = -RowsTop + used * RowH + (footerShown ? 30f : 10f);
        if (Math.Abs(_panelRect.sizeDelta.y - newPanelH) > 0.5f)
            _panelRect.sizeDelta = new Vector2(PanelW, newPanelH);
        for (int i = 0; i < _rowBgs.Count; i++)
            _rowBgs[i].gameObject.SetActive(i < used);
        _footerText.rectTransform.anchoredPosition = new Vector2(12f, RowsTop - used * RowH - 4f);
    }

    /// <summary>按人树：一级=玩家（含随从合计），二级=功法明细 + 灵侍/召唤。返回下一行号。</summary>
    private int RenderTree(double now, int rowIdx, out int hidden)
    {
        int burstWin = Math.Clamp(Plugin.CfgBurstWindow?.Value ?? 10, 5, 30);
        hidden = 0;
        var actors = _ledger.SortedActors();
        var byUid = actors.ToDictionary(a => a.Uid);
        var entries = actors.Select(a => (Actor: a, Entry: _registry.Resolve(a.Uid))).ToList();

        // 兜底：单人局里归属仍未解析的侍从/召唤挂到唯一玩家名下（多人局不猜，保持独立行）
        // 玩家身份判定用 活解析∪快照——大厅里活解析全灭，快照仍能认出谁是玩家
        int? solePlayerUid = null;
        var playerEntries = entries
            .Where(x => x.Entry.Kind == CharKind.Player || (x.Actor.SnapValid && x.Actor.KindSnap == CharKind.Player))
            .ToList();
        if (playerEntries.Count == 1) solePlayerUid = playerEntries[0].Actor.Uid;

        int? AttachOwner(Ledger.ActorStats actor, CharEntry entry)
        {
            // 主人与类别：活解析优先，快照兜底（回大厅后 Registry 退化成 Unknown）
            int? owner = entry.OwnerUid ?? (actor.SnapValid ? actor.OwnerUidSnap : null);
            CharKind kind = entry.Kind != CharKind.Unknown ? entry.Kind
                : (actor.SnapValid ? actor.KindSnap : CharKind.Unknown);
            if (owner.HasValue && byUid.ContainsKey(owner.Value) && owner.Value != actor.Uid)
                return owner;
            if ((kind == CharKind.Squire || kind == CharKind.Summon)
                && solePlayerUid.HasValue && solePlayerUid.Value != actor.Uid)
                return solePlayerUid;
            return null;
        }

        var pairs = entries.Select(x => (x, Owner: AttachOwner(x.Actor, x.Entry))).ToList();
        var followerOf = pairs.Where(t => t.Owner.HasValue).ToLookup(t => t.Owner.Value, t => t.x);
        // 一级行 = 自己不是随从的行。注意不能拿 followerOf.Contains(uid) 当判据——
        // 那是「该 uid 是否养了随从」（键=主人），用它过滤会把有随从的玩家整行吞掉（2026-10-18 实战炸过）
        var followerUids = new HashSet<int>(pairs.Where(t => t.Owner.HasValue).Select(t => t.x.Actor.Uid));


        var tops = entries
            .Where(x => !followerUids.Contains(x.Actor.Uid))
            .Select(x =>
            {
                double total = x.Actor.TotalDamage;
                double dps = _ledger.CurrentDps(x.Actor, now);
                long kills = x.Actor.Kills;
                long hits = x.Actor.Hits;
                long crits = x.Actor.Crits;
                foreach (var f in followerOf[x.Actor.Uid])
                {
                    total += f.Actor.TotalDamage;
                    dps += _ledger.CurrentDps(f.Actor, now);
                    kills += f.Actor.Kills;
                    hits += f.Actor.Hits;
                    crits += f.Actor.Crits;
                }
                return (x.Actor, x.Entry, Own: x.Actor.TotalDamage, Total: total, Dps: dps, Kills: kills, Hits: hits, Crits: crits);
            })
            .OrderByDescending(t => t.Total)
            .ToList();

        double grand = tops.Sum(t => t.Total);

        // 动态明细预算（2026-10-24 用户定案）：RowCount=24 装不下 4 人×11 行明细。
        // 规则：折叠的一级只占 1 行，剩余行数均摊给展开的一级——4 人全展开=每人 5 行明细，
        // 收起几个其余自动变多；明细行不分工法/触发预算，混一个池子按行 DPS 排序取 top。
        int expandedTops = 0;
        foreach (var top in tops)
            if ((followerOf[top.Actor.Uid].Any() || top.Actor.BySkill.Count > 0) && IsExpanded(top.Actor.Uid))
                expandedTops++;
        int detailBudget = expandedTops > 0 ? Math.Max(0, (RowCount - tops.Count) / expandedTops) : 0;

        foreach (var top in tops)
        {
            if (rowIdx >= RowCount) { hidden++; continue; }

            bool hasChildren = followerOf[top.Actor.Uid].Any() || top.Actor.BySkill.Count > 0;
            bool expanded = IsExpanded(top.Actor.Uid);
            string marker = hasChildren ? (expanded ? "▼ " : "▶ ") : "  ";
            double share = grand > 0 ? top.Total / grand * 100.0 : 0;
            // 一级暴击=滚动窗口（玩家+随从各环合并，锚在全员最后一跳）：局内暴击加成会变，全程均值没意义
            var topRings = new List<Ledger.CritRing> { top.Actor.Ring };
            double topAnchor = top.Actor.LastEventMs;
            foreach (var f in followerOf[top.Actor.Uid])
            {
                topRings.Add(f.Actor.Ring);
                if (f.Actor.LastEventMs > topAnchor) topAnchor = f.Actor.LastEventMs;
            }
            string critRate = CritPct(topRings, topAnchor);
            double topBurst = top.Actor.Burst.Sum(now, burstWin);
            foreach (var fb in followerOf[top.Actor.Uid]) topBurst += fb.Actor.Burst.Sum(now, burstWin);
            // 一级行始终显示含随从的合计：收起时随从明细藏起但不丢数字，本体细分看二级功法行
            _rowMarkerUid[rowIdx] = hasChildren ? top.Actor.Uid : -1; // 行首▼/▶点击热区
            SetBar(rowIdx, BarPlayer, grand > 0 ? top.Total / grand : 0);
            SetRow(rowIdx++,
                marker + (top.Actor.DisplayName ?? top.Entry.Name),
                Fmt(top.Total),
                Fmt(top.Dps),
                share.ToString("F1", Inv) + "%",
                critRate,
                top.Kills.ToString(Inv),
                Fmt(topBurst / burstWin));

            if (!expanded) continue;

            // 二级：功法明细 + 触发细分。功法按族合并（升级前基础体+进化体/XDD 同族滚一行，
            // 显示名取出现过的最高进化档全名——WoW Details! 同法术不同等级合并的同款口径），占比按本体算。
            // 行 DPS 口径（本局局内解锁的 roguelite 结构适配版）：技能伤害 ÷【该行首次出现 → 该玩家收手】
            // 的时长——后解锁的功法不按整场除（FFLogs 整场分母的前提是开局满配，这里不成立）；
            // 分母终点锚在玩家末跳（冻结口径），起点=该行首跳（族=最早成员体）。这样：
            // ①晚期解锁的功法不冤枉 ②单跳技能也不会除以 1 秒虚高 ③DPS 排序≠总伤排序，有意义。
            double ownBase = top.Actor.TotalDamage;
            double rowEndMs = top.Actor.LastEventMs;
            var detailRows = new List<(string Label, double Damage, bool IsTrigger, double FirstMs, List<Ledger.CritRing> Rings, List<Ledger.RollingWindow> Bursts, string SettleRecent)>();
            var fams = new Dictionary<string, (string Label, double Damage, double FirstMs, List<Ledger.CritRing> Rings, List<Ledger.RollingWindow> Bursts)>();
            foreach (var sk in top.Actor.BySkill.Values)
            {
                if (sk.SkillIndex < 0) continue;
                var (fam, baseLabel, _) = SkillNames.FamilyOf(sk.SkillIndex);
                // 标签口径：玩家槽里实际选中的进化（EvoByFamily）优先；没进化/没采集到就基础名
                string label = baseLabel;
                if (top.Entry.EvoByFamily != null && top.Entry.EvoByFamily.TryGetValue(fam, out var evoRow))
                    label = SkillNames.EvolutionLabel(evoRow) ?? baseLabel;
                if (fams.TryGetValue(fam, out var f))
                {
                    f.Rings.Add(sk.Ring);
                    f.Bursts.Add(sk.Burst);
                    fams[fam] = (f.Label, f.Damage + sk.Damage, Math.Min(f.FirstMs, sk.FirstMs), f.Rings, f.Bursts);
                }
                else
                {
                    fams[fam] = (label, sk.Damage, sk.FirstMs, new List<Ledger.CritRing> { sk.Ring }, new List<Ledger.RollingWindow> { sk.Burst });
                }
            }
            // 结算触发后缀+近窗计数（2026-10-24）：功法行名字带累计结算次数（毒N 爆N 外N），
            // 第 8 列=近 N 秒该功法的结算触发次数（与近N秒 DPS 同窗口）
            foreach (var fk in fams)
            {
                var f = fk.Value;
                detailRows.Add((f.Label + SettlementLedger.SuffixFor(top.Actor.Uid, fk.Key), f.Damage, false, f.FirstMs, f.Rings, f.Bursts,
                    SettlementLedger.RecentSuffixFor(top.Actor.Uid, fk.Key, now, burstWin)));
            }
            if (top.Actor.ByTrigger.Count > 0)
            {
                foreach (var tr in top.Actor.ByTrigger.Values)
                    detailRows.Add((TriggerNames.Get(tr.Name), tr.Damage, true, tr.FirstMs, new List<Ledger.CritRing> { tr.Ring }, new List<Ledger.RollingWindow> { tr.Burst }, null));
            }
            else if (top.Actor.BySkill.TryGetValue(-1, out var noSkill))
            {
                detailRows.Add(("触发/无功法", noSkill.Damage, true, noSkill.FirstMs, new List<Ledger.CritRing> { noSkill.Ring }, new List<Ledger.RollingWindow> { noSkill.Burst }, null));
            }

            double RowDps((string L, double D, bool T, double F, List<Ledger.CritRing> R, List<Ledger.RollingWindow> B, string S) r) =>
                r.D / Math.Max((rowEndMs - r.F) / 1000.0, 1.0);

            // 明细行混排：功法/触发一个池子按行 DPS 降序取 top（行类型只影响占比条颜色）。
            // 毒系 build 毒跳行 DPS 高自然靠前；带技能号的功法行同理——排序说了算，无类型配额
            foreach (var row in detailRows.OrderByDescending(RowDps).Take(detailBudget))
            {
                if (rowIdx >= RowCount) { hidden++; continue; }
                double skShare = ownBase > 0 ? row.Damage / ownBase * 100.0 : 0;
                SetBar(rowIdx, row.IsTrigger ? BarTrigger : BarSkill, ownBase > 0 ? row.Damage / ownBase : 0);
                double rowBurst = 0;
                foreach (var bw in row.Bursts) rowBurst += bw.Sum(now, burstWin);
                SetRow(rowIdx++,
                    "    · " + row.Label,
                    Fmt(row.Damage),
                    Fmt(RowDps(row)),
                    skShare.ToString("F1", Inv) + "%",
                    CritPct(row.Rings, top.Actor.LastEventMs),
                    "",
                    Fmt(rowBurst / burstWin),
                    row.SettleRecent ?? "");
            }

            // 二级：灵侍/召唤分立行
            foreach (var f in followerOf[top.Actor.Uid].OrderByDescending(f => f.Actor.TotalDamage))
            {
                if (rowIdx >= RowCount) { hidden++; continue; }
                double fShare = top.Total > 0 ? f.Actor.TotalDamage / top.Total * 100.0 : 0;
                double fDps = _ledger.CurrentDps(f.Actor, now);
                SetBar(rowIdx, f.Entry.Kind == CharKind.Squire || (f.Actor.SnapValid && f.Actor.KindSnap == CharKind.Squire) ? BarSquire : BarSummon, top.Total > 0 ? f.Actor.TotalDamage / top.Total : 0);
                SetRow(rowIdx++,
                    "    ◦ " + (f.Actor.DisplayName ?? f.Entry.Name),
                    Fmt(f.Actor.TotalDamage),
                    Fmt(fDps),
                    fShare.ToString("F1", Inv) + "%",
                    CritPct(new List<Ledger.CritRing> { f.Actor.Ring }, f.Actor.LastEventMs),
                    f.Actor.Kills.ToString(Inv),
                    Fmt(f.Actor.Burst.Sum(now, burstWin) / burstWin));
            }
        }

        return rowIdx;
    }

    private void SetHeader(IReadOnlyList<string> labels)
    {
        for (int c = 0; c < ColX.Length; c++)
            _headerCells[c].text = c < labels.Count ? labels[c] : "";
    }

    private void SetRow(int idx, string c0, string c1, string c2, string c3, string c4, string c5, string c6 = "", string c7 = "")
    {
        if (idx >= _rowCells.Count) return;
        var cells = _rowCells[idx];
        cells[0].text = c0 ?? "";
        cells[1].text = c1 ?? "";
        cells[2].text = c2 ?? "";
        cells[3].text = c3 ?? "";
        cells[4].text = c4 ?? "";
        cells[5].text = c5 ?? "";
        cells[6].text = c6 ?? "";
        cells[7].text = c7 ?? "";
    }

    /// <summary>给行铺伤害占比条：宽度 = frac × 行宽（Details! 式横向条），frac 无效则灭条。</summary>
    private void SetBar(int idx, Color color, double frac)
    {
        if (idx >= _rowFills.Count) return;
        var f = _rowFills[idx];
        float w = (float)Math.Clamp(frac, 0.0, 1.0) * (PanelW - 20f);
        if (w < 1f)
        {
            f.gameObject.SetActive(false);
            return;
        }
        f.color = color;
        f.rectTransform.sizeDelta = new Vector2(w, RowH - 6f);
        f.gameObject.SetActive(true);
    }
}
