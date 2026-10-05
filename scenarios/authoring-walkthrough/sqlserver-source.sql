CREATE TABLE dbo.records (id int NOT NULL PRIMARY KEY, value nvarchar(50) NULL);
INSERT INTO dbo.records(id,value) VALUES (1,N'synthetic-first'),(2,N'synthetic-second');