namespace Memo.UI;

internal enum DockState
{
    Expanded,
    DockPreview,
    Docked,
    RestoreDrag
}

internal enum DockStateEvent
{
    BeginPreview,
    LeavePreview,
    CommitPreview,
    BeginRestoreDrag,
    CompleteRestore,
    CaptureLost,
    Deactivated,
    Disable,
    Hide,
    Close
}

internal readonly record struct DockStateTransition(
    DockState Previous,
    DockState Current,
    bool FinalizeDock,
    bool RestoreExpanded,
    bool Persist,
    bool CancelInteraction);

/// <summary>
/// Explicit docking state transitions. Window/input code owns geometry; this
/// class owns the legal state graph and makes interruption paths converge.
/// </summary>
internal sealed class DockStateMachine
{
    internal DockStateMachine(DockState initial = DockState.Expanded)
    {
        State = initial;
    }

    internal DockState State { get; private set; }

    internal bool Enabled { get; private set; } = true;

    internal bool IsVisible { get; private set; } = true;

    internal bool IsClosing { get; private set; }

    internal DockStateTransition Apply(DockStateEvent @event)
    {
        DockState previous = State;
        bool finalizeDock = false;
        bool restoreExpanded = false;
        bool persist = false;
        bool cancelInteraction = false;

        switch (@event)
        {
            case DockStateEvent.BeginPreview
                when Enabled
                    && !IsClosing
                    && State is DockState.Expanded or DockState.RestoreDrag:
                State = DockState.DockPreview;
                break;
            case DockStateEvent.LeavePreview when State == DockState.DockPreview:
                State = DockState.RestoreDrag;
                restoreExpanded = true;
                break;
            case DockStateEvent.CommitPreview when State == DockState.DockPreview:
                State = DockState.Docked;
                finalizeDock = true;
                persist = true;
                break;
            case DockStateEvent.BeginRestoreDrag when State == DockState.Docked:
                State = DockState.RestoreDrag;
                restoreExpanded = true;
                break;
            case DockStateEvent.CompleteRestore when State == DockState.RestoreDrag:
                State = DockState.Expanded;
                restoreExpanded = true;
                persist = true;
                break;
            case DockStateEvent.CaptureLost:
            case DockStateEvent.Deactivated:
                cancelInteraction = true;
                if (State == DockState.DockPreview)
                {
                    State = DockState.Docked;
                    finalizeDock = true;
                    persist = true;
                }
                else if (State == DockState.RestoreDrag)
                {
                    State = DockState.Expanded;
                    restoreExpanded = true;
                    persist = true;
                }
                break;
            case DockStateEvent.Disable:
                Enabled = false;
                if (State is DockState.Docked or DockState.DockPreview or DockState.RestoreDrag)
                {
                    State = DockState.Expanded;
                    restoreExpanded = true;
                    persist = true;
                    cancelInteraction = true;
                }
                break;
            case DockStateEvent.Hide:
                IsVisible = false;
                cancelInteraction = true;
                ConvergeInterruptedState(ref finalizeDock, ref restoreExpanded, ref persist);
                break;
            case DockStateEvent.Close:
                IsClosing = true;
                IsVisible = false;
                cancelInteraction = true;
                ConvergeInterruptedState(ref finalizeDock, ref restoreExpanded, ref persist);
                break;
        }

        return new DockStateTransition(
            previous,
            State,
            finalizeDock,
            restoreExpanded,
            persist,
            cancelInteraction);
    }

    internal void Enable()
    {
        if (!IsClosing)
        {
            Enabled = true;
        }
    }

    internal void Show()
    {
        if (!IsClosing)
        {
            IsVisible = true;
        }
    }

    private void ConvergeInterruptedState(
        ref bool finalizeDock,
        ref bool restoreExpanded,
        ref bool persist)
    {
        if (State == DockState.DockPreview)
        {
            State = DockState.Docked;
            finalizeDock = true;
            persist = true;
        }
        else if (State == DockState.RestoreDrag)
        {
            State = DockState.Expanded;
            restoreExpanded = true;
            persist = true;
        }
    }
}
