namespace MemeMomo.UI.Dock;

/// <summary>
/// 便签贴边三态。Floating 为现状浮动便签；Docked 为贴边未弹出的 D 形标签；
/// DockedExpanded 为贴边弹出态（宽度 = CollapsedWidth + PopLength）。
/// </summary>
public enum MemoPopoutDockMode
{
    Floating,
    Docked,
    DockedExpanded,
}
