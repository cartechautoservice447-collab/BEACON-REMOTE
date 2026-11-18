namespace Beacon.Companion.Core.Protocol;

/// <summary>
/// Exact W3C <c>KeyboardEvent.code</c> allowlist from <c>src/lib/remote/keys.ts</c> (<c>KEY_CATALOG</c>).
/// Unknown or uncatalogued codes are rejected by Core permission validation.
/// </summary>
public static class KeyCodeCatalog
{
    public static IReadOnlySet<string> AllowedCodes { get; } = BuildAllowedCodes();

    public static IReadOnlySet<string> ModifierCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ShiftLeft",
        "ControlLeft",
        "AltLeft",
        "MetaLeft",
    };

    public static IReadOnlySet<string> LockCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CapsLock",
    };

    public static bool IsAllowed(string? code) =>
        !string.IsNullOrEmpty(code) && AllowedCodes.Contains(code);

    public static bool IsModifier(string? code) =>
        !string.IsNullOrEmpty(code) && ModifierCodes.Contains(code);

    public static bool IsLock(string? code) =>
        !string.IsNullOrEmpty(code) && LockCodes.Contains(code);

    private static HashSet<string> BuildAllowedCodes()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        for (var c = 'A'; c <= 'Z'; c++)
        {
            set.Add($"Key{c}");
        }

        for (var d = 0; d <= 9; d++)
        {
            set.Add($"Digit{d}");
        }

        for (var f = 1; f <= 12; f++)
        {
            set.Add($"F{f}");
        }

        string[] namedKeys =
        [
            "Escape", "Tab", "CapsLock",
            "ShiftLeft", "ControlLeft", "AltLeft", "MetaLeft",
            "Space", "Enter", "Backspace", "Delete", "Insert",
            "Home", "End", "PageUp", "PageDown",
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "PrintScreen",
            "Minus", "Equal", "BracketLeft", "BracketRight",
            "Backslash", "Semicolon", "Quote", "Comma", "Period", "Slash", "Backquote",
        ];

        foreach (var code in namedKeys)
        {
            set.Add(code);
        }

        return set;
    }
}
