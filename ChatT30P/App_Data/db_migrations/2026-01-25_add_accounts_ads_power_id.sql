IF COL_LENGTH('dbo.accounts', 'ads_power_id') IS NULL
BEGIN
    ALTER TABLE dbo.accounts
    ADD ads_power_id NVARCHAR(128) NULL;
END
