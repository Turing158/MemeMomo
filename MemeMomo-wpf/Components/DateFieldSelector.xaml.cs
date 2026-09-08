using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Popup;
using MemeMomo.UI.Text;
using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

/// <summary>
/// 日期选择字段：折叠态显示 yyyy-MM-dd，展开后在下方弹出主题化月视图。
/// 替代原生 DatePicker，使配色与圆角完全走仓库调色板。
/// </summary>
public partial class DateFieldSelector : WpfUserControl
{
    internal const int WeekCount = 6;
    internal const int DaysPerWeek = 7;
    internal const int CellCount = WeekCount * DaysPerWeek;

    /// <summary>月视图与年视图共用 3 列 × 4 行：月份正好 12 格，年份为十年加首尾各一格补位。</summary>
    internal const int PickerColumns = 3;
    internal const int PickerRows = 4;
    internal const int PickerCellCount = PickerColumns * PickerRows;
    internal const int YearsPerDecade = 10;

    // Keep the cell's 1 DIP vertical margins inside each row so the rounded
    // bottom edge has a full layout slot to render into.
    private const double DayCellSize = 34;
    private const double DayRowHeight = DayCellSize + 2;
    private const double PickerRowHeight = 46;

    /// <summary>
    /// DisplayMonth 的「未设置」哨兵。不能用 default(DateTime)：它等于 DateTime.MinValue，
    /// 而 MinValue 是调用方可以合法设置的月份，会被误判成未初始化。MaxValue 不是某月 1 号，
    /// 永远无法通过 MonthOf 规范化后仍等于自身，因此可安全作为哨兵。
    /// </summary>
    private static readonly DateTime UnsetMonth = DateTime.MaxValue;

    private static readonly string[] WeekdayLabels = ["一", "二", "三", "四", "五", "六", "日"];

    private readonly WpfButton[] _dayCells = new WpfButton[CellCount];
    private readonly DateTime[] _cellDates = new DateTime[CellCount];
    private readonly WpfButton[] _monthCells = new WpfButton[PickerCellCount];
    private readonly WpfButton[] _yearCells = new WpfButton[PickerCellCount];
    private readonly int[] _yearCellValues = new int[PickerCellCount];
    private readonly object _monthAnimationChannel = new();
    private readonly object _chevronAnimationChannel = new();
    private readonly object _levelAnimationChannel = new();

    private bool _updatingSelection;
    private DateTime? _focusAnchor;
    private int _pickerAnchor;
    private Window? _ownerWindow;
    private CalendarViewLevel _viewLevel = CalendarViewLevel.Day;

    /// <summary>年份视图当前区间的起始年（十年区间的首年，如 2020）。-1 表示尚未确定。</summary>
    private int _decadeStart = -1;

    public DateFieldSelector()
    {
        InitializeComponent();

        BuildWeekdayHeader();
        BuildDayCells();
        BuildMonthCells();
        BuildYearCells();

        Field.MouseLeftButtonUp += OnFieldClick;
        Field.KeyDown += OnFieldKeyDown;
        CalendarPopup.Closed += OnCalendarPopupClosed;
        HeaderButton.Click += OnHeaderClick;
        PreviousMonthButton.Click += (_, _) => StepLevel(-1);
        NextMonthButton.Click += (_, _) => StepLevel(1);
        TodayButton.Click += OnTodayClick;
        CalendarSurface.KeyDown += OnPopupKeyDown;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate),
        typeof(DateTime?),
        typeof(DateFieldSelector),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedDatePropertyChanged));

    public static readonly DependencyProperty DisplayMonthProperty = DependencyProperty.Register(
        nameof(DisplayMonth),
        typeof(DateTime),
        typeof(DateFieldSelector),
        new FrameworkPropertyMetadata(UnsetMonth, OnDisplayMonthPropertyChanged));

    public static readonly DependencyProperty IsDropDownOpenProperty = DependencyProperty.Register(
        nameof(IsDropDownOpen),
        typeof(bool),
        typeof(DateFieldSelector),
        new FrameworkPropertyMetadata(false));

    /// <summary>选中的日期，仅承载日期部分；时间分量由调用方组合。</summary>
    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>当前月视图所在月份，始终规范化到该月 1 日。</summary>
    public DateTime DisplayMonth
    {
        get => (DateTime)GetValue(DisplayMonthProperty);
        set => SetValue(DisplayMonthProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => (bool)GetValue(IsDropDownOpenProperty);
        private set => SetCurrentValue(IsDropDownOpenProperty, value);
    }

    public event EventHandler<DateSelectionChangedEventArgs>? SelectedDateChanged;

    internal Border FieldPart => Field;

    internal System.Windows.Controls.Primitives.Popup PopupPart => CalendarPopup;

    internal Border PopupSurfacePart => CalendarSurface;

    internal TextBlock FieldTextPart => FieldText;

    internal TextBlock MonthHeaderPart => MonthHeader;

    internal WpfButton HeaderPart => HeaderButton;

    internal WpfButton PreviousMonthPart => PreviousMonthButton;

    internal WpfButton NextMonthPart => NextMonthButton;

    internal WpfButton TodayPart => TodayButton;

    internal FrameworkElement DayViewPart => DayView;

    internal FrameworkElement MonthPickerPart => MonthPickerGrid;

    internal FrameworkElement YearPickerPart => YearPickerGrid;

    internal IReadOnlyList<WpfButton> DayCells => _dayCells;

    internal IReadOnlyList<WpfButton> MonthCells => _monthCells;

    internal IReadOnlyList<WpfButton> YearCells => _yearCells;

    internal DateTime CellDate(int index) => _cellDates[index];

    internal int YearCellValue(int index) => _yearCellValues[index];

    /// <summary>当前层级：日 / 月 / 年。标题按钮逐级上钻，选中后逐级下钻。</summary>
    internal CalendarViewLevel ViewLevel => _viewLevel;

    internal int DecadeStart => _decadeStart;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DisplayMonth == UnsetMonth)
        {
            SetCurrentValue(DisplayMonthProperty, MonthOf(SelectedDate ?? DateTime.Today));
        }

        AttachOwnerWindow(Window.GetWindow(this));
        PopupAnimations.SetIsEnabled(CalendarPopup, true);
        RefreshField();
        RefreshMonth();
        ApplyViewLevel(CalendarViewLevel.Day, animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Teardown();

    /// <summary>
    /// 关闭宿主窗口时 WPF 不保证给子元素发 Unloaded，只靠 Unloaded 会把弹窗的
    /// 全局输入监听（PopupDismissalMonitor）留在 InputManager 上。因此同时挂宿主
    /// 窗口的 Closed，两条路径都收敛到幂等的 Teardown。
    /// </summary>
    private void AttachOwnerWindow(Window? window)
    {
        if (ReferenceEquals(window, _ownerWindow))
        {
            return;
        }

        DetachOwnerWindow();
        _ownerWindow = window;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Closed += OnOwnerWindowClosed;
        }
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e) => Teardown();

    private void Teardown()
    {
        MotionAnimations.Cancel(_monthAnimationChannel);
        MotionAnimations.Cancel(_chevronAnimationChannel);
        MotionAnimations.Cancel(_levelAnimationChannel);
        if (Field.IsMouseCaptured)
        {
            Field.ReleaseMouseCapture();
        }

        // 先复位组件状态，再关闭 Popup，避免 Closed 回调在卸载期间重新启动动画。
        IsDropDownOpen = false;
        Field.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        FieldChevronRotation.Angle = 0;
        PopupAnimations.Close(CalendarPopup, immediate: true, restoreFocus: false);

        // Close() only releases the dismissal monitor's global input hook through the
        // popup's Closed event, which does not fire if the host window is torn down
        // first. Disabling the attachment disposes that state outright, so the
        // InputManager subscription cannot outlive the control.
        PopupAnimations.SetIsEnabled(CalendarPopup, false);
        DetachOwnerWindow();
    }

    /// <summary>
    /// PopupDismissalMonitor 直接关闭 Popup 时不会经过 CloseDropDown，
    /// 因此必须从 Popup 的实际 Closed 事件同步字段状态，否则下一次点击会被误判为“再次关闭”。
    /// </summary>
    private void OnCalendarPopupClosed(object? sender, EventArgs e)
    {
        if (!IsDropDownOpen)
        {
            return;
        }

        IsDropDownOpen = false;
        Field.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        AnimateChevron(0);
    }

    private void BuildWeekdayHeader()
    {
        for (int column = 0; column < DaysPerWeek; column++)
        {
            WeekdayHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock label = new()
            {
                FontSize = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            LocalizeExtension.Set(label, TextBlock.TextProperty, WeekdayLabels[column]);
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            Grid.SetColumn(label, column);
            WeekdayHeaderGrid.Children.Add(label);
        }
    }

    private void BuildDayCells()
    {
        for (int column = 0; column < DaysPerWeek; column++)
        {
            MonthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int row = 0; row < WeekCount; row++)
        {
            MonthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DayRowHeight) });
        }

        for (int index = 0; index < CellCount; index++)
        {
            WpfButton cell = new()
            {
                Name = $"DayCell{index}",
                Height = DayCellSize,
                Margin = new Thickness(1),
                Focusable = true,
                Tag = index,
            };
            cell.SetResourceReference(StyleProperty, "CalendarDayCellStyle");
            cell.SetResourceReference(FocusVisualStyleProperty, "MemoFocusVisualStyle");
            cell.Click += OnDayCellClick;

            Grid.SetRow(cell, index / DaysPerWeek);
            Grid.SetColumn(cell, index % DaysPerWeek);
            MonthGrid.Children.Add(cell);
            _dayCells[index] = cell;
        }
    }

    private static DateTime MonthOf(DateTime value) => new(value.Year, value.Month, 1);

    /// <summary>月视图：12 个月份格，3 列 × 4 行。</summary>
    private void BuildMonthCells()
    {
        BuildPickerGrid(MonthPickerGrid);

        for (int index = 0; index < PickerCellCount; index++)
        {
            WpfButton cell = BuildPickerCell($"MonthCell{index}", index);
            cell.Click += OnMonthCellClick;
            MonthPickerGrid.Children.Add(cell);
            _monthCells[index] = cell;
        }
    }

    /// <summary>年视图：十年区间的 10 个年份格，首尾各补一格前后十年的邻接年。</summary>
    private void BuildYearCells()
    {
        BuildPickerGrid(YearPickerGrid);

        for (int index = 0; index < PickerCellCount; index++)
        {
            WpfButton cell = BuildPickerCell($"YearCell{index}", index);
            cell.Click += OnYearCellClick;
            YearPickerGrid.Children.Add(cell);
            _yearCells[index] = cell;
        }
    }

    private static void BuildPickerGrid(Grid grid)
    {
        for (int column = 0; column < PickerColumns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int row = 0; row < PickerRows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PickerRowHeight) });
        }
    }

    private static WpfButton BuildPickerCell(string name, int index)
    {
        WpfButton cell = new()
        {
            Name = name,
            Height = PickerRowHeight - 4,
            Margin = new Thickness(2),
            Focusable = true,
            Tag = index,
        };
        cell.SetResourceReference(StyleProperty, "CalendarUnitCellStyle");
        cell.SetResourceReference(FocusVisualStyleProperty, "MemoFocusVisualStyle");

        Grid.SetRow(cell, index / PickerColumns);
        Grid.SetColumn(cell, index % PickerColumns);
        return cell;
    }

    /// <summary>月视图首格日期：以周一为首列回退到包含当月 1 日的那一周。</summary>
    internal static DateTime FirstCellDate(DateTime month)
    {
        DateTime first = MonthOf(month);
        int offset = ((int)first.DayOfWeek + 6) % DaysPerWeek;
        return first.AddDays(-offset);
    }

    private void RefreshField()
    {
        FieldText.Text = SelectedDate is { } date
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
        if (SelectedDate is { } named)
            LocalizeExtension.Set(Field, AutomationProperties.NameProperty, "提醒日期 {0:yyyy 年 M 月 d 日}", named);
        else
            LocalizeExtension.Set(Field, AutomationProperties.NameProperty, "提醒日期");
    }

    private void RefreshMonth()
    {
        DateTime month = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);

        // 年层级的区间要先跟上显示年份，标题与年份格才会指向同一个十年。
        if (_viewLevel == CalendarViewLevel.Year)
        {
            SyncDecadeToDisplayMonth();
        }

        RefreshHeader();

        DateTime cursor = FirstCellDate(month);
        DateTime today = DateTime.Today;
        DateTime? selected = SelectedDate?.Date;

        for (int index = 0; index < CellCount; index++)
        {
            DateTime cellDate = cursor;
            _cellDates[index] = cellDate;

            WpfButton cell = _dayCells[index];
            cell.Content = cellDate.Day.ToString(CultureInfo.InvariantCulture);
            cell.SetResourceReference(
                StyleProperty,
                cellDate.Month == month.Month ? "CalendarDayCellStyle" : "CalendarAdjacentDayCellStyle");
            LocalizeExtension.Set(cell, AutomationProperties.NameProperty, "{0:yyyy 年 M 月 d 日}", cellDate);

            bool isSelected = selected == cellDate;
            InteractionState.SetIsSelected(cell, isSelected);
            InteractionState.SetIsRevealed(cell, !isSelected && cellDate == today);

            // DateTime.MaxValue 附近不得越界；末格保持最后一个合法日期。
            cursor = cellDate < DateTime.MaxValue.Date ? cellDate.AddDays(1) : cellDate;
        }

        if (_viewLevel == CalendarViewLevel.Month)
        {
            RefreshMonthPicker();
        }
        else if (_viewLevel == CalendarViewLevel.Year)
        {
            RefreshYearPicker();
        }
    }

    /// <summary>标题文案与可读名称随层级变化：年月 / 年 / 年份区间。</summary>
    private void RefreshHeader()
    {
        DateTime month = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);

        (string text, string automationName) = _viewLevel switch
        {
            CalendarViewLevel.Month => ("{0:yyyy 年}", "切换到年份选择"),
            CalendarViewLevel.Year => (
                $"{_decadeStart} - {_decadeStart + YearsPerDecade - 1}",
                "返回月份选择"),
            _ => ("{0:yyyy 年 M 月}", "切换到月份选择"),
        };

        LocalizeExtension.Set(MonthHeader, TextBlock.TextProperty, text, month);
        LocalizeExtension.Set(HeaderButton, AutomationProperties.NameProperty, automationName);
        LocalizeExtension.Set(PreviousMonthButton, AutomationProperties.NameProperty, _viewLevel switch
        {
            CalendarViewLevel.Month => "上一年",
            CalendarViewLevel.Year => "上十年",
            _ => "上一月",
        });
        LocalizeExtension.Set(NextMonthButton, AutomationProperties.NameProperty, _viewLevel switch
        {
            CalendarViewLevel.Month => "下一年",
            CalendarViewLevel.Year => "下十年",
            _ => "下一月",
        });
    }

    private void RefreshMonthPicker()
    {
        DateTime month = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        DateTime today = DateTime.Today;

        for (int index = 0; index < PickerCellCount; index++)
        {
            WpfButton cell = _monthCells[index];

            // 12 个月正好铺满 3×4，没有补位格。
            int monthNumber = index + 1;
            DateTime monthDate = new(month.Year, monthNumber, 1);
            LocalizeExtension.Set(cell, WpfButton.ContentProperty, "{0:M 月}", monthDate);
            LocalizeExtension.Set(cell, AutomationProperties.NameProperty, "{0:yyyy 年 M 月}", monthDate);
            cell.SetResourceReference(StyleProperty, "CalendarUnitCellStyle");

            // 月份层级表示当前正在浏览的月份，而不是仍可能停留在旧月份的
            // SelectedDate。选中年份后回到月份层级时，active 必须跟随 DisplayMonth。
            bool isSelected = month.Month == monthNumber;
            InteractionState.SetIsSelected(cell, isSelected);
            InteractionState.SetIsRevealed(
                cell,
                !isSelected && today.Year == month.Year && today.Month == monthNumber);
        }
    }

    private void RefreshYearPicker()
    {
        int today = DateTime.Today.Year;
        int displayedYear = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth).Year;

        for (int index = 0; index < PickerCellCount; index++)
        {
            WpfButton cell = _yearCells[index];

            // 首格与末格是前后十年的邻接年，落在 DateTime 范围外时该格禁用而不是抛异常。
            int year = _decadeStart + index - 1;
            bool inRange = year >= DateTime.MinValue.Year && year <= DateTime.MaxValue.Year;
            bool inDecade = year >= _decadeStart && year < _decadeStart + YearsPerDecade;

            _yearCellValues[index] = year;
            cell.Content = inRange ? year.ToString(CultureInfo.InvariantCulture) : string.Empty;
            cell.IsEnabled = inRange;
            LocalizeExtension.Set(cell, AutomationProperties.NameProperty, inRange ? "{0} 年" : string.Empty, year);
            cell.SetResourceReference(
                StyleProperty,
                inDecade ? "CalendarUnitCellStyle" : "CalendarAdjacentUnitCellStyle");

            // 年份层级同样跟随当前显示年份。SelectedDate 可能仍是用户进入
            // picker 前的日期，不能让它覆盖刚刚浏览到的年份。
            bool isSelected = inRange && displayedYear == year;
            InteractionState.SetIsSelected(cell, isSelected);
            InteractionState.SetIsRevealed(cell, inRange && !isSelected && year == today);
        }
    }

    /// <summary>年份所属十年区间的首年。DateTime 年份恒为 1..9999，无需处理负数。</summary>
    private static int DecadeStartOf(int year) => year - (year % YearsPerDecade);

    /// <summary>
    /// 让年份区间覆盖当前显示年份。已经覆盖时保持不动，这样翻十年后区间不会被
    /// 无关的刷新拽回显示年份所在的十年。
    /// </summary>
    private void SyncDecadeToDisplayMonth()
    {
        DateTime month = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        if (_decadeStart < 0 || month.Year < _decadeStart || month.Year >= _decadeStart + YearsPerDecade)
        {
            _decadeStart = DecadeStartOf(month.Year);
        }
    }

    private static void OnSelectedDatePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        DateFieldSelector selector = (DateFieldSelector)sender;
        DateTime? oldValue = (DateTime?)e.OldValue;
        DateTime? newValue = (DateTime?)e.NewValue;

        if (newValue is { } value && value != value.Date)
        {
            // 只承载日期部分：带时间分量的写入规范化后重入一次，
            // 期间抑制事件，让订阅方只看到规范化后的那一次变更。
            if (selector._updatingSelection)
            {
                return;
            }

            selector._updatingSelection = true;
            try
            {
                selector.SetCurrentValue(SelectedDateProperty, value.Date);
            }
            finally
            {
                selector._updatingSelection = false;
            }

            selector.SelectedDateChanged?.Invoke(
                selector,
                new DateSelectionChangedEventArgs(oldValue, value.Date));
            return;
        }

        selector.RefreshField();

        if (newValue is { } target && MonthOf(target) != MonthOf(selector.DisplayMonth))
        {
            selector.SetCurrentValue(DisplayMonthProperty, MonthOf(target));
        }
        else
        {
            selector.RefreshMonth();
        }

        if (!selector._updatingSelection)
        {
            selector.SelectedDateChanged?.Invoke(selector, new DateSelectionChangedEventArgs(oldValue, newValue));
        }
    }

    private static void OnDisplayMonthPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        DateFieldSelector selector = (DateFieldSelector)sender;
        DateTime requested = (DateTime)e.NewValue;
        if (requested != UnsetMonth && requested != MonthOf(requested))
        {
            selector.SetCurrentValue(DisplayMonthProperty, MonthOf(requested));
            return;
        }

        selector.RefreshMonth();
    }

    private void OnFieldClick(object sender, MouseButtonEventArgs e)
    {
        Field.Focus();
        ToggleDropDown();
        e.Handled = true;
    }

    private void OnTodayClick(object sender, RoutedEventArgs e) => CommitDate(DateTime.Today, closePopup: true);

    private void OnDayCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: int index })
        {
            CommitDate(_cellDates[index], closePopup: true);
        }
    }

    /// <summary>标题按钮逐级上钻：日 → 月 → 年；年层级再点回到月层级。</summary>
    private void OnHeaderClick(object sender, RoutedEventArgs e) => ApplyViewLevel(
        _viewLevel switch
        {
            CalendarViewLevel.Day => CalendarViewLevel.Month,
            CalendarViewLevel.Month => CalendarViewLevel.Year,
            _ => CalendarViewLevel.Month,
        },
        animate: true);

    private void OnMonthCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int index })
        {
            return;
        }

        DateTime current = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        SetCurrentValue(DisplayMonthProperty, new DateTime(current.Year, index + 1, 1));
        ApplyViewLevel(CalendarViewLevel.Day, animate: true);
    }

    private void OnYearCellClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: int index })
        {
            return;
        }

        int year = _yearCellValues[index];
        if (year < DateTime.MinValue.Year || year > DateTime.MaxValue.Year)
        {
            return;
        }

        DateTime current = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        SetCurrentValue(DisplayMonthProperty, new DateTime(year, current.Month, 1));

        // 越出当前区间的补位年份被选中时，区间随之跟到该年所在的十年。
        _decadeStart = DecadeStartOf(year);
        ApplyViewLevel(CalendarViewLevel.Month, animate: true);
    }

    /// <summary>
    /// 切换层级。三个视图叠在同一格且非活动者为 Hidden：Hidden 仍参与测量，
    /// 因此浮层高度取三者最大值，逐级钻取时高度不跳动。
    /// </summary>
    private void ApplyViewLevel(CalendarViewLevel level, bool animate)
    {
        int direction = level > _viewLevel ? 1 : level < _viewLevel ? -1 : 0;
        _viewLevel = level;

        if (level == CalendarViewLevel.Year)
        {
            SyncDecadeToDisplayMonth();
        }

        DayView.Visibility = level == CalendarViewLevel.Day ? Visibility.Visible : Visibility.Hidden;
        MonthPickerGrid.Visibility = level == CalendarViewLevel.Month ? Visibility.Visible : Visibility.Hidden;
        YearPickerGrid.Visibility = level == CalendarViewLevel.Year ? Visibility.Visible : Visibility.Hidden;

        RefreshHeader();
        switch (level)
        {
            case CalendarViewLevel.Month:
                RefreshMonthPicker();
                break;
            case CalendarViewLevel.Year:
                RefreshYearPicker();
                break;
        }

        // Hidden 元素无法获得键盘焦点，因此焦点迁移必须在可见性切换之后；
        // 只在浮层已展开时迁移，避免 Loaded 阶段抢走宿主的初始焦点。
        if (IsDropDownOpen && direction != 0)
        {
            MoveFocusIntoActiveView();
        }

        AnimateLevelShift(direction, animate);
    }

    private void MoveFocusIntoActiveView()
    {
        DateTime month = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        switch (_viewLevel)
        {
            case CalendarViewLevel.Month:
                FocusPickerCell(_monthCells, month.Month - 1);
                break;
            case CalendarViewLevel.Year:
                FocusPickerCell(_yearCells, month.Year - _decadeStart + 1);
                break;
            default:
                // 下钻回日层级时选中日期可能不在新显示月内，此时落到该月 1 日。
                DateTime? selected = SelectedDate?.Date;
                FocusCell(selected is { } pick && MonthOf(pick) == month ? pick : month);
                break;
        }
    }

    /// <summary>层级切换动效：上钻内容后退、下钻内容前进，短距离位移 + 透明度。</summary>
    private void AnimateLevelShift(int direction, bool animate)
    {
        // MotionAnimations.Cancel 不回写数值，被打断的视图会停在中途位移与半透明上。
        // 翻页通道也要一起取消：它与层级切换争的是同一批 offset / Opacity。
        MotionAnimations.Cancel(_levelAnimationChannel);
        MotionAnimations.Cancel(_monthAnimationChannel);
        ResetView(MonthGrid, MonthGridOffset);
        ResetView(DayView, DayViewOffset);
        ResetView(MonthPickerGrid, MonthPickerOffset);
        ResetView(YearPickerGrid, YearPickerOffset);

        if (direction == 0 || !animate || !MotionPreferences.AnimationsEnabled)
        {
            return;
        }

        TranslateTransform offset = ActiveViewOffset;
        FrameworkElement view = ActiveView;
        double from = direction > 0 ? 14 : -14;
        view.Opacity = 0;
        offset.Y = from;
        MotionAnimations.Start(
            _levelAnimationChannel,
            MotionPreferences.Effective(TimeSpan.FromMilliseconds(170)),
            MotionEasing.CubicEaseOut,
            progress =>
            {
                offset.Y = from * (1 - progress);
                view.Opacity = progress;
            },
            () =>
            {
                offset.Y = 0;
                view.Opacity = 1;
            });
    }

    private static void ResetView(FrameworkElement view, TranslateTransform offset)
    {
        offset.X = 0;
        offset.Y = 0;
        view.Opacity = 1;
    }

    private FrameworkElement ActiveView => _viewLevel switch
    {
        CalendarViewLevel.Month => MonthPickerGrid,
        CalendarViewLevel.Year => YearPickerGrid,
        _ => DayView,
    };

    private TranslateTransform ActiveViewOffset => _viewLevel switch
    {
        CalendarViewLevel.Month => MonthPickerOffset,
        CalendarViewLevel.Year => YearPickerOffset,
        _ => DayViewOffset,
    };

    private void CommitDate(DateTime date, bool closePopup)
    {
        SetCurrentValue(SelectedDateProperty, date.Date);
        if (closePopup)
        {
            CloseDropDown(restoreFocus: true);
        }
    }

    internal void ToggleDropDown()
    {
        if (IsDropDownOpen)
        {
            CloseDropDown(restoreFocus: true);
        }
        else
        {
            OpenDropDown();
        }
    }

    internal void OpenDropDown()
    {
        if (IsDropDownOpen)
        {
            return;
        }

        if (DisplayMonth == UnsetMonth)
        {
            SetCurrentValue(DisplayMonthProperty, MonthOf(SelectedDate ?? DateTime.Today));
        }

        RefreshMonth();
        IsDropDownOpen = true;
        Field.SetResourceReference(Border.BorderBrushProperty, "BorderFocusBrush");
        AnimateChevron(180);
        PopupAnimations.Open(CalendarPopup, Field);

        // 每次展开都回到日层级：上一次钻到月/年层级不应成为下一次的起点。
        ApplyViewLevel(CalendarViewLevel.Day, animate: false);
        FocusCell(SelectedDate?.Date ?? DateTime.Today);
    }

    internal void CloseDropDown(bool restoreFocus)
    {
        if (!IsDropDownOpen)
        {
            return;
        }

        IsDropDownOpen = false;
        Field.SetResourceReference(Border.BorderBrushProperty, "BorderDefaultBrush");
        AnimateChevron(0);
        PopupAnimations.Close(CalendarPopup, immediate: false, restoreFocus: false);

        if (restoreFocus)
        {
            Field.Focus();
        }
    }

    private void AnimateChevron(double angle)
    {
        double from = FieldChevronRotation.Angle;
        if (!MotionPreferences.AnimationsEnabled)
        {
            MotionAnimations.Cancel(_chevronAnimationChannel);
            FieldChevronRotation.Angle = angle;
            return;
        }

        MotionAnimations.Start(
            _chevronAnimationChannel,
            MotionPreferences.Effective(TimeSpan.FromMilliseconds(160)),
            MotionEasing.CubicEaseOut,
            progress => FieldChevronRotation.Angle = from + ((angle - from) * progress));
    }

    /// <summary>导航按钮按当前层级翻页：日层级翻月、月层级翻年、年层级翻十年。</summary>
    internal void StepLevel(int delta)
    {
        switch (_viewLevel)
        {
            case CalendarViewLevel.Month:
                StepYear(delta);
                break;
            case CalendarViewLevel.Year:
                StepDecade(delta);
                break;
            default:
                StepMonth(delta);
                break;
        }
    }

    /// <summary>翻年（月层级）。越界时收敛到合法年份而不抛异常。</summary>
    internal void StepYear(int delta)
    {
        DateTime current = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        DateTime target;
        try
        {
            target = current.AddYears(delta);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (target == current)
        {
            return;
        }

        SetCurrentValue(DisplayMonthProperty, target);
        AnimateMonthShift(delta);
    }

    /// <summary>翻十年（年层级）。区间被 DateTime 年份范围夹住。</summary>
    internal void StepDecade(int delta)
    {
        int target = _decadeStart + (delta * YearsPerDecade);
        if (target < DecadeStartOf(DateTime.MinValue.Year) || target > DecadeStartOf(DateTime.MaxValue.Year))
        {
            return;
        }

        _decadeStart = target;
        RefreshHeader();
        RefreshYearPicker();
        AnimateMonthShift(delta);
    }

    /// <summary>翻月。跨年与 MinValue/MaxValue 边界处收敛到合法月份而不抛异常。</summary>
    internal void StepMonth(int delta)
    {
        DateTime current = MonthOf(DisplayMonth == UnsetMonth ? DateTime.Today : DisplayMonth);
        DateTime target;
        try
        {
            target = current.AddMonths(delta);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (target == current)
        {
            return;
        }

        SetCurrentValue(DisplayMonthProperty, target);
        AnimateMonthShift(delta);
    }

    /// <summary>
    /// 翻页动效。按当前层级选目标：日层级只滑日期网格（星期表头保持不动），
    /// 月 / 年层级滑对应的选择网格。
    /// </summary>
    private void AnimateMonthShift(int delta)
    {
        (FrameworkElement view, TranslateTransform offset) = _viewLevel switch
        {
            CalendarViewLevel.Month => ((FrameworkElement)MonthPickerGrid, MonthPickerOffset),
            CalendarViewLevel.Year => (YearPickerGrid, YearPickerOffset),
            _ => (MonthGrid, MonthGridOffset),
        };

        double from = delta > 0 ? 18 : -18;
        if (!MotionPreferences.AnimationsEnabled)
        {
            MotionAnimations.Cancel(_monthAnimationChannel);
            offset.X = 0;
            view.Opacity = 1;
            return;
        }

        view.Opacity = 0;
        offset.X = from;
        MotionAnimations.Start(
            _monthAnimationChannel,
            MotionPreferences.Effective(TimeSpan.FromMilliseconds(170)),
            MotionEasing.CubicEaseOut,
            progress =>
            {
                offset.X = from * (1 - progress);
                view.Opacity = progress;
            },
            () =>
            {
                offset.X = 0;
                view.Opacity = 1;
            });
    }

    private void FocusCell(DateTime date)
    {
        int index = IndexOf(date);
        if (index < 0)
        {
            index = 0;
        }

        _focusAnchor = _cellDates[index];
        _dayCells[index].Focus();
    }

    internal int IndexOf(DateTime date)
    {
        DateTime target = date.Date;
        for (int index = 0; index < CellCount; index++)
        {
            if (_cellDates[index] == target)
            {
                return index;
            }
        }

        return -1;
    }

    private void OnFieldKeyDown(object sender, WpfKeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
            case Key.Enter:
                ToggleDropDown();
                e.Handled = true;
                break;
            case Key.Escape when IsDropDownOpen:
                CloseDropDown(restoreFocus: true);
                e.Handled = true;
                break;
        }
    }

    private void OnPopupKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDropDown(restoreFocus: true);
            e.Handled = true;
            return;
        }

        bool handled = _viewLevel switch
        {
            CalendarViewLevel.Month => HandleMonthLevelKey(e.Key),
            CalendarViewLevel.Year => HandleYearLevelKey(e.Key),
            _ => HandleDayLevelKey(e.Key),
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    private bool HandleDayLevelKey(Key key)
    {
        DateTime focused = FocusedDate;
        switch (key)
        {
            case Key.Left:
                MoveFocus(focused, -1);
                break;
            case Key.Right:
                MoveFocus(focused, 1);
                break;
            case Key.Up:
                MoveFocus(focused, -DaysPerWeek);
                break;
            case Key.Down:
                MoveFocus(focused, DaysPerWeek);
                break;
            case Key.PageUp:
                StepMonth(-1);
                FocusCell(ClampToDisplayMonth(focused));
                break;
            case Key.PageDown:
                StepMonth(1);
                FocusCell(ClampToDisplayMonth(focused));
                break;
            case Key.Home:
                FocusCell(MonthOf(DisplayMonth));
                break;
            case Key.End:
                FocusCell(MonthOf(DisplayMonth).AddMonths(1).AddDays(-1));
                break;
            case Key.Enter:
            case Key.Space:
                CommitDate(focused, closePopup: true);
                break;
            default:
                return false;
        }

        return true;
    }

    private bool HandleMonthLevelKey(Key key)
    {
        int index = PickerFocusIndex;
        switch (key)
        {
            case Key.Left:
                FocusPickerCell(_monthCells, index - 1);
                break;
            case Key.Right:
                FocusPickerCell(_monthCells, index + 1);
                break;
            case Key.Up:
                FocusPickerCell(_monthCells, index - PickerColumns);
                break;
            case Key.Down:
                FocusPickerCell(_monthCells, index + PickerColumns);
                break;
            case Key.PageUp:
                StepYear(-1);
                FocusPickerCell(_monthCells, index);
                break;
            case Key.PageDown:
                StepYear(1);
                FocusPickerCell(_monthCells, index);
                break;
            case Key.Home:
                FocusPickerCell(_monthCells, 0);
                break;
            case Key.End:
                FocusPickerCell(_monthCells, PickerCellCount - 1);
                break;
            case Key.Enter:
            case Key.Space:
                _monthCells[index].RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
                break;
            default:
                return false;
        }

        return true;
    }

    private bool HandleYearLevelKey(Key key)
    {
        int index = PickerFocusIndex;
        switch (key)
        {
            case Key.Left:
                FocusPickerCell(_yearCells, index - 1);
                break;
            case Key.Right:
                FocusPickerCell(_yearCells, index + 1);
                break;
            case Key.Up:
                FocusPickerCell(_yearCells, index - PickerColumns);
                break;
            case Key.Down:
                FocusPickerCell(_yearCells, index + PickerColumns);
                break;
            case Key.PageUp:
                StepDecade(-1);
                FocusPickerCell(_yearCells, index);
                break;
            case Key.PageDown:
                StepDecade(1);
                FocusPickerCell(_yearCells, index);
                break;
            case Key.Home:
                // 首格是上一个十年的补位年，区间首年在索引 1。
                FocusPickerCell(_yearCells, 1);
                break;
            case Key.End:
                FocusPickerCell(_yearCells, YearsPerDecade);
                break;
            case Key.Enter:
            case Key.Space:
                if (_yearCells[index].IsEnabled)
                {
                    _yearCells[index].RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
                }

                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>
    /// 月 / 年视图的键盘锚点。与 FocusedDate 同理：测试宿主里焦点可能落不到格子上，
    /// 因此回退到最近一次通过 FocusPickerCell 设置的索引。
    /// </summary>
    internal int PickerFocusIndex
    {
        get
        {
            IReadOnlyList<WpfButton> cells = _viewLevel == CalendarViewLevel.Year ? _yearCells : _monthCells;
            if (Keyboard.FocusedElement is WpfButton { Tag: int index }
                && index >= 0
                && index < cells.Count
                && ReferenceEquals(cells[index], Keyboard.FocusedElement))
            {
                return index;
            }

            return Math.Clamp(_pickerAnchor, 0, PickerCellCount - 1);
        }
    }

    private void FocusPickerCell(WpfButton[] cells, int index)
    {
        int clamped = Math.Clamp(index, 0, PickerCellCount - 1);
        _pickerAnchor = clamped;
        cells[clamped].Focus();
    }

    /// <summary>
    /// 键盘导航的当前锚点。优先取真正获得焦点的日期格；测试宿主里键盘焦点可能落不到格子上，
    /// 因此回退到最近一次通过 FocusCell 设置的锚点，再回退到选中日期。
    /// </summary>
    internal DateTime FocusedDate
    {
        get
        {
            // 月 / 年格也带 int Tag，必须确认焦点确实落在日期格上再按索引取日期。
            if (Keyboard.FocusedElement is WpfButton { Tag: int index }
                && index >= 0
                && index < CellCount
                && ReferenceEquals(_dayCells[index], Keyboard.FocusedElement))
            {
                return _cellDates[index];
            }

            return _focusAnchor ?? SelectedDate?.Date ?? DateTime.Today;
        }
    }

    private DateTime ClampToDisplayMonth(DateTime date)
    {
        DateTime month = MonthOf(DisplayMonth);
        int lastDay = DateTime.DaysInMonth(month.Year, month.Month);
        return new DateTime(month.Year, month.Month, Math.Min(date.Day, lastDay));
    }

    private void MoveFocus(DateTime from, int dayDelta)
    {
        DateTime target;
        try
        {
            target = from.AddDays(dayDelta);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (MonthOf(target) != MonthOf(DisplayMonth))
        {
            SetCurrentValue(DisplayMonthProperty, MonthOf(target));
        }

        FocusCell(target);
    }
}

/// <summary>日期选择变更事件参数，携带旧值与新值。</summary>
public sealed class DateSelectionChangedEventArgs(DateTime? oldDate, DateTime? newDate) : EventArgs
{
    public DateTime? OldDate { get; } = oldDate;

    public DateTime? NewDate { get; } = newDate;
}

/// <summary>
/// 日历浮层的层级。数值递增即「上钻」方向，层级切换动效据此判断位移正负。
/// </summary>
public enum CalendarViewLevel
{
    Day = 0,
    Month = 1,
    Year = 2,
}
