namespace MemeMomo.UI;

/// <summary>
/// 拖动中鼠标移动的按下状态判定。贴边预览 / 脱离 morph 切换期间 UI 线程忙碌，
/// 松手后的 move 仍会带着"按下"状态排队到达，而处理时实时读取的指针位置已是
/// 松手后的新位置——若按事件队列的按键状态继续拖动，会把贴边窗口瞬间拉出。
/// 因此事件驱动的移动处理以物理按键状态为准，物理状态不可读时才回退队列状态。
/// </summary>
internal static class DockPointerGate
{
    public static bool IsDragPressed(bool eventQueuePressed, bool physicalKnown, bool physicalPressed) =>
        physicalKnown ? physicalPressed : eventQueuePressed;
}
