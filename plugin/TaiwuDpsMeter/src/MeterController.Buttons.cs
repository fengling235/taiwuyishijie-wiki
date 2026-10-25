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
    /// <summary>
    /// DPS 统计总开关：关=面板隐藏+DamageBus.CollectionPaused 停收（Execute/飘字两个热 hook 空转近零开销）。
    /// 期间数据不采集不回补（F7 重开从当刻继续累计，要干净账先清空）。
    /// </summary>
    private void ToggleDps()
    {
        _dpsOff = !_dpsOff;
        Plugin.CfgDpsEnabled.Value = !_dpsOff;
        DamageBus.CollectionPaused = _dpsOff;
        try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
        Plugin.Log.LogInfo($"[dps] DPS 统计 → {(_dpsOff ? "关闭（主面板隐藏+停止采集，F7 重开）" : "开启")}");
        if (!_dpsOff) ShowToast("✓ DPS 统计已开启", true); // 关闭时面板随藏、toast 不可见，只写日志
    }


    /// <summary>按钮可见性与状态色/文字（每帧渲染时刷新）。</summary>
    private void UpdateButtons()
    {
        foreach (var b in _buttons)
        {
            // 可见性按归属面板/tab 判定：标题图标常驻、tab 行随开关、内容键只在自己的 tab 显示。
            // 同 tab 互斥=非当前 tab 整组 SetActive(false)，HitButton 跳过 inactive——旧「同 y 重叠点错键」事故结构性根除。
            bool visible = b.Tab switch
            {
                -1 => true,
                _ => true, // 公开版全部是标题行常驻键（Tab=-1），此臂仅兜底
            };
            if (b.Root.activeSelf != visible)
            {
                b.Root.SetActive(visible);
                b.Label.gameObject.SetActive(visible);
            }
            if (!visible) continue;
            // 公开版标题行只有 DPS✓ / Boss✓ / 清空 三个常驻键
            switch (b.Action)
            {
                case ButtonAction.BossToggle:
                    b.Label.text = _bossUserOpen ? "Boss ✓" : "Boss ✗";
                    b.Img.color = _bossUserOpen ? BtnGreen : BtnNormal;
                    break;
                case ButtonAction.DpsToggle:
                    b.Label.text = _dpsOff ? "DPS ✗" : "DPS ✓";
                    b.Img.color = _dpsOff ? BtnNormal : BtnGreen;
                    break;
                default:
                    b.Img.color = BtnNormal;
                    break;
            }
    }
    }

    /// <summary>屏幕坐标（y 向下）命中可见按钮（按所属面板各自坐标系换算）。</summary>
    private ButtonAction? HitButton(Vector2 mouse)
    {
        float s = _uiScale;
        foreach (var b in _buttons)
        {
            if (!b.Root.activeInHierarchy) continue; // 用层级有效态：面板整组隐藏（SetActive false）时热区必须跟着失效，
                                                     // 否则点得到「隐形的按钮」（activeSelf 不随父级变，activeInHierarchy 才会）
            var panel = b.Panel != null ? b.Panel : _panelRect;
            float px = panel.anchoredPosition.x;
            float py = -panel.anchoredPosition.y;
            float bx = px + b.Rt.anchoredPosition.x * s;
            float by = py + (-b.Rt.anchoredPosition.y) * s;
            if (PointInRect(mouse, bx, by, b.Rt.sizeDelta.x * s, b.Rt.sizeDelta.y * s))
                return b.Action;
        }
        return null;
    }

    private void RunButton(ButtonAction a)
    {
        switch (a)
        {
            case ButtonAction.BossToggle:
                _bossUserOpen = !_bossUserOpen;
                break;
            case ButtonAction.DpsToggle:
                ToggleDps();
                break;
            case ButtonAction.ClearData:
                _ledger.Reset();
                _registry.Reset();
                DamageBus.Reset();
                ResetBossState();
                SettlementLedger.Reset();
                _teamPeakDps = 0;
                Plugin.Log.LogInfo("统计已清空（面板按钮）");
                break;
            case ButtonAction.ResetWindow:
                _uiScale = 1f;
                _panelRect.localScale = Vector3.one;
                _panelRect.anchoredPosition = new Vector2(0.02f * Screen.width, -0.45f * Screen.height);
                SaveUiPos();
                break;
        }
    }

}
