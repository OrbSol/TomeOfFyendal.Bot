using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomeOfFyendal.Bot;
using TomeOfFyendal.Bot.Data;
using VaultSharp;
using VaultSharp.Extensions.Configuration;
using VaultSharp.V1.AuthMethods.AppRole;

// Flag text formats percentages; the Linux host has no locale, so pin en-US.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");

var builder = Host.CreateApplicationBuilder(args);

// Secrets: Vault (production) → environment variables → user-secrets (development). Never appsettings.
var vaultRoleId = Environment.GetEnvironmentVariable("VAULT_ROLE_ID");
var vaultSecretId = Environment.GetEnvironmentVariable("VAULT_SECRET_ID");
if (!string.IsNullOrEmpty(vaultRoleId) && !string.IsNullOrEmpty(vaultSecretId))
{
    var vaultUri = builder.Configuration["Vault:Uri"] ?? "http://127.0.0.1:8200";
    var vaultPath = builder.Configuration["Vault:Path"] ?? "clients/tome-bot";
    builder.Configuration.AddVaultConfiguration(
        () => new VaultOptions(vaultUri, new AppRoleAuthMethodInfo(vaultRoleId, vaultSecretId)),
        vaultPath, "secret");

    // Vault sub-paths arrive as "subpath:key"; map them onto the keys the bot reads.
    var mapped = new Dictionary<string, string?>();
    void Map(string vaultKey, string appKey)
    {
        var v = builder.Configuration[vaultKey];
        if (!string.IsNullOrWhiteSpace(v)) mapped[appKey] = v;
    }
    Map("database:ConnectionString", "ConnectionStrings:DefaultConnection");
    Map("discord:BotToken", "Bot:BotToken");
    builder.Configuration.AddInMemoryCollection(mapped);
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not set (Vault, environment variable or user-secrets).");

builder.Services.AddDbContext<BotDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<FabCardResolver>();
builder.Services.AddOptions<BotSettings>()
    .Bind(builder.Configuration.GetSection("Bot"))
    .Validate(s => !string.IsNullOrWhiteSpace(s.BotToken) && s.GuildId != 0, "Bot:BotToken and Bot:GuildId are required")
    .ValidateOnStart();
builder.Services.AddHostedService<DiscordMarketBot>();

builder.Build().Run();
