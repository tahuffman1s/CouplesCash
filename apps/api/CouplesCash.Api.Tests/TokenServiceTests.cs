using System.Collections.Concurrent;
using CouplesCash.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CouplesCash.Api.Tests;

public class TokenServiceTests
{
    private static readonly Guid UserId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid HouseholdId = Guid.Parse("018f0000-0000-7000-8000-0000000000aa");

    private readonly JwtOptions _options = new() { Secret = new string('s', 48), AccessMinutes = 15, RefreshDays = 30 };
    // Real "now" (to the second) so tokens validate against the library's wall clock; refresh tests only advance it.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * TimeSpan.TicksPerSecond + DateTime.UnixEpoch.Ticks, TimeSpan.Zero));
    private readonly InMemoryRefreshTokenStore _store = new();
    private readonly TokenService _service;

    public TokenServiceTests()
    {
        _service = new TokenService(Options.Create(_options), _store, _clock, NullLogger<TokenService>.Instance);
    }

    [Fact]
    public async Task Access_token_round_trips_with_contract_claims()
    {
        var token = _service.CreateAccessToken(UserId, "a@b.co", "Alex", HouseholdId);

        Assert.Equal(15 * 60, token.ExpiresInSeconds);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, _options.CreateValidationParameters());
        Assert.True(result.IsValid, result.Exception?.ToString());

        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("couplescash", jwt.Issuer);
        Assert.Contains("couplescash", jwt.Audiences);
        Assert.Equal(UserId.ToString(), jwt.Subject);
        Assert.Equal("a@b.co", jwt.GetClaim("email").Value);
        Assert.Equal("Alex", jwt.GetClaim("name").Value);
        Assert.Equal(HouseholdId.ToString(), jwt.GetClaim("hh").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(15), jwt.ValidTo, TimeSpan.FromSeconds(1));
        Assert.Equal("HS256", jwt.Alg);
    }

    [Fact]
    public async Task Access_token_omits_hh_claim_without_household()
    {
        var token = _service.CreateAccessToken(UserId, "a@b.co", "Alex", null);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, _options.CreateValidationParameters());
        Assert.True(result.IsValid);
        Assert.False(((JsonWebToken)result.SecurityToken).TryGetClaim("hh", out _));
    }

    [Fact]
    public async Task Access_token_signed_with_other_secret_is_rejected()
    {
        var token = _service.CreateAccessToken(UserId, "a@b.co", "Alex", null);
        var other = new JwtOptions { Secret = new string('x', 48) };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, other.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Refresh_token_is_opaque_base64url_and_stored_hashed()
    {
        var issued = await _service.IssueRefreshTokenAsync(UserId, "ua", CancellationToken.None);

        Assert.Equal(43, issued.Token.Length); // 32 bytes, base64url without padding
        Assert.DoesNotContain('=', issued.Token);
        Assert.DoesNotContain('+', issued.Token);
        Assert.DoesNotContain('/', issued.Token);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(30), issued.ExpiresAt);

        var record = Assert.Single(_store.Records.Values);
        Assert.Equal(TokenService.HashToken(issued.Token), record.TokenHash);
        Assert.Equal(issued.FamilyId, record.FamilyId);
        Assert.Equal("ua", record.UserAgent);
        Assert.Null(record.RevokedAt);
    }

    [Fact]
    public async Task Rotate_revokes_old_token_and_keeps_family()
    {
        var first = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(20));

        var rotation = await _service.RotateAsync(first.Token, null, CancellationToken.None);

        Assert.NotNull(rotation);
        Assert.Equal(UserId, rotation.UserId);
        Assert.NotEqual(first.Token, rotation.Refresh.Token);
        Assert.Equal(first.FamilyId, rotation.Refresh.FamilyId);

        var old = _store.Find(first.Token);
        var current = _store.Find(rotation.Refresh.Token);
        Assert.NotNull(old.RevokedAt);
        Assert.Null(current.RevokedAt);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_family()
    {
        var first = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        var second = (await _service.RotateAsync(first.Token, null, CancellationToken.None))!.Refresh;
        var third = (await _service.RotateAsync(second.Token, null, CancellationToken.None))!.Refresh;

        // Attacker (or stale client) replays the first token.
        var replay = await _service.RotateAsync(first.Token, null, CancellationToken.None);

        Assert.Null(replay);
        Assert.NotNull(_store.Find(third.Token).RevokedAt);
        Assert.All(_store.Records.Values.Where(r => r.FamilyId == first.FamilyId), r => Assert.NotNull(r.RevokedAt));

        // The legitimate holder of the newest token is now locked out too.
        Assert.Null(await _service.RotateAsync(third.Token, null, CancellationToken.None));
    }

    [Fact]
    public async Task Reuse_does_not_touch_other_families()
    {
        var phone = await _service.IssueRefreshTokenAsync(UserId, "phone", CancellationToken.None);
        var laptop = await _service.IssueRefreshTokenAsync(UserId, "laptop", CancellationToken.None);
        var phone2 = (await _service.RotateAsync(phone.Token, null, CancellationToken.None))!.Refresh;

        Assert.Null(await _service.RotateAsync(phone.Token, null, CancellationToken.None));

        Assert.NotNull(_store.Find(phone2.Token).RevokedAt);
        Assert.Null(_store.Find(laptop.Token).RevokedAt);
        Assert.NotNull(await _service.RotateAsync(laptop.Token, null, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_and_unknown_tokens_are_rejected()
    {
        var issued = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(31));

        Assert.Null(await _service.RotateAsync(issued.Token, null, CancellationToken.None));
        Assert.Null(await _service.RotateAsync("definitely-not-a-token", null, CancellationToken.None));
        Assert.Null(await _service.RotateAsync("", null, CancellationToken.None));
    }

    [Fact]
    public async Task Logout_revokes_only_the_callers_token()
    {
        var mine = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        var otherUser = Guid.CreateVersion7();
        var theirs = await _service.IssueRefreshTokenAsync(otherUser, null, CancellationToken.None);

        await _service.RevokeAsync(theirs.Token, UserId, CancellationToken.None); // not mine: ignored
        await _service.RevokeAsync(mine.Token, UserId, CancellationToken.None);

        Assert.NotNull(_store.Find(mine.Token).RevokedAt);
        Assert.Null(_store.Find(theirs.Token).RevokedAt);
    }

    [Fact]
    public async Task RevokeAll_kills_every_family_of_the_user()
    {
        var a = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        var b = await _service.IssueRefreshTokenAsync(UserId, null, CancellationToken.None);
        var other = await _service.IssueRefreshTokenAsync(Guid.CreateVersion7(), null, CancellationToken.None);

        await _service.RevokeAllAsync(UserId, CancellationToken.None);

        Assert.Null(await _service.RotateAsync(a.Token, null, CancellationToken.None));
        Assert.Null(await _service.RotateAsync(b.Token, null, CancellationToken.None));
        Assert.NotNull(await _service.RotateAsync(other.Token, null, CancellationToken.None));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
    {
        public ConcurrentDictionary<Guid, RefreshTokenRecord> Records { get; } = new();

        public RefreshTokenRecord Find(string token) =>
            Records.Values.Single(r => r.TokenHash.AsSpan().SequenceEqual(TokenService.HashToken(token)));

        public Task<RefreshTokenRecord?> FindAsync(byte[] tokenHash, CancellationToken ct) =>
            Task.FromResult(Records.Values.SingleOrDefault(r => r.TokenHash.AsSpan().SequenceEqual(tokenHash)));

        public Task InsertAsync(RefreshTokenRecord record, CancellationToken ct)
        {
            Records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task RevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct)
        {
            Revoke(r => r.Id == id, revokedAt);
            return Task.CompletedTask;
        }

        public Task RevokeFamilyAsync(Guid familyId, DateTime revokedAt, CancellationToken ct)
        {
            Revoke(r => r.FamilyId == familyId, revokedAt);
            return Task.CompletedTask;
        }

        public Task RevokeAllForUserAsync(Guid userId, DateTime revokedAt, CancellationToken ct)
        {
            Revoke(r => r.UserId == userId, revokedAt);
            return Task.CompletedTask;
        }

        private void Revoke(Func<RefreshTokenRecord, bool> predicate, DateTime revokedAt)
        {
            foreach (var record in Records.Values.Where(r => predicate(r) && r.RevokedAt is null).ToList())
            {
                Records[record.Id] = new RefreshTokenRecord
                {
                    Id = record.Id,
                    UserId = record.UserId,
                    TokenHash = record.TokenHash,
                    FamilyId = record.FamilyId,
                    ExpiresAt = record.ExpiresAt,
                    RevokedAt = revokedAt,
                    UserAgent = record.UserAgent,
                    CreatedAt = record.CreatedAt,
                };
            }
        }
    }
}
