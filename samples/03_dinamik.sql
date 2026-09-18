CREATE PROCEDURE dbo.uspDinamikYukle
    @Yil int = 2025,
    @Tablo sysname = N'dbo.Stage_Siparis',
    @Filtre nvarchar(200) = NULL
AS
BEGIN
    DECLARE @sql nvarchar(max), @kaynak sysname, @hedef sysname, @n int;

    -- 1) parametre default'u ile çözülen tablo adı
    SET @sql = N'INSERT INTO ' + @Tablo + N' (SiparisId, MusteriId, Tutar) SELECT SiparisId, MusteriId, Tutar FROM dbo.Siparis WHERE YEAR(Tarih) = ' + CAST(@Yil AS nvarchar(4));
    EXEC sp_executesql @sql;

    -- 2) ad parçası deseni: dbo.Satis_2024 / dbo.Satis_2025
    SET @sql = N'INSERT INTO dbo.Satis_' + CAST(@Yil AS nvarchar(4)) + N' (SiparisId, Tutar) SELECT SiparisId, Tutar FROM dbo.Siparis';
    EXEC (@sql);

    -- 3) sp_executesql parametreli, OUTPUT
    EXEC sp_executesql N'SELECT @cnt = COUNT(*) FROM dbo.Musteri WHERE Il = @il', N'@il nvarchar(50), @cnt int OUTPUT', @il = N'Ankara', @cnt = @n OUTPUT;
    INSERT INTO dbo.Log (Mesaj, Tarih) VALUES (CAST(@n AS nvarchar(20)), GETDATE());

    -- 4) config tablosu + cursor ile dinamik
    DECLARE c CURSOR FOR SELECT KaynakTablo, HedefTablo FROM dbo.ETL_Config WHERE Aktif = 1;
    OPEN c;
    FETCH NEXT FROM c INTO @kaynak, @hedef;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'INSERT INTO ' + QUOTENAME(@hedef) + N' SELECT * FROM ' + QUOTENAME(@kaynak);
        IF @Filtre IS NOT NULL SET @sql = @sql + N' WHERE ' + @Filtre;
        EXEC (@sql);
        FETCH NEXT FROM c INTO @kaynak, @hedef;
    END
    CLOSE c; DEALLOCATE c;

    -- 5) tamamen literal dinamik
    EXEC (N'UPDATE dbo.Urun SET Fiyat = Fiyat * 1.1 WHERE Kategori = N''Altın''');

    -- 6) dinamik prosedür adı
    DECLARE @proc sysname = N'dbo.uspMusteriOzetYukle';
    EXEC @proc @Il = N'İzmir';

    -- 7) INSERT ... EXEC
    INSERT INTO dbo.Log (Mesaj, LogId, Tarih) EXEC dbo.uspMusteriOzetYukle @Il = N'Bursa';
END
GO
CREATE PROCEDURE dbo.uspCagiran AS
BEGIN
    EXEC dbo.uspDinamikYukle @Yil = 2024, @Tablo = N'dbo.Stage_Musteri';
    EXEC dbo.uspMusteriOzetYukle N'Ankara';
END
GO
