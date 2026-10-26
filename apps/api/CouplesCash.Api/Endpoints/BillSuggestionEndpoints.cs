using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CouplesCash.Api.Endpoints;

/// <summary>v6 found bills: regular payments the matcher detected in imported transactions ("We found 3 regular payments").</summary>
public static class BillSuggestionEndpoints
{
    public static RouteGroupBuilder MapBillSuggestions(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/bill-suggestions").WithTags("Bill suggestions").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Open found-bill suggestions, best first");
        group.MapPost("/{id:guid}/accept", Accept).WithValidation<BillSuggestionAcceptRequest>()
            .WithSummary("Add it ({ name?, kind?, amountCents?, frequency?, nextDue? } override the detection): creates the item with bankNames = [bankName] and matches the transactions it was found from");
        group.MapPost("/{id:guid}/dismiss", Dismiss).WithSummary("Not a bill: never suggested again");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<BillSuggestionDto>>> List(ClaimsPrincipal principal, BillSuggestionService bills, CancellationToken ct) =>
        TypedResults.Ok(await bills.ListAsync(principal.GetHouseholdId(), ct));

    private static async Task<Results<Created<BillSuggestionAcceptedDto>, NotFound, ValidationProblem, ProblemHttpResult>> Accept(
        Guid id,
        BillSuggestionAcceptRequest? request,
        ClaimsPrincipal principal,
        BillSuggestionService bills,
        CancellationToken ct)
    {
        var outcome = await bills.AcceptAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), id,
            request ?? new BillSuggestionAcceptRequest(null, null, null, null, null), ct);
        return outcome switch
        {
            { NotFound: true } => TypedResults.NotFound(),
            { Conflict: { } message } => Problems.Conflict("Suggestion already handled", message),
            { Error: { } error } => Problems.Validation(error.Field, error.Message),
            { Accepted: { } accepted } => TypedResults.Created($"/v1/recurring/{accepted.Item.Id}", accepted),
            _ => TypedResults.NotFound(),
        };
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Dismiss(Guid id, ClaimsPrincipal principal, BillSuggestionService bills, CancellationToken ct)
    {
        var outcome = await bills.DismissAsync(principal.GetHouseholdId(), principal.GetUserId(), principal.GetDisplayName(), id, ct);
        return outcome switch
        {
            { NotFound: true } => TypedResults.NotFound(),
            { Conflict: { } message } => Problems.Conflict("Suggestion already handled", message),
            _ => TypedResults.NoContent(),
        };
    }
}
