using UnityEngine;
using UnityEngine.UI;

namespace TaiwuDpsMeter;

/// <summary>
/// 构建层：uGUI 控件工厂（纯函数，不持有面板状态）。
/// 两条 UI 铁律集中在这一处，所有控件只能经由本类创建：
/// ①同一 GameObject 只 AddComponent 一个组件——本运行时（Unity 6000.3 / IL2CPP v39）第二次 AddComponent
///   拿到坏代理，赋值即 NRE（2026-10-18 实测）——图与文永远拆两个 GO；
/// ②一切 Image/Text raycastTarget=false——面板必须完全点击穿透，GraphicRaycaster/吃射线
///   会连面板底下的游戏按钮一起拦掉（「升不了级」事故）。
/// </summary>
internal static class UiKit
{
    /// <summary>面板内绝对定位的文字（点锚点，父级左上角原点，y 向下为负）。</summary>
    public static Text Cell(Transform parent, Font font, float x, float y, float w, float h,
        TextAnchor align, Color color, int size, bool bold)
    {
        var go = new GameObject("c");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);

        var text = go.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        text.alignment = align;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.color = color;
        return text;
    }

    /// <summary>面板内绝对定位的纯色图（点锚点，sizeDelta 即绝对尺寸）。</summary>
    public static Image Img(Transform parent, float y, float x, float w, float h, Color color)
    {
        var go = new GameObject("img");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        img.color = color;
        return img;
    }

    /// <summary>运行时生成圆角白色贴图做 9 宫格底板；失败则退化为直角。</summary>
    public static Sprite RoundedSprite()
    {
        try
        {
            const int size = 32, r = 9;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Min(x, size - 1 - x) - (size / 2 - r));
                    float dy = Mathf.Max(0, Mathf.Min(y, size - 1 - y) - (size / 2 - r));
                    bool inside = !(dx > 0 && dy > 0 && dx * dx + dy * dy > r * r);
                    px[y * size + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            var border = new Vector4(r, r, r, r);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>OS 中文字体优先，失败退回引擎内置字体；全失败返回 null（UI 放弃创建）。</summary>
    public static Font CreateFont(int size)
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "微软雅黑" })
        {
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(name, size);
                if (f != null) return f;
            }
            catch { /* 试下一个 */ }
        }
        foreach (var builtin in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try
            {
                var f = Resources.GetBuiltinResource<Font>(builtin);
                if (f != null) return f;
            }
            catch { /* 试下一个 */ }
        }
        return null;
    }
}
