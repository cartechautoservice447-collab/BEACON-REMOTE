using System.Text;
using System.Text.Json;
using Beacon.Companion.Core.Protocol;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class RemoteProtocolContractTests
{
    private static readonly string SharedFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "remote-protocol-contract.json");

    [Fact]
    public void SerializesAndDeserializesAllFiveProtocolEventsMatchingTypeScriptSchema()
    {
        RemoteInputEvent[] events =
        [
            new PointerMoveAbsEvent(0.25, 0.75),
            new PointerButtonEvent(MouseButton.Left, InputAction.Down, 0.5, 0.5),
            new PointerButtonEvent(MouseButton.Right, InputAction.Up, 1.0, 0.0),
            new PointerButtonEvent(MouseButton.Middle, InputAction.Down, 0.0, 1.0),
            new PointerWheelEvent(-12.5, 24.0),
            new KeyInputEvent("KeyA", InputAction.Down),
            new InputResetEvent(),
        ];

        foreach (var ev in events)
        {
            var json = RemoteProtocolSerializer.Serialize(ev);
            var roundTripped = RemoteProtocolSerializer.Deserialize(json);
            Assert.Equal(ev, roundTripped);

            var utf8 = RemoteProtocolSerializer.SerializeToUtf8Bytes(ev);
            var utf8Parsed = RemoteProtocolSerializer.TryDeserialize(utf8);
            Assert.True(utf8Parsed.Succeeded);
            Assert.Equal(ev, utf8Parsed.Command);
        }

        var buttonJson = RemoteProtocolSerializer.Serialize(
            new PointerButtonEvent(MouseButton.Right, InputAction.Down, 0.4, 0.6));
        using var doc = JsonDocument.Parse(buttonJson);
        Assert.Equal("pointer.button", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("right", doc.RootElement.GetProperty("button").GetString());
        Assert.Equal("down", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(0.4, doc.RootElement.GetProperty("x").GetDouble());
        Assert.Equal(0.6, doc.RootElement.GetProperty("y").GetDouble());

        var resetJson = RemoteProtocolSerializer.Serialize(new InputResetEvent());
        using var resetDoc = JsonDocument.Parse(resetJson);
        Assert.Equal("input.reset", resetDoc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void KeyCodeCatalogMatchesAll80FrontendCatalogEntries()
    {
        Assert.Equal(80, KeyCodeCatalog.AllowedCodes.Count);
        Assert.True(KeyCodeCatalog.IsAllowed("KeyA"));
        Assert.True(KeyCodeCatalog.IsAllowed("Digit0"));
        Assert.True(KeyCodeCatalog.IsAllowed("F12"));
        Assert.True(KeyCodeCatalog.IsAllowed("PrintScreen"));
        Assert.True(KeyCodeCatalog.IsModifier("ShiftLeft"));
        Assert.True(KeyCodeCatalog.IsModifier("MetaLeft"));
        Assert.True(KeyCodeCatalog.IsLock("CapsLock"));
        Assert.False(KeyCodeCatalog.IsAllowed("UnknownKey"));
    }

    [Fact]
    public void WheelEventPreservesRawFiniteDeltasWithoutHardcodedUnitConversion()
    {
        const string json = """{"type":"pointer.wheel","dx":-3.5,"dy":18.25}""";
        var parsed = Assert.IsType<PointerWheelEvent>(RemoteProtocolSerializer.Deserialize(json));
        Assert.Equal(-3.5, parsed.Dx);
        Assert.Equal(18.25, parsed.Dy);
        Assert.Contains("scrollSpeed", ProtocolCompatibilityNotes.WheelProducerSemantics, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsOutOfOrderTypeDiscriminatorAndOptionalMetadataWithoutBreakingRequiredFields()
    {
        const string json = """{"x":0.25,"seq":42,"y":0.75,"type":"pointer.move.abs"}""";
        var parsed = Assert.IsType<PointerMoveAbsEvent>(RemoteProtocolSerializer.Deserialize(json));
        Assert.Equal(0.25, parsed.X);
        Assert.Equal(0.75, parsed.Y);
    }

    [Theory]
    [InlineData("", ProtocolParseFailureReason.EmptyPayload)]
    [InlineData("   ", ProtocolParseFailureReason.EmptyPayload)]
    [InlineData("{", ProtocolParseFailureReason.MalformedJson)]
    [InlineData("{\"type\":\"input.reset\",}", ProtocolParseFailureReason.MalformedJson)]
    [InlineData("{\"type\":\"input.reset\"} trailing", ProtocolParseFailureReason.MalformedJson)]
    [InlineData("[]", ProtocolParseFailureReason.InvalidRootType)]
    [InlineData("null", ProtocolParseFailureReason.InvalidRootType)]
    [InlineData("{\"x\":0.5,\"y\":0.5}", ProtocolParseFailureReason.MissingDiscriminator)]
    [InlineData("{\"type\":123}", ProtocolParseFailureReason.InvalidPropertyType)]
    [InlineData("{\"type\":\"pointer.move.rel\",\"dx\":1,\"dy\":2}", ProtocolParseFailureReason.UnknownEventType)]
    [InlineData("{\"type\":\"Pointer.Move.Abs\",\"x\":0.5,\"y\":0.5}", ProtocolParseFailureReason.UnknownEventType)]
    [InlineData("{\"type\":\"pointer.move.abs\",\"x\":0.5}", ProtocolParseFailureReason.MissingRequiredField)]
    [InlineData("{\"type\":\"pointer.button\",\"button\":\"left\",\"action\":\"down\",\"x\":0.5}", ProtocolParseFailureReason.MissingRequiredField)]
    [InlineData("{\"type\":\"pointer.wheel\",\"dx\":1}", ProtocolParseFailureReason.MissingRequiredField)]
    [InlineData("{\"type\":\"key\",\"action\":\"down\"}", ProtocolParseFailureReason.MissingRequiredField)]
    [InlineData("{\"type\":\"pointer.move.abs\",\"x\":\"0.5\",\"y\":0.5}", ProtocolParseFailureReason.InvalidPropertyType)]
    [InlineData("{\"type\":\"pointer.button\",\"button\":0,\"action\":\"down\",\"x\":0.5,\"y\":0.5}", ProtocolParseFailureReason.InvalidPropertyType)]
    [InlineData("{\"type\":\"pointer.button\",\"button\":\"LEFT\",\"action\":\"down\",\"x\":0.5,\"y\":0.5}", ProtocolParseFailureReason.InvalidEnumValue)]
    [InlineData("{\"type\":\"key\",\"code\":\"KeyA\",\"action\":\"UP\"}", ProtocolParseFailureReason.InvalidEnumValue)]
    [InlineData("{\"type\":\"key\",\"code\":65,\"action\":\"down\"}", ProtocolParseFailureReason.InvalidPropertyType)]
    public void RejectsMalformedUnknownMissingAndWrongTypePayloads(
        string payload,
        ProtocolParseFailureReason expectedReason)
    {
        var result = RemoteProtocolSerializer.TryDeserialize(payload);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedReason, result.FailureReason);
        Assert.Throws<JsonException>(() => RemoteProtocolSerializer.Deserialize(payload));
    }

    [Fact]
    public void RejectsOversizedPayloadsBeforeParsing()
    {
        var oversized = $$"""{"type":"key","code":"{{new string('A', 3000)}}","action":"down"}""";
        var result = RemoteProtocolSerializer.TryDeserialize(oversized, maxMessageBytes: 512);

        Assert.False(result.Succeeded);
        Assert.Equal(ProtocolParseFailureReason.PayloadTooLarge, result.FailureReason);

        var utf8 = Encoding.UTF8.GetBytes(oversized);
        var utf8Result = RemoteProtocolSerializer.TryDeserialize(utf8, maxMessageBytes: 512);
        Assert.False(utf8Result.Succeeded);
        Assert.Equal(ProtocolParseFailureReason.PayloadTooLarge, utf8Result.FailureReason);
    }

    [Fact]
    public void SharedCrossLanguageFixtureMatchesCSharpProtocolAndAll80KeyCodes()
    {
        Assert.True(File.Exists(SharedFixturePath), $"Missing shared fixture at '{SharedFixturePath}'.");
        using var fixture = JsonDocument.Parse(File.ReadAllText(SharedFixturePath));
        var root = fixture.RootElement;

        var fixtureKeys = root.GetProperty("allKeyCodes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        Assert.Equal(80, fixtureKeys.Length);
        Assert.Equal(KeyCodeCatalog.AllowedCodes.Count, fixtureKeys.Length);
        foreach (var code in fixtureKeys)
        {
            Assert.True(KeyCodeCatalog.IsAllowed(code), $"Key '{code}' from shared TS fixture missing in C# KeyCodeCatalog.");
        }

        var fixtureModifiers = root.GetProperty("modifierKeyCodes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(KeyCodeCatalog.ModifierCodes.SetEquals(fixtureModifiers));

        var fixtureLocks = root.GetProperty("lockKeyCodes")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(KeyCodeCatalog.LockCodes.SetEquals(fixtureLocks));

        var validator = new InputCommandValidator();

        foreach (var entry in root.GetProperty("validPayloads").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var expectedType = entry.GetProperty("expectedType").GetString()!;

            var parsed = RemoteProtocolSerializer.TryDeserialize(json);
            Assert.True(parsed.Succeeded, $"Fixture '{entry.GetProperty("name").GetString()}' failed parse: {parsed.ErrorDetail}");
            Assert.NotNull(parsed.Command);

            var validation = validator.Validate(parsed.Command!);
            Assert.True(validation.IsValid, $"Fixture '{entry.GetProperty("name").GetString()}' failed validation: {validation.Detail}");

            var roundTrippedJson = RemoteProtocolSerializer.Serialize(parsed.Command!);
            using var roundTrippedDoc = JsonDocument.Parse(roundTrippedJson);
            Assert.Equal(expectedType, roundTrippedDoc.RootElement.GetProperty("type").GetString());
        }

        foreach (var entry in root.GetProperty("invalidWirePayloads").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var stage = entry.GetProperty("stage").GetString()!;

            var parsed = RemoteProtocolSerializer.TryDeserialize(json);
            if (stage == "parse")
            {
                Assert.False(parsed.Succeeded, $"Expected parse failure for '{entry.GetProperty("name").GetString()}'.");
            }
            else
            {
                Assert.True(parsed.Succeeded);
                var validation = validator.Validate(parsed.Command!);
                Assert.False(validation.IsValid, $"Expected validation failure for '{entry.GetProperty("name").GetString()}'.");
            }
        }
    }
}
