using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class SessionStateMachineTests
{
    private static readonly CompanionOptions DefaultOptions = new()
    {
        ServerOrigin = new Uri("https://beacon.example.com"),
        DeviceName = "TEST-LAPTOP",
    };

    [Fact]
    public void RequiresOwnerApprovalForInteractiveFullControlWhenNotPreApproved()
    {
        var machine = new SessionStateMachine();
        var request = new SessionApprovalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PermissionMode.FullControl,
            IsUnattendedAttempt: false,
            DateTimeOffset.UtcNow);

        var decision = machine.BeginSession(request, DefaultOptions, ownerExplicitlyApproved: false);

        Assert.False(decision.Approved);
        Assert.Equal(SessionState.AwaitingOwnerApproval, machine.State);
        Assert.Equal(PermissionMode.ViewOnly, machine.Mode);

        Assert.True(machine.ResolveOwnerApproval(approved: true, PermissionMode.FullControl));
        Assert.Equal(SessionState.ActiveFullControl, machine.State);
        Assert.Equal(PermissionMode.FullControl, machine.Mode);
    }

    [Fact]
    public void DecliningOwnerApprovalTerminatesSession()
    {
        var machine = new SessionStateMachine();
        var request = new SessionApprovalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PermissionMode.FullControl,
            IsUnattendedAttempt: false,
            DateTimeOffset.UtcNow);

        machine.BeginSession(request, DefaultOptions);
        var approved = machine.ResolveOwnerApproval(approved: false);

        Assert.False(approved);
        Assert.Equal(SessionState.Terminated, machine.State);
        Assert.Null(machine.ActiveSessionId);
    }

    [Fact]
    public void UnattendedPolicyRestrictsOrDeniesFullControlUnlessPreAuthorized()
    {
        var machineDisabled = new SessionStateMachine();
        var unattendedReq = new SessionApprovalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PermissionMode.FullControl,
            IsUnattendedAttempt: true,
            DateTimeOffset.UtcNow);

        var denied = machineDisabled.BeginSession(unattendedReq, DefaultOptions);
        Assert.False(denied.Approved);
        Assert.Equal(SessionState.Idle, machineDisabled.State);

        var machineViewOnly = new SessionStateMachine();
        var viewOnlyOptions = DefaultOptions with { UnattendedAccess = UnattendedAccessPolicy.AllowViewOnly };
        var downgraded = machineViewOnly.BeginSession(unattendedReq, viewOnlyOptions);
        Assert.True(downgraded.Approved);
        Assert.Equal(PermissionMode.ViewOnly, downgraded.GrantedMode);
        Assert.Equal(SessionState.ActiveViewOnly, machineViewOnly.State);

        var machinePreAuth = new SessionStateMachine();
        var preAuthOptions = DefaultOptions with { UnattendedAccess = UnattendedAccessPolicy.AllowFullControlWithPreAuthorization };
        var granted = machinePreAuth.BeginSession(unattendedReq, preAuthOptions);
        Assert.True(granted.Approved);
        Assert.Equal(PermissionMode.FullControl, granted.GrantedMode);
        Assert.Equal(SessionState.ActiveFullControl, machinePreAuth.State);
    }

    [Fact]
    public void SteppingDownPauseAndLockRequireInputReset()
    {
        var machine = new SessionStateMachine();
        machine.BeginSession(
            new SessionApprovalRequest(Guid.NewGuid(), Guid.NewGuid(), PermissionMode.FullControl, false, DateTimeOffset.UtcNow),
            DefaultOptions,
            ownerExplicitlyApproved: true);

        Assert.True(machine.Pause());
        Assert.Equal(SessionState.Paused, machine.State);

        machine.Resume();
        Assert.Equal(SessionState.ActiveFullControl, machine.State);

        Assert.True(machine.SetPermissionMode(PermissionMode.ViewOnly));
        Assert.Equal(SessionState.ActiveViewOnly, machine.State);

        Assert.False(machine.NotifyDesktopLockedOrUnavailable());
        Assert.Equal(SessionState.LockedOrUnavailable, machine.State);

        machine.NotifyDesktopUnlocked();
        Assert.Equal(SessionState.ActiveViewOnly, machine.State);

        machine.Terminate();
        Assert.Equal(SessionState.Terminated, machine.State);
    }
}
