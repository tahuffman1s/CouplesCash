using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CouplesCash.Api.Auth;

namespace CouplesCash.Api.Tests;

public partial class CoupleCodeServiceTests
{
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$")]
    private static partial Regex CrockfordCode();

    [Fact]
    public void Generate_produces_formatted_crockford_code_and_prefixed_payload()
    {
        var generated = new CoupleCodeService().Generate();

        Assert.Matches(CrockfordCode(), generated.Code);
        Assert.StartsWith("cc1.", generated.Payload, StringComparison.Ordinal);

        var bytes = Base64Url.DecodeFromChars(generated.Payload.AsSpan(4));
        Assert.Equal(20, bytes.Length);
        Assert.Equal(32, generated.PayloadHash.Length);
        Assert.Equal(32, generated.CodeHash.Length);
    }

    [Fact]
    public void Code_is_first_40_bits_of_sha256_of_payload_bytes()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)(i * 7)).ToArray();
        var generated = CoupleCodeService.FromBytes(bytes);

        var hash = SHA256.HashData(bytes);
        var expectedCode = CoupleCodeService.EncodeCode(hash.AsSpan(0, 5));

        Assert.Equal(CoupleCodeService.FormatCode(expectedCode), generated.Code);
        Assert.Equal(hash, generated.PayloadHash);
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(expectedCode)), generated.CodeHash);
    }

    [Fact]
    public void EncodeCode_known_vectors()
    {
        Assert.Equal("00000000", CoupleCodeService.EncodeCode(new byte[5]));
        Assert.Equal("ZZZZZZZZ", CoupleCodeService.EncodeCode([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal("00000001", CoupleCodeService.EncodeCode([0, 0, 0, 0, 1]));
        Assert.Equal("10000000", CoupleCodeService.EncodeCode([0x08, 0, 0, 0, 0]));
    }

    [Fact]
    public void Lookup_hash_for_payload_matches_stored_payload_hash()
    {
        var generated = new CoupleCodeService().Generate();

        Assert.True(CoupleCodeService.TryComputeLookupHash(generated.Payload, out var hash));
        Assert.Equal(generated.PayloadHash, hash);

        Assert.True(CoupleCodeService.TryComputeLookupHash("  " + generated.Payload.ToUpperInvariant()[..4] + generated.Payload[4..] + " ", out var hash2));
        Assert.Equal(generated.PayloadHash, hash2);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{1}")]
    [InlineData(" {0} ")]
    public void Lookup_hash_for_typed_code_matches_stored_code_hash_in_any_form(string template)
    {
        var generated = new CoupleCodeService().Generate();
        var withDash = generated.Code;
        var lower = generated.Code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var input = string.Format(null, template, withDash, lower);

        Assert.True(CoupleCodeService.TryComputeLookupHash(input, out var hash));
        Assert.Equal(generated.CodeHash, hash);
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData("ABCD EFGH", "ABCDEFGH")]
    [InlineData("abcdefgh", "ABCDEFGH")]
    [InlineData("0O1I-1LAB", "00111 1AB")]
    public void Normalize_uppercases_strips_separators_and_maps_crockford_aliases(string input, string expected)
    {
        Assert.True(CoupleCodeService.TryNormalizeCode(input, out var code));
        Assert.Equal(expected.Replace(" ", "", StringComparison.Ordinal), code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("ABCD-EFGH-J")]
    [InlineData("ABCU-EFGH")]   // U is not in the Crockford alphabet
    [InlineData("ABC*-EFGH")]
    [InlineData("cc1.not-base64url!!")]
    [InlineData("cc1.AAAA")]    // wrong length
    public void Invalid_inputs_are_rejected(string input)
    {
        Assert.False(CoupleCodeService.TryComputeLookupHash(input, out _));
    }

    [Fact]
    public void Codes_are_unique_across_generations()
    {
        var service = new CoupleCodeService();
        var codes = Enumerable.Range(0, 200).Select(_ => service.Generate().Code).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, codes.Count);
    }
}
