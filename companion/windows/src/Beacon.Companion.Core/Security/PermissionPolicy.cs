using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Protocol;

namespace Beacon.Companion.Core.Security;

public enum PermissionMode
{
    /// <summary>Remote user may view the shared display; all input events except input.reset are rejected.</summary>
    ViewOnly = 0,

    /// <summary>Remote user may inject validated mouse and keyboard events into the interactive user desktop.</summary>
    FullControl = 1,
}

public enum SessionState
{
    Idle = 0,
    AwaitingOwnerApproval = 1,
    ActiveViewOnly = 2,
    ActiveFullControl = 3,
    Paused = 4,
    LockedOrUnavailable = 5,
    Terminated = 6,
}

public sealed record SessionApprovalRequest(
    Guid SessionId,
    Guid DeviceId,
    PermissionMode RequestedMode,
    bool IsUnattendedAttempt,
    DateTimeOffset RequestedAtUtc);

public sealed record SessionApprovalDecision(
    bool Approved,
    PermissionMode GrantedMode,
    string Reason);

public enum InputRejectionReason
{
    None = 0,
    SessionNotActive = 1,
    ViewOnlyMode = 2,
    SessionPaused = 3,
    DesktopLocked = 4,
    InvalidCoordinates = 5,
    InvalidWheelDelta = 6,
    DisallowedKeyCode = 7,
}

public sealed record InputPermissionDecision(
    bool Allowed,
    InputRejectionReason Reason = InputRejectionReason.None,
    string? Detail = null)
{
    public static InputPermissionDecision Allow() => new(true);

    public static InputPermissionDecision Deny(InputRejectionReason reason, string detail) =>
        new(false, reason, detail);
}

public interface IInputPermissionEvaluator
{
    InputPermissionDecision Evaluate(SessionState state, PermissionMode mode, RemoteInputEvent command);
}

/// <summary>
/// Pure security evaluator separating input authorization decisions from Windows OS execution.
/// Normalized coordinates must be finite and within [0, 1]; wheel deltas must be finite;
/// keys must exist in <see cref="KeyCodeCatalog"/>; <c>input.reset</c> is always permitted so held state can unwind cleanly.
/// </summary>
public sealed class InputPermissionEvaluator : IInputPermissionEvaluator
{
    public InputPermissionDecision Evaluate(SessionState state, PermissionMode mode, RemoteInputEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command is InputResetEvent)
        {
            return InputPermissionDecision.Allow();
        }

        switch (state)
        {
            case SessionState.Paused:
                return InputPermissionDecision.Deny(InputRejectionReason.SessionPaused, "Remote control is paused.");
            case SessionState.LockedOrUnavailable:
                return InputPermissionDecision.Deny(InputRejectionReason.DesktopLocked, "Interactive desktop is locked or on Secure Desktop.");
            case SessionState.Idle:
            case SessionState.AwaitingOwnerApproval:
            case SessionState.Terminated:
                return InputPermissionDecision.Deny(InputRejectionReason.SessionNotActive, "No active remote session.");
            case SessionState.ActiveViewOnly:
                return InputPermissionDecision.Deny(InputRejectionReason.ViewOnlyMode, "Session is in View-Only mode.");
            case SessionState.ActiveFullControl:
                break;
        }

        if (mode != PermissionMode.FullControl)
        {
            return InputPermissionDecision.Deny(InputRejectionReason.ViewOnlyMode, "PermissionMode is ViewOnly.");
        }

        return command switch
        {
            PointerMoveAbsEvent move => ValidateNormalizedCoords(move.X, move.Y),
            PointerButtonEvent btn => ValidateNormalizedCoords(btn.X, btn.Y),
            PointerWheelEvent wheel => ValidateWheel(wheel.Dx, wheel.Dy),
            KeyInputEvent key => KeyCodeCatalog.IsAllowed(key.Code)
                ? InputPermissionDecision.Allow()
                : InputPermissionDecision.Deny(InputRejectionReason.DisallowedKeyCode, $"Key code '{key.Code}' is not in the allowed catalog."),
            _ => InputPermissionDecision.Deny(InputRejectionReason.SessionNotActive, "Unsupported input command."),
        };
    }

    private static InputPermissionDecision ValidateNormalizedCoords(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0.0 || x > 1.0 || y < 0.0 || y > 1.0)
        {
            return InputPermissionDecision.Deny(
                InputRejectionReason.InvalidCoordinates,
                "Pointer coordinates must be finite numbers in [0, 1].");
        }

        return InputPermissionDecision.Allow();
    }

    private static InputPermissionDecision ValidateWheel(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
        {
            return InputPermissionDecision.Deny(
                InputRejectionReason.InvalidWheelDelta,
                "Wheel deltas must be finite numbers.");
        }

        return InputPermissionDecision.Allow();
    }
}

/// <summary>
/// Manages session lifecycle transitions, laptop-owner approval requirements for FullControl,
/// unattended-access policy gates, and deterministic input-reset signaling.
/// </summary>
public sealed class SessionStateMachine
{
    public SessionState State { get; private set; } = SessionState.Idle;
    public PermissionMode Mode { get; private set; } = PermissionMode.ViewOnly;
    public Guid? ActiveSessionId { get; private set; }

    public SessionApprovalDecision BeginSession(
        SessionApprovalRequest request,
        CompanionOptions options,
        bool ownerExplicitlyApproved = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (State is not SessionState.Idle and not SessionState.Terminated)
        {
            throw new InvalidOperationException($"Cannot start a new session while in state {State}.");
        }

        if (request.IsUnattendedAttempt)
        {
            if (options.UnattendedAccess == UnattendedAccessPolicy.Disabled)
            {
                return new SessionApprovalDecision(false, PermissionMode.ViewOnly, "Unattended access is disabled.");
            }

            if (request.RequestedMode == PermissionMode.FullControl &&
                options.UnattendedAccess != UnattendedAccessPolicy.AllowFullControlWithPreAuthorization)
            {
                ActiveSessionId = request.SessionId;
                Mode = PermissionMode.ViewOnly;
                State = SessionState.ActiveViewOnly;
                return new SessionApprovalDecision(true, PermissionMode.ViewOnly, "Unattended policy restricts session to View-Only.");
            }
        }
        else if (request.RequestedMode == PermissionMode.FullControl &&
                 options.RequireOwnerApprovalForFullControl &&
                 !ownerExplicitlyApproved)
        {
            ActiveSessionId = request.SessionId;
            Mode = PermissionMode.ViewOnly;
            State = SessionState.AwaitingOwnerApproval;
            return new SessionApprovalDecision(false, PermissionMode.ViewOnly, "Explicit laptop-owner approval is required before Full Control begins.");
        }

        ActiveSessionId = request.SessionId;
        Mode = request.RequestedMode;
        State = Mode == PermissionMode.FullControl
            ? SessionState.ActiveFullControl
            : SessionState.ActiveViewOnly;

        return new SessionApprovalDecision(true, Mode, "Session authorized.");
    }

    /// <summary>
    /// Completes interactive laptop-owner consent when awaiting approval.
    /// Returns false if owner declined (transitioning to Terminated or ViewOnly).
    /// </summary>
    public bool ResolveOwnerApproval(bool approved, PermissionMode grantedMode = PermissionMode.FullControl)
    {
        if (State != SessionState.AwaitingOwnerApproval)
        {
            throw new InvalidOperationException($"Session is not awaiting approval (current state: {State}).");
        }

        if (!approved)
        {
            Terminate();
            return false;
        }

        Mode = grantedMode;
        State = Mode == PermissionMode.FullControl
            ? SessionState.ActiveFullControl
            : SessionState.ActiveViewOnly;
        return true;
    }

    /// <summary>
    /// Updates permission mode; returns true if the caller must run <c>IInputExecutor.ResetAllAsync</c>
    /// because control stepped down from FullControl.
    /// </summary>
    public bool SetPermissionMode(PermissionMode nextMode)
    {
        var requiresReset = State == SessionState.ActiveFullControl && nextMode != PermissionMode.FullControl;
        Mode = nextMode;

        if (State is SessionState.ActiveFullControl or SessionState.ActiveViewOnly)
        {
            State = nextMode == PermissionMode.FullControl
                ? SessionState.ActiveFullControl
                : SessionState.ActiveViewOnly;
        }

        return requiresReset;
    }

    /// <summary>Returns true if active FullControl inputs must be released immediately.</summary>
    public bool Pause()
    {
        var requiresReset = State == SessionState.ActiveFullControl;
        if (State is SessionState.ActiveFullControl or SessionState.ActiveViewOnly)
        {
            State = SessionState.Paused;
        }

        return requiresReset;
    }

    public void Resume()
    {
        if (State != SessionState.Paused)
        {
            return;
        }

        State = Mode == PermissionMode.FullControl
            ? SessionState.ActiveFullControl
            : SessionState.ActiveViewOnly;
    }

    /// <summary>Returns true if active FullControl inputs must be released immediately.</summary>
    public bool NotifyDesktopLockedOrUnavailable()
    {
        var requiresReset = State == SessionState.ActiveFullControl;
        if (State is SessionState.ActiveFullControl or SessionState.ActiveViewOnly or SessionState.Paused)
        {
            State = SessionState.LockedOrUnavailable;
        }

        return requiresReset;
    }

    public void NotifyDesktopUnlocked()
    {
        if (State != SessionState.LockedOrUnavailable)
        {
            return;
        }

        State = Mode == PermissionMode.FullControl
            ? SessionState.ActiveFullControl
            : SessionState.ActiveViewOnly;
    }

    /// <summary>Terminates the session and returns true if held inputs should be reset.</summary>
    public bool Terminate()
    {
        var hadControl = State == SessionState.ActiveFullControl || State == SessionState.Paused;
        State = SessionState.Terminated;
        Mode = PermissionMode.ViewOnly;
        ActiveSessionId = null;
        return hadControl;
    }
}
