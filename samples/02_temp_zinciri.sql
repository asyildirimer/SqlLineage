CREATE PROCEDURE dbo.uspMusteriOzetYukle
    @Il nvarchar(50) = NULL,
    @Sayac int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    -- 1. temp: müşteri
    SELECT m.MusteriId, m.Ad, m.Soyad, m.Il
    INTO #musteri
    FROM dbo.Musteri m
    WHERE m.Durum = 'A' AND (@Il IS NULL OR m.Il = @Il);

    -- 2. temp: sipariş toplamları
    CREATE TABLE #siparis (MusteriId int, Toplam decimal(18,2), Adet int, SonTarih datetime);
    INSERT INTO #siparis (MusteriId, Toplam, Adet, SonTarih)
    SELECT s.MusteriId, SUM(s.Tutar), COUNT(*), MAX(s.Tarih)
    FROM dbo.Siparis s
    JOIN #musteri mu ON mu.MusteriId = s.MusteriId
    WHERE s.Durum IN (1, 2)
    GROUP BY s.MusteriId;

    -- 3. temp: birleşim (kolon listesiz INSERT + SELECT *)
    SELECT mu.MusteriId, mu.Ad + N' ' + mu.Soyad AS AdSoyad, ISNULL(sp.Toplam, 0) AS ToplamTutar, ISNULL(sp.Adet, 0) AS SiparisSayisi, sp.SonTarih, mu.Il
    INTO #sonuc
    FROM #musteri mu
    LEFT JOIN #siparis sp ON sp.MusteriId = mu.MusteriId;

    -- 4. temp: kopya
    SELECT * INTO #sonuc2 FROM #sonuc WHERE ToplamTutar > 0;

    TRUNCATE TABLE dbo.MusteriOzet;
    INSERT INTO dbo.MusteriOzet (MusteriId, AdSoyad, ToplamTutar, SiparisSayisi, SonSiparis, Il, YuklemeTarihi)
    SELECT MusteriId, AdSoyad, ToplamTutar, SiparisSayisi, SonTarih, Il, GETDATE() FROM #sonuc2;

    -- UPDATE ... FROM alias
    UPDATE mo SET mo.ToplamTutar = mo.ToplamTutar + dbo.fnKdv(mo.ToplamTutar)
    FROM dbo.MusteriOzet mo JOIN #sonuc2 s ON s.MusteriId = mo.MusteriId
    WHERE s.Il = N'İstanbul';

    SELECT @Sayac = COUNT(*) FROM dbo.MusteriOzet;

    -- MERGE + OUTPUT INTO
    MERGE dbo.Arsiv_Siparis AS t
    USING (SELECT s.SiparisId, s.MusteriId, s.Tutar FROM dbo.Siparis s WHERE s.Tarih < DATEADD(year, -1, GETDATE())) AS src
    ON t.SiparisId = src.SiparisId
    WHEN MATCHED THEN UPDATE SET t.Tutar = src.Tutar, t.ArsivTarihi = GETDATE()
    WHEN NOT MATCHED THEN INSERT (SiparisId, MusteriId, Tutar, ArsivTarihi) VALUES (src.SiparisId, src.MusteriId, src.Tutar, GETDATE())
    OUTPUT $action, inserted.SiparisId, inserted.Tutar INTO dbo.Log (Mesaj, LogId, Tarih);

    DELETE s FROM dbo.Siparis s WHERE s.Tarih < DATEADD(year, -3, GETDATE());

    -- view ve TVF üzerinden okuma; CTE
    ;WITH aktif AS (SELECT v.MusteriId, v.AdSoyad, v.Bakiye FROM dbo.vAktifMusteri v WHERE v.Bakiye > 100)
    INSERT INTO dbo.Log (Mesaj, Tarih)
    SELECT a.AdSoyad + ':' + CAST(f.Tutar AS nvarchar(20)), f.Tarih FROM aktif a CROSS APPLY dbo.fnMusteriSiparisleri(a.MusteriId) f;

    -- sonuç kümesi
    SELECT MusteriId, AdSoyad, ToplamTutar FROM dbo.MusteriOzet ORDER BY ToplamTutar DESC;
END
GO
