using CouplesCash.Api.Contracts;
using FluentValidation;

namespace CouplesCash.Api.Validation;

internal static class Rules
{
    public const string HexColor = "^#[0-9a-fA-F]{6}$";
    public const string Icon = "^[a-z0-9]+(-[a-z0-9]+)*$";
    public const long MaxCents = 9_999_999_999_99; // 9.99 billion in cents; keeps sums far from overflow

    public static bool IsHttpUrl(string? url) =>
        url is null || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
}

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(10).MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().Length(1, 40);
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator() => RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(256);
}

public sealed class UpdateMeRequestValidator : AbstractValidator<UpdateMeRequest>
{
    public UpdateMeRequestValidator() =>
        RuleFor(x => x.DisplayName).Length(1, 40).When(x => x.DisplayName is not null);
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(256);
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(10).MaximumLength(256)
            .NotEqual(x => x.CurrentPassword).WithMessage("New password must differ from the current password.");
    }
}

public sealed class CoupleCodeRequestValidator : AbstractValidator<CoupleCodeRequest>
{
    public CoupleCodeRequestValidator() =>
        RuleFor(x => x.Name).Length(1, 60).When(x => x.Name is not null);
}

public sealed class JoinRequestValidator : AbstractValidator<JoinRequest>
{
    public JoinRequestValidator() => RuleFor(x => x.Code).NotEmpty().MaximumLength(64);
}

public sealed class RenameHouseholdRequestValidator : AbstractValidator<RenameHouseholdRequest>
{
    public RenameHouseholdRequestValidator() => RuleFor(x => x.Name).NotEmpty().Length(1, 60);
}

public sealed class AccountRequestValidator : AbstractValidator<AccountRequest>
{
    public AccountRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Institution).NotNull().MaximumLength(80);
        RuleFor(x => x.Type).NotNull().IsInEnum();
        RuleFor(x => x.BalanceCents).InclusiveBetween(-Rules.MaxCents, Rules.MaxCents);
        RuleFor(x => x.Color).Matches(Rules.HexColor).When(x => x.Color is not null);
        RuleFor(x => x.Icon).Matches(Rules.Icon).MaximumLength(64).When(x => x.Icon is not null);
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class BalanceRequestValidator : AbstractValidator<BalanceRequest>
{
    public BalanceRequestValidator() => RuleFor(x => x.BalanceCents).InclusiveBetween(-Rules.MaxCents, Rules.MaxCents);
}

public sealed class CategoryRequestValidator : AbstractValidator<CategoryRequest>
{
    public CategoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(40);
        RuleFor(x => x.Color).NotEmpty().Matches(Rules.HexColor);
        RuleFor(x => x.Icon).NotEmpty().Matches(Rules.Icon).MaximumLength(64);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).When(x => x.SortOrder is not null);
    }
}

public sealed class RecurringRequestValidator : AbstractValidator<RecurringRequest>
{
    public RecurringRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Kind).NotNull().IsInEnum();
        RuleFor(x => x.AmountCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents);
        RuleFor(x => x.Frequency).NotNull().IsInEnum();
        RuleFor(x => x.AnchorDate).NotNull();
        RuleFor(x => x.EndDate)
            .GreaterThanOrEqualTo(x => x.AnchorDate!.Value)
            .When(x => x.EndDate is not null && x.AnchorDate is not null)
            .WithMessage("End date must be on or after the anchor date.");
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.Url).MaximumLength(2048).Must(Rules.IsHttpUrl).WithMessage("Must be an absolute http(s) URL.");
    }
}

public sealed class MarkPaidRequestValidator : AbstractValidator<MarkPaidRequest>
{
    public MarkPaidRequestValidator() =>
        RuleFor(x => x.AmountCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents).When(x => x.AmountCents is not null);
}
