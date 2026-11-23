using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LedgerPay.Api.Features;
using LedgerPay.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace LedgerPay.Api.Tests;

/// <summary>One Postgres container and one app (projector running) for the whole suite.</summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task InitializeAsync() => _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Ledger", _db.GetConnectionString());

    public async Task<Customer> RegisterAsync(string name = "Ali")
    {
        var res = await CreateClient().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest($"{name.ToLower()}-{Guid.NewGuid():N}@example.com", name, "Passw0rd!"), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return new Customer(this, (await res.Content.ReadFromJsonAsync<SessionDto>(Json))!);
    }

    public async Task<Customer> AdminAsync()
    {
        var res = await CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@ledgerpay.dev", "Admin#2025"), Json);
        return new Customer(this, (await Ok<SessionDto>(res))!);
    }

    public static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;

public record Accepted<T>(T Result, long Position);
public record MoveResult(Guid Id, string Number, decimal Balance, bool Frozen);

public record Customer(ApiFixture App, SessionDto Session)
{
    public HttpClient Http
    {
        get
        {
            var c = App.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Session.Token);
            return c;
        }
    }

    public async Task<T> Get<T>(string url) => await ApiFixture.Ok<T>(await Http.GetAsync(url));
    public async Task<T> Post<T>(string url, object body) => await ApiFixture.Ok<T>(await Http.PostAsJsonAsync(url, body, ApiFixture.Json));

    public async Task<AccountView> OpenWallet(string currency = "PKR", decimal deposit = 0)
    {
        var opened = await Post<Accepted<AccountView>>("/api/accounts", new OpenAccountRequest("Main", currency));
        if (deposit > 0) await Post<Accepted<MoveResult>>($"/api/accounts/{opened.Result.Id}/deposit", new MoneyRequest(deposit, "top-up"));
        return opened.Result;
    }

    public async Task<AccountView> Wallet(Guid id, long after = 0) => await Get<AccountView>($"/api/accounts/{id}?after={after}");

    public Task<HttpResponseMessage> Send(Guid from, string toNumber, decimal amount, string? key = null, string memo = "test") =>
        SendWithKey(from, toNumber, amount, key ?? Guid.NewGuid().ToString(), memo);

    private async Task<HttpResponseMessage> SendWithKey(Guid from, string toNumber, decimal amount, string key, string memo)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/transfers")
        {
            Content = JsonContent.Create(new TransferRequest(from, toNumber, amount, memo), options: ApiFixture.Json),
        };
        req.Headers.Add("Idempotency-Key", key);
        return await Http.SendAsync(req);
    }

    public async Task<Accepted<TransferView>> Transfer(Guid from, string toNumber, decimal amount, string? key = null) =>
        await ApiFixture.Ok<Accepted<TransferView>>(await Send(from, toNumber, amount, key));
}
