using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TaiwuDpsMeter;

/// <summary>
/// 编排层：主线程消费者 + 游戏内 uGUI 面板（主面板/Boss面板/工具箱）。Tick() 由 StartGame.Update
/// 的 Postfix 每帧驱动（主线程），渲染 250ms 节流；面板完全点击穿透，点击全靠 HitButton 矩形轮询命中。
/// 按人视图为两级树：一级玩家（含随从合计），二级功法明细 + 灵侍/召唤分立行；计时用游戏钟（墙钟 − 累计暂停时长）。
/// 文件分层（partial 同类，按层拆文件）：UiKit=控件工厂（构建，两条 UI 铁律集中处）；
/// *.Build=面板构建、*.Render=渲染、*.Input=键鼠交互、*.Buttons=按钮状态与动作、
/// *.BossPanel=Boss 面板、*.DebugLog=对账日志、*.Demo=演示剧本。
/// </summary>
public partial class MeterController
{
    private static MeterController _instance;
    public static MeterController Instance => _instance ??= new MeterController();

    private const int RowCount = 24;
    private const double RenderIntervalMs = 250;
    private const double DebugSummaryMs = 30_000;
    private const double HostCheckIntervalMs = 10_000;
    private const double UiRetryIntervalMs = 3_000;
    private const int UiMaxAttempts = 10;

    private const float PanelW = 804f;
    private const float RowH = 24f;
    private const float HeaderY = -52f;  // 表头行（标题/摘要之下——20 键工具栏已移入独立工具箱面板）
    private const float RowsTop = -70f;  // 数据行起始（表头之下）
    private const float TbW = 340f;      // 工具箱面板宽
    private const float TbTabY = -6f;    // 工具箱 tab 行 y
    private const float TbRowTop = -34f; // 工具箱内容首行 y（列距 110、行距 32、按钮高 26）

    private static readonly float[] ColX = { 10f, 250f, 352f, 422f, 478f, 532f, 596f, 676f };
    private static readonly float[] ColW = { 236f, 98f, 66f, 52f, 50f, 58f, 76f, 118f };

    // 伤害条配色（抄 Details!/kagerou 的横向占比条）：玩家金/灵侍绿/召唤蓝/功法钢蓝/触发紫/目标红
    private static readonly Color BarPlayer = new(1f, 0.72f, 0.26f, 0.34f);
    private static readonly Color BarSquire = new(0.30f, 0.85f, 0.55f, 0.28f);
    private static readonly Color BarSummon = new(0.35f, 0.75f, 1f, 0.28f);
    private static readonly Color BarSkill = new(0.45f, 0.58f, 0.85f, 0.20f);
    private static readonly Color BarTrigger = new(0.75f, 0.45f, 1f, 0.24f);
    private static readonly Color BarTarget = new(1f, 0.40f, 0.40f, 0.25f);

    // 按钮配色
    private static readonly Color BtnNormal = new(0.14f, 0.17f, 0.23f, 0.92f);
    private static readonly Color BtnAccent = new(0.55f, 0.42f, 0.16f, 0.95f);
    private static readonly Color BtnGreen = new(0.16f, 0.42f, 0.24f, 0.95f);
    private static readonly Color BtnYellow = new(0.48f, 0.40f, 0.14f, 0.95f);

    private enum ButtonAction { SimStart, SimPause, ClearData, ResetWindow, BossToggle, DpsToggle, ToolboxToggle, TbClose, TabDifficulty, TabEnv, TabBuild, TabDebug, TabRamp, EnemyHpCycle, SpawnCountCycle, SpawnSpeedCycle, EliteChanceCycle, SmallBossChanceCycle, SmallBossDelayCycle, BossTimedCycle, BossFinalCountCycle, ResetAllMods, ModsToggle, VfxToggle, MoveSpeedCycle, EquipDropCycle, MinRarityCycle, ItemDropCycle, PickUpDropCycle, BossStopToggle, EliteXpToggle, PartyExpCycle, EnemyDamageCycle, RampToggle, RampShape, GiveEquip, RampHpSlope, RampDmgSlope, RampMoveSlope, RampCountSlope, RampSpeedSlope, RampEliteSlope, SmallBossRampSlope, BossSelect, BossSummon, DummyToggle, LevelJump, LoadBuild, SummonShield, ZhuijiaToggle, GlobalDmgToggle, DummyRefresh, PartyHpToggle, ResAdd, DummyGuardToggle }

    private sealed class UiButton
    {
        public GameObject Root;
        public RectTransform Rt;
        public Image Img;
        public Text Label;
        public ButtonAction Action;
        public RectTransform Panel; // 所属面板（null=主面板）；HitButton 按各自坐标系命中
        public int Tab;             // -1=主面板标题图标、-2=工具箱 tab 行、0-3=工具箱内容页
    }

    private static readonly Color ColName = new(1f, 1f, 1f);
    private static readonly Color ColTotal = new(0.92f, 0.92f, 1f);
    private static readonly Color ColDps = new(1f, 0.69f, 0.36f);
    private static readonly Color ColShare = new(0.62f, 0.69f, 0.75f);
    private static readonly Color ColCrit = new(0.50f, 0.85f, 1f);
    private static readonly Color ColBurst = new(0.62f, 0.95f, 0.78f); // 近N秒滚动 DPS：亮青绿
    private static readonly Color ColSettle = new(0.95f, 0.76f, 0.45f); // 近N秒结算触发次数：暖橙（毒/爆/外结算频率）
    private static readonly Color ColKill = new(1f, 0.61f, 0.61f);
    private static readonly Color ColHeader = new(0.53f, 0.57f, 0.63f);
    private static readonly Color ColAccent = new(1f, 0.84f, 0.40f);
    private static readonly Color ColDim = new(0.49f, 0.53f, 0.58f);

    private readonly Ledger _ledger = new();
    private readonly CharacterRegistry _registry = new();

    private bool _uiReady;
    private bool _uiGivenUp;
    private int _uiAttempts;
    private double _nextUiAttemptMs;

    private Font _font;
    private GameObject _panel;
    private RectTransform _panelRect;
    private Text _modeText;
    private Text _toast;                 // 操作结果浮层（装配BD/木桩等按钮反馈，短暂盖住右上角模式文字）
    private double _toastUntilMs;
    private Text _summaryText;
    private Text _footerText;
    private readonly List<Text> _headerCells = new();
    private readonly List<Text[]> _rowCells = new();
    private readonly List<Image> _rowBgs = new();   // 每行暗色底条
    private readonly List<Image> _rowFills = new(); // 每行伤害占比填充条（宽度随占比）
    private double _teamPeakDps;    // 全队近N秒 DPS 的历史峰值（250ms 渲染粒度，清空/F9 归零）
    private readonly HashSet<int> _nestLogged = new(); // 随从挂主诊断：每个 uid 只记一次

    private bool _selfDisabled;
    private bool _visible = true;
    private int _view; // 0=按人/全队, 1=按目标
    private bool _treeExpanded;
    private readonly Dictionary<int, bool> _nodeOpen = new();  // 各玩家节点独立展开态（点▼/▶切换，F11重置）
    private readonly int[] _rowMarkerUid = new int[RowCount];  // 行号→该行▼/▶对应的玩家uid（-1=无标记）
    // 数值直输模式：右键点调整按钮进入，键盘敲数字回车生效（比循环档位自由）
    private bool _inputMode;
    private ButtonAction _inputTargetAction;
    private string _inputBuf = "";
    private bool? _inputWorks;

    private bool _hostMode;
    private double _lastHostCheckMs = -HostCheckIntervalMs;
    private double _lastRenderMs;

    // 鼠标交互（轮询式；本运行时不能注入 MonoBehaviour，事件接口走不了）
    private bool? _mouseWorks;
    private bool _dragMove;
    private bool _dragScale;
    private Vector2 _dragOffset;       // 面板左上角 − 鼠标（屏幕像素，y 向下）
    private Vector2 _scaleStartMouse;
    private float _scaleStart;
    private float _uiScale = 1f;
    private double _uiSaveAtMs = -1;   // 滚轮缩放的防抖落盘时刻
    private Text _gripText;

    // 按钮状态（点击走鼠标轮询命中，不走 Unity 事件——本运行时注入不了 MonoBehaviour）
    private readonly List<UiButton> _buttons = new();

    // 工具箱面板：20 键按 难度/环境/构筑/调试 4 个 tab 分组的独立小面板（Details!/kagerou 的「控制台」同构）
    private GameObject _toolboxPanel;
    private RectTransform _toolboxRect;
    private bool _toolboxOpen; // 「工具箱」/× 键切换，状态存配置
    private int _toolboxTab;   // 当前 tab：0=难度 1=环境 2=构筑 3=调试
    private bool _dragToolbox; // 工具箱拖拽中
    private Vector2 _tbDragOffset;

    private Key _kToggle = Key.F8;
    private Key _kReset = Key.F9;
    private Key _kView = Key.F10;
    private Key _kTree = Key.F6;
    private Key _kDps = Key.F7;
    private string _treeKeyLabel = "F6";

    /// <summary>DPS 统计总开关（true=关闭）：面板隐藏+DamageBus 停收（hooks 空转近零开销），调参工具不受影响。</summary>
    private bool _dpsOff;

    // 暂停补偿
    private double _pausedMs;
    private double _lastTickWallMs = -1;






    private MeterController()
    {
        _treeExpanded = Plugin.CfgTreeExpanded?.Value ?? true;
        _toolboxOpen = Plugin.CfgToolboxOpen?.Value ?? false;
        _toolboxTab = Math.Clamp(Plugin.CfgToolboxTab?.Value ?? 1, 1, 3); // 公开版 3 个 tab（环境=1）
        _kToggle = ParseInputKey(Plugin.CfgToggleKey?.Value ?? "F8");
        _kReset = ParseInputKey(Plugin.CfgResetKey?.Value ?? "F9");
        _kView = ParseInputKey(Plugin.CfgViewKey?.Value ?? "F10");
        _kTree = ParseInputKey(Plugin.CfgTreeKey?.Value ?? "F6");
        _treeKeyLabel = (Plugin.CfgTreeKey?.Value ?? "F6").Trim();
        _kDps = ParseInputKey(Plugin.CfgDpsKey?.Value ?? "F7");
        _dpsOff = Plugin.CfgDpsEnabled?.Value == false;
        DamageBus.CollectionPaused = _dpsOff;
    }

    public void Tick()
    {
        if (_selfDisabled) return;
        try
        {
            TickCore();
        }
        catch (Exception e)
        {
            _selfDisabled = true;
            Plugin.Log.LogError($"MeterController 异常，面板停用（统计不再更新）：{e}");
        }
    }

    private void TickCore()
    {
        double wall = DamageBus.NowMs;
        double gameNow = AdvanceGameClock(wall);

        // 每帧都要读的三个配置收口到 250ms 缓存（2026-10-23 热路径收尾；直读 ConfigEntry 带锁）
        int tick = Environment.TickCount;
        if (tick - _cfgAt >= 250)
        {
            _cfgAt = tick;
            _cfgIdleSec = Plugin.CfgSegmentIdleSeconds.Value;
        }

        if (!_uiReady && !_uiGivenUp && wall >= _nextUiAttemptMs)
        {
            _nextUiAttemptMs = wall + UiRetryIntervalMs;
            TryBuildUi();
        }

        if (wall - _lastHostCheckMs >= HostCheckIntervalMs)
        {
            _lastHostCheckMs = wall;
            _hostMode = SafeIsHost();
        }




        double idleMs = Math.Max(_cfgIdleSec, 1f) * 1000.0;
        while (DamageBus.TryDequeue(out var e))
        {
            if (_dpsOff) continue; // 统计关闭：只排空队列不处理
            e.TimestampMs -= (long)_pausedMs; // 事件墙钟时间戳 → 游戏钟
            // 触发效果暴击诊断（2026-10-24）：按效果名统计 跳数/暴击数——神火爆炸"吃暴击"但面板 0% 的定案手段，
            // hook 抓到 crit=true=游戏真在暴击（面板/口径问题）；全 false=游戏侧该效果确实不暴击（面板正确）
            // Type=10 结算触发计数（2026-10-24）：功法族后缀+近窗列（面板）+ [dot] 汇总（日志）
            if (e.Kind == EventKind.Settlement)
            {
                SettlementLedger.Add(e.AttackerUid, e.EffectName, e.SkillIndex, e.TimestampMs);
            }
            _ledger.Consume(e, _registry, _hostMode, Plugin.CfgAggregateSquire.Value, idleMs);
        }

        _ledger.CloseIdleSegments(gameNow, idleMs);
        HandleKeys();
        HandleMouse();

        if (_uiSaveAtMs > 0 && DamageBus.NowMs > _uiSaveAtMs)
        {
            _uiSaveAtMs = -1;
            SaveUiPos();
        }

        if (wall - _lastRenderMs >= RenderIntervalMs)
        {
            _lastRenderMs = wall;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Render(gameNow);
        }




    }

    // TickCore 每帧配置的 250ms 缓存值（_cfgAt=-250 保证首帧即刷）
    private float _cfgIdleSec = 8f;
    private int _cfgAt = -250;

    /// <summary>维护游戏钟（墙钟 − 累计暂停时长），返回当前游戏时刻。</summary>
    private double AdvanceGameClock(double wall)
    {
        try
        {
            float timeScale = Time.timeScale;
            if (_lastTickWallMs >= 0)
            {
                double dt = wall - _lastTickWallMs;
                if (dt > 0 && dt < 2000 && timeScale <= 0.01f) _pausedMs += dt;
            }
        }
        catch
        {
            // timeScale 读不到就按"从不暂停"处理
        }
        _lastTickWallMs = wall;
        return wall - _pausedMs;
    }

    private static bool SafeIsHost()
    {
        try
        {
            var gnm = StartGame.GameNetworkManager();
            return gnm != null && gnm.IsServer();
        }
        catch
        {
            return false;
        }
    }


    private static string MmSs(double sec)    {
        // 中文单位格式：1:27.9 会被读成小数「1.27」（用户实测看不懂），改成「1分27.9秒」
        sec = Math.Max(sec, 0);
        int total = (int)sec;
        int tenth = (int)((sec - total) * 10);
        int m = total / 60;
        int s = total % 60;
        if (m >= 60)
        {
            int h = m / 60;
            m %= 60;
            return $"{h}时{m}分{s:D2}.{tenth}秒";
        }
        return m > 0 ? $"{m}分{s:D2}.{tenth}秒" : $"{s}.{tenth}秒";
    }

    // ---------------------------------------------------------------- 工具

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Fmt(double v) => ((long)Math.Round(v)).ToString("N0", Inv);

    /// <summary>
    /// 滚动窗口暴击率：合并多个环（族行=多个技能体、一级行=玩家+随从），窗口锚在 anchorMs
    /// （该行最后一跳）。锚在自己末跳上意味着窗口内必有 ≥1 跳，行还活着就总有值；停手后随 DPS 一起冻结。
    /// </summary>
    private static string CritPct(List<Ledger.CritRing> rings, double anchorMs)
    {
        if (rings == null || rings.Count == 0) return "";
        double w = Math.Max(Plugin.CfgCritWindowSeconds?.Value ?? 5f, 0.5f) * 1000.0;
        long h = 0, c = 0;
        foreach (var r in rings)
        {
            var (hh, cc) = r.Window(anchorMs, w);
            h += hh; c += cc;
        }
        return h > 0 ? (c * 100.0 / h).ToString("F1", Inv) + "%" : "";
    }

    /// <summary>把配置里的按键名解析成 InputSystem 的 Key（支持 F1-F12/字母；失败返回 Key.None）。</summary>
    internal static Key ParseInputKey(string name)
    {
        try
        {
            if (Enum.TryParse<Key>(name?.Trim(), true, out var k)) return k;
        }
        catch { /* 忽略 */ }
        return Key.None;
    }
}
