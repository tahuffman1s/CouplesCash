using System.Globalization;
using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.SimpleFin;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

/// <summary>Linked banks (SimpleFIN). The access URL never appears in any response.</summary>
public static class BankEndpoints
{
    public static RouteGroupBuilder MapBanks(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/banks").WithTags("Banks").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Bank links with their external accounts and mappings");
        group.MapPost("/simplefin/claim", Claim).RequireDataKey().WithValidation<BankClaimRequest>()
            .WithSummary("Claim a SimpleFIN setup token, store the access URL encrypted and run a first sync (503 without Security__DataKey)");
        group.MapDelete("/{id:guid}", Delete).WithSummary("Unlink a bank (inbox rows and mapped accounts are kept)");
        group.MapPatch("/{id:guid}", Patch).WithValidation<BankLinkPatchRequest>().WithSummary("Rename a link or pause/resume its sync");
        group.MapPost("/{id:guid}/sync", Sync).WithSummary("Sync now (429 with Retry-After inside the 15-minute cooldown; 502 when the Bridge fails)");
        group.MapPut("/accounts/{externalAccountId:guid}", Map).WithValidation<ExternalAccountMapRequest>()
            .WithSummary("Map an external account to a CouplesCash account ({ accountId } or { create }), or stop tracking it ({ accountId: null })");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<BankLinkDto>>> List(ClaimsPrincipal principal, BankRepository banks, BankSyncService sync, CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var links = await banks.ListLinksAsync(householdId, ct);
        return TypedResults.Ok(await sync.ToDtosAsync(householdId, links, ct));
    }

    private static async Task<Results<Created<BankLinkDto>, ValidationProblem, ProblemHttpResult>> Claim(
        BankClaimRequest request,
        ClaimsPrincipal principal,
        BankSyncService sync,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        try
        {
            var link = await sync.ClaimAsync(householdId, principal.GetUserId(), principal.GetDisplayName(), request.SetupToken, request.Name, ct);
            var dto = (await sync.ToDtosAsync(householdId, [link], ct))[0];
            return TypedResults.Created($"/v1/banks/{link.Id}", dto);
        }
        catch (SimpleFinException ex) when (ex.Error is SimpleFinError.InvalidToken or SimpleFinError.BadResponse)
        {
            return Problems.Validation("setupToken", ex.Message);
        }
        catch (SimpleFinException ex)
        {
            return Problems.BridgeUnreachable(ex.Message, [ex.Message]);
        }
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BankRepository banks,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var link = await banks.DeleteLinkAsync(uow.Connection, householdId, id, ct);
        if (link is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "bank.unlinked", "bank", link.Id,
            ActivitySummaries.BankUnlinked(link.Name), new { linkId = link.Id }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<BankLinkDto>, NotFound>> Patch(
        Guid id,
        BankLinkPatchRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BankRepository banks,
        BankSyncService sync,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var link = await banks.UpdateLinkAsync(uow.Connection, householdId, id, request.Name?.Trim(), request.SyncEnabled, ct);
        if (link is null)
        {
            return TypedResults.NotFound();
        }

        var summary = request.SyncEnabled switch
        {
            false => $"paused syncing {link.Name}",
            true when request.Name is null => $"resumed syncing {link.Name}",
            _ => $"updated bank {link.Name}",
        };
        await activity.RecordAsync(uow, householdId, userId, "bank.updated", "bank", link.Id,
            summary, new { linkId = link.Id, syncEnabled = link.SyncEnabled }, ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);

        return TypedResults.Ok((await sync.ToDtosAsync(householdId, [link], ct))[0]);
    }

    private static async Task<Results<Ok<SyncResultDto>, NotFound, ProblemHttpResult>> Sync(
        Guid id,
        ClaimsPrincipal principal,
        HttpContext httpContext,
        BankRepository banks,
        BankSyncService sync,
        SyncOptions options,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var link = await banks.GetLinkAsync(householdId, id, ct);
        if (link is null)
        {
            return TypedResults.NotFound();
        }

        if (!sync.Configured)
        {
            return Problems.BankSyncNotConfigured();
        }

        if (BankSyncRules.CooldownRemaining(link.LastManualSyncAt, time.Now(), options.ManualCooldown) is { } remaining)
        {
            var seconds = BankSyncRules.RetryAfterSeconds(remaining);
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Problems.TooManyRequests(seconds);
        }

        try
        {
            return TypedResults.Ok(await sync.SyncAsync(link, principal.GetUserId(), principal.GetDisplayName(), manual: true, ct));
        }
        catch (SimpleFinException ex)
        {
            return Problems.BridgeUnreachable(ex.Message, [ex.Message]);
        }
    }

    private static async Task<Results<Ok<ExternalAccountDto>, NotFound, ValidationProblem>> Map(
        Guid externalAccountId,
        ExternalAccountMapRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        BankRepository banks,
        ImportRepository imports,
        AccountRepository accounts,
        HouseholdRepository households,
        BankSyncService sync,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var actorName = principal.GetDisplayName();
        var currency = await households.CurrencyAsync(householdId, ct);
        var now = time.Now();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var external = await banks.GetExternalAccountAsync(uow.Connection, householdId, externalAccountId, ct);
        if (external is null)
        {
            return TypedResults.NotFound();
        }

        AccountRow? account = null;
        if (request.Create is { } create)
        {
            var type = create.Type ?? GuessType(external.Name);
            var fields = new AccountFields(
                create.Name.Trim(),
                string.IsNullOrWhiteSpace(create.Institution) ? external.OrgName : create.Institution.Trim(),
                type,
                BankSyncRules.ToAccountBalance(type, external.BalanceCents),
                AccountEndpoints.DefaultColor,
                AccountEndpoints.DefaultIcon(type),
                null);
            account = await accounts.CreateAsync(uow.Connection, householdId, userId, fields, now, ct);
            await activity.RecordAsync(uow, householdId, userId, "account.created", "account", account.Id,
                ActivitySummaries.AccountCreated(account, currency), new { accountId = account.Id, balanceCents = account.BalanceCents, fromBank = true }, ct, actorName);
        }
        else if (request.AccountId is { } accountId)
        {
            account = await accounts.GetAsync(uow.Connection, householdId, accountId, ct);
            if (account is null)
            {
                return Problems.Validation("accountId", "Unknown account.");
            }
        }

        var updated = await banks.SetMappingAsync(uow.Connection, householdId, external.Id, account?.Id, now, ct);
        if (updated is null)
        {
            return TypedResults.NotFound();
        }

        if (account is not null)
        {
            await sync.ApplyMappingAsync(uow.Connection, householdId, external, account, userId, now, ct);
            await activity.RecordAsync(uow, householdId, userId, "bank.mapped", "bank", external.BankLinkId,
                ActivitySummaries.BankMapped(external.Name, account.Name), new { externalAccountId = external.Id, accountId = account.Id }, ct, actorName);
        }
        else
        {
            await imports.SetAccountForExternalAsync(uow.Connection, householdId, external.Id, null, ct);
            await activity.RecordAsync(uow, householdId, userId, "bank.unmapped", "bank", external.BankLinkId,
                ActivitySummaries.BankUnmapped(external.Name), new { externalAccountId = external.Id }, ct, actorName);
        }

        var fresh = await banks.GetExternalAccountAsync(uow.Connection, householdId, external.Id, ct) ?? updated;
        await uow.CommitAsync(ct);
        return TypedResults.Ok(fresh.ToDto());
    }

    /// <summary>Best-effort account type from the bank's account name ("Visa Signature" → credit, "Online Savings" → savings).</summary>
    internal static AccountType GuessType(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("credit") || lower.Contains("card") || lower.Contains("visa") || lower.Contains("mastercard") || lower.Contains("amex"))
        {
            return AccountType.Credit;
        }

        if (lower.Contains("saving"))
        {
            return AccountType.Savings;
        }

        if (lower.Contains("loan") || lower.Contains("mortgage"))
        {
            return AccountType.Loan;
        }

        if (lower.Contains("invest") || lower.Contains("brokerage") || lower.Contains("401") || lower.Contains("ira"))
        {
            return AccountType.Investment;
        }

        return AccountType.Checking;
    }
}
