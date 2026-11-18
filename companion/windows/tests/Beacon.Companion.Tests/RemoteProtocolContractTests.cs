using System.Text.Json;
using Beacon.Companion.Core.Protocol;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class RemoteProtocolContractTests
{
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
}
