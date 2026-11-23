using System.Text.Json.Serialization;
using FluentValidation;
using LedgerPay.Api.Features;
using LedgerPay.Api.Infrastructure;
using LedgerPay.Api.Projections;
using LedgerPay.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi.Models;
using Npgsql;
using Store = LedgerPay.Api.EventStore.EventStore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config.GetConnectionString("Ledger") ?? "Host=localhost;Database=ledgerpay;Username=ledgerpay;Password=ledgerpay";
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Projector>();
builder.Services.AddSingleton<ReadModels>();
builder.Services.AddSingleton<Users>();
builder.Services.AddSingleton(FraudEngine.Default);
builder.Services.AddSingleton<AccountService>();
builder.Services.AddSingleton<TransferService>();
if (config.GetValue("Projector:Enabled", true)) builder.Services.AddHostedService<ProjectorService>();

var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = jwt.Validation();
});
builder.Services.AddAuthorizationBuilder().AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin));

builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "database");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "LedgerPay API", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    o.AddSecurityDefinition("Bearer", scheme);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
Endpoints.Map(app);

await Store.InitialiseAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);
var users = app.Services.GetRequiredService<Users>();
if (await users.FindAsync(config["Seed:AdminEmail"] ?? "admin@ledgerpay.dev") is null)
    await users.CreateAsync(config["Seed:AdminEmail"] ?? "admin@ledgerpay.dev", "LedgerPay Reviewer", config["Seed:AdminPassword"] ?? "Admin#2025", Roles.Admin);

app.Run();

public partial class Program;
