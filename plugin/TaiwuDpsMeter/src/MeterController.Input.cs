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
    // ---------------------------------------------------------------- 交互（新 Input System）

    private void HandleKeys()
    {
        if (_inputWorks == false) return;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (_kToggle == Key.None && _kReset == Key.None && _kView == Key.None && _kTree == Key.None && _kDps == Key.None) return;

            if (KeyPressed(kb, _kToggle)) _visible = !_visible;
            if (KeyPressed(kb, _kDps)) ToggleDps();
            if (KeyPressed(kb, _kReset))
            {
                // 与标题行「清空」按钮同效（2026-10-23 对齐：原先不复活熔断/不清调试计数，用户按 F9 期望完整重置）
                _ledger.Reset();
                _registry.Reset();
                DamageBus.Reset();
                ResetBossState();
                SettlementLedger.Reset();
                _teamPeakDps = 0;
                Plugin.Log.LogInfo("统计已清空");
            }
            if (KeyPressed(kb, _kView)) _view = (_view + 1) % 2;
            if (KeyPressed(kb, _kTree))
            {
                _treeExpanded = !_treeExpanded;
                _nodeOpen.Clear(); // 全局切换时清掉独立展开态
            }
            _inputWorks = true;
        }
        catch (Exception e)
        {
            _inputWorks = false;
            Plugin.Log.LogWarning($"InputSystem 不可用（{e.GetType().Name}: {e.Message}），快捷键禁用");
        }
    }

    /// <summary>本游戏 interop 的 Keyboard 没有 wasPressedThisFrame(Key) 方法/索引器，只有每键属性。</summary>
    private static bool KeyPressed(Keyboard kb, Key k) => k switch
    {
        Key.F1 => kb.f1Key.wasPressedThisFrame,
        Key.F2 => kb.f2Key.wasPressedThisFrame,
        Key.F3 => kb.f3Key.wasPressedThisFrame,
        Key.F4 => kb.f4Key.wasPressedThisFrame,
        Key.F5 => kb.f5Key.wasPressedThisFrame,
        Key.F6 => kb.f6Key.wasPressedThisFrame,
        Key.F7 => kb.f7Key.wasPressedThisFrame,
        Key.F8 => kb.f8Key.wasPressedThisFrame,
        Key.F9 => kb.f9Key.wasPressedThisFrame,
        Key.F10 => kb.f10Key.wasPressedThisFrame,
        Key.F11 => kb.f11Key.wasPressedThisFrame,
        Key.F12 => kb.f12Key.wasPressedThisFrame,
        Key.Space => kb.spaceKey.wasPressedThisFrame,
        Key.Enter => kb.enterKey.wasPressedThisFrame,
        Key.Tab => kb.tabKey.wasPressedThisFrame,
        Key.Digit0 => kb.digit0Key.wasPressedThisFrame,
        Key.Digit1 => kb.digit1Key.wasPressedThisFrame,
        Key.Digit2 => kb.digit2Key.wasPressedThisFrame,
        Key.Digit3 => kb.digit3Key.wasPressedThisFrame,
        Key.Digit4 => kb.digit4Key.wasPressedThisFrame,
        Key.Digit5 => kb.digit5Key.wasPressedThisFrame,
        Key.Digit6 => kb.digit6Key.wasPressedThisFrame,
        Key.Digit7 => kb.digit7Key.wasPressedThisFrame,
        Key.Digit8 => kb.digit8Key.wasPressedThisFrame,
        Key.Digit9 => kb.digit9Key.wasPressedThisFrame,
        Key.Numpad0 => kb.numpad0Key.wasPressedThisFrame,
        Key.Numpad1 => kb.numpad1Key.wasPressedThisFrame,
        Key.Numpad2 => kb.numpad2Key.wasPressedThisFrame,
        Key.Numpad3 => kb.numpad3Key.wasPressedThisFrame,
        Key.Numpad4 => kb.numpad4Key.wasPressedThisFrame,
        Key.Numpad5 => kb.numpad5Key.wasPressedThisFrame,
        Key.Numpad6 => kb.numpad6Key.wasPressedThisFrame,
        Key.Numpad7 => kb.numpad7Key.wasPressedThisFrame,
        Key.Numpad8 => kb.numpad8Key.wasPressedThisFrame,
        Key.Numpad9 => kb.numpad9Key.wasPressedThisFrame,
        Key.Period => kb.periodKey.wasPressedThisFrame,
        Key.NumpadPeriod => kb.numpadPeriodKey.wasPressedThisFrame,
        Key.Comma => kb.commaKey.wasPressedThisFrame,
        Key.Backspace => kb.backspaceKey.wasPressedThisFrame,
        Key.Escape => kb.escapeKey.wasPressedThisFrame,
        Key.NumpadEnter => kb.numpadEnterKey.wasPressedThisFrame,
        _ => false,
    };

    // ---------------------------------------------------------------- 鼠标（轮询式拖拽/缩放）

    private void HandleMouse()
    {
        if (_mouseWorks == false || !_uiReady) return;
        try
        {
            var m = Mouse.current;
            if (m == null) return;
            if (!Plugin.CfgShowPanel.Value || !_visible)
            {
                _dragMove = _dragScale = false;
                return;
            }

            Vector2 mp = m.position.ReadValue();                  // 屏幕坐标（y 向上）
            var mouse = new Vector2(mp.x, Screen.height - mp.y);  // 转 y 向下，方便和面板左上角比
            bool pressed = m.leftButton.isPressed;
            bool justPressed = m.leftButton.wasPressedThisFrame;

            var rt = _panelRect;
            float s = _uiScale;
            float x = rt.anchoredPosition.x;
            float y = -rt.anchoredPosition.y;
            float w = rt.sizeDelta.x * s;
            float h = rt.sizeDelta.y * s;

            const bool inToolbox = false; // 公开版无工具箱面板

            // 滚轮：悬停面板时缩放（与拖拽互斥）
            float wheel = 0f;
            try { wheel = m.scroll.ReadValue().y; } catch { /* 无滚轮设备 */ }
            if (Math.Abs(wheel) > 0.5f && !_dragMove && !_dragScale && !inToolbox && PointInRect(mouse, x, y, w, h))
            {
                ApplyScale(_uiScale + Mathf.Sign(wheel) * 0.08f, false);
                _uiSaveAtMs = DamageBus.NowMs + 1500; // 静置 1.5s 后统一落盘，避免连滚狂写配置
            }

            if (_dragScale)
            {
                if (!pressed) { _dragScale = false; SaveUiPos(); }
                else ApplyScale(_scaleStart + (mouse.x - _scaleStartMouse.x) / 320f + (mouse.y - _scaleStartMouse.y) / 320f, false);
            }
            else if (_dragMove)
            {
                if (!pressed) { _dragMove = false; SaveUiPos(); }
                else rt.anchoredPosition = ClampPos(mouse + _dragOffset, w, h);
            }
            else if (justPressed)
            {
                var btn = HitButton(mouse);
                if (btn.HasValue)
                {
                    RunButton(btn.Value);
                }
                else if (HitMarker(mouse, x, y, s, out int uid))
                {
                    _nodeOpen[uid] = !IsExpanded(uid); // 点▼/▶：单独收起/展开该玩家
                }
                else if (PointInRect(mouse, x + w - 28f * s, y + h - 28f * s, 28f * s, 28f * s))
                {
                    _dragScale = true;
                    _scaleStart = _uiScale;
                    _scaleStartMouse = mouse;
                }
                else if (PointInRect(mouse, x, y, w, h))
                {
                    _dragMove = true;
                    _dragOffset = new Vector2(x - mouse.x, y - mouse.y);
                }
            }

            _mouseWorks = true;
        }
        catch (Exception e)
        {
            _mouseWorks = false;
            Plugin.Log.LogWarning($"鼠标交互不可用（{e.GetType().Name}: {e.Message}），拖拽/缩放禁用");
        }
    }

    private static bool PointInRect(Vector2 p, float x, float y, float w, float h) =>
        p.x >= x && p.x <= x + w && p.y >= y && p.y <= y + h;

    /// <summary>该玩家节点的有效展开态：独立覆盖优先，否则用全局 _treeExpanded。</summary>
    private bool IsExpanded(int uid) => _nodeOpen.TryGetValue(uid, out var v) ? v : _treeExpanded;

    /// <summary>屏幕坐标命中某行行首的 ▼/▶ 热区（px/py/s = 面板左上角与缩放）。</summary>
    private bool HitMarker(Vector2 mouse, float px, float py, float s, out int uid)
    {
        uid = -1;
        float lx = (mouse.x - px) / s;
        float ly = (mouse.y - py) / s; // 面板内纵向坐标（向下为正）
        if (lx < 8f || lx > 44f) return false;
        float top = -RowsTop;
        if (ly < top) return false;
        int i = (int)((ly - top) / RowH);
        if (i < 0 || i >= RowCount || _rowMarkerUid[i] < 0) return false;
        uid = _rowMarkerUid[i];
        return true;
    }

    private void ApplyScale(float v, bool save)
    {
        _uiScale = Mathf.Clamp(v, 0.6f, 2f);
        _panelRect.localScale = new Vector3(_uiScale, _uiScale, 1f);
        if (save) SaveUiPos();
    }

    /// <summary>限制面板至少留一角在屏幕内；返回 anchoredPosition（y 向上坐标）。</summary>
    private static Vector2 ClampPos(Vector2 topLeft, float w, float h)
    {
        float mx = Mathf.Clamp(topLeft.x, -w + 90f, Screen.width - 90f);
        float my = Mathf.Clamp(topLeft.y, 0f, Screen.height - 60f);
        return new Vector2(mx, -my);
    }

    private void SaveUiPos()
    {
        try
        {
            var p = _panelRect.anchoredPosition;
            Plugin.CfgPanelX.Value = p.x / Screen.width;
            Plugin.CfgPanelY.Value = -p.y / Screen.height;
            Plugin.CfgUIScale.Value = _uiScale;
            Plugin.ConfigFile?.Save();
        }
        catch { /* 配置写不进去就算了，本次会话内仍生效 */ }
    }



    /// <summary>操作反馈浮层：面板右上角显示 4 秒（盖住模式文字），成功绿/失败红。</summary>
    private void ShowToast(string msg, bool ok)
    {
        try
        {
            if (_toast == null) return;
            if (msg != null && msg.Length > 46) msg = msg[..46] + "…";
            _toast.text = msg;
            _toast.color = ok ? new Color(0.55f, 0.95f, 0.6f) : new Color(1f, 0.45f, 0.4f);
            _toast.gameObject.SetActive(true);
            _toastUntilMs = DamageBus.NowMs + 4000;
        }
        catch { /* toast 失败无碍 */ }
    }

}
