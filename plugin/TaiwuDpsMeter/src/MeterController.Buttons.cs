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
    /// DPS 统计总开关：关=面板隐藏+DamageBus.CollectionPaused 停收（Execute/飘字两个热 hook 空转近零开销），
    /// 难度工具箱/曲线/经验等调参不受影响。期间数据不采集不回补（F7 重开从当刻继续累计，要干净账先清空）。
    /// </summary>
    private void ToggleDps()
    {
        _dpsOff = !_dpsOff;
        Plugin.CfgDpsEnabled.Value = !_dpsOff;
        DamageBus.CollectionPaused = _dpsOff;
        if (_dpsOff && !_toolboxOpen)
        {
            // 关统计后自动弹开工具箱，保证始终有调参入口（含调试页的 DPS 开关）——2026-10-21 用户反馈
            _toolboxOpen = true;
            Plugin.CfgToolboxOpen.Value = true;
        }
        try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
        Plugin.Log.LogInfo($"[dps] DPS 统计 → {(_dpsOff ? "关闭（主面板隐藏+停止采集；工具箱与调参照常可用，F7 重开）" : "开启")}");
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
                -2 => _toolboxOpen,
                _ => _toolboxOpen && b.Tab == _toolboxTab,
            };
            if (b.Root.activeSelf != visible)
            {
                b.Root.SetActive(visible);
                b.Label.gameObject.SetActive(visible);
            }
            if (!visible) continue;
            if (_inputMode && b.Action == _inputTargetAction)
            {
                b.Label.text = $"…{_inputBuf}";
                b.Img.color = BtnAccent;
                continue;
            }
switch (b.Action)
            {
                case ButtonAction.ToolboxToggle:
                    b.Label.text = _toolboxOpen ? "工具箱 ▾" : "工具箱 ▴";
                    b.Img.color = _toolboxOpen ? BtnAccent : BtnNormal;
                    break;
                case ButtonAction.TabDifficulty:
                case ButtonAction.TabEnv:
                case ButtonAction.TabBuild:
                case ButtonAction.TabDebug:
                case ButtonAction.TabRamp:
                    b.Img.color = (b.Action - ButtonAction.TabDifficulty) == _toolboxTab ? BtnAccent : BtnNormal;
                    break;
                case ButtonAction.TbClose:
                    b.Img.color = BtnNormal;
                    break;
                case ButtonAction.BossToggle:
                    b.Label.text = _bossUserOpen ? "Boss ✓" : "Boss ✗";
                    b.Img.color = _bossUserOpen ? BtnGreen : BtnNormal;
                    break;
                case ButtonAction.DpsToggle:
                    b.Label.text = _dpsOff ? "DPS ✗" : "DPS ✓";
                    b.Img.color = _dpsOff ? BtnNormal : BtnGreen;
                    break;
                case ButtonAction.DummyToggle:
                {
                    bool on = Plugin.CfgDummyMode?.Value == true;
                    int n = Math.Clamp(Plugin.CfgDummyCount?.Value ?? 1, 1, 16);
                    b.Label.text = (on ? "木桩开" : "木桩关") + $"×{n}";
                    b.Img.color = on ? BtnAccent : BtnNormal;
                    break;
                }
                case ButtonAction.LoadBuild:
                    b.Label.text = "装配BD";
                    b.Img.color = BtnGreen;
                    break;
                case ButtonAction.DummyRefresh:
                    b.Label.text = "刷木桩";
                    b.Img.color = BtnGreen;
                    break;
                case ButtonAction.DummyGuardToggle:
                {
                    bool on = Plugin.CfgDummyAntiExecute?.Value != false;
                    b.Label.text = on ? "防斩杀:开" : "防斩杀:关";
                    b.Img.color = on ? BtnGreen : BtnNormal;
                    break;
                }
                default:
                    b.Img.color = BtnNormal;
                    break;
            }
    }
    }

    /// <summary>屏幕坐标（y 向下）命中可见按钮（主面板/工具箱各自坐标系换算）。</summary>
    private ButtonAction? HitButton(Vector2 mouse)
    {
        float s = _uiScale;
        foreach (var b in _buttons)
        {
            if (!b.Root.activeInHierarchy) continue; // 用层级有效态：主面板/工具箱整组隐藏（SetActive false）时热区必须跟着失效，
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
            case ButtonAction.ToolboxToggle:
            case ButtonAction.TbClose:
                _toolboxOpen = a == ButtonAction.ToolboxToggle ? !_toolboxOpen : false;
                Plugin.CfgToolboxOpen.Value = _toolboxOpen;
                try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
                break;
            case ButtonAction.TabDifficulty:
            case ButtonAction.TabEnv:
            case ButtonAction.TabBuild:
            case ButtonAction.TabDebug:
            case ButtonAction.TabRamp:
                _toolboxTab = (int)(a - ButtonAction.TabDifficulty);
                Plugin.CfgToolboxTab.Value = _toolboxTab;
                try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
                break;
            case ButtonAction.BossToggle:
                _bossUserOpen = !_bossUserOpen;
                break;
            case ButtonAction.DpsToggle:
                ToggleDps();
                break;
            case ButtonAction.DummyGuardToggle:
            {
                bool on = Plugin.CfgDummyAntiExecute?.Value != true;
                Plugin.CfgDummyAntiExecute.Value = on;
                TuningCache.ForceRefresh();
                try { Plugin.ConfigFile?.Save(); } catch { /* 忽略 */ }
                Plugin.Log.LogInfo($"[dummy] 木桩防斩杀 → {(on ? "开（处决对木桩无效）" : "关（斩杀可杀死木桩）")}");
                break;
            }
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
                if (_toolboxRect != null) _toolboxRect.localScale = Vector3.one; // 工具箱跟随重置缩放
                _panelRect.anchoredPosition = new Vector2(0.02f * Screen.width, -0.04f * Screen.height);
                SaveUiPos();
                break;
        }
    }

}
