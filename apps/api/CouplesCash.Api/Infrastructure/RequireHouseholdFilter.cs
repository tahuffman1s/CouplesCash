using CouplesCash.Api.Auth;

namespace CouplesCash.Api.Infrastructure;

/// <summary>Household routes need an <c>hh</c> claim; without one the contract says 409 "No household".</summary>
public sealed class RequireHouseholdFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.User.TryGetHouseholdId(out _)
            ? next(context)
            : ValueTask.FromResult<object?>(Problems.NoHousehold());
}

public static class RequireHouseholdExtensions
{
    public static TBuilder RequireHousehold<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RequireHouseholdFilter>();
}
