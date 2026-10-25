using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace TaiwuDpsMeter;

/// <summary>太吾异世劫 队友 DPS 统计插件。</summary>
/// <remarks>
/// 数据源（两层，均由 DamageHooks 捕获、DamageBus 传递、OverlayUI 主线程消费）：
/// 1. 房主侧 BattleEffect.Execute Postfix —— 攻击者/受击者/伤害/暴击/击杀全归属口径（当房主或单机时有效）；
/// 2. 客户端 BulletManager.EnqueuePCreateEffect Prefix —— 飘字广播（CharacterId 是受击者，无攻击者），
///    客人端用于全队总伤/实扣估算，房主端仅用于溢出估算，不与口径 1 重复记账。
/// 另挂 BattleRecordManager.AddDamage 作为官方战报口径的对账基准。
/// </remarks>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "taiwu.dpsmeter";
    public const string PluginName = "TaiwuDpsMeter";
    public const string PluginVersion = "0.1.0";

    internal static new ManualLogSource Log;

    internal static ConfigEntry<bool> CfgShowPanel;
    internal static ConfigEntry<bool> CfgDpsEnabled;
    internal static ConfigEntry<string> CfgDpsKey;
    internal static ConfigEntry<string> CfgToggleKey;
    internal static ConfigEntry<string> CfgResetKey;
    internal static ConfigEntry<string> CfgViewKey;
    internal static ConfigEntry<float> CfgSegmentIdleSeconds;
    internal static ConfigEntry<bool> CfgAggregateSquire;
    internal static ConfigEntry<float> CfgPanelX;
    internal static ConfigEntry<float> CfgPanelY;
    internal static ConfigEntry<int> CfgFontSize;
    internal static ConfigEntry<float> CfgOpacity;
    internal static ConfigEntry<string> CfgTreeKey;
    internal static ConfigEntry<bool> CfgTreeExpanded;
    internal static ConfigEntry<float> CfgUIScale;
    internal static ConfigEntry<float> CfgCritWindowSeconds;
    internal static ConfigEntry<int> CfgBurstWindow;
    internal static BepInEx.Configuration.ConfigFile ConfigFile;

    public override void Load()
    {
        Log = base.Log;

        CfgShowPanel = Config.Bind("UI", "ShowPanel", true, "面板是否显示（快捷键切换的初始值）");
        CfgDpsEnabled = Config.Bind("UI", "DpsEnabled", true, "DPS 统计总开关：false=停止采集+隐藏面板（热 hook 空转近零开销，F7 重开）。标题行「DPS✓」或快捷键切换");
        CfgDpsKey = Config.Bind("UI", "DpsKey", "F7", "DPS 统计总开关快捷键");
        CfgToggleKey = Config.Bind("UI", "ToggleKey", "F8", "显示/隐藏面板按键（InputSystem Key 名，如 F1-F12/字母）");
        CfgResetKey = Config.Bind("UI", "ResetKey", "F9", "清空统计按键");
        CfgViewKey = Config.Bind("UI", "ViewKey", "F10", "切换视图（按人 / 按目标）按键");
        CfgTreeKey = Config.Bind("UI", "TreeKey", "F6", "展开/收起二级明细（功法/灵侍/召唤）按键。默认F6：F11是游戏自带调试浮窗的快捷键，会撞");
        CfgTreeExpanded = Config.Bind("UI", "TreeExpanded", true, "启动时二级明细是否展开");
        CfgAggregateSquire = Config.Bind("Stats", "AggregateSquireToOwner", false, "一级玩家行是否并入随从合计（二级明细始终分立展示）");
        CfgPanelX = Config.Bind("UI", "PanelX", 0.02f, "面板左上角 X（屏幕宽度占比，拖拽后自动更新）");
        CfgPanelY = Config.Bind("UI", "PanelY", 0.45f, "面板左上角 Y（屏幕高度占比，拖拽后自动更新）");
        CfgUIScale = Config.Bind("UI", "UIScale", 1.0f, "面板缩放（0.6~2.0，拖右下角◢或滚轮调整后自动更新）");
        ConfigFile = Config;
        CfgFontSize = Config.Bind("UI", "FontSize", 16, "字体大小");
        CfgOpacity = Config.Bind("UI", "Opacity", 0.85f, "背景不透明度 0~1");
        CfgSegmentIdleSeconds = Config.Bind("Stats", "SegmentIdleSeconds", 8f, "停手多少秒后自动切段（DPS 统计窗口）");
        CfgCritWindowSeconds = Config.Bind("Stats", "CritWindowSeconds", 5f, "暴击率滚动窗口秒数：只统计该行最近 N 秒的命中（局内暴击加成会变，全程均值没意义）。窗口锚在该行最后一跳，伤害停了数值跟着冻结");
        CfgBurstWindow = Config.Bind("Stats", "BurstWindowSeconds", 10, "「近N秒」滚动 DPS 列的窗口秒数（5~30）。测 BD 当前循环输出用：持续输出才计入，停手后数值自然衰减到 0（与段 DPS 的冻结口径并存）");

        DamageBus.Reset();

        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);

        // 注意：不使用 ClassInjector 注入自造 MonoBehaviour——本游戏 il2cpp 运行时下注入会 NRE
        // （Unity 6000.3 / metadata v39，Class::Init signatures exhausted）。
        // 心跳改由 StartGame.Update Postfix 驱动，见 DamageHooks.StartGame_Update_Patch。

        Log.LogInfo($"{PluginName} v{PluginVersion} 已加载。hooks: BattleEffect.Execute / BulletManager.EnqueuePCreateEffect / BattleRecordManager.AddDamage / AddKillCount / StartGame.Update");
        Log.LogInfo($"按键：{CfgToggleKey.Value}=开关面板 {CfgResetKey.Value}=清空 {CfgViewKey.Value}=切换视图；房主模式下按人归属，客人模式仅全队总伤。");
    }
}
