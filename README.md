# Tome of Fyendal — Discord bot

The Discord bot behind Tome of Fyendal, a community-run Flesh and Blood price tracker. It reads the
marketplace channels of one Discord server and turns WTS/WTB/WTT posts into listings that the website
shows next to TCGplayer prices. This repository is the bot only, so it can be reviewed on its own.

.NET 9 worker service · Discord.Net · EF Core / SQL Server · ~900 lines.

## What it does and does not do

**Does**
- Listens to the configured marketplace channels and, once at startup, reads up to 30 days of their history.
- Parses each post into card lines (name, set number, finish, condition, quantity, price), matches them
  to cards from the open [the-fab-cube](https://github.com/the-fab-cube/flesh-and-blood-cards) dataset,
  and stores those parsed facts.
- Follows edits and deletions: an edited post is re-parsed, a deleted post is marked removed, including
  posts deleted while the bot was offline. Listings expire after 30 days.
- Deletes for good: a listing is permanently deleted 7 days after it leaves the board (deleted, edited
  into something else, or expired). A member's row is deleted once they have no listings, unless they
  opted out (kept so the opt-out is honoured) or were reported.
- Respects opt-out: members can remove their data and opt out on the website; their posts are skipped.
- Reads `#paypal-watchlist`: a post that names **exactly one** member marks that member "reported before".
  A post naming several people changes nothing, so reporters and victims are never marked.
- Keeps Discord's own link to a post's first picture, with the expiry Discord puts in that link (about a day).
  The website shows the picture to signed-in members of the server until then, loading it from Discord.

**Never**
- Posts, replies, reacts, edits, deletes, pins, DMs or joins voice. There is no code path that sends
  anything to Discord except reading messages.
- Stores message text. Only the parsed fields and a generated one-line summary ("WTS · 3 cards · $12 to
  $95") are kept. The original post stays in Discord.
- Downloads, copies or re-hosts pictures. Once Discord's link expires the picture is simply not shown.
- Lists members, tracks presence or builds profiles. No seller scores or ratings are kept.
- Uses message content for AI training, or sells or shares data.

## Discord permissions

| | |
|---|---|
| Channel permissions | View Channels, Read Message History — on the marketplace channels and `#paypal-watchlist` only |
| Gateway intents | `Guilds`, `GuildMessages`, `MessageContent` (privileged) |
| Not requested | `GuildMembers`, `GuildPresences`, any send/manage permission |

Message Content is needed because marketplace posts are free text that already exists; a slash command
cannot replace reading them.

## What is stored

Code: [`src/TomeOfFyendal.Bot/Data/BotDbContext.cs`](src/TomeOfFyendal.Bot/Data/BotDbContext.cs) — the
complete list of tables and columns the bot can see.

| Table | Contents |
|---|---|
| `FabDiscordListings` | message/channel/author ids, author display name, post time, type, status, generated summary, flags, Discord's link to the first picture and its expiry |
| `FabDiscordListingItems` | per card line: matched card, set code, finish, condition, special version (Marvel, EA, graded…), price, quantity. Lines that match no card are not stored |
| `FabDiscordSellers` | author id, first/last seen, opted out, reported before |

It reads (never writes) the card catalog, TCGplayer product names (for new sets the card dataset has not
added yet) and daily TCGplayer prices, to match card names and to flag an
ask 25% or more under market for the **same finish**. Its database login has exactly those rights
([`deploy/db-access.sql`](deploy/db-access.sql)).

On the website, seller names and "jump to post" links are shown only to visitors signed in with Discord
who are members of the server. Everyone else sees "Server member".

## Flags (advisory)

A listing is flagged when the seller was reported before, the post mentions payment outside Discord or
PayPal Goods & Services (Venmo, Cash App, Zelle, F&F), or an ask is 25%+ under the TCGplayer market.

## Code map

```
src/TomeOfFyendal.Bot/
  DiscordMarketBot.cs        gateway events, backfill, edits/deletes, watchlist, expiry
  DiscordListingParser.cs    post text -> card lines (pure, unit tested)
  FabCardResolver.cs         card line -> card and TCGplayer product
  MarketPrice.cs             which market price an ask is compared with
  Data/BotDbContext.cs       the tables the bot touches
  Program.cs, BotSettings.cs configuration
tests/                       parser and price tests
deploy/                      systemd unit, deploy script, database rights
```

## Run

Secrets (bot token, connection string) come from HashiCorp Vault in production and `dotnet user-secrets`
locally; never from tracked files. A pre-commit hook and a CI gitleaks scan guard against leaks
(`git config core.hooksPath .githooks` once per clone).

```powershell
dotnet user-secrets set "Bot:BotToken" "<token>" --project src/TomeOfFyendal.Bot
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<connection string>" --project src/TomeOfFyendal.Bot
dotnet user-secrets set "Bot:GuildId" "<server id>" --project src/TomeOfFyendal.Bot
dotnet user-secrets set "Bot:ListingChannelIds:0" "<channel id>" --project src/TomeOfFyendal.Bot
$env:DOTNET_ENVIRONMENT = 'Development'   # user-secrets are only read in Development
dotnet run --project src/TomeOfFyendal.Bot
dotnet test TomeOfFyendal.Bot.slnx
```
