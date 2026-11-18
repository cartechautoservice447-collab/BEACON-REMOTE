using Beacon.Companion.Core.Protocol;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class InputPermissionEvaluatorTests
{
    private readonly InputPermissionEvaluator _evaluator = new();

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    [InlineData(-0.001, 0.5)]
    [InlineData(0.5, 1.001)]
    public void RejectsInvalidPointerCoordinates(double x, double y)
    {
        var decision = _evaluator.Evaluate(
            SessionState.ActiveFullControl,
            PermissionMode.FullControl,
            new PointerMoveAbsEvent(x, y));

        Assert.False(decision.Allowed);
        Assert.Equal(InputRejectionReason.InvalidCoordinates, decision.Reason);
    }

    [Fact]
    public void RejectsDisallowedKeyCodeAndNonFiniteWheelDelta()
    {
        var keyDecision = _evaluator.Evaluate(
            SessionState.ActiveFullControl,
            PermissionMode.FullControl,
            new KeyInputEvent("F24", InputAction.Down));

        Assert.False(keyDecision.Allowed);
        Assert.Equal(InputRejectionReason.DisallowedKeyCode, keyDecision.Reason);

        var wheelDecision = _evaluator.Evaluate(
            SessionState.ActiveFullControl,
            PermissionMode.FullControl,
            new PointerWheelEvent(0, double.NaN));

        Assert.False(wheelDecision.Allowed);
        Assert.Equal(InputRejectionReason.InvalidWheelDelta, wheelDecision.Reason);
    }

    [Theory]
    [InlineData(SessionState.Idle, PermissionMode.FullControl, InputRejectionReason.SessionNotActive)]
    [InlineData(SessionState.AwaitingOwnerApproval, PermissionMode.FullControl, InputRejectionReason.SessionNotActive)]
    [InlineData(SessionState.ActiveViewOnly, PermissionMode.ViewOnly, InputRejectionReason.ViewOnlyMode)]
    [InlineData(SessionState.Paused, PermissionMode.FullControl, InputRejectionReason.SessionPaused)]
    [InlineData(SessionState.LockedOrUnavailable, PermissionMode.FullControl, InputRejectionReason.DesktopLocked)]
    [InlineData(SessionState.Terminated, PermissionMode.FullControl, InputRejectionReason.SessionNotActive)]
    public void DeniesMotionAndKeyEventsUnlessActiveFullControl(
        SessionState state,
        PermissionMode mode,
        InputRejectionReason expectedReason)
    {
        var decision = _evaluator.Evaluate(state, mode, new KeyInputEvent("KeyA", InputAction.Down));

        Assert.False(decision.Allowed);
        Assert.Equal(expectedReason, decision.Reason);
    }

    [Theory]
    [InlineData(SessionState.ActiveViewOnly, PermissionMode.ViewOnly)]
    [InlineData(SessionState.Paused, PermissionMode.FullControl)]
    [InlineData(SessionState.LockedOrUnavailable, PermissionMode.FullControl)]
    [InlineData(SessionState.Terminated, PermissionMode.ViewOnly)]
    public void AlwaysAllowsInputResetSoHeldInputsCanReleaseCleanly(SessionState state, PermissionMode mode)
    {
        var decision = _evaluator.Evaluate(state, mode, new InputResetEvent());
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AllowsValidEventsInActiveFullControl()
    {
        Assert.True(_evaluator.Evaluate(
            SessionState.ActiveFullControl,
            PermissionMode.FullControl,
            new PointerButtonEvent(MouseButton.Left, InputAction.Down, 0.0, 1.0)).Allowed);

        Assert.True(_evaluator.Evaluate(
            SessionState.ActiveFullControl,
            PermissionMode.FullControl,
            new KeyInputEvent("ControlLeft", InputAction.Down)).Allowed);
    }
}
