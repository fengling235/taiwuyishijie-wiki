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
    // ---------------------------------------------------------------- UI 构建

    private void TryBuildUi()
    {
        try
        {
            _font = UiKit.CreateFont(Plugin.CfgFontSize.Value);
            if (_font == null) throw new Exception("没有可用字体（OS 字体与内置字体均失败）");

            var rootGo = new GameObject("TaiwuDpsMeter_UI");
            UnityEngine.Object.DontDestroyOnLoad(rootGo);
            var canvas = rootGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32760;
            // 注意：不加 GraphicRaycaster、底图不吃射线——实测拦截会连面板底下的游戏按钮一起吃掉（升不了级事故），恢复完全点击穿透

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(rootGo.transform, false);
            var prt = _panel.AddComponent<RectTransform>();
            prt.anchorMin = new Vector2(0f, 1f);
            prt.anchorMax = new Vector2(0f, 1f);
            prt.pivot = new Vector2(0f, 1f);
            prt.anchoredPosition = new Vector2(
                Plugin.CfgPanelX.Value * Screen.width,
                -Plugin.CfgPanelY.Value * Screen.height);
            float panelH = -RowsTop + RowCount * RowH + 34f;
            prt.sizeDelta = new Vector2(PanelW, panelH);
            _panelRect = prt;

            var bg = _panel.AddComponent<Image>();
            var alpha = Mathf.Clamp01(Plugin.CfgOpacity.Value) * 0.94f;
            bg.color = new Color(0.07f, 0.085f, 0.12f, alpha);
            bg.raycastTarget = false;
            var rounded = UiKit.RoundedSprite();
            if (rounded != null)
            {
                bg.sprite = rounded;
                bg.type = Image.Type.Sliced;
            }

            _uiScale = Mathf.Clamp(Plugin.CfgUIScale?.Value ?? 1f, 0.6f, 2f);
            prt.localScale = new Vector3(_uiScale, _uiScale, 1f);

            var title = MakeCell(12f, -6f, 240f, 18f, TextAnchor.UpperLeft, ColName, 17, true);
            title.text = "◆ TaiwuDpsMeter";
            _modeText = MakeCell(240f, -8f, 264f, 20f, TextAnchor.UpperRight, ColDim, 13, false);
            _toast = MakeCell(240f, -8f, PanelW - 250f, 20f, TextAnchor.UpperRight, Color.white, 13, true);
            _toast.gameObject.SetActive(false);

            _summaryText = MakeCell(12f, -28f, PanelW - 24f, 18f, TextAnchor.UpperLeft, ColAccent, 14, true);

            // 标题行右侧功能键：DPS统计开关 / [工具箱] / Boss 面板开关 / 清空数据。
            // 图标用中文短标签：YaHei 没有 ⚙/↺ 字形，会渲染成豆腐块。
            MakeButton(ButtonAction.DpsToggle, 462f, 48f, "DPS ✓", -5f, 18f, -1);
            // 公开版无工具箱：三键紧凑排在标题行右侧
            MakeButton(ButtonAction.BossToggle, 516f, 58f, "Boss ✓", -5f, 18f, -1);
            MakeButton(ButtonAction.ClearData, 578f, 44f, "清空", -5f, 18f, -1);

            for (int c = 0; c < ColX.Length; c++)
                _headerCells.Add(MakeCell(ColX[c], HeaderY, ColW[c], 16f, CellAlign(c), ColHeader, 12, false));

            for (int i = 0; i < RowCount; i++)
            {
                float y = RowsTop - i * RowH;

                // 行结构：暗色底条 → 伤害占比填充条（宽度渲染时更新）→ 文字
                var rowBg = MakeImg(y, 10f, PanelW - 20f, RowH - 4f, new Color(1f, 1f, 1f, 0.05f));
                _rowBgs.Add(rowBg);
                var fill = MakeImg(y, 10f, 0f, RowH - 6f, Color.white);
                fill.gameObject.SetActive(false);
                _rowFills.Add(fill);

                var cells = new Text[ColX.Length];
                for (int c = 0; c < ColX.Length; c++)
                    cells[c] = MakeCell(ColX[c], y, ColW[c], RowH, CellAlign(c), CellColor(c), Plugin.CfgFontSize.Value - 2, c == 0);
                _rowCells.Add(cells);
            }

            // 工具栏与表头之间的分隔细线
            MakeImg(-48f, 10f, PanelW - 20f, 1f, new Color(1f, 0.84f, 0.40f, 0.35f));

            _footerText = MakeCell(12f, RowsTop - RowCount * RowH - 4f, PanelW - 24f, 18f, TextAnchor.UpperLeft, ColDim, 11, false);

            // 右下角缩放角标：锚到面板右下角，高度收缩时自动跟随
            var gripGo = new GameObject("grip");
            gripGo.transform.SetParent(_panel.transform, false);
            var grt = gripGo.AddComponent<RectTransform>();
            grt.anchorMin = new Vector2(1f, 0f);
            grt.anchorMax = new Vector2(1f, 0f);
            grt.pivot = new Vector2(1f, 0f);
            grt.anchoredPosition = new Vector2(-4f, 4f);
            grt.sizeDelta = new Vector2(20f, 20f);
            _gripText = gripGo.AddComponent<Text>();
            _gripText.font = _font;
            _gripText.fontSize = 15;
            _gripText.fontStyle = FontStyle.Bold;
            _gripText.text = "◢";
            _gripText.color = new Color(1f, 1f, 1f, 0.45f);
            _gripText.alignment = TextAnchor.LowerRight;
            _gripText.supportRichText = false;
            _gripText.raycastTarget = false;

            _uiReady = true;
            BuildBossPanel(rootGo, alpha, rounded);
            Plugin.Log.LogInfo($"UI 创建成功（字体：{_font.name}，圆角：{rounded != null}，宿主模式检测：{_hostMode}）");
        }
        catch (Exception e)
        {
            _uiAttempts++;
            if (_uiAttempts >= UiMaxAttempts)
            {
                _uiGivenUp = true;
                Plugin.Log.LogError($"UI 创建失败 {_uiAttempts} 次，放弃面板（统计与日志照常工作）：{e}");
            }
        }
    }

    private static TextAnchor CellAlign(int col) => col == 0 ? TextAnchor.UpperLeft : TextAnchor.UpperRight;

    private static Color CellColor(int col) => col switch
    {
        0 => ColName,
        1 => ColTotal,
        2 => ColDps,
        3 => ColShare,
        4 => ColCrit,
        5 => ColKill,
        7 => ColSettle,
        _ => ColBurst,
    };

    /// <summary>薄封装：UiKit.Cell 的主面板版（默认父级=_panel，调用点零改动）。</summary>
    private Text MakeCell(float x, float y, float w, float h, TextAnchor align, Color color, int size, bool bold, Transform parent = null)
        => UiKit.Cell(parent != null ? parent : _panel.transform, _font, x, y, w, h, align, color, size, bold);

    /// <summary>薄封装：UiKit.Img 的主面板版（默认父级=_panel，调用点零改动）。</summary>
    private Image MakeImg(float y, float x, float w, float h, Color color, Transform parent = null)
        => UiKit.Img(parent != null ? parent : _panel.transform, y, x, w, h, color);

    // ---------------------------------------------------------------- 面板按钮（轮询命中）

    /// <summary>创建按钮（图+文两个 GO）。tab：-1=主面板标题图标、-2=工具箱 tab 行、0-3=工具箱内容页。</summary>
    private void MakeButton(ButtonAction action, float x, float w, string label, float y, float h, int tab, Transform parent = null, RectTransform panel = null)
    {
        // 本游戏 interop 实测坑：同一 GameObject 连续两次 AddComponent 会拿到坏代理（NRE）。
        // 按钮拆两个 GO——底图走 MakeImg（RectTransform+Image），文字单独 GO（RectTransform+Text）。
        try
        {
            var img = MakeImg(y, x, w, h, BtnNormal, parent);
            var t = MakeCell(x, y, w, h, TextAnchor.MiddleCenter, new Color(0.92f, 0.94f, 1f), 12, false, parent);
            t.text = label;
            _buttons.Add(new UiButton { Root = img.gameObject, Rt = img.rectTransform, Img = img, Label = t, Action = action, Panel = panel, Tab = tab });
        }
        catch (Exception e)
        {
            // 单个按钮坏不炸整块面板
            Plugin.Log.LogWarning($"按钮 {action} 创建失败，跳过：{e.GetType().Name}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 工具箱面板（20 键按 4 个 tab 分组；整块=完整版专属，公开版无工具箱）
}
