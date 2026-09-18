CREATE TABLE dbo.Musteri (MusteriId int IDENTITY(1,1), Ad nvarchar(100), Soyad nvarchar(100), Il nvarchar(50), KayitTarihi datetime, Durum char(1), Bakiye decimal(18,2), RowVer rowversion);
GO
CREATE TABLE dbo.Siparis (SiparisId int IDENTITY, MusteriId int, Tutar decimal(18,2), Tarih datetime, Durum int, KanalKodu varchar(10));
GO
CREATE TABLE dbo.SiparisKalem (KalemId int IDENTITY, SiparisId int, UrunKodu varchar(20), Adet int, BirimFiyat decimal(18,2));
GO
CREATE TABLE dbo.Urun (UrunKodu varchar(20), UrunAdi nvarchar(200), Kategori nvarchar(50), Fiyat decimal(18,2));
GO
CREATE TABLE dbo.MusteriOzet (MusteriId int, AdSoyad nvarchar(200), ToplamTutar decimal(18,2), SiparisSayisi int, SonSiparis datetime, Il nvarchar(50), YuklemeTarihi datetime);
GO
CREATE TABLE dbo.Log (LogId int IDENTITY, Mesaj nvarchar(max), Tarih datetime);
GO
CREATE TABLE dbo.ETL_Config (Id int, KaynakTablo sysname, HedefTablo sysname, Aktif bit);
GO
CREATE TABLE dbo.Stage_Musteri (MusteriId int, Ad nvarchar(100), Soyad nvarchar(100), Il nvarchar(50));
GO
CREATE TABLE dbo.Stage_Siparis (SiparisId int, MusteriId int, Tutar decimal(18,2));
GO
CREATE TABLE dbo.Arsiv_Siparis (SiparisId int, MusteriId int, Tutar decimal(18,2), ArsivTarihi datetime);
GO
CREATE TABLE dbo.Satis_2024 (SiparisId int, Tutar decimal(18,2));
GO
CREATE TABLE dbo.Satis_2025 (SiparisId int, Tutar decimal(18,2));
GO
CREATE TABLE dbo.MusteriDenetim (MusteriId int, EskiBakiye decimal(18,2), YeniBakiye decimal(18,2), Tarih datetime);
GO
CREATE VIEW dbo.vAktifMusteri AS SELECT m.MusteriId, m.Ad + ' ' + m.Soyad AS AdSoyad, m.Il, m.Bakiye FROM dbo.Musteri m WHERE m.Durum = 'A';
GO
CREATE FUNCTION dbo.fnKdv(@tutar decimal(18,2)) RETURNS decimal(18,2) AS BEGIN RETURN @tutar * 0.20 END
GO
CREATE FUNCTION dbo.fnMusteriSiparisleri(@musteriId int) RETURNS TABLE AS RETURN (SELECT s.SiparisId, s.Tutar, s.Tarih FROM dbo.Siparis s WHERE s.MusteriId = @musteriId);
GO
CREATE TRIGGER dbo.trMusteriBakiye ON dbo.Musteri AFTER UPDATE AS
BEGIN
    INSERT INTO dbo.MusteriDenetim (MusteriId, EskiBakiye, YeniBakiye, Tarih)
    SELECT d.MusteriId, d.Bakiye, i.Bakiye, GETDATE() FROM inserted i JOIN deleted d ON d.MusteriId = i.MusteriId WHERE i.Bakiye <> d.Bakiye;
END
GO
