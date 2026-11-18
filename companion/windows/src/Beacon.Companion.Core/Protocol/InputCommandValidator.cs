namespace Beacon.Companion.Core.Protocol;

public sealed record InputCommandValidationOptions
{
    /// <summary>
    /// Defensive upper bound on incoming wheel CSS-pixel deltas per frame to block extreme numerical payloads.
    /// </summary>
    public double MaxAbsWheelDelta { get; init; } = 10_000.0;

    /// <summary>
    /// Upper length guard for <see cref="KeyInputEvent.Code"/> before allowlist lookup.
    /// </summary>
    public int MaxKeyCodeLength { get; init; } = 32;

    public void Validate()
    {
        if (!double.IsFinite(MaxAbsWheelDelta) || MaxAbsWheelDelta <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAbsWheelDelta),
                "MaxAbsWheelDelta must be a positive finite number.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxKeyCodeLength);
    }
}

public enum InputValidationFailureReason
{
    None = 0,
    InvalidPointerCoordinates = 1,
    UnsupportedMouseButton = 2,
    UnsupportedInputAction = 3,
    InvalidWheelDelta = 4,
    WheelDeltaOutOfBounds = 5,
    DisallowedKeyCode = 6,
    UnsupportedCommandType = 7,
}

public sealed record InputValidationResult(
    bool IsValid,
    InputValidationFailureReason Reason = InputValidationFailureReason.None,
    string? Detail = null)
{
    public static InputValidationResult Valid() => new(true);

    public static InputValidationResult Invalid(InputValidationFailureReason reason, string detail) =>
        new(false, reason, detail);
}

/// <summary>
/// Pure syntactic and domain validator for deserialized <see cref="RemoteInputEvent"/> instances.
/// Intentionally separate from session authorization (<see cref="Security.IInputPermissionEvaluator"/>).
/// </summary>
public interface IInputCommandValidator
{
    InputValidationResult Validate(RemoteInputEvent command);
}

public sealed class InputCommandValidator : IInputCommandValidator
{
    private readonly InputCommandValidationOptions _options;

    public InputCommandValidator(InputCommandValidationOptions? options = null)
    {
        _options = options ?? new InputCommandValidationOptions();
        _options.Validate();
    }

    public InputValidationResult Validate(RemoteInputEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            PointerMoveAbsEvent move => ValidateCoordinates(move.X, move.Y),
            PointerButtonEvent button => ValidatePointerButton(button),
            PointerWheelEvent wheel => ValidateWheel(wheel),
            KeyInputEvent key => ValidateKey(key),
            InputResetEvent => InputValidationResult.Valid(),
            _ => InputValidationResult.Invalid(
                InputValidationFailureReason.UnsupportedCommandType,
                "Unsupported remote input command type."),
        };
    }

    private static InputValidationResult ValidatePointerButton(PointerButtonEvent button)
    {
        if (!Enum.IsDefined(button.Button))
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.UnsupportedMouseButton,
                "Mouse button must be 'left', 'right', or 'middle'.");
        }

        if (!Enum.IsDefined(button.Action))
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.UnsupportedInputAction,
                "Pointer button action must be 'down' or 'up'.");
        }

        return ValidateCoordinates(button.X, button.Y);
    }

    private InputValidationResult ValidateWheel(PointerWheelEvent wheel)
    {
        if (!double.IsFinite(wheel.Dx) || !double.IsFinite(wheel.Dy))
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.InvalidWheelDelta,
                "Wheel deltas must be finite numbers.");
        }

        if (Math.Abs(wheel.Dx) > _options.MaxAbsWheelDelta ||
            Math.Abs(wheel.Dy) > _options.MaxAbsWheelDelta)
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.WheelDeltaOutOfBounds,
                $"Wheel deltas must not exceed magnitude {_options.MaxAbsWheelDelta}.");
        }

        return InputValidationResult.Valid();
    }

    private InputValidationResult ValidateKey(KeyInputEvent key)
    {
        if (!Enum.IsDefined(key.Action))
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.UnsupportedInputAction,
                "Key action must be 'down' or 'up'.");
        }

        if (string.IsNullOrEmpty(key.Code) ||
            key.Code.Length > _options.MaxKeyCodeLength ||
            !KeyCodeCatalog.IsAllowed(key.Code))
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.DisallowedKeyCode,
                $"Key code '{key.Code}' is not in the allowed catalog.");
        }

        return InputValidationResult.Valid();
    }

    private static InputValidationResult ValidateCoordinates(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0.0 || x > 1.0 || y < 0.0 || y > 1.0)
        {
            return InputValidationResult.Invalid(
                InputValidationFailureReason.InvalidPointerCoordinates,
                "Pointer coordinates must be finite numbers within [0, 1].");
        }

        return InputValidationResult.Valid();
    }
}
