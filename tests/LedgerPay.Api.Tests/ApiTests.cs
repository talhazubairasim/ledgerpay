using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LedgerPay.Api.Features;
using LedgerPay.Domain;

namespace LedgerPay.Api.Tests;

[Collection("api")]
public class WalletTests(ApiFixture app)
{
    [Fact]
    public async Task Deposit_withdraw_and_read_your_writes()
    {
        var ali = await app.RegisterAsync();
        var wallet = await ali.OpenWallet();
        Assert.Matches("^LP[0-9]{10}$", wallet.Number);
        Assert.Equal(0m, wallet.Balance);

        var dep = await ali.Post<Accepted<MoveResult>>($"/api/accounts/{wallet.Id}/deposit", new MoneyRequest(1500.50m, "card"));
        Assert.Equal(1500.50m, dep.Result.Balance);           // write side answers immediately
        Assert.Equal(1500.50m, (await ali.Wallet(wallet.Id, dep.Position)).Balance);   // read side waits for the projection

        var w = await ali.Post<Accepted<MoveResult>>($"/api/accounts/{wallet.Id}/withdraw", new MoneyRequest(500.25m, "bank"));
        Assert.Equal(1000.25m, (await ali.Wallet(wallet.Id, w.Position)).Balance);

        var over = await ali.Http.PostAsJsonAsync($"/api/accounts/{wallet.Id}/withdraw", new MoneyRequest(5000, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("Insufficient funds", await over.Content.ReadAsStringAsync());
        var cents = await ali.Http.PostAsJsonAsync($"/api/accounts/{wallet.Id}/deposit", new MoneyRequest(1.005m, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, cents.StatusCode);

        var activity = await ali.Get<List<ActivityView>>($"/api/accounts/{wallet.Id}/activity?after={w.Position}");
        Assert.Equal(["Withdrawal", "Deposit"], activity.Select(a => a.Kind));
        Assert.Equal(1000.25m, activity[0].BalanceAfter);

        var events = await ali.Get<List<JsonElement>>($"/api/accounts/{wallet.Id}/events");
        Assert.Equal(["AccountOpened", "FundsDeposited", "FundsWithdrawn"], events.Select(e => e.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task Customers_only_see_their_own_wallets()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var wallet = await ali.OpenWallet(deposit: 10);
        Assert.Equal(HttpStatusCode.NotFound, (await sara.Http.GetAsync($"/api/accounts/{wallet.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sara.Http.PostAsJsonAsync($"/api/accounts/{wallet.Id}/withdraw", new MoneyRequest(1, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.Http.GetAsync("/api/admin/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/accounts")).StatusCode);
        Assert.Empty(await sara.Get<List<AccountView>>("/api/accounts"));

        var payee = await sara.Get<PayeeView>($"/api/payees/{wallet.Number}");
        Assert.Equal("Ali", payee.Owner);
        var dup = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest("admin@ledgerpay.dev", "X", "Passw0rd!"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var bad = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ledgerpay.dev", "wrong"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }
}

[Collection("api")]
public class TransferTests(ApiFixture app)
{
    [Fact]
    public async Task Transfers_move_money_atomically_and_are_idempotent()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var a = await ali.OpenWallet(deposit: 1000);
        var s = await sara.OpenWallet();

        var first = await ali.Send(a.Id, s.Number, 250, key: "rent-jan");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var t = await first.Content.ReadFromJsonAsync<Accepted<TransferView>>(ApiFixture.Json);
        Assert.Equal(TransferStatus.Completed, t!.Result.Status);

        var replay = await ali.Send(a.Id, s.Number, 250, key: "rent-jan");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<Accepted<TransferView>>(ApiFixture.Json))!.Result.Replayed);
        var reused = await ali.Send(a.Id, s.Number, 999, key: "rent-jan");
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        Assert.Equal(750m, (await ali.Wallet(a.Id, t.Position)).Balance);
        Assert.Equal(250m, (await sara.Wallet(s.Id, t.Position)).Balance);
        var history = await sara.Get<List<ActivityView>>($"/api/accounts/{s.Id}/activity");
        Assert.Contains($"Transfer from {a.Number} · test", history[0].Description);
        var mine = await sara.Get<List<TransferRow>>($"/api/transfers?after={t.Position}");
        Assert.Single(mine);

        var noKey = await ali.Http.PostAsJsonAsync("/api/transfers", new TransferRequest(a.Id, s.Number, 1, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ali.Send(a.Id, "LP0000000000", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sara.Send(a.Id, s.Number, 1)).StatusCode);   // not Sara's wallet

        var broke = await ali.Transfer(a.Id, s.Number, 5000);
        Assert.Equal(TransferStatus.Rejected, broke.Result.Status);
        Assert.Equal("Insufficient funds.", broke.Result.RejectionReason);
        Assert.Equal(750m, (await ali.Wallet(a.Id, broke.Position)).Balance);

        var usd = await sara.OpenWallet("USD");
        Assert.Equal(HttpStatusCode.Conflict, (await ali.Send(a.Id, usd.Number, 1)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_transfers_never_overdraw()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var a = await ali.OpenWallet(deposit: 100);
        var s = await sara.OpenWallet();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ali.Transfer(a.Id, s.Number, 30)));
        Assert.Equal(3, results.Count(r => r.Result.Status == TransferStatus.Completed));
        Assert.Equal(2, results.Count(r => r.Result.Status == TransferStatus.Rejected));

        var last = results.Max(r => r.Position);
        Assert.Equal(10m, (await ali.Wallet(a.Id, last)).Balance);
        Assert.Equal(90m, (await sara.Wallet(s.Id, last)).Balance);
    }

    [Fact]
    public async Task Suspicious_transfers_wait_for_a_reviewer()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var admin = await app.AdminAsync();
        var a = await ali.OpenWallet(deposit: 50_000);
        var s = await sara.OpenWallet();

        var big = await ali.Transfer(a.Id, s.Number, 15_000);
        Assert.Equal(TransferStatus.PendingReview, big.Result.Status);
        Assert.Contains(big.Result.Reasons, r => r.StartsWith("Large amount"));
        Assert.Equal(50_000m, (await ali.Wallet(a.Id, big.Position)).Balance);   // nothing moves yet

        var queue = await admin.Get<List<TransferRow>>($"/api/admin/reviews?after={big.Position}");
        var row = Assert.Single(queue, q => q.Id == big.Result.Id);
        Assert.Equal(100, row.Score);

        var approved = await admin.Post<Accepted<TransferView>>($"/api/admin/transfers/{big.Result.Id}/approve", new { });
        Assert.Equal(TransferStatus.Completed, approved.Result.Status);
        Assert.Equal(35_000m, (await ali.Wallet(a.Id, approved.Position)).Balance);
        Assert.Equal(15_000m, (await sara.Wallet(s.Id, approved.Position)).Balance);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Http.PostAsync($"/api/admin/transfers/{big.Result.Id}/approve", null)).StatusCode);

        var second = await ali.Transfer(a.Id, s.Number, 20_000);
        var rejected = await admin.Post<Accepted<TransferView>>($"/api/admin/transfers/{second.Result.Id}/reject", new ReasonRequest("Customer unreachable"));
        Assert.Equal(TransferStatus.Rejected, rejected.Result.Status);
        Assert.Equal(35_000m, (await ali.Wallet(a.Id, rejected.Position)).Balance);
        Assert.DoesNotContain(await admin.Get<List<TransferRow>>($"/api/admin/reviews?after={rejected.Position}"), q => q.Id == second.Result.Id);
    }

    [Fact]
    public async Task Frozen_wallets_cannot_send_or_receive()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var admin = await app.AdminAsync();
        var a = await ali.OpenWallet(deposit: 100);
        var s = await sara.OpenWallet(deposit: 100);

        var frozen = await admin.Post<Accepted<MoveResult>>($"/api/admin/accounts/{s.Id}/freeze", new ReasonRequest("KYC review"));
        Assert.True(frozen.Result.Frozen);
        var toFrozen = await ali.Transfer(a.Id, s.Number, 10);
        Assert.Equal(TransferStatus.Rejected, toFrozen.Result.Status);
        Assert.Contains("frozen", toFrozen.Result.RejectionReason);
        Assert.Equal(HttpStatusCode.Conflict, (await sara.Http.PostAsJsonAsync($"/api/accounts/{s.Id}/withdraw", new MoneyRequest(1, null), ApiFixture.Json)).StatusCode);
        Assert.True((await sara.Wallet(s.Id, frozen.Position)).Frozen);

        var thawed = await admin.Post<Accepted<MoveResult>>($"/api/admin/accounts/{s.Id}/unfreeze", new { });
        Assert.False(thawed.Result.Frozen);
        Assert.Equal(TransferStatus.Completed, (await ali.Transfer(a.Id, s.Number, 10)).Result.Status);
    }
}

[Collection("api")]
public class LedgerTests(ApiFixture app)
{
    [Fact]
    public async Task Books_balance_and_projections_rebuild_to_the_same_state()
    {
        var ali = await app.RegisterAsync();
        var sara = await app.RegisterAsync("Sara");
        var admin = await app.AdminAsync();
        var a = await ali.OpenWallet(deposit: 900);
        var s = await sara.OpenWallet(deposit: 100);
        await ali.Transfer(a.Id, s.Number, 123.45m);
        var last = await sara.Transfer(s.Id, a.Number, 23.45m);
        await sara.Post<Accepted<MoveResult>>($"/api/accounts/{s.Id}/withdraw", new MoneyRequest(50, "atm"));

        var before = await admin.Get<TrialBalance>($"/api/admin/trial-balance?after={last.Position}");
        Assert.True(before.Balanced, string.Join(", ", before.Mismatches));
        var aliBefore = await ali.Wallet(a.Id, last.Position);
        Assert.Equal(800m, aliBefore.Balance);

        var rebuilt = await admin.Post<JsonElement>("/api/admin/projections/rebuild", new { });
        Assert.True(rebuilt.GetProperty("replayed").GetInt32() > 0);
        Assert.Equal(800m, (await ali.Wallet(a.Id)).Balance);
        Assert.Equal(150m, (await sara.Wallet(s.Id)).Balance);
        var after = await admin.Get<TrialBalance>("/api/admin/trial-balance");
        Assert.True(after.Balanced);
        Assert.Equal(before.WalletsChecked, after.WalletsChecked);
        Assert.Contains(after.Lines, l => l.Kind == "total" && l.Total == 0);

        var log = await admin.Get<List<JsonElement>>("/api/admin/events?take=500");
        Assert.Contains(log, e => e.GetProperty("type").GetString() == "TransferCompleted");
    }
}
