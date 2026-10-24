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
            if (_inputMode) { HandleTuneInput(kb); return; } // 输入模式下吞掉所有按键（含F键，防误触面板开关）
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
                _dragToolbox = false;
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

            // 工具箱面板矩形（与主面板同缩放；命中/拖拽独立坐标系）
            float tbx = 0f, tby = 0f, tbw = 0f, tbh = 0f;
            bool tbReady = _toolboxRect != null;
            if (tbReady)
            {
                tbx = _toolboxRect.anchoredPosition.x;
                tby = -_toolboxRect.anchoredPosition.y;
                tbw = _toolboxRect.sizeDelta.x * s;
                tbh = _toolboxRect.sizeDelta.y * s;
            }
            bool inToolbox = tbReady && _toolboxOpen && PointInRect(mouse, tbx, tby, tbw, tbh);

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
            else if (_dragToolbox)
            {
                if (!pressed) { _dragToolbox = false; SaveToolboxPos(); }
                else _toolboxRect.anchoredPosition = ClampPos(mouse + _tbDragOffset, tbw, tbh);
            }
            else if (TryStartTuneInput(mouse))
            {
                // 右键点中调整按钮 → 进入数值直输模式（本帧不再处理左键）
            }
            else if (justPressed)
            {
                if (_inputMode) CancelTuneInput(); // 输入中点左键=取消
                var btn = HitButton(mouse);
                if (btn.HasValue)
                {
                    RunButton(btn.Value);
                }
                else if (inToolbox)
                {
                    _dragToolbox = true; // 工具箱空白处按住拖动（位置存配置）
                    _tbDragOffset = new Vector2(tbx - mouse.x, tby - mouse.y);
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
        // 工具箱必须同步缩放：否则视觉是旧缩放、点击命中按新 _uiScale 计算 → 点击错位
        if (_toolboxRect != null)
            _toolboxRect.localScale = new Vector3(_uiScale, _uiScale, 1f);
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

    private void SaveToolboxPos()
    {
        try
        {
            var p = _toolboxRect.anchoredPosition;
            Plugin.CfgToolboxX.Value = p.x / Screen.width;
            Plugin.CfgToolboxY.Value = -p.y / Screen.height;
            Plugin.ConfigFile?.Save();
        }
        catch { /* 配置写不进去就算了，本次会话内仍生效 */ }
    }

    /// <summary>右键点中可调参数按钮 → 进入数值直输（返回是否消费了本帧右键）。</summary>
    private bool TryStartTuneInput(Vector2 mouse)
    {
        bool rightPressed = false;
        try
        {
            var m = Mouse.current;
            rightPressed = m != null && m.rightButton.wasPressedThisFrame;
        }
        catch { /* 无右键 */ }
        if (!rightPressed) return false;
        var btn = HitButton(mouse);
        if (!btn.HasValue) return false;
        if (btn.Value == ButtonAction.DummyToggle)
        {
            // 右键「木桩」= 输入木桩数量（开着立即多退少补）
            _inputMode = true;
            _inputTargetAction = ButtonAction.DummyToggle;
            _inputBuf = (Plugin.CfgDummyCount?.Value ?? 1).ToString(Inv);
            return true;
        }
        if (btn.Value == ButtonAction.LoadBuild)
        {
            // 右键「装配BD」= 直接读剪贴板装配（wiki 复制 JSON → 右键即装，不走输入模式）
            string err = BuildLoader.ApplyFromClipboard();
            if (err == null) { Plugin.Log.LogInfo("[build] 剪贴板构筑已装配（左键=读文件版）"); ShowToast(BuildLoader.LastSummary, true); }
            else { Plugin.Log.LogWarning($"[build] 剪贴板装配失败：{err}"); ShowToast("✗ " + err, false); }
            return true;
        }
        if (!IsTunable(btn.Value)) return false;
        _inputMode = true;
        _inputTargetAction = btn.Value;
        _inputBuf = CurrentTuneValue(btn.Value).ToString("0.###", Inv);
        return true;
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

    // 公开版：可调项只剩木桩（数量）
    private static bool IsTunable(ButtonAction a) => a is ButtonAction.DummyToggle;

    private static string TuneLabel(ButtonAction a) => a switch
    {
        ButtonAction.DummyToggle => "木桩数量",
        _ => "参数",
    };

    private static float CurrentTuneValue(ButtonAction a) => a switch
    {
        _ => 1f, // 公开版不可达（IsTunable 只放行 DummyToggle，且 DummyToggle 在 TryStartTuneInput 早退）——留安全兜底防未来加项漏写
    };

    private static (float Min, float Max) TuneRange(ButtonAction a) => a switch
    {
        _ => (0.05f, 10f),
    };

    private void ApplyTuneValue(ButtonAction a, float v)
    {
        switch (a)
        {
            // 公开版无可调浮点项：木桩数量走 HandleTuneInput 的 DummyToggle 整数分支就地处理，不经此处
        }
        try { Plugin.ConfigFile?.Save(); } catch { /* 存不进就算了，本次会话内仍生效 */ }
    }

    private void CancelTuneInput()
    {
        _inputMode = false;
        _inputBuf = "";
    }

    private void HandleTuneInput(Keyboard kb)
    {
        for (int i = 0; i <= 9; i++)
        {
            if (KeyPressed(kb, Key.Digit0 + i) || KeyPressed(kb, Key.Numpad0 + i)) { _inputBuf += i.ToString(); return; }
        }
        if (KeyPressed(kb, Key.Period) || KeyPressed(kb, Key.NumpadPeriod) || KeyPressed(kb, Key.Comma))
        {
            if (!_inputBuf.Contains(".")) _inputBuf += ".";
            return;
        }
        // 减号不走 KeyPressed 枚举表：本 interop 的 Key 枚举没有 Minus/NumpadSubtract 成员（Unity 6 精简），
        // 但 Keyboard 的 minusKey/numpadMinusKey 属性存在——直接读属性
        if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame)
        {
            // 负号=开头切换正负（曲线负斜率用；整数类输入回车校验会自然拦掉）
            _inputBuf = _inputBuf.StartsWith("-") ? _inputBuf[1..] : "-" + _inputBuf;
            return;
        }
        if (KeyPressed(kb, Key.Backspace))
        {
            if (_inputBuf.Length > 0) _inputBuf = _inputBuf[..^1];
            return;
        }
        if (KeyPressed(kb, Key.Escape))
        {
            CancelTuneInput();
            Plugin.Log.LogInfo("[tune] 输入取消");
            return;
        }
        if (KeyPressed(kb, Key.Enter) || KeyPressed(kb, Key.NumpadEnter))
        {
            if (_inputTargetAction == ButtonAction.DummyToggle)
            {
                if (int.TryParse(_inputBuf, NumberStyles.Integer, Inv, out var n) && n >= 1 && n <= 16)
                {
                    string err = DummyController.AdjustCount(n);
                    if (err == null) { Plugin.Log.LogInfo($"[dummy] 木桩数量 → {n}（已调整）"); ShowToast($"✓ 木桩×{n}", true); }
                    else { Plugin.Log.LogWarning($"[dummy] 数量调整失败：{err}（配置未改）"); ShowToast("✗ " + err, false); }
                }
                else { Plugin.Log.LogWarning($"[dummy] 「{_inputBuf}」不是 1~16 的整数，已取消"); ShowToast("✗ 需 1~16 整数", false); }
                CancelTuneInput();
                return;
            }
            if (float.TryParse(_inputBuf, NumberStyles.Float, Inv, out var v))
            {
                var (min, max) = TuneRange(_inputTargetAction);
                v = Mathf.Clamp(v, min, max);
                ApplyTuneValue(_inputTargetAction, v);
                Plugin.Log.LogInfo($"[tune] {TuneLabel(_inputTargetAction)} → ×{v:0.###}（直输）");
            }
            else
            {
                Plugin.Log.LogWarning($"[tune] 「{_inputBuf}」不是有效数字，已取消");
            }
            CancelTuneInput();
        }
    }
}
