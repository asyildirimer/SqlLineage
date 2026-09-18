CREATE TABLE dbo.BATCH_ISLEM (ID int, ISLEM nvarchar(max), AKTIF bit);
GO
CREATE PROCEDURE dbo.uspEskiRaiserror AS
BEGIN
    DECLARE @str3 varchar(max);
    IF NOT EXISTS (SELECT 1 FROM dbo.Musteri) raiserror 60101 'Kullanıcı sistemde bulunamadı!!!'
    SELECT a.MusteriId INTO #x FROM dbo.Musteri a, dbo.Siparis k WHERE a.%%physloc%% = k.%%physloc%%
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT CAST(MusteriId AS nvarchar(20)), GETDATE() FROM #x;
END
GO
CREATE PROCEDURE dbo.uspExecVar @TABLE_NAME sysname = 'Siparis', @REPORT_DATE date = '20260101' AS
BEGIN
    DECLARE @SQL nvarchar(max);
    SET @SQL = 'IF (SELECT COUNT(*) FROM dbo.' + @TABLE_NAME + ' WHERE Tarih = ''' + CAST(@REPORT_DATE AS varchar(10)) + ''') > 0 BEGIN DELETE FROM dbo.' + @TABLE_NAME + ' WHERE Tarih = ''' + CAST(@REPORT_DATE AS varchar(10)) + ''' END';
    EXEC @SQL;
    SET @SQL = 'INSERT INTO dbo.Arsiv_Siparis (SiparisId, MusteriId, Tutar) SELECT SiparisId, MusteriId, Tutar FROM dbo.' + @TABLE_NAME;
    EXEC @SQL;
    DECLARE @proc sysname = 'FILES_' + CAST(YEAR(GETDATE()) AS varchar(4)) + '.sys.sp_executesql';
    EXEC @proc N'INSERT INTO dbo.Log (Mesaj, Tarih) SELECT UrunAdi, GETDATE() FROM dbo.Urun';
END
GO
CREATE PROCEDURE dbo.uspKomutKuyrugu AS
BEGIN
    CREATE TABLE #CommandQueue (Id int, SqlStatement nvarchar(max));
    INSERT INTO #CommandQueue (Id, SqlStatement) VALUES (1, 'TRUNCATE TABLE dbo.Stage_Siparis'), (2, 'INSERT INTO dbo.Stage_Siparis (SiparisId, MusteriId, Tutar) SELECT SiparisId, MusteriId, Tutar FROM dbo.Siparis');
    INSERT INTO #CommandQueue SELECT 3, 'UPDATE dbo.Urun SET Fiyat = Fiyat * 2' UNION ALL SELECT 4, 'DELETE FROM dbo.Log';
    DECLARE @s nvarchar(max);
    DECLARE c CURSOR FOR SELECT SqlStatement FROM #CommandQueue ORDER BY Id;
    OPEN c; FETCH NEXT FROM c INTO @s;
    WHILE @@FETCH_STATUS = 0 BEGIN EXEC (@s); FETCH NEXT FROM c INTO @s; END
    CLOSE c; DEALLOCATE c;
    -- tabloda duran komut: --config-tables olmadan Unresolved + not
    SELECT @s = ISLEM FROM dbo.BATCH_ISLEM WHERE AKTIF = 1;
    EXEC sp_executesql @s;
END
GO
CREATE PROCEDURE dbo.uspKodYaratanTabloOkur AS
BEGIN
    -- RP_MASRAF_YUKU başka bir proc'ta SELECT INTO ile yaratılıyor (aşağıda); burada okunuyor
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT CAST(Toplam AS nvarchar(30)), GETDATE() FROM dbo.RP_MASRAF_YUKU;
    EXEC ('CREATE SYNONYM dbo.SYN_MUSTERI FOR dbo.Musteri');
END
GO
CREATE PROCEDURE dbo.uspKodYaratanTablo AS
BEGIN
    IF OBJECT_ID('dbo.RP_MASRAF_YUKU') IS NOT NULL DROP TABLE dbo.RP_MASRAF_YUKU;
    SELECT MusteriId, SUM(Tutar) AS Toplam INTO dbo.RP_MASRAF_YUKU FROM dbo.Siparis GROUP BY MusteriId;
END
GO
