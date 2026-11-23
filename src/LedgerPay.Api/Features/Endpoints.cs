using System.Security.Claims;
using FluentValidation;
using LedgerPay.Api.EventStore;
using LedgerPay.Api.Infrastructure;
using LedgerPay.Api.Projections;
using LedgerPay.Domain;

namespace LedgerPay.Api.Features;

public record RegisterRequest(string Email, string FullName, string Password);
public record LoginRequest(string Email, string Password);
public record OpenAccountRequest(string Name, string Currency);
public record MoneyRequest(decimal Amount, string? Reference);
public record TransferRequest(Guid FromAccountId, string ToAccountNumber, decimal Amount, string? Memo);
public record ReasonRequest(string Reason);
/// <summary>Command responses carry the event position so the client can ask reads to wait for it.</summary>
public record Accepted<T>(T Result, long Position);

public class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).Matches("[A-Z]").Matches("[0-9]")
            .WithMessage("Password needs 8+ characters with an uppercase letter and a digit.");
    }
}

public class TransferValidator : AbstractValidator<TransferRequest>
{
    public TransferValidator()
    {
        RuleFor(x => x.FromAccountId).NotEmpty();
        RuleFor(x => x.ToAccountNumber).NotEmpty().Matches("^LP[0-9]{10}$").WithMessage("Account numbers look like LP0123456789.");
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(12, 2, true);
        RuleFor(x => x.Memo).MaximumLength(140);
    }
}

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth").AllowAnonymous();
        auth.MapPost("/register", async (RegisterRequest req, IValidator<RegisterRequest> v, Users users, JwtOptions jwt) =>
        {
            await v.ValidateAndThrowAsync(req);
            var user = await users.CreateAsync(req.Email, req.FullName, req.Password, Roles.Customer);
            return user is null
                ? Results.Problem(statusCode: 409, title: "That email is already registered.")
                : Results.Created("/api/me", jwt.Issue(user));
        });
        auth.MapPost("/login", async (LoginRequest req, Users users, JwtOptions jwt) =>
            await users.FindAsync(req.Email) is { } u && users.Verify(u, req.Password)
                ? Results.Ok(jwt.Issue(u))
                : Results.Problem(statusCode: 401, title: "Invalid email or password."));

        var accounts = app.MapGroup("/api/accounts").WithTags("Wallets").RequireAuthorization();
        accounts.MapGet("/", async (ClaimsPrincipal me, ReadModels rm, Projector p, long? after) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await rm.AccountsOfAsync(me.UserId());
        });
        accounts.MapPost("/", async (OpenAccountRequest req, ClaimsPrincipal me, AccountService svc, ReadModels rm, Projector p) =>
        {
            var mine = await rm.AccountsOfAsync(me.UserId());
            if (mine.Count >= AccountService.MaxWalletsPerUser)
                throw new DomainException($"You can have at most {AccountService.MaxWalletsPerUser} wallets.");
            var opened = await svc.OpenAsync(me.UserId(), req.Name, req.Currency.ToUpperInvariant());
            await p.WaitForAsync(opened.Position, TimeSpan.FromSeconds(3));
            return Results.Created($"/api/accounts/{opened.Value.AccountId}",
                new Accepted<AccountView?>(await rm.AccountAsync(opened.Value.AccountId), opened.Position));
        });
        accounts.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal me, ReadModels rm, Projector p, long? after) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await Owned(id, me, rm) is { } a ? Results.Ok(a) : Results.NotFound();
        });
        accounts.MapGet("/{id:guid}/activity", async (Guid id, ClaimsPrincipal me, ReadModels rm, Projector p, long? after, int? take) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await Owned(id, me, rm) is null ? Results.NotFound() : Results.Ok(await rm.ActivityAsync(id, Math.Clamp(take ?? 50, 1, 500)));
        });
        accounts.MapGet("/{id:guid}/events", async (Guid id, ClaimsPrincipal me, ReadModels rm, EventStore.EventStore store) =>
            await Owned(id, me, rm) is null ? Results.NotFound()
                : Results.Ok((await store.ReadStreamAsync(Streams.Account(id))).Select(e => new { e.Position, e.Version, e.Type, e.CreatedAt, Data = (object)e.Data })));
        accounts.MapPost("/{id:guid}/deposit", (Guid id, MoneyRequest req, ClaimsPrincipal me, AccountService svc, ReadModels rm) =>
            Move(id, me, rm, () => svc.DepositAsync(id, Amounts.ToMinor(req.Amount), req.Reference?.Trim() ?? "")));
        accounts.MapPost("/{id:guid}/withdraw", (Guid id, MoneyRequest req, ClaimsPrincipal me, AccountService svc, ReadModels rm) =>
            Move(id, me, rm, () => svc.WithdrawAsync(id, Amounts.ToMinor(req.Amount), req.Reference?.Trim() ?? "")));

        app.MapGet("/api/payees/{number}", async (string number, ReadModels rm) =>
            await rm.PayeeAsync(number) is { } p ? Results.Ok(p) : Results.NotFound()).WithTags("Wallets").RequireAuthorization();

        var transfers = app.MapGroup("/api/transfers").WithTags("Transfers").RequireAuthorization();
        transfers.MapGet("/", async (ClaimsPrincipal me, ReadModels rm, Projector p, long? after) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await rm.TransfersForUserAsync(me.UserId());
        });
        transfers.MapPost("/", async (TransferRequest req, HttpRequest http, ClaimsPrincipal me, IValidator<TransferRequest> v,
            TransferService svc, ReadModels rm) =>
        {
            if (http.Headers["Idempotency-Key"].FirstOrDefault() is not { Length: > 0 and <= 100 } key)
                return Results.Problem(statusCode: 400, title: "Send an Idempotency-Key header (1–100 characters) with every transfer.");
            await v.ValidateAndThrowAsync(req);
            var payee = await rm.PayeeAsync(req.ToAccountNumber);
            if (payee is null) return Results.Problem(statusCode: 404, title: "No wallet with that number.");
            var result = await svc.RequestAsync(me.UserId(), key, req.FromAccountId, payee.Id, Amounts.ToMinor(req.Amount), req.Memo ?? "");
            return result.Value.Replayed
                ? Results.Ok(new Accepted<TransferView>(result.Value, result.Position))
                : Results.Created($"/api/transfers/{result.Value.Id}", new Accepted<TransferView>(result.Value, result.Position));
        });

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization(Roles.Admin);
        admin.MapGet("/reviews", async (ReadModels rm, Projector p, long? after) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await rm.TransfersByStatusAsync(nameof(TransferStatus.PendingReview));
        });
        admin.MapPost("/transfers/{id:guid}/approve", async (Guid id, ClaimsPrincipal me, TransferService svc) =>
            Results.Ok(Accept(await svc.ApproveAsync(id, me.UserId()))));
        admin.MapPost("/transfers/{id:guid}/reject", async (Guid id, ReasonRequest req, TransferService svc) =>
            Results.Ok(Accept(await svc.RejectAsync(id, string.IsNullOrWhiteSpace(req.Reason) ? "Rejected by reviewer" : req.Reason.Trim()))));
        admin.MapPost("/accounts/{id:guid}/freeze", async (Guid id, ReasonRequest req, AccountService svc) =>
            Results.Ok(Accept(await svc.FreezeAsync(id, req.Reason), Summary)));
        admin.MapPost("/accounts/{id:guid}/unfreeze", async (Guid id, AccountService svc) =>
            Results.Ok(Accept(await svc.UnfreezeAsync(id), Summary)));
        admin.MapGet("/trial-balance", async (ReadModels rm, Projector p, long? after) =>
        {
            await p.WaitForAsync(after ?? 0, TimeSpan.FromSeconds(3));
            return await rm.TrialBalanceAsync();
        });
        admin.MapGet("/events", async (EventStore.EventStore store, long? after, int? take) =>
            (await store.ReadGlobalAsync(after ?? 0, Math.Clamp(take ?? 50, 1, 500)))
                .Select(e => new { e.Position, e.StreamId, e.Version, e.Type, e.CreatedAt, Data = (object)e.Data }));
        admin.MapPost("/projections/rebuild", async (Projector p) => Results.Ok(new { replayed = await p.RebuildAsync() }));
    }

    private static object Summary(Account a) => new { a.Id, a.Number, Balance = a.Balance / 100m, a.Frozen, a.Version };

    private static Accepted<T> Accept<T>(CommandResult<T> r) => new(r.Value, r.Position);
    private static Accepted<object> Accept(CommandResult<Account> r, Func<Account, object> view) => new(view(r.Value), r.Position);

    private static async Task<AccountView?> Owned(Guid id, ClaimsPrincipal me, ReadModels rm) =>
        await rm.AccountAsync(id) is { } a && (a.OwnerId == me.UserId() || me.IsAdmin()) ? a : null;

    private static async Task<IResult> Move(Guid id, ClaimsPrincipal me, ReadModels rm, Func<Task<CommandResult<Account>>> command)
    {
        if (await Owned(id, me, rm) is null) return Results.NotFound();
        var r = await command();
        return Results.Ok(new Accepted<object>(Summary(r.Value), r.Position));
    }
}
