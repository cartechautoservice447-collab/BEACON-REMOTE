using System.Text;
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
/// See <see cref="WheelDeltaAccumulator"/> and <see cref="ProtocolCompatibilityNotes"/> for the explicit conversion boundary.
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

public enum ProtocolParseFailureReason
{
    None = 0,
    EmptyPayload = 1,
    PayloadTooLarge = 2,
    MalformedJson = 3,
    InvalidRootType = 4,
    MissingDiscriminator = 5,
    UnknownEventType = 6,
    MissingRequiredField = 7,
    InvalidPropertyType = 8,
    InvalidEnumValue = 9,
}

public sealed record ProtocolParseResult(
    bool Succeeded,
    RemoteInputEvent? Command = null,
    ProtocolParseFailureReason FailureReason = ProtocolParseFailureReason.None,
    string? ErrorDetail = null)
{
    public static ProtocolParseResult Success(RemoteInputEvent command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new(true, command);
    }

    public static ProtocolParseResult Failure(ProtocolParseFailureReason reason, string detail) =>
        new(false, null, reason, detail);
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

/// <summary>
/// Zero-reflection, bounded-size JSON wire serializer and strict schema parser for <see cref="RemoteInputEvent"/>.
/// </summary>
public static class RemoteProtocolSerializer
{
    public const int DefaultMaxMessageBytes = 2048;
    public const int MaxJsonDepth = 8;

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxJsonDepth,
    };

    public static string Serialize(RemoteInputEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, RemoteProtocolJsonContext.Default.RemoteInputEvent);
    }

    public static byte[] SerializeToUtf8Bytes(RemoteInputEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, RemoteProtocolJsonContext.Default.RemoteInputEvent);
    }

    public static RemoteInputEvent Deserialize(string json, int maxMessageBytes = DefaultMaxMessageBytes)
    {
        var result = TryDeserialize(json, maxMessageBytes);
        if (!result.Succeeded || result.Command is null)
        {
            throw new JsonException(result.ErrorDetail ?? "Invalid remote input protocol payload.");
        }

        return result.Command;
    }

    public static ProtocolParseResult TryDeserialize(string? json, int maxMessageBytes = DefaultMaxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);

        if (string.IsNullOrWhiteSpace(json))
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.EmptyPayload,
                "Wire message must not be empty.");
        }

        // Fast upper-bound UTF-8 guard before encoding the full string.
        if (json.Length > maxMessageBytes)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.PayloadTooLarge,
                $"Wire message length exceeds maximum allowed {maxMessageBytes} UTF-8 bytes.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > maxMessageBytes)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.PayloadTooLarge,
                $"Wire message size ({byteCount} bytes) exceeds maximum allowed {maxMessageBytes} bytes.");
        }

        byte[] utf8Buffer = new byte[byteCount];
        _ = Encoding.UTF8.GetBytes(json, utf8Buffer);
        return TryDeserialize(utf8Buffer.AsSpan(), maxMessageBytes);
    }

    public static ProtocolParseResult TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        int maxMessageBytes = DefaultMaxMessageBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageBytes);

        if (utf8Json.IsEmpty || IsAsciiWhitespaceOnly(utf8Json))
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.EmptyPayload,
                "Wire message must not be empty.");
        }

        if (utf8Json.Length > maxMessageBytes)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.PayloadTooLarge,
                $"Wire message size ({utf8Json.Length} bytes) exceeds maximum allowed {maxMessageBytes} bytes.");
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = StrictDocumentOptions.AllowTrailingCommas,
                CommentHandling = StrictDocumentOptions.CommentHandling,
                MaxDepth = StrictDocumentOptions.MaxDepth,
            });
            using var document = JsonDocument.ParseValue(ref reader);

            // Reject trailing non-whitespace bytes after the root JSON value.
            if (reader.BytesConsumed < utf8Json.Length &&
                !IsAsciiWhitespaceOnly(utf8Json[(int)reader.BytesConsumed..]))
            {
                return ProtocolParseResult.Failure(
                    ProtocolParseFailureReason.MalformedJson,
                    "Unexpected trailing content after JSON root value.");
            }

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ProtocolParseResult.Failure(
                    ProtocolParseFailureReason.InvalidRootType,
                    "Wire message root must be a JSON object.");
            }

            return ParseEventObject(root);
        }
        catch (JsonException ex)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MalformedJson,
                ex.Message);
        }
    }

    private static ProtocolParseResult ParseEventObject(JsonElement root)
    {
        string? eventType = null;
        var hasType = false;

        JsonElement xElem = default;
        var hasX = false;
        JsonElement yElem = default;
        var hasY = false;
        JsonElement dxElem = default;
        var hasDx = false;
        JsonElement dyElem = default;
        var hasDy = false;
        JsonElement buttonElem = default;
        var hasButton = false;
        JsonElement actionElem = default;
        var hasAction = false;
        JsonElement codeElem = default;
        var hasCode = false;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("type"))
            {
                if (hasType)
                {
                    return ProtocolParseResult.Failure(
                        ProtocolParseFailureReason.MalformedJson,
                        "Duplicate 'type' discriminator property.");
                }

                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    return ProtocolParseResult.Failure(
                        ProtocolParseFailureReason.InvalidPropertyType,
                        "Property 'type' must be a string.");
                }

                eventType = prop.Value.GetString();
                hasType = true;
            }
            else if (prop.NameEquals("x"))
            {
                if (hasX)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'x' property.");
                }

                xElem = prop.Value;
                hasX = true;
            }
            else if (prop.NameEquals("y"))
            {
                if (hasY)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'y' property.");
                }

                yElem = prop.Value;
                hasY = true;
            }
            else if (prop.NameEquals("dx"))
            {
                if (hasDx)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'dx' property.");
                }

                dxElem = prop.Value;
                hasDx = true;
            }
            else if (prop.NameEquals("dy"))
            {
                if (hasDy)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'dy' property.");
                }

                dyElem = prop.Value;
                hasDy = true;
            }
            else if (prop.NameEquals("button"))
            {
                if (hasButton)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'button' property.");
                }

                buttonElem = prop.Value;
                hasButton = true;
            }
            else if (prop.NameEquals("action"))
            {
                if (hasAction)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'action' property.");
                }

                actionElem = prop.Value;
                hasAction = true;
            }
            else if (prop.NameEquals("code"))
            {
                if (hasCode)
                {
                    return ProtocolParseResult.Failure(ProtocolParseFailureReason.MalformedJson, "Duplicate 'code' property.");
                }

                codeElem = prop.Value;
                hasCode = true;
            }
        }

        if (!hasType || string.IsNullOrEmpty(eventType))
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MissingDiscriminator,
                "Missing required 'type' discriminator property.");
        }

        return eventType switch
        {
            "pointer.move.abs" => ParsePointerMoveAbs(hasX, xElem, hasY, yElem),
            "pointer.button" => ParsePointerButton(hasButton, buttonElem, hasAction, actionElem, hasX, xElem, hasY, yElem),
            "pointer.wheel" => ParsePointerWheel(hasDx, dxElem, hasDy, dyElem),
            "key" => ParseKey(hasCode, codeElem, hasAction, actionElem),
            "input.reset" => ProtocolParseResult.Success(new InputResetEvent()),
            _ => ProtocolParseResult.Failure(
                ProtocolParseFailureReason.UnknownEventType,
                $"Unknown remote input event type '{eventType}'."),
        };
    }

    private static ProtocolParseResult ParsePointerMoveAbs(
        bool hasX,
        JsonElement xElem,
        bool hasY,
        JsonElement yElem)
    {
        if (!TryReadRequiredDouble(hasX, xElem, "x", out var x, out var errX))
        {
            return errX!;
        }

        if (!TryReadRequiredDouble(hasY, yElem, "y", out var y, out var errY))
        {
            return errY!;
        }

        return ProtocolParseResult.Success(new PointerMoveAbsEvent(x, y));
    }

    private static ProtocolParseResult ParsePointerButton(
        bool hasButton,
        JsonElement buttonElem,
        bool hasAction,
        JsonElement actionElem,
        bool hasX,
        JsonElement xElem,
        bool hasY,
        JsonElement yElem)
    {
        if (!TryReadRequiredButton(hasButton, buttonElem, out var button, out var errButton))
        {
            return errButton!;
        }

        if (!TryReadRequiredAction(hasAction, actionElem, out var action, out var errAction))
        {
            return errAction!;
        }

        if (!TryReadRequiredDouble(hasX, xElem, "x", out var x, out var errX))
        {
            return errX!;
        }

        if (!TryReadRequiredDouble(hasY, yElem, "y", out var y, out var errY))
        {
            return errY!;
        }

        return ProtocolParseResult.Success(new PointerButtonEvent(button, action, x, y));
    }

    private static ProtocolParseResult ParsePointerWheel(
        bool hasDx,
        JsonElement dxElem,
        bool hasDy,
        JsonElement dyElem)
    {
        if (!TryReadRequiredDouble(hasDx, dxElem, "dx", out var dx, out var errDx))
        {
            return errDx!;
        }

        if (!TryReadRequiredDouble(hasDy, dyElem, "dy", out var dy, out var errDy))
        {
            return errDy!;
        }

        return ProtocolParseResult.Success(new PointerWheelEvent(dx, dy));
    }

    private static ProtocolParseResult ParseKey(
        bool hasCode,
        JsonElement codeElem,
        bool hasAction,
        JsonElement actionElem)
    {
        if (!hasCode)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MissingRequiredField,
                "Missing required 'code' property on 'key' event.");
        }

        if (codeElem.ValueKind != JsonValueKind.String)
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.InvalidPropertyType,
                "Property 'code' must be a string.");
        }

        var code = codeElem.GetString();
        if (string.IsNullOrEmpty(code))
        {
            return ProtocolParseResult.Failure(
                ProtocolParseFailureReason.InvalidPropertyType,
                "Property 'code' must be a non-empty string.");
        }

        if (!TryReadRequiredAction(hasAction, actionElem, out var action, out var errAction))
        {
            return errAction!;
        }

        return ProtocolParseResult.Success(new KeyInputEvent(code, action));
    }

    private static bool TryReadRequiredDouble(
        bool present,
        JsonElement elem,
        string name,
        out double value,
        out ProtocolParseResult? error)
    {
        if (!present)
        {
            value = 0;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MissingRequiredField,
                $"Missing required numeric property '{name}'.");
            return false;
        }

        if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetDouble(out value))
        {
            value = 0;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.InvalidPropertyType,
                $"Property '{name}' must be a JSON number.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadRequiredButton(
        bool present,
        JsonElement elem,
        out MouseButton button,
        out ProtocolParseResult? error)
    {
        if (!present)
        {
            button = default;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MissingRequiredField,
                "Missing required 'button' property.");
            return false;
        }

        if (elem.ValueKind != JsonValueKind.String)
        {
            button = default;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.InvalidPropertyType,
                "Property 'button' must be a lowercase string ('left', 'right', or 'middle').");
            return false;
        }

        var raw = elem.GetString();
        switch (raw)
        {
            case "left":
                button = MouseButton.Left;
                error = null;
                return true;
            case "right":
                button = MouseButton.Right;
                error = null;
                return true;
            case "middle":
                button = MouseButton.Middle;
                error = null;
                return true;
            default:
                button = default;
                error = ProtocolParseResult.Failure(
                    ProtocolParseFailureReason.InvalidEnumValue,
                    $"Invalid mouse button '{raw}'. Expected 'left', 'right', or 'middle'.");
                return false;
        }
    }

    private static bool TryReadRequiredAction(
        bool present,
        JsonElement elem,
        out InputAction action,
        out ProtocolParseResult? error)
    {
        if (!present)
        {
            action = default;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.MissingRequiredField,
                "Missing required 'action' property.");
            return false;
        }

        if (elem.ValueKind != JsonValueKind.String)
        {
            action = default;
            error = ProtocolParseResult.Failure(
                ProtocolParseFailureReason.InvalidPropertyType,
                "Property 'action' must be a lowercase string ('down' or 'up').");
            return false;
        }

        var raw = elem.GetString();
        switch (raw)
        {
            case "down":
                action = InputAction.Down;
                error = null;
                return true;
            case "up":
                action = InputAction.Up;
                error = null;
                return true;
            default:
                action = default;
                error = ProtocolParseResult.Failure(
                    ProtocolParseFailureReason.InvalidEnumValue,
                    $"Invalid input action '{raw}'. Expected 'down' or 'up'.");
                return false;
        }
    }

    private static bool IsAsciiWhitespaceOnly(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }
}
