using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Protocol;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class InputCommandPipelineTests
{
    private static readonly SessionAuthorizationContext FullControlAuth =
        new(SessionState.ActiveFullControl, PermissionMode.FullControl, IsConnectionAuthenticated: true);

    [Fact]
    public void ExcessivePointerMovesCoalesceToLatestCoordinateInO1Memory()
    {
        var fakeTime = new FakeTimeProvider();
        var pipeline = new InputCommandPipeline(timeProvider: fakeTime);

        for (var i = 0; i < 500; i++)
        {
            var x = i / 499.0;
            var res = pipeline.ProcessWireMessage(
                $$"""{"type":"pointer.move.abs","x":{{x}},"y":0.5}""",
                FullControlAuth);
            Assert.True(res.Accepted);
        }

        var metrics = pipeline.GetMetrics();
        Assert.True(metrics.HasPendingMove);
        Assert.Equal(499, metrics.CoalescedMoveCount);
        Assert.Equal(0, metrics.PendingDiscreteCount);

        var drained = pipeline.DrainReadyEvents();
        var finalMove = Assert.IsType<PointerMoveAbsEvent>(Assert.Single(drained));
        Assert.Equal(1.0, finalMove.X, precision: 6);
        Assert.Equal(0.5, finalMove.Y, precision: 6);
    }

    [Fact]
    public void CriticalKeyUpAndButtonUpAreNeverDroppedUnderMoveOrDownFlood()
    {
        var fakeTime = new FakeTimeProvider();
        var pipeline = new InputCommandPipeline(
            new InputFloodOptions
            {
                MaxDiscreteEventsPerSecond = 10,
                DiscreteBurstCapacity = 5,
                MaxQueuedDiscreteEvents = 5,
                MaxQueuedCriticalReleases = 16,
            },
            timeProvider: fakeTime);

        // Hold KeyA and Left mouse button down.
        Assert.True(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Down), FullControlAuth).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new PointerButtonEvent(MouseButton.Left, InputAction.Down, 0.2, 0.2), FullControlAuth).Accepted);

        // Flood pointer moves and KeyB downs past discrete burst capacity.
        for (var i = 0; i < 200; i++)
        {
            _ = pipeline.ProcessParsedCommand(new PointerMoveAbsEvent(0.3, 0.4), FullControlAuth);
            _ = pipeline.ProcessParsedCommand(new KeyInputEvent("KeyB", InputAction.Down), FullControlAuth);
        }

        // Critical releases must still be accepted even while down events are rate-limited!
        var keyUpRes = pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Up), FullControlAuth);
        var btnUpRes = pipeline.ProcessParsedCommand(new PointerButtonEvent(MouseButton.Left, InputAction.Up, 0.3, 0.4), FullControlAuth);

        Assert.True(keyUpRes.Accepted);
        Assert.True(btnUpRes.Accepted);

        var drained = pipeline.DrainReadyEvents();
        Assert.Contains(drained, e => e is KeyInputEvent { Code: "KeyA", Action: InputAction.Up });
        Assert.Contains(drained, e => e is PointerButtonEvent { Button: MouseButton.Left, Action: InputAction.Up });
    }

    [Fact]
    public void PreservesCausalMoveOrderBeforeClickAndRelease()
    {
        var fakeTime = new FakeTimeProvider();
        var pipeline = new InputCommandPipeline(timeProvider: fakeTime);

        Assert.True(pipeline.ProcessParsedCommand(new PointerMoveAbsEvent(0.2, 0.2), FullControlAuth).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new PointerButtonEvent(MouseButton.Left, InputAction.Down, 0.2, 0.2), FullControlAuth).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new PointerMoveAbsEvent(0.8, 0.8), FullControlAuth).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new PointerButtonEvent(MouseButton.Left, InputAction.Up, 0.8, 0.8), FullControlAuth).Accepted);

        var drained = pipeline.DrainReadyEvents();
        Assert.Equal(4, drained.Count);
        Assert.Equal(new PointerMoveAbsEvent(0.2, 0.2), drained[0]);
        Assert.Equal(new PointerButtonEvent(MouseButton.Left, InputAction.Down, 0.2, 0.2), drained[1]);
        Assert.Equal(new PointerMoveAbsEvent(0.8, 0.8), drained[2]);
        Assert.Equal(new PointerButtonEvent(MouseButton.Left, InputAction.Up, 0.8, 0.8), drained[3]);
    }

    [Fact]
    public void InputResetClearsPendingFloodAndProcessesImmediatelyWithinAuthorizedSession()
    {
        var fakeTime = new FakeTimeProvider();
        var pipeline = new InputCommandPipeline(
            new InputFloodOptions
            {
                MaxDiscreteEventsPerSecond = 1,
                DiscreteBurstCapacity = 2,
                MaxQueuedDiscreteEvents = 2,
            },
            timeProvider: fakeTime);

        Assert.True(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Down), FullControlAuth).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyB", InputAction.Down), FullControlAuth).Accepted);
        var limited = pipeline.ProcessParsedCommand(new KeyInputEvent("KeyC", InputAction.Down), FullControlAuth);
        Assert.False(limited.Accepted);
        Assert.Equal(InputCommandDisposition.RejectedRateLimited, limited.Disposition);

        // Even while rate-limited and with SessionState in ViewOnly/Paused/Locked, input.reset clears pending queue and succeeds.
        var pausedAuth = new SessionAuthorizationContext(SessionState.Paused, PermissionMode.FullControl, IsConnectionAuthenticated: true);
        var resetRes = pipeline.ProcessWireMessage("""{"type":"input.reset"}""", pausedAuth);
        Assert.True(resetRes.Accepted);

        var drained = pipeline.DrainReadyEvents();
        Assert.IsType<InputResetEvent>(Assert.Single(drained));

        // However, unauthenticated or Idle/AwaitingApproval/Terminated sessions CANNOT use input.reset.
        var unauth = new SessionAuthorizationContext(SessionState.ActiveFullControl, PermissionMode.FullControl, IsConnectionAuthenticated: false);
        var unauthReset = pipeline.ProcessWireMessage("""{"type":"input.reset"}""", unauth);
        Assert.False(unauthReset.Accepted);
        Assert.Equal(InputCommandDisposition.RejectedUnauthorized, unauthReset.Disposition);
        Assert.Equal(InputRejectionReason.SessionNotAuthenticated, unauthReset.AuthorizationFailure);
    }

    [Fact]
    public void MalformedCommandFloodTriggersCooldownAndRecoversAfterWindow()
    {
        var fakeTime = new FakeTimeProvider();
        var pipeline = new InputCommandPipeline(
            new InputFloodOptions
            {
                MaxMalformedCommandsPerSecond = 2,
                MalformedBurstCapacity = 3,
                MalformedCooldownDuration = TimeSpan.FromSeconds(5),
                MaxMessageBytes = 256,
            },
            timeProvider: fakeTime);

        for (var i = 0; i < 3; i++)
        {
            var r = pipeline.ProcessWireMessage("{bad", FullControlAuth);
            Assert.False(r.Accepted);
            Assert.Equal(InputCommandDisposition.RejectedProtocol, r.Disposition);
        }

        // 4th malformed message trips cooldown; even valid messages are blocked during cooldown.
        var tripped = pipeline.ProcessWireMessage("{bad", FullControlAuth);
        Assert.Equal(InputCommandDisposition.RejectedMalformedFlood, tripped.Disposition);

        var blockedValid = pipeline.ProcessWireMessage("""{"type":"key","code":"KeyA","action":"down"}""", FullControlAuth);
        Assert.False(blockedValid.Accepted);
        Assert.Equal(InputCommandDisposition.RejectedMalformedFlood, blockedValid.Disposition);

        // Advance past cooldown window -> normal traffic recovers.
        fakeTime.Advance(TimeSpan.FromSeconds(6));
        var recovered = pipeline.ProcessWireMessage("""{"type":"key","code":"KeyA","action":"down"}""", FullControlAuth);
        Assert.True(recovered.Accepted);
    }

    [Fact]
    public void PipelineRespectsSessionStateMachineLifecycleTransitions()
    {
        var machine = new SessionStateMachine();
        var pipeline = new InputCommandPipeline();
        var options = new CompanionOptions { ServerOrigin = new Uri("https://beacon.example.com"), DeviceName = "WIN" };

        var request = new SessionApprovalRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            PermissionMode.FullControl,
            IsUnattendedAttempt: false,
            DateTimeOffset.UtcNow);
        _ = machine.BeginSession(request, options, ownerExplicitlyApproved: false);

        // Awaiting owner approval blocks both input and wire input.reset.
        Assert.False(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Down), machine.GetAuthorizationContext()).Accepted);
        Assert.False(pipeline.ProcessParsedCommand(new InputResetEvent(), machine.GetAuthorizationContext()).Accepted);

        Assert.True(machine.ResolveOwnerApproval(approved: true, PermissionMode.FullControl));
        Assert.True(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Down), machine.GetAuthorizationContext()).Accepted);

        Assert.True(machine.SetPermissionMode(PermissionMode.ViewOnly));
        Assert.False(pipeline.ProcessParsedCommand(new KeyInputEvent("KeyA", InputAction.Up), machine.GetAuthorizationContext()).Accepted);
        Assert.True(pipeline.ProcessParsedCommand(new InputResetEvent(), machine.GetAuthorizationContext()).Accepted);

        _ = machine.Terminate();
        Assert.False(pipeline.ProcessParsedCommand(new InputResetEvent(), machine.GetAuthorizationContext()).Accepted);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _utcNow.UtcTicks;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
