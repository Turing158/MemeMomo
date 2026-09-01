using System.Windows;
using WpfPoint = System.Windows.Point;

namespace Memo.UI.Windows;

public readonly record struct MonitorSnapshot(PixelMonitorInfo Info, bool IsPrimary);

internal static class MonitorSelection
{
    internal static MonitorSnapshot? Select(
        IReadOnlyList<MonitorSnapshot> monitors,
        Rect savedWorkingArea)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        if (savedWorkingArea.Width > 0 && savedWorkingArea.Height > 0)
        {
            foreach (MonitorSnapshot monitor in monitors)
            {
                if (monitor.Info.WorkingArea == savedWorkingArea)
                {
                    return monitor;
                }
            }

            WpfPoint center = new(
                savedWorkingArea.Left + savedWorkingArea.Width / 2,
                savedWorkingArea.Top + savedWorkingArea.Height / 2);
            MonitorSnapshot? containing = monitors
                .Where(monitor => monitor.Info.Bounds.Contains(center) || monitor.Info.WorkingArea.Contains(center))
                .OrderByDescending(monitor => monitor.Info.WorkingArea.Contains(center))
                .Select(monitor => (MonitorSnapshot?)monitor)
                .FirstOrDefault();
            if (containing.HasValue)
            {
                return containing;
            }
        }

        foreach (MonitorSnapshot monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return monitors[0];
    }
}
