using CouplesCash.Api.Contracts;
using CouplesCash.Api.Domain;
using CouplesCash.Api.Endpoints;
using CouplesCash.Api.Validation;
using FluentValidation;

namespace CouplesCash.Api.Tests;

public class V3ValidatorTests
{
    private static readonly Guid Account = Guid.Parse("00000000-0000-7000-8000-000000000010");

    private static CsvRowRequest Row(long cents = -1249, string description = "KROGER #123", int day = 2) =>
        new(new DateOnly(2026, 10, day), cents, description, null, null);

    private static string[] Fields<T>(AbstractValidator<T> validator, T value) =>
        ValidationFilter<object>.ToErrors(validator.Validate(value)).Keys.Order().ToArray();

    // ---------------------------------------------------------------------------------------------
    // CSV import
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Csv_import_accepts_a_normal_file()
    {
        var request = new CsvImportRequest(Account, "chase.csv", [Row(), Row(cents: 320000, description: "PAYROLL", day: 15)]);

        Assert.True(new CsvImportRequestValidator().Validate(request).IsValid);
    }

    [Fact]
    public void Csv_import_requires_account_filename_and_rows()
    {
        var request = new CsvImportRequest(null, "", null);

        Assert.Equal(["accountId", "filename", "rows"], Fields(new CsvImportRequestValidator(), request));
    }

    [Fact]
    public void Csv_import_limits_rows_to_5000()
    {
        var tooMany = Enumerable.Range(0, ImportEndpoints.MaxRows + 1).Select(i => Row(cents: -100 - i)).ToArray();

        Assert.Equal(["rows"], Fields(new CsvImportRequestValidator(), new CsvImportRequest(Account, "big.csv", tooMany)));
        Assert.Equal(["rows"], Fields(new CsvImportRequestValidator(), new CsvImportRequest(Account, "empty.csv", [])));
        Assert.True(new CsvImportRequestValidator().Validate(new CsvImportRequest(Account, "max.csv", tooMany[..ImportEndpoints.MaxRows])).IsValid);
    }

    [Fact]
    public void Csv_rows_need_a_date_a_non_zero_amount_and_a_description()
    {
        var request = new CsvImportRequest(Account, "bad.csv",
        [
            new CsvRowRequest(null, -100, "no date", null, null),
            new CsvRowRequest(new DateOnly(2026, 10, 1), 0, "zero", null, null),
            new CsvRowRequest(new DateOnly(2026, 10, 1), -100, "", null, null),
            new CsvRowRequest(new DateOnly(2026, 10, 1), -100, new string('x', 201), null, null),
            new CsvRowRequest(new DateOnly(2026, 10, 1), Rules.MaxCents + 1, "huge", null, null),
        ]);

        var fields = Fields(new CsvImportRequestValidator(), request);

        Assert.Contains("rows[0].PostedOn", fields);
        Assert.Contains("rows[1].AmountCents", fields);
        Assert.Contains("rows[2].Description", fields);
        Assert.Contains("rows[3].Description", fields);
        Assert.Contains("rows[4].AmountCents", fields);
    }

    [Fact]
    public void Csv_row_validator_accepts_inflows_and_optional_payee_memo()
    {
        var row = new CsvRowRequest(new DateOnly(2026, 10, 1), 320000, "ACME PAYROLL", "Acme", "October");

        Assert.True(new CsvRowRequestValidator().Validate(row).IsValid);
    }

    // ---------------------------------------------------------------------------------------------
    // Inbox
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Confirm_as_occurrence_needs_item_and_due_date()
    {
        var validator = new InboxConfirmRequestValidator();

        Assert.Equal(["dueDate", "itemId"], Fields(validator, new InboxConfirmRequest(ConfirmAs.Occurrence, null, null, null, null)));
        Assert.True(validator.Validate(new InboxConfirmRequest(ConfirmAs.Occurrence, null, null, Guid.NewGuid(), new DateOnly(2026, 10, 1))).IsValid);
    }

    [Fact]
    public void Confirm_as_expense_needs_only_as_and_a_short_name()
    {
        var validator = new InboxConfirmRequestValidator();

        Assert.True(validator.Validate(new InboxConfirmRequest(ConfirmAs.Expense, null, null, null, null)).IsValid);
        Assert.True(validator.Validate(new InboxConfirmRequest(ConfirmAs.Expense, Guid.NewGuid(), "Weekly shop", null, null)).IsValid);
        Assert.Equal(["name"], Fields(validator, new InboxConfirmRequest(ConfirmAs.Expense, null, new string('n', 81), null, null)));
        Assert.Equal(["as"], Fields(validator, new InboxConfirmRequest(null, null, null, null, null)));
    }

    [Fact]
    public void Bulk_requires_a_known_action_and_unique_ids()
    {
        var validator = new InboxBulkRequestValidator();
        var id = Guid.NewGuid();

        Assert.True(validator.Validate(new InboxBulkRequest("confirm-suggested", null)).IsValid);
        Assert.True(validator.Validate(new InboxBulkRequest("ignore", [id])).IsValid);
        Assert.Equal(["action"], Fields(validator, new InboxBulkRequest("confirm-all", null)));
        Assert.Equal(["ids"], Fields(validator, new InboxBulkRequest("ignore", [id, id])));
        Assert.Equal(["ids"], Fields(validator, new InboxBulkRequest("ignore", Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray())));
    }

    // ---------------------------------------------------------------------------------------------
    // Banks
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Claim_requires_a_token()
    {
        var validator = new BankClaimRequestValidator();

        Assert.Equal(["setupToken"], Fields(validator, new BankClaimRequest("", null)));
        Assert.Equal(["name"], Fields(validator, new BankClaimRequest("dG9rZW4=", new string('n', 61))));
        Assert.True(validator.Validate(new BankClaimRequest("dG9rZW4=", "Chase")).IsValid);
    }

    [Fact]
    public void Patch_needs_at_least_one_field()
    {
        var validator = new BankLinkPatchRequestValidator();

        Assert.Equal(["name"], Fields(validator, new BankLinkPatchRequest(null, null)));
        Assert.True(validator.Validate(new BankLinkPatchRequest(null, false)).IsValid);
        Assert.True(validator.Validate(new BankLinkPatchRequest("Chase", null)).IsValid);
    }

    [Fact]
    public void Mapping_takes_an_account_id_or_a_create_spec_not_both()
    {
        var validator = new ExternalAccountMapRequestValidator();

        Assert.True(validator.Validate(new ExternalAccountMapRequest(Account, null)).IsValid);
        Assert.True(validator.Validate(new ExternalAccountMapRequest(null, null)).IsValid); // unmap
        Assert.True(validator.Validate(new ExternalAccountMapRequest(null, new ExternalAccountCreateSpec("Visa", AccountType.Credit, null))).IsValid);
        Assert.Equal(["accountId"], Fields(validator, new ExternalAccountMapRequest(Account, new ExternalAccountCreateSpec("Visa", null, null))));
        Assert.Equal(["create.name"], Fields(validator, new ExternalAccountMapRequest(null, new ExternalAccountCreateSpec("", null, null))));
    }

    [Theory]
    [InlineData("Visa Signature", AccountType.Credit)]
    [InlineData("Online Savings", AccountType.Savings)]
    [InlineData("Auto Loan", AccountType.Loan)]
    [InlineData("Brokerage", AccountType.Investment)]
    [InlineData("Total Checking", AccountType.Checking)]
    public void Mapping_create_guesses_the_account_type_from_the_bank_name(string name, AccountType type)
    {
        Assert.Equal(type, BankEndpoints.GuessType(name));
    }

    // ---------------------------------------------------------------------------------------------
    // Inbox cursor
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Inbox_cursor_round_trips_and_accepts_a_bare_date()
    {
        var id = Guid.Parse("00000000-0000-7000-8000-0000000000aa");

        Assert.True(InboxEndpoints.TryParseCursor($"2026-10-23_{id}", out var cursor));
        Assert.Equal(new DateOnly(2026, 10, 23), cursor.PostedOn);
        Assert.Equal(id, cursor.Id);

        Assert.True(InboxEndpoints.TryParseCursor("2026-10-23", out var bare));
        Assert.Equal(Guid.Empty, bare.Id);

        Assert.False(InboxEndpoints.TryParseCursor("yesterday", out _));
        Assert.False(InboxEndpoints.TryParseCursor("2026-10-23_nope", out _));
    }
}
