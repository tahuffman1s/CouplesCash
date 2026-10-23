using System.Security.Claims;
using CouplesCash.Api.Auth;
using CouplesCash.Api.Contracts;
using CouplesCash.Api.Data;
using CouplesCash.Api.Infrastructure;
using CouplesCash.Api.Services;
using CouplesCash.Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace CouplesCash.Api.Endpoints;

public static class TodoEndpoints
{
    public const int DoneWindowDays = 30;

    public static RouteGroupBuilder MapTodos(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/todos").WithTags("To-pay").RequireAuthorization().RequireHousehold();

        group.MapGet("", List).WithSummary("Open items by sort order; ?includeDone=true appends items done in the last 30 days, newest first");
        group.MapPost("", Create).WithValidation<TodoRequest>().WithSummary("Add a to-pay item");
        group.MapPut("/order", Reorder).WithValidation<TodoOrderRequest>().WithSummary("Reorder items: sortOrder = position in ids");
        group.MapPut("/{id:guid}", Update).WithValidation<TodoRequest>().WithSummary("Edit a to-pay item");
        group.MapPost("/{id:guid}/done", Done).WithSummary("Check off an item (records who and when)");
        group.MapDelete("/{id:guid}/done", Undone).WithSummary("Un-check an item");
        group.MapDelete("/{id:guid}", Delete).WithSummary("Delete an item");

        return group;
    }

    private static async Task<Ok<List<TodoDto>>> List(ClaimsPrincipal principal, TodoRepository todos, TimeProvider time, CancellationToken ct, bool includeDone = false)
    {
        var rows = await todos.ListAsync(principal.GetHouseholdId(), includeDone, time.Now(), DoneWindowDays, ct);
        return TypedResults.Ok(rows.Select(t => t.ToDto()).ToList());
    }

    private static async Task<Created<TodoDto>> Create(
        TodoRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        TodoRepository todos,
        HouseholdRepository households,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();
        var currency = await households.CurrencyAsync(householdId, ct);

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await todos.CreateAsync(uow.Connection, householdId, userId, request.Title.Trim(), request.AmountCents, request.DueDate, time.Now(), ct);
        await activity.RecordAsync(uow, householdId, userId, "todo.created", "todo", row.Id,
            ActivitySummaries.TodoCreated(row, currency), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.Created($"/v1/todos/{row.Id}", row.ToDto());
    }

    private static async Task<Results<Ok<TodoDto>, NotFound>> Update(
        Guid id,
        TodoRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        TodoRepository todos,
        TimeProvider time,
        CancellationToken ct)
    {
        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await todos.UpdateAsync(uow.Connection, principal.GetHouseholdId(), id, request.Title.Trim(), request.AmountCents, request.DueDate, request.SortOrder, time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto());
    }

    private static Task<Results<Ok<TodoDto>, NotFound>> Done(Guid id, ClaimsPrincipal principal, NpgsqlDataSource db, TodoRepository todos, ActivityService activity, TimeProvider time, CancellationToken ct) =>
        SetDone(id, true, principal, db, todos, activity, time, ct);

    private static Task<Results<Ok<TodoDto>, NotFound>> Undone(Guid id, ClaimsPrincipal principal, NpgsqlDataSource db, TodoRepository todos, ActivityService activity, TimeProvider time, CancellationToken ct) =>
        SetDone(id, false, principal, db, todos, activity, time, ct);

    private static async Task<Results<Ok<TodoDto>, NotFound>> SetDone(
        Guid id,
        bool done,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        TodoRepository todos,
        ActivityService activity,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var before = await todos.GetAsync(uow.Connection, householdId, id, ct);
        if (before is null)
        {
            return TypedResults.NotFound();
        }

        var row = await todos.SetDoneAsync(uow.Connection, householdId, id, userId, done, time.Now(), ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        // Only a real transition is worth a feed entry (checking an already-done item is a no-op).
        if ((before.DoneAt is null) == done)
        {
            await activity.RecordAsync(uow, householdId, userId, done ? "todo.done" : "todo.undone", "todo", row.Id,
                done ? ActivitySummaries.TodoDone(row) : ActivitySummaries.TodoUndone(row), Data(row), ct, principal.GetDisplayName());
        }

        await uow.CommitAsync(ct);
        return TypedResults.Ok(row.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid id,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        TodoRepository todos,
        ActivityService activity,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();
        var userId = principal.GetUserId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        var row = await todos.DeleteAsync(uow.Connection, householdId, id, ct);
        if (row is null)
        {
            return TypedResults.NotFound();
        }

        await activity.RecordAsync(uow, householdId, userId, "todo.deleted", "todo", row.Id,
            ActivitySummaries.TodoDeleted(row), Data(row), ct, principal.GetDisplayName());
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem>> Reorder(
        TodoOrderRequest request,
        ClaimsPrincipal principal,
        NpgsqlDataSource db,
        TodoRepository todos,
        TimeProvider time,
        CancellationToken ct)
    {
        var householdId = principal.GetHouseholdId();

        await using var uow = await db.BeginUnitOfWorkAsync(ct);
        if (await todos.CountOwnedAsync(uow.Connection, householdId, request.Ids, ct) != request.Ids.Length)
        {
            return Problems.Validation("ids", "Every id must be a to-pay item of this household.");
        }

        await todos.ReorderAsync(uow.Connection, householdId, request.Ids, time.Now(), ct);
        await uow.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static object Data(TodoRow t) => new { todoId = t.Id, amountCents = t.AmountCents, dueDate = t.DueDate };
}
