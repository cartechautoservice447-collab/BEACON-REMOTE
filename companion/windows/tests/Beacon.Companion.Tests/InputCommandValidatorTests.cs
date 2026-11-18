using Beacon.Companion.Core.Protocol;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class InputCommandValidatorTests
{
    private readonly InputCommandValidator _validator = new();

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    public void AcceptsNormalizedPointerMoveAndButtonBoundaries(double x, double y)
    {
        Assert.True(_validator.Validate(new PointerMoveAbsEvent(x, y)).IsValid);
        Assert.True(_validator.Validate(new PointerButtonEvent(MouseButton.Left, InputAction.Down, x, y)).IsValid);
        Assert.True(_validator.Validate(new PointerButtonEvent(MouseButton.Right, InputAction.Up, x, y)).IsValid);
        Assert.True(_validator.Validate(new PointerButtonEvent(MouseButton.Middle, InputAction.Down, x, y)).IsValid);
    }

    [Theory]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    [InlineData(0.5, double.NegativeInfinity)]
    [InlineData(-0.0001, 0.5)]
    [InlineData(0.5, 1.0001)]
    public void RejectsNonFiniteAndOutOfRangePointerCoordinates(double x, double y)
    {
        var moveResult = _validator.Validate(new PointerMoveAbsEvent(x, y));
        Assert.False(moveResult.IsValid);
        Assert.Equal(InputValidationFailureReason.InvalidPointerCoordinates, moveResult.Reason);

        var btnResult = _validator.Validate(new PointerButtonEvent(MouseButton.Left, InputAction.Down, x, y));
        Assert.False(btnResult.IsValid);
        Assert.Equal(InputValidationFailureReason.InvalidPointerCoordinates, btnResult.Reason);
    }

    [Fact]
    public void RejectsInvalidButtonAndActionEnumsEvenIfConstructedInMemory()
    {
        var badButton = _validator.Validate(new PointerButtonEvent((MouseButton)99, InputAction.Down, 0.5, 0.5));
        Assert.False(badButton.IsValid);
        Assert.Equal(InputValidationFailureReason.UnsupportedMouseButton, badButton.Reason);

        var badButtonAction = _validator.Validate(new PointerButtonEvent(MouseButton.Left, (InputAction)99, 0.5, 0.5));
        Assert.False(badButtonAction.IsValid);
        Assert.Equal(InputValidationFailureReason.UnsupportedInputAction, badButtonAction.Reason);

        var badKeyAction = _validator.Validate(new KeyInputEvent("KeyA", (InputAction)99));
        Assert.False(badKeyAction.IsValid);
        Assert.Equal(InputValidationFailureReason.UnsupportedInputAction, badKeyAction.Reason);
    }

    [Fact]
    public void ValidatesAllAllowedKeysAndRejectsUnknownOrOverlongKeyCodes()
    {
        foreach (var code in KeyCodeCatalog.AllowedCodes)
        {
            Assert.True(_validator.Validate(new KeyInputEvent(code, InputAction.Down)).IsValid);
            Assert.True(_validator.Validate(new KeyInputEvent(code, InputAction.Up)).IsValid);
        }

        var unknown = _validator.Validate(new KeyInputEvent("F24", InputAction.Down));
        Assert.False(unknown.IsValid);
        Assert.Equal(InputValidationFailureReason.DisallowedKeyCode, unknown.Reason);

        var empty = _validator.Validate(new KeyInputEvent(string.Empty, InputAction.Down));
        Assert.False(empty.IsValid);
        Assert.Equal(InputValidationFailureReason.DisallowedKeyCode, empty.Reason);

        var overlong = _validator.Validate(new KeyInputEvent(new string('K', 64), InputAction.Down));
        Assert.False(overlong.IsValid);
        Assert.Equal(InputValidationFailureReason.DisallowedKeyCode, overlong.Reason);
    }

    [Theory]
    [InlineData(0.0, 25.0)]
    [InlineData(0.0, -25.0)]
    [InlineData(18.5, 0.0)]
    [InlineData(-0.125, 0.25)]
    [InlineData(10_000.0, -10_000.0)]
    public void AcceptsSafeBoundedWheelDeltas(double dx, double dy)
    {
        Assert.True(_validator.Validate(new PointerWheelEvent(dx, dy)).IsValid);
    }

    [Theory]
    [InlineData(double.NaN, 0.0, InputValidationFailureReason.InvalidWheelDelta)]
    [InlineData(0.0, double.PositiveInfinity, InputValidationFailureReason.InvalidWheelDelta)]
    [InlineData(0.0, double.NegativeInfinity, InputValidationFailureReason.InvalidWheelDelta)]
    [InlineData(10_001.0, 0.0, InputValidationFailureReason.WheelDeltaOutOfBounds)]
    [InlineData(0.0, -10_001.0, InputValidationFailureReason.WheelDeltaOutOfBounds)]
    [InlineData(1e15, 0.0, InputValidationFailureReason.WheelDeltaOutOfBounds)]
    public void RejectsNonFiniteAndExcessivelyLargeWheelDeltas(
        double dx,
        double dy,
        InputValidationFailureReason expectedReason)
    {
        var result = _validator.Validate(new PointerWheelEvent(dx, dy));
        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void WheelAccumulatorHandlesPositiveNegativeHorizontalSmallFractionalAndReset()
    {
        var accumulator = new WheelDeltaAccumulator(new WheelConversionOptions
        {
            WheelUnitsPerCssPixel = 1.0,
            MaxWheelUnitsPerBatch = 500,
        });

        // Positive vertical scroll
        var up = accumulator.Accumulate(new PointerWheelEvent(0.0, 40.0));
        Assert.Equal(40, up.VerticalUnits);
        Assert.Equal(0, up.HorizontalUnits);

        // Negative vertical scroll
        var down = accumulator.Accumulate(new PointerWheelEvent(0.0, -25.0));
        Assert.Equal(-25, down.VerticalUnits);
        Assert.Equal(0, down.HorizontalUnits);

        // Horizontal scrolling
        var horiz = accumulator.Accumulate(new PointerWheelEvent(-12.0, 0.0));
        Assert.Equal(0, horiz.VerticalUnits);
        Assert.Equal(-12, horiz.HorizontalUnits);

        // Small fractional deltas accumulate without being dropped
        var f1 = accumulator.Accumulate(new PointerWheelEvent(0.25, 0.4));
        var f2 = accumulator.Accumulate(new PointerWheelEvent(0.25, 0.4));
        var f3 = accumulator.Accumulate(new PointerWheelEvent(0.5, 0.4));
        Assert.Equal(0, f1.VerticalUnits);
        Assert.Equal(0, f2.VerticalUnits);
        Assert.Equal(1, f3.VerticalUnits);
        Assert.Equal(0.2, f3.RemainingVerticalUnits, precision: 6);
        Assert.Equal(1, f3.HorizontalUnits);
        Assert.Equal(0.0, f3.RemainingHorizontalUnits, precision: 6);

        // Reset clears accumulated remainders
        accumulator.Reset();
        var afterReset = accumulator.Accumulate(new PointerWheelEvent(0.0, 0.8));
        Assert.Equal(0, afterReset.VerticalUnits);
        Assert.Equal(0.8, afterReset.RemainingVerticalUnits, precision: 6);
    }

    [Fact]
    public void ValidationAndAuthorizationRemainStrictlySeparate()
    {
        var evaluator = new InputPermissionEvaluator(_validator);
        var viewOnlyAuth = new SessionAuthorizationContext(SessionState.ActiveViewOnly, PermissionMode.ViewOnly, IsConnectionAuthenticated: true);
        var validKey = new KeyInputEvent("KeyA", InputAction.Down);

        // Syntactically valid command...
        Assert.True(_validator.Validate(validKey).IsValid);
        // ...is still unauthorized under View-Only!
        var authDecision = evaluator.Authorize(viewOnlyAuth, validKey);
        Assert.False(authDecision.Allowed);
        Assert.Equal(InputRejectionReason.ViewOnlyMode, authDecision.Reason);
    }
}
