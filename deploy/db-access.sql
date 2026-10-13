-- Database rights for the bot: write its three Discord tables, read the card catalog and prices.
-- No DDL, no access to anything else. The tables are created by the website's migrations.
--
-- One time, as an admin, in master (password generated, then stored only in Vault):
--   CREATE LOGIN [tome_bot] WITH PASSWORD = '<generated>', CHECK_POLICY = ON;
-- Then run this file:
--   sqlcmd -S <server> -U <admin> -C -I -i db-access.sql
-- Vault (on the VPS): secret/clients/tome-bot/database, key ConnectionString. Same format as the
-- website's (Server=127.0.0.1,1433, Database=TomeOfFyendalDB, Encrypt, TrustServerCertificate)
-- with User Id=tome_bot and the generated password.

USE [TomeOfFyendalDB];
GO

IF SUSER_ID(N'tome_bot') IS NULL
    THROW 50000, 'Login tome_bot does not exist yet: run the CREATE LOGIN step from this file''s header first.', 1;
IF USER_ID(N'tome_bot') IS NULL CREATE USER [tome_bot] FOR LOGIN [tome_bot];

GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.FabDiscordListings     TO [tome_bot];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.FabDiscordListingItems TO [tome_bot];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.FabDiscordSellers      TO [tome_bot];   -- DELETE: retention purge

GRANT SELECT ON dbo.FabCards          TO [tome_bot];
GRANT SELECT ON dbo.FabPrintings      TO [tome_bot];
GRANT SELECT ON dbo.FabPriceSnapshots TO [tome_bot];
GRANT SELECT ON dbo.FabTcgProducts    TO [tome_bot];   -- product names, for cards the card dataset has not added yet
GO
