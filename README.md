# SqlLineage — MSSQL veri soyağacı (lineage) çıkarıcı

Tek dosyalık .NET 10 konsol uygulaması. Verilen sunuculardaki tüm stored procedure / view / function /
trigger / Agent job adımlarını `Microsoft.SqlServer.TransactSql.ScriptDom` ile parse eder, tablo ve
kolon düzeyinde "veri nereden geliyor, nereye gidiyor" satırları üretir (ODI lineage mantığı),
dinamik SQL'i statik string-akış analiziyle çözer, çıktıyı Excel + CSV + MSSQL tablo DDL'i olarak yazar.

## Çalıştırma

```bash
# 1) örnek yapılandırma üret, bağlantı dizesini düzenle
dotnet run SqlLineage.cs -- --init

# 2) tüm kullanıcı DB'leri
dotnet run SqlLineage.cs -- --config lineage.json

# 3) tek sunucu / seçili DB'ler, config dosyasız
dotnet run SqlLineage.cs -- --server "ETL01=Server=etl01;Integrated Security=True;TrustServerCertificate=True" --db DWH --db Staging --out out

# 4) bağlantısız: .sql dosyalarından (CREATE TABLE'lar sentetik katalog olur)
dotnet run SqlLineage.cs -- --files ./samples --out out

# 5) sonuçları doğrudan bir MSSQL DB'sindeki lineage.* tablolarına bas
dotnet run SqlLineage.cs -- --config lineage.json --sql-target "Server=...;Database=LineageDb;Integrated Security=True;TrustServerCertificate=True"
```

Gerekli: .NET 10 SDK (`dotnet --version` → 10.x). Paketler dosyanın başındaki `#:package` satırlarıyla
ilk çalıştırmada NuGet'ten iner (ScriptDom 170, Microsoft.Data.SqlClient 6, ClosedXML 0.105).

Tek klasör/exe olarak dağıtmak için:

```bash
dotnet publish SqlLineage.cs -c Release -o bin                      # çerçeve bağımlı (hedefte .NET 10 runtime olmalı)
dotnet publish SqlLineage.cs -c Release -r win-x64 --self-contained -o bin-win   # Windows, runtime gerekmez
```

## İzinler (salt okunur)

Her instance'ta: `VIEW ANY DEFINITION` (ya da DB başına `VIEW DEFINITION`), DB başına `VIEW DATABASE STATE`,
`VIEW SERVER STATE` (`sys.servers`), `msdb` için `SQLAgentReaderRole` (job adımları; yoksa uyarı verip geçer).
Hiçbir kullanıcı tablosu sorgulanmaz; yalnız `sys.*` ve `msdb.dbo.sysjob*` okunur.

Microsoft.Data.SqlClient varsayılan olarak `Encrypt=True`; sertifika yoksa bağlantı dizesine
`TrustServerCertificate=True` ekleyin.

## lineage.json

```json
{
  "connections": [ { "name": "ETL01", "connectionString": "Server=etl01;Integrated Security=True;TrustServerCertificate=True" } ],
  "databases": ["*"],            // ya da ["DWH","Staging"]; sistem DB'leri hariç
  "schemas": ["*"],
  "includeTriggers": true,
  "includeJobSteps": true,
  "includeSystemObjects": false,
  "parallelism": 8,
  "moduleTimeoutSeconds": 60,
  "maxDynamicAlternatives": 16,
  "maxPatternMatches": 50,
  "output": { "directory": "out", "xlsx": true, "csv": true, "csvSeparator": ",", "sqlTableDdl": true, "sqlBulkTarget": null, "sqlSchema": "lineage", "maxRowsPerSheet": 900000 }
}
```

## Çıktılar (`out/`)

`lineage-<RunId>.xlsx` (sayfalar) ve `csv-<RunId>/*.csv` (aynı sayfalar) ve `lineage_tables.sql` (MSSQL DDL).

| Sayfa | İçerik |
|---|---|
| **ColumnLineage** | Ana çıktı. Her satır: `Modül, İfadeNo, İfadeTürü, Satır, Kaynak(sunucu,db,şema,nesne,tür,kolon) → Hedef(…), FlowKind, Expression, Provenance, Confidence, Note`. `FlowKind`: Direct (aynen kopya), Positional (kolon listesiz INSERT / INSERT EXEC / FETCH), Expression (ifadeden türetildi), Aggregate (SUM/COUNT/window), Indirect (WHERE/JOIN/GROUP/TOP: değeri değil hangi satırların geçeceğini belirler; hedef kolon `*`). |
| **ObjectLineage** | Modül × ifade × nesne × eylem: `Reads, Insert, Update, Delete, Merge, Truncate, Create, Drop, Alter, Calls, ResolvesTo, References`. `Provenance=Interprocedural` satırları çağrılan prosedürün etkilerini çağırana taşır (`Note` = çağrı yolu); `Trigger` satırları yazmanın tetiklediği trigger etkileridir. |
| **Collapsed** | Temp tablo / tablo değişkeni / view / fonksiyon / prosedür sonuç kümesi / `IN:@param` düğümleri üzerinden geçilip **kalıcı tablodan kalıcı tabloya** indirgenmiş kolon akışı; `Path` tam yol, `Hops` sekme sayısı. "SP, T1'den 30 temp'e aktarıp X'e yazıyor" sorusunun tek satırlık cevabı. |
| **Modules** | Modül başına parse durumu (`Parsed`, `ParsedWithHoles`, `Quarantined`, `NoDefinition(Encrypted/CLR)`, `Timeout`, `Error`), kullanılan parser sürümü, hata metni, açılan delikler, ifade/dinamik site/satır sayıları, süre. |
| **DynamicSql** | Her `EXEC()`, `sp_executesql`, `EXEC @proc`, `EXEC … AT`, `sp_MSforeach*`, `sp_send_dbmail @query` sitesi: üretilen şablon (delikler `⟨@param⟩`), delik kökenleri (Parameter / Column:tablo.kolon / cursor / Unknown), durum (`Exact` / `Partial` / `Unresolved`), üretilen kenar sayısı, parse hatası. |
| **Unresolved** | Katalogda bulunamayan adlar ve teşhisler: `ObjectNotFound`, `DatabaseNotScanned`, `LinkedServerNotScanned`, `AmbientTempTable` (çağıranın yarattığı temp), `DynamicPlaceholder`, `SchemaAssumed`, `AmbiguousColumn`, `CatalogDepMissed` (SQL Server'ın `sys.sql_expression_dependencies`'te gördüğü ama analizin görmediği referans = motor açığı), `StatementError`, … |
| **Summary** | Sunucu/DB başına KPI: `ParseRate`, `BindRate`, `DynamicResolutionRate`, satır sayıları, `CatalogDepsMissed`. |

Özel düğüm adları: temp tablolar `dbo.uspX.#t` (şema alanı = sahibi modül), `##global` şeması `##`;
prosedür sonuç kümesi `RS1:<kolon>` (pozisyonel referansta `RS1:#i`); parametreler `IN:@p`, çıkış
parametreleri `OUT:@p`, fonksiyon dönüşü `RETURN`; dosyalar `FILE` sunucusu; linked server nesneleri
`External` türü ve `data_source` sunucu adıyla.

`Confidence`: `Exact` katalogda bulundu; `High` pozisyonel eşleme / şekli bilinmeyen ama adı belli;
`Medium` şema varsayıldı, desenle eşleşti, ambient temp, taranmamış sunucu; `Low` çözülemedi / dinamik kısmi.

## Sorgu, graf ve artımlı koşu

```bash
dotnet run -- --config lineage.json --query "dw_production.dbo.MUSTERI_OZET" --direction both --max-hops 10
dotnet run -- --config lineage.json --graph "dw_production.dbo.MUSTERI_OZET.AD_SOYAD" --graph-level column --graph-hops 3 --graph-format dot
dot -Tsvg out/graph-<RunId>.dot -o graph.svg          # Graphviz ile görsel
dotnet run -- --config lineage.json --sql-target "..." --incremental     # değişmeyen modüller önceki koşudan
dotnet run -- infa --xml export.xml                    # Informatica modu (aynı dosya)
```

- `--query`: indirgenmiş kenarlar üzerinden **modüller arası** çok sekmeli kolon soyağacı. `Query` sayfası/CSV: başlangıç kolonu,
  uç düğüm, sekme, FlowKind (yolun en güçlü dönüşümü), Confidence (en zayıf), `TerminalReason` (PHYSICAL_SOURCE, CYCLE, MAX_HOPS,
  UNRESOLVED), `Path`, `Modules` (yoldaki prosedürler).
- `--graph`: düğüm çevresinde ±N sekme alt-graf; `dot` (Graphviz), `graphml` (yEd/Gephi), `json`.
- MSSQL hedefinde hazır sorgu katmanı (`lineage_tables.sql` ile de gelir): `vw_LatestRun`, `vw_ColumnEdges` (indirgenmiş, son koşu),
  `vw_ColumnEdgesDetail`, `vw_ObjectEdges`, `vw_Nodes`, özyinelemeli `fn_Upstream(db, schema, obje, kolon|NULL, maxHops)` ve
  `fn_Downstream(...)`. Power BI/Excel doğrudan bu view'lere bağlanır.
- `--incremental`: `--sql-target` gerekir. Önceki koşunun `Modules.DefinitionHash` ve `Summary.EngineSignature` (motor sürümü +
  ayarlar + katalog şekli) karşılaştırılır; aynı olan modüller analiz edilmez, satırları geri yüklenir; Interprocedural/Trigger/Collapsed
  yeniden hesaplanır. Katalogda kolon değişirse o DB tamamen yeniden analiz edilir.
- `--statements`: her ifadenin metni (`Statements` tablosu; kenarın kaynağı olan SQL). `CatalogDeps`: `sys.sql_expression_dependencies`
  kanıt tablosu (`FoundInAnalysis`). `TopIssues`: en çok sorun üreten modüller. `Unresolved.Severity`: error/warning/info.
- Politika: `excludeDatabases` (global ve bağlantı başına), bağlantı başına `databases`, `serverAliases` (linked server adı → taranan sunucu),
  `excludeModuleNameContains`, `excludeObjectNameContains` (bu adlara giden/gelen satırlar düşülür), `maxDefinitionChars`.

## MSSQL'e yükleme

`--sql-target` ile program `lineage` şemasını ve tabloları yaratıp `SqlBulkCopy` ile doldurur (her koşu
yeni `RunId` ile eklenir). Elle: `lineage_tables.sql`'i çalıştırıp CSV'leri `bcp`/`BULK INSERT` ile yükleyin
(UTF-8 BOM, `,` ayraç, RFC4180 tırnaklama).

## Nasıl çalışır (özet)

1. **Katalog**: her DB'den `sys.objects/columns/sql_modules/parameters/synonyms/triggers/table_types/
   sql_expression_dependencies`, `sys.servers`, `msdb` job adımları belleğe alınır. Modül adı her zaman
   `sys.objects`'ten (sp_rename başlığı güncellemez).
2. **Parser merdiveni**: `[compat==80 ? TSql80 : sunucu sürümü, TSql170, compat, TSql80]`; `uses_quoted_identifier`
   aynen parser'a verilir. Tümü başarısızsa hatalı ifade boşaltılıp (delik) yeniden denenir (5 kez); hâlâ olmazsa
   karantina + `sys.sql_expression_dependencies`'ten nesne düzeyi satır.
3. **Bağlayıcı**: kapsam yığını (CTE, türetilmiş tablo, APPLY, alt sorgu), şemasız ad → modül şeması → dbo,
   synonym → hedef, 3-4 parçalı adlar → taranan diğer DB/sunucu, `SELECT *` ve kolon listesiz INSERT → katalog
   kolon sırası (identity/computed/rowversion atlanır), temp tablolar tanım sıralı kayıt, cursor `FETCH INTO`,
   değişken bağımlılık haritası (IF/ELSE birleşim, WHILE 2 geçiş), trigger `inserted/deleted` → ana tablo.
4. **Dinamik SQL**: string değişkenleri şablon kümesine indirgenir (`+`, `+=`, CONCAT, QUOTENAME, REPLACE, CASE/ISNULL
   alternatifleri, cursor/`SELECT @v = col FROM` kökenli delikler, parametre default'ları ve statik çağıranların literal
   argümanları). Sink'te delikler bağlama göre yer tutucu alır, metin aynı merdivenle parse edilip normal kurallardan
   geçer (`Provenance = DynamicStatic`); parse olmazsa parçalara bölme → regex nesne çıkarımı (`DynamicPartial`).
5. **Son geçişler**: çağrı grafı üzerinden nesne düzeyi yayılım (derinlik 8) + trigger yayılımı; kalıcı→kalıcı
   kolon indirgemesi (temp/@tablo/view/TVF/proc sonuç kümesi/`IN:@p`, çağıran↔çağrılan paylaşılan temp'ler dahil).

## Bilinen sınırlar (v1)

- Kolon düzeyi prosedürler arası akış yalnız `IN:@p` / `OUT:@p` / `RETURN` / `RS1` düğümleri ve paylaşılan temp'ler
  üzerinden; TVP argümanları tablo düzeyinde.
- Config tablosundan gelen tablo adları (`FETCH … INTO @tbl` → `EXEC`) canlı sorgulanmaz; `DynamicSql` sayfasında
  kökeni (`Column:dbo.ETL_Config.HedefTablo`) ile `Unresolved` olarak listelenir. v2: config tablosu değerlerini okuma,
  çalışma zamanı gözlemi (Extended Events / Query Store).
- `WITH ENCRYPTION` ve SQLCLR modüller: metin yok; yalnız katalog bağımlılıkları.
- SSIS/SSRS/uygulama gömülü SQL kapsam dışı.
- Çok büyük ortamlarda (3 milyondan fazla satır) lineage sayfaları yalnız CSV'ye yazılır; Excel'e özet sayfalar
  (`lineage-ozet-<RunId>.xlsx`: Summary, Modules, DynamicSql, Unresolved, UnresolvedByKind) konur. Tam veri için `--sql-target`.
- `--config-tables`: dinamik SQL metni ya da tablo adı bir **tabloda** duruyorsa (`SELECT @sql = ISLEM FROM dbo.BATCH_ISLEM`,
  cursor ile config tablosu) kaynak kolondan `SELECT DISTINCT TOP 50` okunur ve her değer dinamik batch olarak analiz edilir
  (canlı, salt okunur, NOLOCK, 20 s zaman aşımı; WHERE koşulu uygulanmaz → süperküme, güven Medium). Varsayılan kapalı.
- `archiveDatabasePatterns` (lineage.json): `["^(RAPOR)_(\\d{4}|@YEAR)$"]` gibi regex'ler; 1. yakalama grubu kanonik ad.
  Eşleşen arşiv DB'leri taranmaz (`skipArchiveDatabases`), `RAPOR_2024.dbo.X` ve dinamik `'RAPOR_' + @YEAR + '.dbo.X'`
  referansları `RAPOR.dbo.X`'e bağlanır. `excludeModuleNameContains`: `["_old","_sil"]` gibi ad parçaları içeren modüller taranmaz.
- XML: `col.modify('… sql:column("a") …')`, `col.value/query/exist(...)` → kolon + `sql:column`/`sql:variable` referansları (Expression, yaklaşık).
- Parse öncesi normalizasyon: eski `RAISERROR 60101 'mesaj'` biçimi, `%%physloc%%`, job adımlarındaki HTML kaçışları (`&gt;`).
- `EXEC @sql` (parantezsiz) ile çalıştırılan ifadeler dinamik SQL sayılır; `RAPOR_<yıl>.sys.sp_executesql` gibi DB deseni
  taranan DB adlarına açılır. Modül içinde literal satırlarla doldurulan `#temp` / `@tablo` komut kuyrukları (`INSERT … VALUES ('EXEC …')`)
  çözülür. Kod tarafından yaratılan kalıcı tablolar (`SELECT INTO` / `CREATE TABLE`, başka modülde) ön geçişte sanal kataloğa alınır.

---

# InfaLineage.cs — Informatica PowerCenter XML export lineage

`InfaLineage.cs`, Designer / Repository Manager "Export Objects" ya da `pmrep objectexport` ile alınan
POWERMART XML dosyalarından (folder, source/target, mapping, mapplet, reusable transformation, shortcut,
workflow/session) kaynak → hedef lineage üretir. `SqlLineage.cs` ile aynı çıktı mantığı.

## Klasik konsol projesinde kullanım

csproj paketleri SqlLineage ile aynı (ScriptDom, Microsoft.Data.SqlClient, ClosedXML). `InfaLineage.cs`'i projeye
ekleyin; `namespace InfaLineage` içinde olduğu için `SqlLineage.cs` ile aynı projede çakışmaz. `Program.cs`:

```csharp
// yalnız Informatica:
return await InfaLineage.App.RunAsync(args);

// ikisi bir arada (ilk argümana göre):
using System.Globalization;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length > 0 && args[0] == "infa") return await InfaLineage.App.RunAsync(args[1..]);
return await App.RunAsync(args);   // SqlLineage
```

```bash
dotnet run -- infa --xml C:\export\dwh_export.xml --xml C:\export\shared --out out
dotnet run -- infa --xml export.xml --sql-target "Server=...;Database=LineageDb;Integrated Security=True;TrustServerCertificate=True"
dotnet run -- infa --xml export.xml --mapping-only      # session bazlı çoğaltma olmadan (tasarım düzeyi)
```

Shared folder'daki kaynak/hedef tanımları shortcut ile kullanılıyorsa **Shared folder'ı da aynı koşuya verin**
(`--xml` tekrarlanabilir); aksi halde `Unresolved` sayfasında `SourceDefinitionNotFound` görürsünüz.

## Çıktılar

| Sayfa | İçerik |
|---|---|
| **ColumnLineage** | Ana çıktı: `Folder, Workflow, Session, Mapping, Kaynak(connection, dbtype, owner, nesne, tür, kolon) → Hedef(…), FlowKind, Hops, Path, Expression, Confidence`. Kaynak türleri: `Source` (source definition), `LookupTable`, `SqlOverrideTable` (SQ SQL override'daki tablolar), `Procedure`, `File`, `Constant/Unconnected` (SESSSTARTTIME, sabit vb.). `FlowKind`: Direct / Expression / Aggregate / Indirect (filter, router grubu, join, lookup koşulu, group by) / Positional (parse edilemeyen SQL override). Session varsa satırlar session başına, session'daki connection ve `Owner Name` / `Target Table Name` / `Table Name Prefix` override'ları uygulanmış olarak çıkar. |
| **ObjectLineage** | Session/mapping × instance × eylem: `Reads`, `Writes`, `TruncateAndWrites`, `Lookup`, `ReadsViaSql`, `Calls`, `TargetNotConnected`. |
| **PortLineage** | Mapping içi tüm port kenarları (connector'lar + transformation içi ifade/koşul bağları). Hata ayıklama ve "bu port nereden geliyor" sorusu için. |
| **Mappings** | Mapping başına durum (`Ok`, `OkWithUnresolved`, `Error`), instance/connector/kenar sayıları, SQL override sayısı ve parse edilen sayısı, çözülmemiş ad sayısı, session sayısı. |
| **Sessions** | Session × instance: reader/writer, connection adı ve türü, owner/tablo/SQL/dosya override'ları. |
| **Unresolved** | `SourceDefinitionNotFound`, `TargetDefinitionNotFound`, `TransformationNotFound`, `MappletNotFound`, `SqlOverrideNotParsed`, `LookupSqlOverrideNotParsed`, `ExpressionRefsNotFound`, `OutputPortWithoutSource`. |
| **Summary** | Folder başına: mapping sayısı, başarı oranı, session/instance/connector sayıları, SQL override parse oranı, **hedef kolon kapsama** (en az bir kaynağa bağlanan hedef kolon / tüm hedef kolonlar), satır sayıları. |

## Transformation kuralları

Connector'lar Direct; Expression/Aggregator çıkış portu ifadesindeki port adları → Expression (SUM/COUNT/… varsa Aggregate);
Router grup portları `REF_FIELD` ile girişe Direct, grup koşulu Indirect; Filter/Update Strategy/Joiner koşulu → tüm
çıkışlara Indirect; Aggregator `GROUPBY`, Sorter/Rank `SORTKEY` → Indirect; Lookup `LOOKUP` portları ← lookup tablosu
kolonları, `Lookup condition` → Indirect, bağlantısız `:LKP.ad(args)` ← lookup RETURN portu; Stored Procedure
transformation ve `:SP.ad(args)` → `Procedure` düğümü (`IN:`/`OUT:`); Source Qualifier `Sql Query` override T-SQL ile
parse edilir (Oracle `(+)`, `NVL`, `$$param` kaba temizlenir): select listesi çıkış portlarına pozisyonel, FROM/JOIN
tabloları `SqlOverrideTable`, WHERE/JOIN kolonları Indirect; parse olmazsa regex ile yalnız tablo adları (Positional,
Medium); `Source Filter` / `User Defined Join` → Indirect; Union/Normalizer/Joiner ifadesiz çıkışlar aynı adlı girişe
Direct; mapplet'ler `MappletInstance.Inner` adıyla açılır; `Pre SQL` / `Post SQL` / `Update Override` içindeki tablolar
nesne düzeyinde. Bilinen sınırlar: SQL transformation ve Java/Custom transformation gövdeleri, Oracle/Teradata'ya özgü
SQL override sözdizimi (parse edilemezse regex), XML/Web Service kaynak-hedefleri (port düzeyi bağlanır, fiziksel ad
dosya/URL olarak kalır).
