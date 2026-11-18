using Beacon.Companion.Core.Pairing;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class QrAndOriginValidationTests
{
    [Theory]
    [InlineData("https://beacon.example.com", "https://beacon.example.com")]
    [InlineData("HTTPS://BEACON.EXAMPLE.COM:443/", "https://beacon.example.com")]
    [InlineData("https://beacon.example.com:8443", "https://beacon.example.com:8443")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000/", "http://127.0.0.1:3000")]
    public void ValidatesAndNormalizesAllowedServerOrigins(string rawOrigin, string expectedCanonical)
    {
        var result = ServerOriginValidator.Validate(rawOrigin, allowDevelopmentLoopbackHttp: true);
        Assert.True(result.IsValid);
        Assert.NotNull(result.CanonicalOrigin);
        Assert.Equal(expectedCanonical, result.CanonicalOrigin!.GetLeftPart(UriPartial.Authority));
    }

    [Theory]
    [InlineData("", ServerOriginValidationError.NullOrEmpty)]
    [InlineData("   ", ServerOriginValidationError.NullOrEmpty)]
    [InlineData("/relative/path", ServerOriginValidationError.NotAbsoluteUri)]
    [InlineData("ws://beacon.example.com", ServerOriginValidationError.InvalidScheme)]
    [InlineData("http://beacon.example.com", ServerOriginValidationError.InsecureHttpNotLoopback)]
    [InlineData("https://user:password@beacon.example.com", ServerOriginValidationError.ContainsUserInfo)]
    [InlineData("https://beacon.example.com/api/public", ServerOriginValidationError.ContainsNonRootPath)]
    [InlineData("https://beacon.example.com?foo=bar", ServerOriginValidationError.ContainsQueryOrFragment)]
    [InlineData("https://beacon.example.com#section", ServerOriginValidationError.ContainsQueryOrFragment)]
    [InlineData("https://beacon.example.com?", ServerOriginValidationError.ContainsQueryOrFragment)]
    [InlineData("https://0.0.0.0:8443", ServerOriginValidationError.UnspecifiedOrLinkLocalAddress)]
    [InlineData("https://169.254.169.254", ServerOriginValidationError.UnspecifiedOrLinkLocalAddress)]
    public void RejectsMaliciousOrMalformedServerOrigins(
        string rawOrigin,
        ServerOriginValidationError expectedError)
    {
        var result = ServerOriginValidator.Validate(rawOrigin, allowDevelopmentLoopbackHttp: true);
        Assert.False(result.IsValid);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void RejectsLocalhostHttpWhenDevelopmentLoopbackIsDisabled()
    {
        var result = ServerOriginValidator.Validate("http://localhost:3000", allowDevelopmentLoopbackHttp: false);
        Assert.False(result.IsValid);
        Assert.Equal(ServerOriginValidationError.InsecureHttpNotLoopback, result.Error);
    }

    [Fact]
    public void OriginTrustPolicyRequiresExplicitApprovalForCustomQrOrigin()
    {
        var policy = new DefaultServerOriginTrustPolicy();
        var configured = new Uri("https://beacon.example.com");
        var matching = new Uri("https://BEACON.example.com/");
        var custom = new Uri("https://custom-beacon.example.org");

        var sameDecision = policy.Evaluate(matching, configured, OriginApprovalMode.ConfiguredOriginOnly);
        Assert.True(sameDecision.IsTrusted);
        Assert.False(sameDecision.IsCustomOrigin);

        var unapprovedCustom = policy.Evaluate(custom, configured, OriginApprovalMode.ConfiguredOriginOnly);
        Assert.False(unapprovedCustom.IsTrusted);
        Assert.True(unapprovedCustom.IsCustomOrigin);

        var mismatchedApproved = policy.Evaluate(
            custom,
            configured,
            OriginApprovalMode.ExplicitlyApprovedCustomOrigin,
            explicitlyApprovedCustomOrigin: new Uri("https://other.example.org"));
        Assert.False(mismatchedApproved.IsTrusted);

        var approvedCustom = policy.Evaluate(
            custom,
            configured,
            OriginApprovalMode.ExplicitlyApprovedCustomOrigin,
            explicitlyApprovedCustomOrigin: custom);
        Assert.True(approvedCustom.IsTrusted);
        Assert.True(approvedCustom.IsCustomOrigin);
    }

    [Fact]
    public void QrValidatorAcceptsValidPayloadRedactsTokenAndClearsOnDispose()
    {
        const string qrJson = """
            {
              "v": 1,
              "type": "beacon-pair",
              "server": "https://beacon.example.com",
              "pairing_id": "550e8400-e29b-41d4-a716-446655440000",
              "token": "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"
            }
            """;

        var result = QrPairingPayloadValidator.Validate(qrJson);
        Assert.True(result.IsValid);
        Assert.NotNull(result.Payload);

        using var payload = result.Payload!;
        Assert.Equal(1, payload.Version);
        Assert.Equal(new Uri("https://beacon.example.com"), payload.CanonicalServerOrigin);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), payload.PairingId);
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01", payload.Token.ToString());
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01", payload.ToString(), StringComparison.Ordinal);

        payload.Dispose();
        Assert.True(payload.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = payload.Token.Length);
    }

    [Theory]
    [InlineData("", QrValidationFailureReason.EmptyPayload)]
    [InlineData("{bad", QrValidationFailureReason.MalformedJson)]
    [InlineData("""{"v":2,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.UnsupportedVersion)]
    [InlineData("""{"v":1,"type":"wrong","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.InvalidTypeDiscriminator)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"http://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.InsecureServerOrigin)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://user:pw@beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.InvalidServerOrigin)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://beacon.example.com/subpath","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.InvalidServerOrigin)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"not-a-guid","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""", QrValidationFailureReason.InvalidPairingId)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"too-short"}""", QrValidationFailureReason.InvalidTokenFormat)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"invalid+base64/chars=="}""", QrValidationFailureReason.InvalidTokenFormat)]
    [InlineData("""{"v":1,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01","extra":1}""", QrValidationFailureReason.MalformedJson)]
    public void QrValidatorRejectsInvalidPayloads(
        string qrJson,
        QrValidationFailureReason expectedReason)
    {
        var result = QrPairingPayloadValidator.Validate(qrJson);
        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.FailureReason);
    }
}
