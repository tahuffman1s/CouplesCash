using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;

namespace CouplesCash.Api.Validation;

/// <summary>Runs the FluentValidation validator for the endpoint's <typeparamref name="T"/> argument and short-circuits with a 400 ValidationProblem.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var argument = context.Arguments.OfType<T>().FirstOrDefault();
        if (argument is not null)
        {
            var result = await validator.ValidateAsync(argument, context.HttpContext.RequestAborted);
            if (!result.IsValid)
            {
                return TypedResults.ValidationProblem(ToErrors(result));
            }
        }

        return await next(context);
    }

    internal static Dictionary<string, string[]> ToErrors(ValidationResult result) =>
        result.Errors
            .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
}

public static class ValidationExtensions
{
    /// <summary>Validates the request body of type <typeparamref name="T"/> before the handler runs.</summary>
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();

    /// <summary>Registers every <see cref="IValidator{T}"/> in this assembly as a singleton.</summary>
    public static IServiceCollection AddCouplesCashValidators(this IServiceCollection services)
    {
        var validatorTypes = typeof(ValidationExtensions).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in validatorTypes)
        {
            services.AddSingleton(service, implementation);
        }

        return services;
    }
}
