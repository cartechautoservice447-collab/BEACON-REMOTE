using Beacon.Companion.Core.Protocol;

namespace Beacon.Companion.Core.Input;

public enum InputExecutionStatus
{
    Executed = 0,
    RejectedInvalid = 1,
    BlockedByPermission = 2,
    BlockedByUipi = 3,
}

public sealed record InputExecutionResult(
    InputExecutionStatus Status,
    string? Reason = null);

public enum InputResetReason
{
    ClientResetCommand = 0,
    SessionPaused = 1,
    SwitchedToViewOnly = 2,
    SessionDisconnected = 3,
    SessionLockedOrDesktopSwitch = 4,
    DeadmanWatchdogTimeout = 5,
    ApplicationExiting = 6,
}

public sealed record InputResetResult(
    int ReleasedKeyCount,
    int ReleasedButtonCount,
    InputResetReason Reason);

public sealed record HeldInputSnapshot(
    IReadOnlySet<MouseButton> HeldButtons,
    IReadOnlySet<string> HeldKeyCodes);

/// <summary>
/// Platform-independent contract for remote input execution (Win32 SendInput adapter in Step 3.6).
/// Implementations must track held buttons/keys and guarantee deterministic release on <see cref="ResetAllAsync"/>.
/// </summary>
public interface IInputExecutor
{
    HeldInputSnapshot GetHeldInputs();
    ValueTask<InputExecutionResult> ExecuteAsync(RemoteInputEvent command, CancellationToken ct = default);
    ValueTask<InputResetResult> ResetAllAsync(InputResetReason reason, CancellationToken ct = default);
}
