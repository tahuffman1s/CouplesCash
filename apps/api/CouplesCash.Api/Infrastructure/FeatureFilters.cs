using CouplesCash.Api.Auth;
using CouplesCash.Api.Push;
using CouplesCash.Api.Security;

namespace CouplesCash.Api.Infrastructure;

/// <summary>Every <c>/v1/push/*</c> route answers 503 <c>push_not_configured</c> until VAPID keys are set.</summary>
public sealed class RequirePushConfiguredFilter(PushOptions options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.Enabled ? next(context) : ValueTask.FromResult<object?>(Problems.PushNotConfigured());
}

/// <summary>Passkey ceremonies answer 503 <c>passkeys_not_configured</c> without an https (or http://localhost) origin.</summary>
public sealed class RequirePasskeysFilter(WebAuthnOptions options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        options.Enabled ? next(context) : ValueTask.FromResult<object?>(Problems.PasskeysNotConfigured());
}

/// <summary>Claiming a bank link answers 503 <c>bank_sync_not_configured</c> until <c>Security__DataKey</c> is set.</summary>
public sealed class RequireDataKeyFilter(DataProtector protector) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        protector.IsConfigured ? next(context) : ValueTask.FromResult<object?>(Problems.BankSyncNotConfigured());
}

public static class FeatureFilterExtensions
{
    public static TBuilder RequireDataKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RequireDataKeyFilter>();

    public static TBuilder RequirePushConfigured<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RequirePushConfiguredFilter>();

    public static TBuilder RequirePasskeys<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RequirePasskeysFilter>();
}
