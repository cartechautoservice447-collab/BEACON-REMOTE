using System.Text;
using System.Text.Json;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Pairing;

public enum QrValidationFailureReason
{
    None = 0,
    EmptyPayload = 1,
    PayloadTooLarge = 2,
    MalformedJson = 3,
    MissingRequiredField = 4,
    InvalidPropertyType = 5,
    UnsupportedVersion = 6,
    InvalidTypeDiscriminator = 7,
    InvalidServerOrigin = 8,
    InsecureServerOrigin = 9,
    InvalidPairingId = 10,
    InvalidTokenFormat = 11,
}

/// <summary>
/// Validated QR pairing payload holding the one-time pairing token inside a pinned <see cref="SensitiveCharBuffer"/>.
/// Must be disposed after the pairing attempt completes.
/// </summary>
public sealed class ValidatedQrPairingPayload : IDisposable
{
    private readonly SensitiveCharBuffer _tokenBuffer;

    internal ValidatedQrPairingPayload(
        int version,
        Uri canonicalServerOrigin,
        Guid pairingId,
        SensitiveCharBuffer tokenBuffer)
    {
        Version = version;
        CanonicalServerOrigin = canonicalServerOrigin;
        PairingId = pairingId;
        _tokenBuffer = tokenBuffer;
    }

    public int Version { get; }
    public Uri CanonicalServerOrigin { get; }
    public Guid PairingId { get; }
    public bool IsDisposed => _tokenBuffer.IsDisposed;
    public ReadOnlySpan<char> Token => _tokenBuffer.Span;

    public override string ToString() =>
        $"ValidatedQrPairingPayload {{ Version = {Version}, Server = {CanonicalServerOrigin}, PairingId = {PairingId}, Token = [REDACTED] }}";

    public void Dispose() => _tokenBuffer.Dispose();
}

public sealed record QrValidationResult(
    bool IsValid,
    ValidatedQrPairingPayload? Payload = null,
    QrValidationFailureReason FailureReason = QrValidationFailureReason.None,
    string? ErrorDetail = null)
{
    public static QrValidationResult Success(ValidatedQrPairingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(true, payload);
    }

    public static QrValidationResult Failure(QrValidationFailureReason reason, string detail) =>
        new(false, null, reason, detail);
}

/// <summary>
/// Strictly validates QR pairing JSON payloads matching <c>src/routes/_authenticated/pair.tsx</c>:
/// <c>{ "v": 1, "type": "beacon-pair", "server": "https://...", "pairing_id": "UUID", "token": "BASE64URL" }</c>.
/// Extracts the one-time token directly from UTF-8 bytes into a pinned <see cref="SensitiveCharBuffer"/>.
/// </summary>
public static class QrPairingPayloadValidator
{
    public const int ExpectedVersion = 1;
    public const string ExpectedType = "beacon-pair";
    public const int MaxPayloadBytes = 1024;
    public const int MinTokenLength = 20;
    public const int MaxTokenLength = 128;

    public static QrValidationResult Validate(
        string? rawQrJson,
        bool allowDevelopmentLoopbackHttp = true)
    {
        if (string.IsNullOrWhiteSpace(rawQrJson))
        {
            return QrValidationResult.Failure(
                QrValidationFailureReason.EmptyPayload,
                "QR payload must not be empty.");
        }

        if (rawQrJson.Length > MaxPayloadBytes)
        {
            return QrValidationResult.Failure(
                QrValidationFailureReason.PayloadTooLarge,
                $"QR payload exceeds maximum length of {MaxPayloadBytes} bytes.");
        }

        var byteCount = Encoding.UTF8.GetByteCount(rawQrJson);
        if (byteCount > MaxPayloadBytes)
        {
            return QrValidationResult.Failure(
                QrValidationFailureReason.PayloadTooLarge,
                $"QR payload size ({byteCount} bytes) exceeds maximum allowed {MaxPayloadBytes} bytes.");
        }

        var utf8Buffer = GC.AllocateArray<byte>(byteCount, pinned: true);
        try
        {
            _ = Encoding.UTF8.GetBytes(rawQrJson, utf8Buffer);
            return Validate(utf8Buffer.AsSpan(), allowDevelopmentLoopbackHttp);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(utf8Buffer);
        }
    }

    public static QrValidationResult Validate(
        ReadOnlySpan<byte> utf8QrJson,
        bool allowDevelopmentLoopbackHttp = true)
    {
        if (utf8QrJson.IsEmpty || IsAsciiWhitespaceOnly(utf8QrJson))
        {
            return QrValidationResult.Failure(
                QrValidationFailureReason.EmptyPayload,
                "QR payload must not be empty.");
        }

        if (utf8QrJson.Length > MaxPayloadBytes)
        {
            return QrValidationResult.Failure(
                QrValidationFailureReason.PayloadTooLarge,
                $"QR payload size ({utf8QrJson.Length} bytes) exceeds maximum allowed {MaxPayloadBytes} bytes.");
        }

        var hasVersion = false;
        var version = 0;
        var hasType = false;
        string? typeStr = null;
        var hasServer = false;
        string? serverStr = null;
        var hasPairingId = false;
        string? pairingIdStr = null;
        SensitiveCharBuffer? tokenBuffer = null;

        try
        {
            var reader = new Utf8JsonReader(utf8QrJson, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return QrValidationResult.Failure(
                    QrValidationFailureReason.MalformedJson,
                    "QR payload root must be a JSON object.");
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    tokenBuffer?.Dispose();
                    return QrValidationResult.Failure(
                        QrValidationFailureReason.MalformedJson,
                        "Unexpected token inside QR JSON object.");
                }

                if (reader.ValueTextEquals("v"u8))
                {
                    if (hasVersion)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, "Duplicate 'v' property.");
                    }

                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out version))
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(
                            QrValidationFailureReason.InvalidPropertyType,
                            "QR property 'v' must be an integer.");
                    }

                    hasVersion = true;
                }
                else if (reader.ValueTextEquals("type"u8))
                {
                    if (hasType)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, "Duplicate 'type' property.");
                    }

                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(
                            QrValidationFailureReason.InvalidPropertyType,
                            "QR property 'type' must be a string.");
                    }

                    typeStr = reader.GetString();
                    hasType = true;
                }
                else if (reader.ValueTextEquals("server"u8))
                {
                    if (hasServer)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, "Duplicate 'server' property.");
                    }

                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(
                            QrValidationFailureReason.InvalidPropertyType,
                            "QR property 'server' must be a string.");
                    }

                    serverStr = reader.GetString();
                    hasServer = true;
                }
                else if (reader.ValueTextEquals("pairing_id"u8))
                {
                    if (hasPairingId)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, "Duplicate 'pairing_id' property.");
                    }

                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        tokenBuffer?.Dispose();
                        return QrValidationResult.Failure(
                            QrValidationFailureReason.InvalidPropertyType,
                            "QR property 'pairing_id' must be a UUID string.");
                    }

                    pairingIdStr = reader.GetString();
                    hasPairingId = true;
                }
                else if (reader.ValueTextEquals("token"u8))
                {
                    if (tokenBuffer is not null)
                    {
                        tokenBuffer.Dispose();
                        return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, "Duplicate 'token' property.");
                    }

                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return QrValidationResult.Failure(
                            QrValidationFailureReason.InvalidPropertyType,
                            "QR property 'token' must be a string.");
                    }

                    if (reader.HasValueSequence || reader.ValueSpan.IsEmpty)
                    {
                        var fallbackStr = reader.GetString();
                        if (string.IsNullOrEmpty(fallbackStr))
                        {
                            return QrValidationResult.Failure(
                                QrValidationFailureReason.InvalidTokenFormat,
                                "QR 'token' must not be empty.");
                        }

                        tokenBuffer = SensitiveCharBuffer.FromChars(fallbackStr.AsSpan());
                    }
                    else
                    {
                        tokenBuffer = SensitiveCharBuffer.FromUtf8Bytes(reader.ValueSpan);
                    }
                }
                else
                {
                    // Reject unknown unexpected properties in QR payloads to keep the QR contract strict.
                    tokenBuffer?.Dispose();
                    return QrValidationResult.Failure(
                        QrValidationFailureReason.MalformedJson,
                        "Unexpected property in QR pairing payload.");
                }
            }

            if (reader.BytesConsumed < utf8QrJson.Length &&
                !IsAsciiWhitespaceOnly(utf8QrJson[(int)reader.BytesConsumed..]))
            {
                tokenBuffer?.Dispose();
                return QrValidationResult.Failure(
                    QrValidationFailureReason.MalformedJson,
                    "Unexpected trailing bytes after QR JSON payload.");
            }
        }
        catch (JsonException ex)
        {
            tokenBuffer?.Dispose();
            return QrValidationResult.Failure(QrValidationFailureReason.MalformedJson, ex.Message);
        }

        if (!hasVersion || !hasType || !hasServer || !hasPairingId || tokenBuffer is null)
        {
            tokenBuffer?.Dispose();
            return QrValidationResult.Failure(
                QrValidationFailureReason.MissingRequiredField,
                "QR payload is missing one or more required fields ('v', 'type', 'server', 'pairing_id', 'token').");
        }

        if (!string.Equals(typeStr, ExpectedType, StringComparison.Ordinal))
        {
            tokenBuffer.Dispose();
            return QrValidationResult.Failure(
                QrValidationFailureReason.InvalidTypeDiscriminator,
                $"QR payload type must be '{ExpectedType}'.");
        }

        if (version != ExpectedVersion)
        {
            tokenBuffer.Dispose();
            return QrValidationResult.Failure(
                QrValidationFailureReason.UnsupportedVersion,
                $"Unsupported QR payload version '{version}'. Expected version {ExpectedVersion}.");
        }

        if (string.IsNullOrWhiteSpace(pairingIdStr) ||
            !Guid.TryParseExact(pairingIdStr, "D", out var pairingId) ||
            pairingId == Guid.Empty)
        {
            tokenBuffer.Dispose();
            return QrValidationResult.Failure(
                QrValidationFailureReason.InvalidPairingId,
                "QR 'pairing_id' must be a valid non-empty canonical UUID.");
        }

        if (!IsValidBase64UrlToken(tokenBuffer.Span))
        {
            tokenBuffer.Dispose();
            return QrValidationResult.Failure(
                QrValidationFailureReason.InvalidTokenFormat,
                $"QR 'token' must be {MinTokenLength}..{MaxTokenLength} URL-safe Base64URL characters.");
        }

        var originValidation = ServerOriginValidator.Validate(serverStr, allowDevelopmentLoopbackHttp);
        if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
        {
            tokenBuffer.Dispose();
            var reason = originValidation.Error == ServerOriginValidationError.InsecureHttpNotLoopback
                ? QrValidationFailureReason.InsecureServerOrigin
                : QrValidationFailureReason.InvalidServerOrigin;
            return QrValidationResult.Failure(
                reason,
                originValidation.ErrorDetail ?? "QR server origin is invalid.");
        }

        return QrValidationResult.Success(new ValidatedQrPairingPayload(
            version,
            originValidation.CanonicalOrigin,
            pairingId,
            tokenBuffer));
    }

    public static bool IsValidBase64UrlToken(ReadOnlySpan<char> token)
    {
        if (token.Length is < MinTokenLength or > MaxTokenLength)
        {
            return false;
        }

        foreach (var ch in token)
        {
            var isValid = ch is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '-'
                or '_';
            if (!isValid)
            {
                return false;
            }
        }

        return true;
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
