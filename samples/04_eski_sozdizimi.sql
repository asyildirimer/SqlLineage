CREATE PROCEDURE dbo.uspEski AS
SELECT m.Ad, s.Tutar FROM dbo.Musteri m, dbo.Siparis s WHERE m.MusteriId *= s.MusteriId
GO
CREATE PROCEDURE dbo.uspBozuk AS
BEGIN
    SELECT Ad FROM dbo.Musteri;
    SELEC bozuk satir burada;
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT UrunAdi, GETDATE() FROM dbo.Urun;
END
GO
