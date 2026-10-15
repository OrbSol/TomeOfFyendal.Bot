using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomeOfFyendal.Bot;
using TomeOfFyendal.Bot.Data;
using VaultSharp;
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
    // Exactly two secrets, read by name (the bot's Vault policy allows only these): its own database
    // login, and the bot token, shared with the website so a token reset is one update.
    var vault = new VaultClient(new VaultClientSettings(
        builder.Configuration["Vault:Uri"] ?? "http://127.0.0.1:8200", new AppRoleAuthMethodInfo(vaultRoleId, vaultSecretId)));
    async Task<string?> ReadAsync(string path, string key) =>
        (await vault.V1.Secrets.KeyValue.V2.ReadSecretAsync(path, mountPoint: "secret")).Data.Data.TryGetValue(key, out var v) ? v?.ToString() : null;
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = await ReadAsync("clients/tome-bot/database", "ConnectionString"),
        ["Bot:BotToken"] = await ReadAsync("clients/tome/discord", "BotToken"),
    });
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
