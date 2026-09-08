using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Windows;

namespace MemeMomo.Views;

using WpfButton = System.Windows.Controls.Button;

public partial class TutorialWindow : BorderlessWindow
{
    private bool _isPinned;
    private AppSettings _settings = AppSettings.CreateDefault();

    public TutorialWindow()
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += OnClosed;
    }

    public TutorialWindow(AppSettings settings)
        : this()
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        BuildContent(settings);
    }

    public new bool IsPinned => _isPinned;
    internal string ContentText => TutorialContent.Text;
    internal WpfButton PinButtonPart => PinButton;
    internal void CloseImmediatelyForTest() => CloseImmediately();

    public void TogglePinned() => SetPinned(!_isPinned);

    public void SetPinned(bool isPinned)
    {
        _isPinned = isPinned;
        Topmost = isPinned;
        InteractionState.SetIsPinActive(PinButton, isPinned);
        if (PinIcon.RenderTransform is not RotateTransform rotation)
        {
            rotation = new RotateTransform();
            PinIcon.RenderTransform = rotation;
        }

        double target = isPinned ? -45 : 0;
        if (!MotionPreferences.AnimationsEnabled)
        {
            rotation.Angle = target;
        }
        else
        {
            double from = rotation.Angle;
            MotionAnimations.Start(PinIcon, MotionPreferences.StandardDuration, MotionEasing.CubicEaseOut,
                progress => rotation.Angle = from + ((target - from) * progress));
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            e.Handled = true;
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => TogglePinned();
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseWithTransition();
    private void OnClosed(object? sender, EventArgs e)
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        MotionAnimations.Cancel(PinIcon);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => BuildContent(_settings);

    private void BuildContent(AppSettings s)
    {
        List<string> lines =
        [
            "一、Markdown 备忘录",
            "顶部是可直接排版和编辑的单框 Markdown 编辑器，支持标题、强调、删除线、有序/无序列表、任务项、引用、链接、代码、表格、分隔线与图片。",
            "新建时按 Ctrl+Enter 新增，Enter 换行；工具栏可直接插入常用格式，「更多」菜单也可打开 Markdown 源码编辑器。",
            "",
            "二、所见即所得编辑",
            "双击卡片进入编辑；停止输入 500ms 后自动保存。编辑已有内容时按 Ctrl+Enter 立即保存，Esc 恢复本次载入的内容。",
            "编辑时可用 Ctrl+B / Ctrl+I / Ctrl+K 快速插入粗体、斜体、链接。",
            "",
            "三、插入图片",
            "可粘贴、拖入或选择本地图片，也可从更多菜单插入 HTTPS 网络图片。",
            "",
            "四、分离便签",
            "按住并拖动卡片可拉出独立窗口；关闭「重复便签」时移动已有便签，开启后总是新建。",
            "",
            "五、窗口调整",
            "主窗口、便签、设置与教程窗口均无边框，可从边缘和四角拖拽缩放。",
            "",
            "六、贴边停靠",
            "拖动主窗口靠近屏幕边缘时会收起为吸附方块；右键方块可打开托盘菜单。",
            "",
            "七、置顶与关闭",
            "点右上角图钉可置顶主窗口、便签或教程；主窗口关闭行为可在设置中选择。",
            "",
            "八、快捷键",
            LocalizationService.Format("置顶主窗口：{0}", FormatHotkey(s.ToggleTopmostHotkey, enabled: true)),
            LocalizationService.Format("切换最近便签任务栏图标：{0}", FormatHotkey(s.ToggleMemoTaskbarHotkey, s.ShowMemoWindowTaskbarIcon)),
            LocalizationService.Format("最小化到托盘：{0}", FormatHotkey(s.MinimizeHotkey, enabled: true)),
            LocalizationService.Format("显示软件：{0}", FormatHotkey(s.ShowWindowHotkey, enabled: true)),
            LocalizationService.Format("快速添加（剪贴板）：{0}", FormatHotkey(s.QuickMemoHotkey, s.QuickMemoEnabled))
        ];

        lines.AddRange(
        [
            "",
            "九、托盘图标",
            "左键双击（或按设置改为单击）托盘图标恢复主窗口；右键打开自定义托盘菜单。",
            "托盘菜单提供：打开备忘录、新建备忘录、窗口置顶、退出应用。",
            "",
            "十、其它设置",
            "快速添加、动效、主题、贴边、任务栏图标等设置均会即时应用并自动保存。",
            "提醒到期默认逐条发送 Windows 系统通知（遵循系统免打扰设置）；可在设置的「提醒方式」中改为应用内窗口弹出。"
        ]);
        TutorialContent.Text = string.Join("\n", lines.Select(LocalizationService.Get));
    }

    private static string FormatHotkey(HotkeySetting hotkey, bool enabled) =>
        enabled ? hotkey.ToString() : LocalizationService.Get("已禁用");
}
