using System.Text.Json;
using System.Text.Json.Serialization;

namespace Beacon.Companion.Core.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
public enum MouseButton
{
    [JsonStringEnumMemberName("left")]
    Left = 0,

    [JsonStringEnumMemberName("right")]
    Right = 1,

    [JsonStringEnumMemberName("middle")]
    Middle = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<InputAction>))]
public enum InputAction
{
    [JsonStringEnumMemberName("down")]
    Down = 0,

    [JsonStringEnumMemberName("up")]
    Up = 1,
}

/// <summary>
/// C# wire contract matching <c>src/lib/remote/protocol.ts</c> (<c>RemoteInputEvent</c>) 1:1.
/// Normalized pointer coordinates (<c>x</c>, <c>y</c>) span [0, 1] across the target desktop.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PointerMoveAbsEvent), "pointer.move.abs")]
[JsonDerivedType(typeof(PointerButtonEvent), "pointer.button")]
[JsonDerivedType(typeof(PointerWheelEvent), "pointer.wheel")]
[JsonDerivedType(typeof(KeyInputEvent), "key")]
[JsonDerivedType(typeof(InputResetEvent), "input.reset")]
public abstract record RemoteInputEvent;

public sealed record PointerMoveAbsEvent(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y) : RemoteInputEvent;

public sealed record PointerButtonEvent(
    [property: JsonPropertyName("button")] MouseButton Button,
    [property: JsonPropertyName("action")] InputAction Action,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y) : RemoteInputEvent;

/// <summary>
/// Scroll wheel input event matching <c>{ type: "pointer.wheel", dx, dy }</c>.
/// Compatibility note for Step 3.2/3.6: Current TypeScript producers (<c>MouseEngine</c> and <c>TouchEngine</c>)
/// emit negated gesture pixel deltas scaled by <c>scrollSpeed</c> rather than Win32 <c>WHEEL_DELTA</c> (120) ticks.
/// Core does not assume or rescale wheel units here.
/// </summary>
public sealed record PointerWheelEvent(
    [property: JsonPropertyName("dx")] double Dx,
    [property: JsonPropertyName("dy")] double Dy) : RemoteInputEvent;

public sealed record KeyInputEvent(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("action")] InputAction Action) : RemoteInputEvent;

public sealed record InputResetEvent : RemoteInputEvent;

/// <summary>
/// Documents exact frontend producer behavior observed in <c>src/lib/remote/</c> for Step 3.2+ calibration.
/// </summary>
public static class ProtocolCompatibilityNotes
{
    public const string WheelProducerSemantics =
        "MouseEngine and TouchEngine currently emit negated CSS-pixel deltas multiplied by scrollSpeed; Step 3.2/3.6 must normalize units cleanly rather than assuming Win32 WHEEL_DELTA.";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteInputEvent))]
[JsonSerializable(typeof(PointerMoveAbsEvent))]
[JsonSerializable(typeof(PointerButtonEvent))]
[JsonSerializable(typeof(PointerWheelEvent))]
[JsonSerializable(typeof(KeyInputEvent))]
[JsonSerializable(typeof(InputResetEvent))]
public sealed partial class RemoteProtocolJsonContext : JsonSerializerContext;

public static class RemoteProtocolSerializer
{
    public static string Serialize(RemoteInputEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, RemoteProtocolJsonContext.Default.RemoteInputEvent);
    }

    public static RemoteInputEvent? Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize(json, RemoteProtocolJsonContext.Default.RemoteInputEvent);
    }
}
