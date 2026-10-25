using System.Text.Json;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Services;
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

public sealed class HouseholdSettingsRequestValidator : AbstractValidator<HouseholdSettingsRequest>
{
    public const string CurrencyPattern = "^[A-Z]{3}$";

    public HouseholdSettingsRequestValidator()
    {
        RuleFor(x => x.Name.Value).NotEmpty().Length(1, 60).When(x => x.Name.HasValue).OverridePropertyName("Name");
        RuleFor(x => x.Currency.Value).NotEmpty().Matches(CurrencyPattern).When(x => x.Currency.HasValue).OverridePropertyName("Currency")
            .WithMessage("Must be a 3-letter upper-case ISO 4217 code.");
        RuleFor(x => x.WeekStart.Value).NotNull().InclusiveBetween(0, 1).When(x => x.WeekStart.HasValue).OverridePropertyName("WeekStart")
            .WithMessage("Must be 0 (Sunday) or 1 (Monday).");
        RuleFor(x => x.Timezone.Value).NotEmpty().MaximumLength(64).Must(IsKnownTimeZone).When(x => x.Timezone.HasValue).OverridePropertyName("Timezone")
            .WithMessage("Must be a valid IANA time zone id (e.g. America/Chicago).");
        RuleFor(x => x.PayPeriod.Value).NotNull().IsInEnum().When(x => x.PayPeriod.HasValue).OverridePropertyName("PayPeriod");
        RuleFor(x => x.WeeklySpendCents.Value!.Value).InclusiveBetween(0, Rules.MaxCents)
            .When(x => x.WeeklySpendCents is { HasValue: true, Value: not null }).OverridePropertyName("WeeklySpendCents");
    }

    public static bool IsKnownTimeZone(string? id) => !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
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
        RuleFor(x => x.PayeeHint).MaximumLength(MaxPayeeHintLength);
    }

    /// <summary>v4 "Bank shows it as" hint length.</summary>
    public const int MaxPayeeHintLength = 80;
}

public sealed class MarkPaidRequestValidator : AbstractValidator<MarkPaidRequest>
{
    public MarkPaidRequestValidator() =>
        RuleFor(x => x.AmountCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents).When(x => x.AmountCents is not null);
}

public sealed class ExpenseRequestValidator : AbstractValidator<ExpenseRequest>
{
    public ExpenseRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.AmountCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents);
        RuleFor(x => x.SpentOn).NotNull();
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class BudgetRequestValidator : AbstractValidator<BudgetRequest>
{
    public BudgetRequestValidator() => RuleFor(x => x.AmountCents).InclusiveBetween(0, Rules.MaxCents);
}

public sealed class TodoRequestValidator : AbstractValidator<TodoRequest>
{
    public TodoRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().Length(1, 120);
        RuleFor(x => x.AmountCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents).When(x => x.AmountCents is not null);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 100_000).When(x => x.SortOrder is not null);
    }
}

public sealed class TodoOrderRequestValidator : AbstractValidator<TodoOrderRequest>
{
    public TodoOrderRequestValidator()
    {
        RuleFor(x => x.Ids).NotNull().Must(ids => ids is { Length: >= 1 and <= 500 }).WithMessage("Provide between 1 and 500 ids.");
        RuleFor(x => x.Ids).Must(ids => ids.Distinct().Count() == ids.Length).When(x => x.Ids is not null).WithMessage("Ids must be unique.");
    }
}

public sealed class GoalRequestValidator : AbstractValidator<GoalRequest>
{
    public GoalRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.TargetCents).GreaterThan(0).LessThanOrEqualTo(Rules.MaxCents);
        RuleFor(x => x.Color).Matches(Rules.HexColor).When(x => x.Color is not null);
        RuleFor(x => x.Icon).Matches(Rules.Icon).MaximumLength(64).When(x => x.Icon is not null);
    }
}

public sealed class ContributeRequestValidator : AbstractValidator<ContributeRequest>
{
    public ContributeRequestValidator() =>
        RuleFor(x => x.AmountCents).NotEqual(0).InclusiveBetween(-Rules.MaxCents, Rules.MaxCents);
}

public sealed class PushSubscriptionRequestValidator : AbstractValidator<PushSubscriptionRequest>
{
    public PushSubscriptionRequestValidator()
    {
        RuleFor(x => x.Endpoint).NotEmpty().MaximumLength(2048)
            .Must(e => Uri.TryCreate(e, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .WithMessage("Must be an absolute https URL.");
        RuleFor(x => x.Keys).NotNull();
        RuleFor(x => x.Keys!.P256dh).NotEmpty().MaximumLength(200).When(x => x.Keys is not null).OverridePropertyName("keys.p256dh");
        RuleFor(x => x.Keys!.Auth).NotEmpty().MaximumLength(100).When(x => x.Keys is not null).OverridePropertyName("keys.auth");
        RuleFor(x => x.UserAgent).MaximumLength(256);
    }
}

public sealed class PushUnsubscribeRequestValidator : AbstractValidator<PushUnsubscribeRequest>
{
    public PushUnsubscribeRequestValidator()
    {
        RuleFor(x => x.Endpoint).MaximumLength(2048);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Endpoint) || x.Id is { } id && id != Guid.Empty)
            .WithMessage("Provide endpoint or id.").OverridePropertyName("endpoint");
    }
}

public sealed class PushPrefsRequestValidator : AbstractValidator<PushPrefsRequest>
{
    public PushPrefsRequestValidator()
    {
        RuleFor(x => x.DaysBefore).NotNull().Must(d => d is { Length: <= 5 }).WithMessage("At most 5 values.");
        RuleForEach(x => x.DaysBefore).InclusiveBetween(0, 14).When(x => x.DaysBefore is not null);
        RuleFor(x => x.HourLocal).InclusiveBetween(0, 23);
    }
}

public sealed class PasskeyOptionsRequestValidator : AbstractValidator<PasskeyOptionsRequest>
{
    public PasskeyOptionsRequestValidator() => RuleFor(x => x.Name).NotEmpty().Length(1, 40);
}

public sealed class PasskeyRegisterRequestValidator : AbstractValidator<PasskeyRegisterRequest>
{
    public PasskeyRegisterRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().Length(1, 40);
        RuleFor(x => x.Credential).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("Must be a RegistrationResponseJSON object.");
    }
}

public sealed class PasskeyLoginRequestValidator : AbstractValidator<PasskeyLoginRequest>
{
    public PasskeyLoginRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Credential).Must(c => c.ValueKind == JsonValueKind.Object).WithMessage("Must be an AuthenticationResponseJSON object.");
    }
}

// ---------------------------------------------------------------------------------------------
// v3
// ---------------------------------------------------------------------------------------------

public sealed class BankClaimRequestValidator : AbstractValidator<BankClaimRequest>
{
    public BankClaimRequestValidator()
    {
        RuleFor(x => x.SetupToken).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.Name).Length(1, 60).When(x => x.Name is not null);
    }
}

public sealed class BankLinkPatchRequestValidator : AbstractValidator<BankLinkPatchRequest>
{
    public BankLinkPatchRequestValidator()
    {
        RuleFor(x => x.Name).Length(1, 60).When(x => x.Name is not null);
        RuleFor(x => x).Must(x => x.Name is not null || x.SyncEnabled is not null).WithMessage("Provide name or syncEnabled.").OverridePropertyName("name");
    }
}

public sealed class ExternalAccountMapRequestValidator : AbstractValidator<ExternalAccountMapRequest>
{
    public ExternalAccountMapRequestValidator()
    {
        RuleFor(x => x).Must(x => x.AccountId is null || x.Create is null).WithMessage("Send accountId or create, not both.").OverridePropertyName("accountId");
        RuleFor(x => x.Create!.Name).NotEmpty().MaximumLength(80).When(x => x.Create is not null).OverridePropertyName("create.name");
        RuleFor(x => x.Create!.Type).IsInEnum().When(x => x.Create?.Type is not null).OverridePropertyName("create.type");
        RuleFor(x => x.Create!.Institution).MaximumLength(80).When(x => x.Create is not null).OverridePropertyName("create.institution");
    }
}

public sealed class InboxConfirmRequestValidator : AbstractValidator<InboxConfirmRequest>
{
    public InboxConfirmRequestValidator()
    {
        RuleFor(x => x.As).NotNull().IsInEnum().WithMessage("Must be 'expense' or 'occurrence'.");
        RuleFor(x => x.Name).MaximumLength(InboxService.MaxExpenseNameLength);
        RuleFor(x => x.ItemId).NotNull().When(x => x.As == ConfirmAs.Occurrence).WithMessage("itemId is required to confirm as a payment.");
        RuleFor(x => x.DueDate).NotNull().When(x => x.As == ConfirmAs.Occurrence).WithMessage("dueDate is required to confirm as a payment.");
    }
}

public sealed class InboxBulkRequestValidator : AbstractValidator<InboxBulkRequest>
{
    public InboxBulkRequestValidator()
    {
        RuleFor(x => x.Action).NotEmpty().Must(a => a is InboxBulkRequest.ConfirmSuggested or InboxBulkRequest.Ignore)
            .WithMessage("Must be 'confirm-suggested' or 'ignore'.");
        RuleFor(x => x.Ids).Must(ids => ids is { Length: <= 500 }).When(x => x.Ids is not null).WithMessage("At most 500 ids.");
        RuleFor(x => x.Ids).Must(ids => ids!.Distinct().Count() == ids!.Length).When(x => x.Ids is not null).WithMessage("Ids must be unique.");
    }
}

public sealed class CsvRowRequestValidator : AbstractValidator<CsvRowRequest>
{
    public CsvRowRequestValidator()
    {
        RuleFor(x => x.PostedOn).NotNull();
        RuleFor(x => x.AmountCents).NotEqual(0).InclusiveBetween(-Rules.MaxCents, Rules.MaxCents);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Payee).MaximumLength(200);
        RuleFor(x => x.Memo).MaximumLength(500);
    }
}

public sealed class CsvImportRequestValidator : AbstractValidator<CsvImportRequest>
{
    public CsvImportRequestValidator()
    {
        RuleFor(x => x.AccountId).NotNull();
        RuleFor(x => x.Filename).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Rows).NotNull().Must(rows => rows is { Length: >= 1 and <= ImportEndpoints.MaxRows })
            .WithMessage($"Provide between 1 and {ImportEndpoints.MaxRows} rows.");
        RuleForEach(x => x.Rows).SetValidator(new CsvRowRequestValidator()).When(x => x.Rows is not null);
    }
}
