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
    // ---------------------------------------------------------------- Boss 面板

    /// <summary>薄封装：UiKit.Cell 的 Boss 面板版（签名保持，调用点零改动）。</summary>
    private Text BossCell(GameObject parent, float x, float y, float w, float h, TextAnchor align, Color color, int size, bool bold)
        => UiKit.Cell(parent.transform, _font, x, y, w, h, align, color, size, bold);

    /// <summary>薄封装：UiKit.Img 的 Boss 面板版。</summary>
    private Image BossImg(GameObject parent, float y, float x, float w, float h, Color color)
        => UiKit.Img(parent.transform, y, x, w, h, color);

    private void BuildBossPanel(GameObject rootGo, float alpha, Sprite rounded)
    {
        var bp = new GameObject("BossPanel");
        bp.transform.SetParent(rootGo.transform, false);
        var brt = bp.AddComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 1f);
        brt.anchorMax = new Vector2(0f, 1f);
        brt.pivot = new Vector2(0f, 1f);
        brt.anchoredPosition = new Vector2(40f, -300f);
        brt.sizeDelta = new Vector2(PanelW, 120f);
        var bbg = bp.AddComponent<Image>();
        bbg.color = new Color(0.10f, 0.05f, 0.08f, alpha);
        bbg.raycastTarget = true;
        if (rounded != null)
        {
            bbg.sprite = rounded;
            bbg.type = Image.Type.Sliced;
        }

        _bossTitle = BossCell(bp, 12f, -6f, 300f, 20f, TextAnchor.UpperLeft, ColAccent, 15, true);
        _bossTimeText = BossCell(bp, 316f, -8f, PanelW - 328f, 20f, TextAnchor.UpperRight, ColDim, 12, false);
        BossImg(bp, -28f, 12f, PanelW - 24f, 14f, new Color(1f, 1f, 1f, 0.10f));
        _bossHpFill = BossImg(bp, -28f, 12f, 0f, 14f, new Color(0.85f, 0.25f, 0.25f, 0.92f));
        _bossHpText = BossCell(bp, 12f, -30f, PanelW - 24f, 12f, TextAnchor.MiddleCenter, ColName, 11, false);
        // 两段式（Details! 的 boss segment 口径：主视图=玩家排行，下钻=技能分解）：
        // 上段「玩家（含随从）」伤害/占比/暴击(窗口)，下段「功法/异常（全队）」
        BossCell(bp, 12f, -44f, 236f, 14f, TextAnchor.UpperLeft, ColHeader, 11, false).text = "玩家（含随从）";
        BossCell(bp, 250f, -44f, 110f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "伤害";
        BossCell(bp, 362f, -44f, 90f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "占比";
        BossCell(bp, 454f, -44f, 134f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "暴击";
        for (int i = 0; i < BossPlayerRowsMax; i++)
        {
            float y = -60f - i * 18f;
            var cells = new Text[4];
            cells[0] = BossCell(bp, 12f, y, 236f, 18f, TextAnchor.UpperLeft, ColName, 12, false);
            cells[1] = BossCell(bp, 250f, y, 110f, 18f, TextAnchor.UpperRight, ColTotal, 12, false);
            cells[2] = BossCell(bp, 362f, y, 90f, 18f, TextAnchor.UpperRight, ColShare, 12, false);
            cells[3] = BossCell(bp, 454f, y, 134f, 18f, TextAnchor.UpperRight, ColCrit, 12, false);
            _bossPlayerRows.Add(cells);
        }
        const float skillHeaderY = -138f; // -60 - 4*18 - 6
        BossCell(bp, 12f, skillHeaderY, 236f, 14f, TextAnchor.UpperLeft, ColHeader, 11, false).text = "功法/异常（全队）";
        BossCell(bp, 250f, skillHeaderY, 110f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "伤害";
        BossCell(bp, 362f, skillHeaderY, 90f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "占比";
        BossCell(bp, 454f, skillHeaderY, 134f, 14f, TextAnchor.UpperRight, ColHeader, 11, false).text = "暴击";
        for (int i = 0; i < BossRowsMax; i++)
        {
            float y = skillHeaderY - 16f - i * 18f;
            var cells = new Text[4];
            cells[0] = BossCell(bp, 12f, y, 236f, 18f, TextAnchor.UpperLeft, ColName, 12, false);
            cells[1] = BossCell(bp, 250f, y, 110f, 18f, TextAnchor.UpperRight, ColTotal, 12, false);
            cells[2] = BossCell(bp, 362f, y, 90f, 18f, TextAnchor.UpperRight, ColShare, 12, false);
            cells[3] = BossCell(bp, 454f, y, 134f, 18f, TextAnchor.UpperRight, ColCrit, 12, false);
            _bossRows.Add(cells);
        }
        _bossPanel = bp;
        _bossPanelRt = brt;
        bp.SetActive(false);
    }

    /// <summary>每帧渲染时刷新 Boss 面板：找活着的 Boss 档敌人（血条档≥3），行=全队对它的功法/异常伤害分解；死亡定格 8 秒。</summary>
    private void UpdateBossPanel(double gameNow)
    {
        if (_bossPanel == null) return;
        try
        {
            float hp = -1f;
            {
                int bestUid = -1;
                string bestName = null;
                float bestHp = 0f;
                bool sawBoss = false; // 本帧探测列表里还能不能看到当前 Boss
                foreach (var t in _ledger.SortedHostTargets(20)) // 探测加深到前20：Boss 挤进榜单更早被发现
                {
                    if (!_registry.TryGetEnemyInfo(t.Key, out var name, out var ehp, out var boss))
                    {
                        _bossProbeLogged.Remove(t.Key);
                        continue;
                    }
                    // 一次性探测日志：下局 Boss 战若面板没出，日志能直接告诉我每个目标的判定结果
                    if (_bossProbeLogged.Add(t.Key))
                        Plugin.Log.LogInfo($"[boss] 探测 uid={t.Key} name={name ?? "?"} hp={ehp:F0} boss={boss}");
                    if (boss)
                    {
                        _bossEverUids.Add(t.Key);
                        if (t.Key == _bossUid)
                        {
                            sawBoss = true;
                            if (ehp <= 0f) _bossDeadZero = true; // 死亡路径1：血量读数归零
                        }
                        if (ehp > 0f && ehp > bestHp)
                        {
                            bestUid = t.Key; bestName = name; bestHp = ehp;
                        }
                    }
                }
                // 剪枝：Boss 60 秒不挨打才清（保住从首跳起的完整账），确认过的 Boss 永不清
                _ledger.PruneBossAgg(gameNow, _bossEverUids);
                // 死亡路径2：最近还在挨打、本帧却从榜上彻底消失（死亡炸尸/对象回收）——30 秒护栏防「活着但暂未上榜」误判
                if (_bossUid >= 0 && !_bossDeadZero && !sawBoss
                    && gameNow - _bossAliveMs > 1500 && gameNow - _bossAliveMs < 30000)
                    _bossDeadZero = true;
                if (bestUid >= 0)
                {
                    if (bestUid != _bossUid) { _bossUid = bestUid; _bossName = bestName; _bossMaxHp = 0f; _bossDeadZero = false; }
                    _bossMaxHp = Math.Max(_bossMaxHp, bestHp);
                    _bossAliveMs = gameNow;
                    hp = bestHp;
                }

                // 行 = 全队对该 Boss 的功法/异常伤害分解（跨玩家按族合并，与主面板明细同一套命名）
                _bossLastRows.Clear();
                _bossPlayerLastRows.Clear();
                _bossTimeValid = false;
                if (_bossUid >= 0 && _ledger.BossByTarget.TryGetValue(_bossUid, out var bagg))
                {
                    // 战斗时间快照（WCL 口径：遭遇时长=首跳→末跳；停机=断流间隙累计）
                    if (bagg.FirstMs >= 0)
                    {
                        _bossTimeValid = true;
                        _bossFightSec = (bagg.LastMs - bagg.FirstMs) / 1000.0;
                        _bossIdleSec = bagg.IdleMs / 1000.0;
                    }

                    // 玩家排行段：按攻击者聚合，随从/召唤并入主人（与主面板一级行同口径）
                    var actors = _ledger.SortedActors();
                    var byOwner = new Dictionary<int, (double Dmg, List<Ledger.CritRing> Rings, double Last)>();
                    foreach (var kv in bagg.ByAttacker)
                    {
                        var ent = _registry.Resolve(kv.Key);
                        int owner = kv.Key;
                        if (ent.Kind == CharKind.Squire || ent.Kind == CharKind.Summon)
                        {
                            owner = ent.OwnerUid ?? kv.Key;
                            if (ent.OwnerUid == null)
                            {
                                foreach (var a in actors)
                                    if (a.Uid == kv.Key && a.SnapValid && a.OwnerUidSnap.HasValue) { owner = a.OwnerUidSnap.Value; break; }
                            }
                        }
                        if (byOwner.TryGetValue(owner, out var o))
                        {
                            o.Dmg += kv.Value.Damage;
                            o.Rings.Add(kv.Value.Ring);
                            if (kv.Value.LastMs > o.Last) o.Last = kv.Value.LastMs;
                            byOwner[owner] = o;
                        }
                        else
                        {
                            byOwner[owner] = (kv.Value.Damage, new List<Ledger.CritRing> { kv.Value.Ring }, kv.Value.LastMs);
                        }
                    }
                    foreach (var kv in byOwner)
                    {
                        string nm = null;
                        foreach (var a in actors) if (a.Uid == kv.Key && !string.IsNullOrEmpty(a.DisplayName)) { nm = a.DisplayName; break; }
                        nm ??= _registry.Resolve(kv.Key).Name;
                        if (string.IsNullOrEmpty(nm)) nm = "#" + kv.Key;
                        _bossPlayerLastRows.Add((nm, kv.Value.Dmg, CritPct(kv.Value.Rings, kv.Value.Last)));
                    }
                    var fams = new Dictionary<string, (string Label, double Dmg, double Last, List<Ledger.CritRing> Rings)>();
                    foreach (var sk in bagg.BySkill.Values)
                    {
                        var (fam, baseLabel, _) = SkillNames.FamilyOf(sk.SkillIndex);
                        if (fams.TryGetValue(fam, out var f))
                        {
                            f.Dmg += sk.Damage;
                            if (sk.LastMs > f.Last) f.Last = sk.LastMs;
                            f.Rings.Add(sk.Ring);
                            fams[fam] = f;
                        }
                        else
                        {
                            fams[fam] = (baseLabel, sk.Damage, sk.LastMs, new List<Ledger.CritRing> { sk.Ring });
                        }
                    }
                    foreach (var f in fams.Values)
                        _bossLastRows.Add((f.Label, f.Dmg, CritPct(f.Rings, f.Last)));
                    foreach (var tr in bagg.ByTrigger.Values)
                        _bossLastRows.Add((TriggerNames.Get(tr.Name), tr.Damage, CritPct(new List<Ledger.CritRing> { tr.Ring }, tr.LastMs)));
                }
            }

            bool alive = hp > 0f;
            // 面板不再 8 秒自动消失：显示由「Boss ✓/✗」按钮控制，死亡后定格（血量归零）直到手动关闭/清空/换 Boss
            bool show = _bossUid >= 0 && _bossUserOpen && Plugin.CfgShowPanel.Value && _visible && _hostMode && _view == 0 && !_dpsOff;
            if (_bossPanel.activeSelf != show) _bossPanel.SetActive(show);
            if (!show) return;

            if (alive) _bossLastHp = hp;
            else if (_bossDeadZero) _bossLastHp = 0f; // 死亡定格：血量清零而不是停在最后一次活读数

            double total = 0;
            foreach (var r in _bossLastRows) total += r.Damage;

            // 位置：主面板正下方，跟随拖拽/缩放；主面板贴近屏幕底部时钳在屏内
            float s = _uiScale;
            var mp = _panelRect;
            float by = mp.anchoredPosition.y - mp.sizeDelta.y * s - 10f;
            var players = _bossPlayerLastRows.OrderByDescending(r => r.Damage).Take(BossPlayerRowsMax).ToList();
            var ordered = _bossLastRows.OrderByDescending(r => r.Damage).Take(BossRowsMax).ToList();
            float bossH = 46 + 14 + Math.Max(players.Count, 1) * 18 + 22 + 14 + Math.Max(ordered.Count, 1) * 18 + 12f;
            if (by < -(Screen.height - bossH * s - 20f)) by = -(Screen.height - bossH * s - 20f);
            _bossPanelRt.anchoredPosition = new Vector2(mp.anchoredPosition.x, by);
            _bossPanelRt.localScale = new Vector3(s, s, 1f);
            _bossPanelRt.sizeDelta = new Vector2(PanelW, bossH);

            _bossTitle.text = $"BOSS · {_bossName}{(alive ? "" : "（已击杀）")}";
            // 主数字=实际战斗时间（全程−离场，用户点名要的是这个）；无转阶段时只显示一个数
            if (_bossTimeValid)
            {
                double active = Math.Max(_bossFightSec - _bossIdleSec, 0);
                _bossTimeText.text = _bossIdleSec >= 1.0
                    ? $"实战 {MmSs(active)} · 离场 {MmSs(_bossIdleSec)} · 全程 {MmSs(_bossFightSec)}"
                    : $"战斗 {MmSs(_bossFightSec)}";
            }
            else
            {
                _bossTimeText.text = "";
            }
            float frac = _bossMaxHp > 0f ? Mathf.Clamp01(_bossLastHp / _bossMaxHp) : 0f;
            _bossHpFill.rectTransform.sizeDelta = new Vector2((PanelW - 24f) * frac, 14f);
            _bossHpText.text = $"{Fmt(_bossLastHp)} / {Fmt(_bossMaxHp)}  ({frac * 100:F0}%)  ·  全队已输出 {Fmt(total)}";

            double pTotal = 0;
            foreach (var r in players) pTotal += r.Damage;
            for (int i = 0; i < _bossPlayerRows.Count; i++)
            {
                var cells = _bossPlayerRows[i];
                if (i < players.Count)
                {
                    var r = players[i];
                    cells[0].text = r.Name;
                    cells[1].text = Fmt(r.Damage);
                    cells[2].text = pTotal > 0 ? (r.Damage / pTotal * 100).ToString("F1", Inv) + "%" : "";
                    cells[3].text = r.Crit ?? "";
                }
                else
                {
                    cells[0].text = cells[1].text = cells[2].text = cells[3].text = "";
                }
            }
            for (int i = 0; i < _bossRows.Count; i++)
            {
                var cells = _bossRows[i];
                if (i < ordered.Count)
                {
                    var r = ordered[i];
                    cells[0].text = r.Name;
                    cells[1].text = Fmt(r.Damage);
                    cells[2].text = total > 0 ? (r.Damage / total * 100).ToString("F1", Inv) + "%" : "";
                    cells[3].text = r.Crit ?? "";
                }
                else
                {
                    cells[0].text = cells[1].text = cells[2].text = cells[3].text = "";
                }
            }

            // [boss] 状态日志（每 5s）：多人局「面板没出/行是空」的排查靠它——show 各条件与行数一目了然
            if (DamageBus.NowMs >= _nextBossStatMs)
            {
                _nextBossStatMs = DamageBus.NowMs + 5000;
                Plugin.Log.LogInfo(
                    $"[boss] 状态 show={_bossPanel.activeSelf} 开关={_bossUserOpen} host={_hostMode} view={_view} uid={_bossUid} 实战={(_bossTimeValid ? MmSs(Math.Max(_bossFightSec - _bossIdleSec, 0)) : "-")} 全程={(_bossTimeValid ? MmSs(_bossFightSec) : "-")} 玩家行={players.Count} 技能行={ordered.Count} 总伤={Fmt(total)}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Boss 面板更新异常，隐藏：{e.GetType().Name}: {e.Message}");
            _bossPanel.SetActive(false);
        }
    }

    // Boss 面板（独立小面板）：血条读敌人对象 _currentHp@0x18，行=全队对该 Boss 的功法/异常伤害
    // （Ledger.BossByTarget 按 targetUid 聚合，血条档位≥3 判定 Boss）
    private GameObject _bossPanel;
    private RectTransform _bossPanelRt;
    private Text _bossTitle;
    private Text _bossTimeText;
    private Image _bossHpFill;
    private Text _bossHpText;
    private const int BossRowsMax = 8;
    private const int BossPlayerRowsMax = 4;
    private readonly List<Text[]> _bossRows = new();
    private readonly List<Text[]> _bossPlayerRows = new();
    private readonly List<(string Name, double Damage, string Crit)> _bossLastRows = new();
    private readonly List<(string Name, double Damage, string Crit)> _bossPlayerLastRows = new();
    private int _bossUid = -1;
    private string _bossName;
    private float _bossMaxHp;
    private float _bossLastHp;
    private double _bossAliveMs;
    private readonly HashSet<int> _bossEverUids = new(); // 探测确认过的 Boss uid（死亡后聚合不许被剪掉）
    private readonly HashSet<int> _bossProbeLogged = new(); // [boss] 探测日志：每 uid 一次
    private bool _bossUserOpen = true; // Boss 面板开关（主工具栏 Boss ✓/✗，取代死亡 8 秒自动隐藏）
    private bool _bossDeadZero; // 已确认死亡（读数≤0 或对象消失）：血条定格为 0
    private bool _bossTimeValid; // 是否拿到战斗时间线（首跳起）
    private double _bossFightSec; // 战斗时长：对该 Boss 首跳→末跳（WCL 遭遇时长口径）
    private double _bossIdleSec; // 无敌/离场累计：断流 >3s 的间隙和（WCL 停机时间口径）
    private double _nextBossStatMs; // [boss] 状态日志的下次输出时刻

    /// <summary>清空 Boss 面板状态（账本已 Reset 时一并调用，防止上局的 Boss uid/聚合漏到下局）。</summary>
    private void ResetBossState()
    {
        _bossEverUids.Clear();
        _bossProbeLogged.Clear();
        _bossLastRows.Clear();
        _bossUid = -1;
        _bossName = null;
        _bossMaxHp = 0f;
        _bossLastHp = 0f;
        _bossAliveMs = 0;
        _bossDeadZero = false;
        _bossTimeValid = false;
        _bossFightSec = 0;
        _bossIdleSec = 0;
    }
}
