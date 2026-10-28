SET NOCOUNT ON;

IF DB_ID(N'byom') IS NULL CREATE DATABASE byom;
GO
USE byom;
GO

DROP TABLE IF EXISTS dbo.work_order_history;
DROP TABLE IF EXISTS dbo.work_orders;
DROP TABLE IF EXISTS dbo.assets;

CREATE TABLE dbo.assets
(
    asset_id varchar(8) NOT NULL PRIMARY KEY,
    asset_name varchar(80) NOT NULL,
    asset_type varchar(20) NOT NULL,
    active bit NOT NULL
);

CREATE TABLE dbo.work_orders
(
    work_order_id varchar(8) NOT NULL PRIMARY KEY,
    asset_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    created_at date NOT NULL,
    description varchar(120) NOT NULL
);

CREATE TABLE dbo.work_order_history
(
    history_id varchar(8) NOT NULL PRIMARY KEY,
    work_order_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    changed_at date NOT NULL,
    note varchar(120) NOT NULL
);

DECLARE @i int = 1;
WHILE @i <= 12
BEGIN
    INSERT dbo.assets(asset_id, asset_name, asset_type, active)
    VALUES
    (
        CONCAT('A', RIGHT(CONCAT('000', @i), 3)),
        CONCAT('Synthetic Asset ', RIGHT(CONCAT('000', @i), 3)),
        CASE WHEN @i % 2 = 0 THEN 'VEHICLE' ELSE 'FACILITY' END,
        CASE WHEN @i % 5 = 0 THEN 0 ELSE 1 END
    );
    SET @i += 1;
END;

SET @i = 1;
WHILE @i <= 30
BEGIN
    INSERT dbo.work_orders(work_order_id, asset_id, status, created_at, description)
    VALUES
    (
        CONCAT('W', RIGHT(CONCAT('0000', @i), 4)),
        CONCAT('A', RIGHT(CONCAT('000', ((@i - 1) % 12) + 1), 3)),
        CASE WHEN @i % 3 = 0 THEN 'CLOSED' WHEN @i % 3 = 1 THEN 'OPEN' ELSE 'NEW' END,
        DATEADD(day, @i, CONVERT(date, '2025-01-01')),
        CONCAT('Synthetic work order ', RIGHT(CONCAT('0000', @i), 4))
    );
    SET @i += 1;
END;

SET @i = 1;
WHILE @i <= 60
BEGIN
    INSERT dbo.work_order_history(history_id, work_order_id, status, changed_at, note)
    VALUES
    (
        CONCAT('H', RIGHT(CONCAT('0000', @i), 4)),
        CONCAT('W', RIGHT(CONCAT('0000', ((@i - 1) / 2) + 1), 4)),
        CASE WHEN @i % 2 = 0 THEN 'CLOSED' ELSE 'OPEN' END,
        DATEADD(day, @i, CONVERT(date, '2025-01-01')),
        CONCAT('Synthetic history event ', RIGHT(CONCAT('0000', @i), 4))
    );
    SET @i += 1;
END;
GO

IF DB_ID(N'byom_shadow') IS NULL CREATE DATABASE byom_shadow;
GO
USE byom_shadow;
GO

DROP TABLE IF EXISTS dbo.work_order_history;
DROP TABLE IF EXISTS dbo.asset_budgets;
DROP TABLE IF EXISTS dbo.work_orders;
DROP TABLE IF EXISTS dbo.assets;

CREATE TABLE dbo.assets
(
    asset_id varchar(8) NOT NULL PRIMARY KEY,
    asset_name varchar(80) NOT NULL,
    asset_type varchar(20) NOT NULL,
    active bit NOT NULL
);

CREATE TABLE dbo.work_orders
(
    work_order_id varchar(8) NOT NULL PRIMARY KEY,
    asset_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    created_at date NOT NULL,
    description varchar(120) NOT NULL
);

CREATE TABLE dbo.work_order_history
(
    history_id varchar(8) NOT NULL PRIMARY KEY,
    work_order_id varchar(8) NOT NULL,
    status varchar(16) NOT NULL,
    changed_at date NOT NULL,
    note varchar(120) NOT NULL
);

CREATE TABLE dbo.asset_budgets
(
    budget_id varchar(8) NOT NULL PRIMARY KEY,
    budget_amount decimal(12,2) NOT NULL
);

INSERT dbo.assets(asset_id, asset_name, asset_type, active)
SELECT asset_id, asset_name, asset_type, active FROM byom.dbo.assets;

INSERT dbo.work_orders(work_order_id, asset_id, status, created_at, description)
SELECT work_order_id, asset_id, status, created_at, description FROM byom.dbo.work_orders;

INSERT dbo.work_order_history(history_id, work_order_id, status, changed_at, note)
SELECT history_id, work_order_id, status, changed_at, note FROM byom.dbo.work_order_history;

DECLARE @i int = 1;
WHILE @i <= 12
BEGIN
    INSERT dbo.asset_budgets(budget_id, budget_amount)
    VALUES (CONCAT('B', RIGHT(CONCAT('000', @i), 3)), 1000 + (@i * 10));
    SET @i += 1;
END;
GO
