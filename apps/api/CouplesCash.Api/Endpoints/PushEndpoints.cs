using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Push;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CouplesCash.Api.Endpoints;

/// <summary>Per-user (no household needed) Web Push routes. All answer 503 <c>push_not_configured</c> without VAPID keys.</summary>
public static class PushEndpoints
{
    public static RouteGroupBuilder MapPush(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/push").WithTags("Push").RequireAuthorization().RequirePushConfigured();

        group.MapGet("/vapid-public-key", VapidPublicKey).WithSummary("The VAPID public key the browser subscribes with");
        group.MapPost("/subscriptions", Subscribe).WithValidation<PushSubscriptionRequest>().WithSummary("Register (upsert by endpoint) a push subscription for the caller");
        group.MapDelete("/subscriptions", Unsubscribe).WithValidation<PushUnsubscribeRequest>().WithSummary("Remove one of the caller's subscriptions by endpoint or id (404 when neither matches)");
        group.MapGet("/prefs", GetPrefs).WithSummary("Notification preferences (defaults are created on first read) + subscriptions");
        group.MapPut("/prefs", PutPrefs).WithValidation<PushPrefsRequest>().WithSummary("Replace notification preferences");
        group.MapPost("/test", Test).WithSummary("Send a test notification to the caller's devices");

        return group;
    }

    private static Ok<VapidPublicKeyResponse> VapidPublicKey(PushOptions options) =>
        TypedResults.Ok(new VapidPublicKeyResponse(options.VapidPublicKey!));

    private static async Task<Created<PushSubscriptionCreated>> Subscribe(
        PushSubscriptionRequest request,
        HttpRequest http,
        ClaimsPrincipal principal,
        PushRepository pushes,
        TimeProvider time,
        CancellationToken ct)
    {
        var userAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? http.UserAgent() : request.UserAgent.Trim();
        var id = await pushes.UpsertSubscriptionAsync(principal.GetUserId(), request.Endpoint.Trim(), request.Keys!.P256dh.Trim(), request.Keys.Auth.Trim(), Truncate(userAgent, 256), time.Now(), ct);
        return TypedResults.Created("/v1/push/prefs", new PushSubscriptionCreated(id));
    }

    private static async Task<Results<NoContent, NotFound>> Unsubscribe([FromBody] PushUnsubscribeRequest request, ClaimsPrincipal principal, PushRepository pushes, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim();
        var id = request.Id is { } i && i != Guid.Empty ? i : (Guid?)null;
        return await pushes.DeleteSubscriptionAsync(principal.GetUserId(), endpoint, id, ct)
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static async Task<Ok<PushPrefsDto>> GetPrefs(ClaimsPrincipal principal, PushRepository pushes, TimeProvider time, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var prefs = await pushes.GetOrCreatePrefsAsync(userId, time.Now(), ct);
        var subscriptions = await pushes.SubscriptionsForUserAsync(userId, ct);
        return TypedResults.Ok(prefs.ToDto(subscriptions));
    }

    private static async Task<Ok<PushPrefsDto>> PutPrefs(PushPrefsRequest request, ClaimsPrincipal principal, PushRepository pushes, TimeProvider time, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var days = (request.DaysBefore ?? []).Distinct().OrderBy(d => d).Select(d => (short)d).ToArray();
        var prefs = await pushes.UpsertPrefsAsync(userId, request.RemindersEnabled, days, request.HourLocal, request.ActivityEnabled, request.WeekSummaryEnabled ?? true, time.Now(), ct);
        var subscriptions = await pushes.SubscriptionsForUserAsync(userId, ct);
        return TypedResults.Ok(prefs.ToDto(subscriptions));
    }

    private static Accepted Test(ClaimsPrincipal principal, PushNotifier notifier)
    {
        notifier.ToUser(principal.GetUserId(), new PushPayload(
            Title: "CouplesCash",
            Body: "CouplesCash notifications are working 🎉",
            Url: "/settings/notifications",
            Tag: "test"));
        return TypedResults.Accepted("/v1/push/prefs");
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
