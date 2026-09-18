CREATE TABLE dbo.XmlBelge (Id int, Icerik xml, Ad nvarchar(50), Tutar decimal(18,2));
GO
CREATE PROCEDURE dbo.uspArsivVeXml @YEAR int = 2024 AS
BEGIN
    -- arşiv DB kanonik: FILES_2024 → FILES
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT Ad, GETDATE() FROM FILES_2024.dbo.Musteri;
    DECLARE @sql nvarchar(max) = N'INSERT INTO dbo.Log (Mesaj, Tarih) SELECT Soyad, GETDATE() FROM FILES_' + CAST(@YEAR AS nvarchar(4)) + N'.dbo.Musteri';
    EXEC (@sql);
    -- XML DML
    UPDATE dbo.XmlBelge SET Icerik.modify('insert <ad>{sql:column("Ad")}</ad> as first into (/belge)[1]');
    INSERT INTO dbo.Log (Mesaj, Tarih) SELECT Icerik.value('(/belge/ad)[1]', 'nvarchar(50)'), GETDATE() FROM dbo.XmlBelge WHERE Icerik.exist('/belge[tutar > sql:column("Tutar")]') = 1;
END
GO
