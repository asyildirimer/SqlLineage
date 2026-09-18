CREATE PROCEDURE dbo.uspTriggerTetikle AS
BEGIN
    UPDATE dbo.Musteri SET Bakiye = Bakiye * 1.05 WHERE Durum = 'A';   -- trMusteriBakiye tetiklenir
    INSERT INTO dbo.Siparis SELECT MusteriId, Bakiye, GETDATE(), 1, 'WEB' FROM dbo.Musteri;  -- kolon listesiz: identity SiparisId atlanır
END
GO
CREATE PROCEDURE dbo.uspDongu AS
BEGIN
    DECLARE @sql nvarchar(max) = N'SELECT MusteriId, Ad', @i int = 1, @kolon sysname;
    WHILE @i <= 3
    BEGIN
        SET @kolon = CASE @i WHEN 1 THEN N'Soyad' WHEN 2 THEN N'Il' ELSE N'Bakiye' END;
        SET @sql += N', ' + @kolon;
        SET @i += 1;
    END
    SET @sql += N' INTO ##genel FROM dbo.Musteri WHERE Durum = ''A''';
    EXEC (@sql);
    INSERT INTO dbo.Stage_Musteri (MusteriId, Ad, Soyad, Il) SELECT MusteriId, Ad, Soyad, Il FROM ##genel;

    BEGIN TRY
        IF @i > 2 SET @sql = N'DELETE FROM dbo.Log WHERE Tarih < GETDATE()-30' ELSE SET @sql = N'TRUNCATE TABLE dbo.Log';
        EXEC sp_executesql @sql;
    END TRY
    BEGIN CATCH
        INSERT INTO dbo.Log (Mesaj, Tarih) VALUES (ERROR_MESSAGE(), GETDATE());
    END CATCH

    EXEC sp_MSforeachtable @command1 = N'UPDATE STATISTICS ?';
    EXEC (N'SELECT * FROM dbo.Urun') AT [UZAKSRV];
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT UrunAdi, GETDATE() FROM OPENQUERY([UZAKSRV], 'SELECT UrunAdi FROM Katalog.dbo.Urun');
END
GO
CREATE PROCEDURE dbo.uspIsci AS
BEGIN
    -- çağıranın yarattığı #work: ambient temp
    INSERT INTO #work (MusteriId, Toplam) SELECT MusteriId, SUM(Tutar) FROM dbo.Siparis GROUP BY MusteriId;
    UPDATE w SET w.Toplam = w.Toplam + 1 FROM #work w;
END
GO
CREATE PROCEDURE dbo.uspKontrol AS
BEGIN
    CREATE TABLE #work (MusteriId int, Toplam decimal(18,2));
    EXEC dbo.uspIsci;
    INSERT INTO dbo.MusteriOzet (MusteriId, ToplamTutar) SELECT MusteriId, Toplam FROM #work;
    DROP TABLE #work;
    CREATE TABLE #work (Id int, Ad nvarchar(50));   -- aynı ad, farklı şekil
    INSERT INTO #work SELECT MusteriId, Ad FROM dbo.Musteri;
    DECLARE @eski decimal(18,2);
    UPDATE dbo.Musteri SET @eski = Bakiye = Bakiye + 10 WHERE MusteriId = 1;
    INSERT INTO dbo.Log (Mesaj, Tarih) VALUES (CAST(@eski AS nvarchar(30)), GETDATE());
END
GO
SET QUOTED_IDENTIFIER OFF
GO
CREATE PROCEDURE dbo.uspTirnak AS
BEGIN
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT "sabit metin", GETDATE() FROM dbo.Urun;
END
GO
SET QUOTED_IDENTIFIER ON
GO
