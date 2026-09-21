# SqlLineage — MSSQL veri soyağacı (lineage) çıkarıcı

.NET 10 konsol uygulaması (çok dosyalı proje: `SqlLineage.csproj`, `Program.cs`, `src/*.cs`). Verilen sunuculardaki tüm
stored procedure / view / function / trigger / Agent job adımlarını `Microsoft.SqlServer.TransactSql.ScriptDom` ile parse
eder, tablo ve kolon düzeyinde "veri nereden geliyor, nereye gidiyor" satırları üretir (ODI lineage mantığı), dinamik SQL'i
statik string-akış analiziyle çözer, çıktıyı CSV + Excel + MSSQL tablo DDL'i / SqlBulkCopy olarak yazar.

**v2.0 — DB DB görev akışı, düşük bellek.** v1 tüm sunucuların tüm modül tanımlarını ve 9 milyon satırı sona kadar bellekte
tutuyordu (4 sunucu için ~12 GB). v2'de:

- **Katalog tembel**: başta yalnız sunucu + DB listesi; bir DB'nin nesne/kolon kataloğu ilk referansta yüklenir.
- **Tanımlar akış halinde**: analiz edilen DB'nin `sys.sql_modules` metni tek sorguyla akar, bellekte yalnız o an işlenen
  modüller durur; prosedür metni analizden sonra bırakılır.
- **Satırlar diske akar**: her modül biter bitmez CSV parçasına yazılır; bellekte satır listesi tutulmaz.
- **Merge ayrı**: prosedürler arası yayılım ve kalıcı→kalıcı indirgeme birleştirme adımında, sıkıştırılmış indeksle
  (string havuzu + 64-bit anahtarlar; yalnız geçici-hedefli kenarlar indekslenir) yapılır.
- **Dağıtık**: `plan.json` = DB görev listesi; 10 düğüm aynı listeden görev alıp işler, sonunda tek `merge`.

## Çalıştırma

```bash
dotnet build -c Release
# 1) örnek yapılandırma üret, bağlantı dizesini düzenle
dotnet run -- --init

# 2) tek makine, tek komut: plan + tüm DB'ler sırayla + merge (eski kullanımla aynı; alt komut verilmezse bu)
dotnet run -- run-all --config lineage.json

# 3) dağıtık: plan bir kez, görevler düğümlerde, merge bir kez (work klasörü paylaşılan olmalı)
dotnet run -- plan  --config lineage.json --work \\paylasim\lineage\work
dotnet run -- run   --config lineage.json --work \\paylasim\lineage\work --next --loop --node NODE01   # her düğümde
dotnet run -- status --work \\paylasim\lineage\work
dotnet run -- merge --config lineage.json --work \\paylasim\lineage\work --out out

# 4) bağlantısız: .sql dosyalarından (CREATE TABLE'lar sentetik katalog olur)
dotnet run -- run-all --files ./samples --out out

# 5) sonuçları merge'de doğrudan bir MSSQL DB'sindeki lineage.* tablolarına bas
dotnet run -- merge --config lineage.json --sql-target "Server=...;Database=LineageDb;Integrated Security=True;TrustServerCertificate=True"
```

Yayınlama: `dotnet publish -c Release -r win-x64 --self-contained -o bin-win` (runtime gerekmez) ya da `-o bin` (çerçeve bağımlı).

### Alt komutlar

| Komut | Ne yapar |
|---|---|
| `run-all` | `plan` (yeni koşu) + bekleyen tüm görevler sırayla + `merge`. `--resume`: mevcut planın kalanını bitirip merge eder. |
| `plan [--force]` | Sunuculara bağlanır, filtreden geçen DB'leri ve modül sayılarını `work/plan.json`'a yazar (görev listesi). Mevcut plan varsa `--force` ister. |
| `run --task <id>` | Tek görev: `prepass:<sunucu>:<db>` ya da `analyze:<sunucu>:<db>`. `--no-plan-update`: plan.json'a durum yazma. |
| `run --next [--loop] [--node ad]` | Kilit altında sıradaki görevi alır ve çalıştırır; `--loop` görev kalmayana kadar sürer. |
| `next` | Sıradaki görevi `running` yapıp JSON olarak yazdırır (durumu kendi kuyruğunuzda tutuyorsanız; sonra `run --task <id> --no-plan-update`). |
| `merge` | Parçaları birleştirir → `out/csv-<RunId>/*.csv`, Excel, DDL, `--sql-target`, `--query`, `--graph`. |
| `status [--json]` | Plan durumu (faz başına bitti/çalışıyor/başarısız/bekliyor). |
| `infa …` | Informatica PowerCenter XML export modu (aşağıda). |

Görevler iki fazdır: **faz 1 `prepass`** (DB başına; çağıran `EXEC` literal argümanları + kod tarafından yaratılan tablolar →
`work/prepass/<sunucu>__<db>.json`) ve **faz 2 `analyze`** (DB başına; tüm faz 1 dosyalarını yükleyip analiz eder →
`work/parts/<sunucu>__<db>/*.csv` + `meta.json`). Faz 2 görevleri ancak tüm faz 1 görevleri bitince açılır; `--next` bunu
kendisi gözetir. Planlanan DB'lerin dışında (örn. `master`) koşan job adımları `<sunucu>:(jobs)` görevine düşer.

### Dağıtık koşu notları

- `--work` klasörü tüm düğümlerin gördüğü paylaşılan bir klasör olmalı (UNC). `plan.json` kilit dosyasıyla (`plan.lock`)
  atomik güncellenir; bir düğüm çökerse görev `running`'de kalır — `status` ile görün, plan.json'da `pending` yapıp yeniden
  verin (ya da `run --task` ile doğrudan çalıştırın; parça üzerine yazılır).
- Düğümler ayrı yerel klasörlerde çalışacaksa: faz 2 başlamadan `prepass/*.json` dosyaları, merge'den önce de
  `parts/<sunucu>__<db>/` klasörleri tek yere kopyalanmalı. Parçalar kendi başına taşınabilir (`meta.json` koşu kimliğini taşır;
  başka koşudan parça merge'de atlanır).
- `--compress-parts` (veya `compressParts: true`): parça CSV'leri gzip (`.csv.gz`); ağ diskine yazarken küçük.
- Her görev, dinamik SQL/çapraz DB referansları için sunuculara kendi bağlanır; lineage.json her düğümde aynı olmalı.
- Görev sırası: büyük DB'ler (toplam tanım baytı) önce dağıtılır.
- Bellek: bir görev ≈ hedef DB kataloğu + referans verdiği DB'lerin kataloğu (tanımsız) + o an işlenen `parallelism × 4`
  modülün tanımı/AST'si. Merge ≈ ObjectLineage indeksi + geçici-hedefli kolon kenarları (havuzlu; 9M satırlık ortamda ~1 GB).

### Kendi API'nizden çağırma

`Program.cs` yalnız `App.RunAsync(args)` çağırır. Servisinizde (Worker / BackgroundService):

```csharp
var cfg = LineageConfig.Load("lineage.json");          // ya da nesneyi kendiniz kurun
string work = @"\\paylasim\lineage\work";

// API: koşu başlat → DB listesi
PlanFile plan = await LineagePipeline.PlanAsync(cfg, work, force: true);

// Düğümlerdeki BackgroundService: görev kalmayana kadar
await LineagePipeline.RunPendingAsync(cfg, work, node: Environment.MachineName, loop: true);
// ya da kuyruğu kendiniz yönetin:
PlanTask? t = LineagePipeline.ClaimNext(work, "NODE01");            // null → görev yok
if (t != null) await LineagePipeline.RunTaskAsync(cfg, work, t.Id, "NODE01", updatePlan: true);

// Hepsi bitince (status: tüm görevler done): tek yerde
await LineagePipeline.MergeAsync(cfg, work);                        // CSV/Excel/DDL/SqlBulkCopy
```

`PlanFile`/`PlanTask` düz JSON nesneleridir; `plan.json`'ı kendi tablonuza da kopyalayabilirsiniz. `LineagePipeline.Status(work, json: true)`
aynı bilgiyi verir.

Gerekli: .NET 10 SDK. Paketler: ScriptDom 170, Microsoft.Data.SqlClient 6, ClosedXML 0.105 (csproj'da).

## İzinler (salt okunur)

Her instance'ta: `VIEW ANY DEFINITION` (ya da DB başına `VIEW DEFINITION`), DB başına `VIEW DATABASE STATE`,
`VIEW SERVER STATE` (`sys.servers`), `msdb` için `SQLAgentReaderRole` (job adımları; yoksa uyarı verip geçer).
Hiçbir kullanıcı tablosu sorgulanmaz; yalnız `sys.*` ve `msdb.dbo.sysjob*` okunur (`--config-tables` hariç).

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
  "workDirectory": "work",       // plan.json, prepass/, parts/ (dağıtıkta paylaşılan klasör)
  "compressParts": false,
  "mergeDedup": true,            // merge'de ColumnLineage/ObjectLineage satır tekilleştirme (yeniden koşulan görevlere karşı)
  "parallelism": 8,
  "moduleTimeoutSeconds": 60,
  "maxDynamicAlternatives": 16,
  "maxPatternMatches": 50,
  "output": { "directory": "out", "xlsx": true, "csv": true, "csvSeparator": ",", "sqlTableDdl": true, "sqlBulkTarget": null, "sqlSchema": "lineage", "maxRowsPerSheet": 900000 }
}
```

## Çıktılar (`out/`)

`csv-<RunId>/*.csv` (merge'in ana ürünü; her sayfa bir dosya), `lineage-<RunId>.xlsx` (aynı sayfalar; büyük çıktıda yalnız
özet sayfalar) ve `lineage_tables.sql` (MSSQL DDL + sorgu katmanı). Parçalar `work/parts/` altında kalır (silmek size ait).

| Sayfa | İçerik |
|---|---|
| **ColumnLineage** | Ana çıktı. Her satır: `Modül, İfadeNo, İfadeTürü, Satır, Kaynak(sunucu,db,şema,nesne,tür,kolon) → Hedef(…), FlowKind, Expression, Provenance, Confidence, Note`. `FlowKind`: Direct (aynen kopya), Positional (kolon listesiz INSERT / INSERT EXEC / FETCH), Expression (ifadeden türetildi), Aggregate (SUM/COUNT/window), Indirect (WHERE/JOIN/GROUP/TOP: değeri değil hangi satırların geçeceğini belirler; hedef kolon `*`). |
| **ObjectLineage** | Modül × ifade × nesne × eylem: `Reads, Insert, Update, Delete, Merge, Truncate, Create, Drop, Alter, Calls, ResolvesTo, References`. `Provenance=Interprocedural` satırları çağrılan prosedürün etkilerini çağırana taşır (`Note` = çağrı yolu); `Trigger` satırları yazmanın tetiklediği trigger etkileridir. |
| **Collapsed** | Temp tablo / tablo değişkeni / view / fonksiyon / prosedür sonuç kümesi / `IN:@param` düğümleri üzerinden geçilip **kalıcı tablodan kalıcı tabloya** indirgenmiş kolon akışı; `Path` tam yol, `Hops` sekme sayısı. "SP, T1'den 30 temp'e aktarıp X'e yazıyor" sorusunun tek satırlık cevabı. |
| **Modules** | Modül başına parse durumu (`Parsed`, `ParsedWithHoles`, `Quarantined`, `NoDefinition(Encrypted/CLR)`, `Timeout`, `Error`), kullanılan parser sürümü, hata metni, açılan delikler, ifade/dinamik site/satır sayıları, süre. |
| **DynamicSql** | Her `EXEC()`, `sp_executesql`, `EXEC @proc`, `EXEC … AT`, `sp_MSforeach*`, `sp_send_dbmail @query` sitesi: üretilen şablon (delikler `⟨@param⟩`), delik kökenleri (Parameter / Column:tablo.kolon / cursor / Unknown), durum (`Exact` / `Partial` / `Unresolved`), üretilen kenar sayısı, parse hatası. |
| **Unresolved** | Katalogda bulunamayan adlar ve teşhisler: `ObjectNotFound`, `DatabaseNotScanned`, `LinkedServerNotScanned`, `AmbientTempTable` (çağıranın yarattığı temp), `DynamicPlaceholder`, `SchemaAssumed`, `AmbiguousColumn`, `CatalogDepMissed` (SQL Server'ın `sys.sql_expression_dependencies`'te gördüğü ama analizin görmediği referans = motor açığı), `StatementError`, … |
| **Summary** | Sunucu/DB başına KPI: `ParseRate`, `BindRate`, `DynamicResolutionRate`, satır sayıları, `CatalogDepsMissed`. `(jobs)` satırı: planlanmamış DB'lerdeki job adımları. |
| **TopIssues / UnresolvedByKind / CatalogDeps / Statements / Query** | En çok sorun üreten modüller; tür başına çözülmemiş sayıları; `sys.sql_expression_dependencies` kanıt tablosu (`FoundInAnalysis`); `--statements` ile ifade metinleri; `--query` sonucu. |

Özel düğüm adları: temp tablolar `dbo.uspX.#t` (şema alanı = sahibi modül), `##global` şeması `##`;
prosedür sonuç kümesi `RS1:<kolon>` (pozisyonel referansta `RS1:#i`); parametreler `IN:@p`, çıkış
parametreleri `OUT:@p`, fonksiyon dönüşü `RETURN`; dosyalar `FILE` sunucusu; linked server nesneleri
`External` türü ve `data_source` sunucu adıyla.

`Confidence`: `Exact` katalogda bulundu; `High` pozisyonel eşleme / şekli bilinmeyen ama adı belli;
`Medium` şema varsayıldı, desenle eşleşti, ambient temp, taranmamış sunucu; `Low` çözülemedi / dinamik kısmi.

Mükerrerlik: modül içinde `ifadeNo|kaynak|hedef|tür` anahtarıyla tekilleştirilir; aynı ilişki farklı ifade/modülde ayrı kanıt
satırıdır (kasıtlı). Benzersiz ilişki için `Collapsed` / `vw_ColumnEdges` üstünde DISTINCT (StatementNo/Line/Path hariç).
Merge'de `mergeDedup` ile birebir aynı satırlar (yeniden koşulan görev) düşülür.

## Sorgu, graf ve artımlı koşu

```bash
dotnet run -- merge --config lineage.json --query "dw_production.dbo.MUSTERI_OZET" --direction both --max-hops 10
dotnet run -- merge --config lineage.json --graph "dw_production.dbo.MUSTERI_OZET.AD_SOYAD" --graph-level column --graph-hops 3 --graph-format dot
dot -Tsvg out/graph-<RunId>.dot -o graph.svg          # Graphviz ile görsel
dotnet run -- run-all --config lineage.json --sql-target "..." --incremental     # değişmeyen modüller önceki koşudan
dotnet run -- infa --xml export.xml                    # Informatica modu
```

- `--query`: indirgenmiş kenarlar üzerinden **modüller arası** çok sekmeli kolon soyağacı. `Query` sayfası/CSV: başlangıç kolonu,
  uç düğüm, sekme, FlowKind (yolun en güçlü dönüşümü), Confidence (en zayıf), `TerminalReason` (PHYSICAL_SOURCE, CYCLE, MAX_HOPS,
  UNRESOLVED), `Path`, `Modules` (yoldaki prosedürler). Merge'de çalışır (Collapsed.csv üzerinden; merge tekrar edilebilir).
- `--graph`: düğüm çevresinde ±N sekme alt-graf; `dot` (Graphviz), `graphml` (yEd/Gephi), `json`.
- MSSQL hedefinde hazır sorgu katmanı (`lineage_tables.sql` ile de gelir): `vw_LatestRun`, `vw_ColumnEdges` (indirgenmiş, son koşu),
  `vw_ColumnEdgesDetail`, `vw_ObjectEdges`, `vw_Nodes`, özyinelemeli `fn_Upstream(db, schema, obje, kolon|NULL, maxHops)` ve
  `fn_Downstream(...)`. Power BI/Excel doğrudan bu view'lere bağlanır.
- `--incremental`: `--sql-target` gerekir; görev bazında çalışır. Önceki koşunun `Modules.DefinitionHash` ve `Summary.EngineSignature`
  (motor sürümü + ayarlar + katalog şekli) karşılaştırılır; aynı olan modüller analiz edilmez, satırları geri yüklenir;
  Interprocedural/Trigger/Collapsed merge'de yeniden hesaplanır. Katalogda kolon değişirse o DB tamamen yeniden analiz edilir.
- `--statements`: her ifadenin metni (`Statements` tablosu). `TopIssues`: en çok sorun üreten modüller. `Unresolved.Severity`: error/warning/info.
- Politika: `excludeDatabases` (global ve bağlantı başına), bağlantı başına `databases`, `serverAliases` (linked server adı → taranan sunucu),
  `excludeModuleNameContains`, `excludeObjectNameContains` (bu adlara giden/gelen satırlar düşülür), `maxDefinitionChars`.

## MSSQL'e yükleme

`--sql-target` ile merge `lineage` şemasını ve tabloları yaratıp CSV'lerden akışla `SqlBulkCopy` ile doldurur (her koşu
yeni `RunId` ile eklenir). Elle: `lineage_tables.sql`'i çalıştırıp CSV'leri `bcp`/`BULK INSERT` ile yükleyin
(UTF-8 BOM, `,` ayraç, RFC4180 tırnaklama).

## Nasıl çalışır (özet)

1. **Katalog** (tembel): sunucudan `sys.databases`, `sys.servers`, `msdb` job adımları; DB ilk referansta
   `sys.objects/columns/parameters/synonyms/triggers/table_types` (+ hedef DB'de `sql_expression_dependencies`). Modül metni
   analiz görevinde `sys.sql_modules`'ten akışla; view/TVF kolon türetimi gerekirse tek nesne için tembel çekilir.
   Modül adı her zaman `sys.objects`'ten (sp_rename başlığı güncellemez).
2. **Parser merdiveni**: `[compat==80 ? TSql80 : sunucu sürümü, TSql170, compat, TSql80]`; `uses_quoted_identifier`
   aynen parser'a verilir. Tümü başarısızsa hatalı ifade boşaltılıp (delik) yeniden denenir (5 kez); hâlâ olmazsa
   karantina + `sys.sql_expression_dependencies`'ten nesne düzeyi satır.
3. **Bağlayıcı**: kapsam yığını (CTE, türetilmiş tablo, APPLY, alt sorgu), şemasız ad → modül şeması → dbo,
   synonym → hedef, 3-4 parçalı adlar → taranan diğer DB/sunucu (tembel yüklenir), `SELECT *` ve kolon listesiz INSERT → katalog
   kolon sırası (identity/computed/rowversion atlanır), temp tablolar tanım sıralı kayıt, cursor `FETCH INTO`,
   değişken bağımlılık haritası (IF/ELSE birleşim, WHILE 2 geçiş), trigger `inserted/deleted` → ana tablo.
4. **Dinamik SQL**: string değişkenleri şablon kümesine indirgenir (`+`, `+=`, CONCAT, QUOTENAME, REPLACE, CASE/ISNULL
   alternatifleri, cursor/`SELECT @v = col FROM` kökenli delikler, parametre default'ları ve statik çağıranların literal
   argümanları — ön geçiş görevlerinden, tüm DB'ler arası). Sink'te delikler bağlama göre yer tutucu alır, metin aynı merdivenle
   parse edilip normal kurallardan geçer (`Provenance = DynamicStatic`); parse olmazsa parçalara bölme → regex nesne çıkarımı (`DynamicPartial`).
5. **Merge son geçişleri**: çağrı grafı üzerinden nesne düzeyi yayılım (derinlik 8) + trigger yayılımı; kalıcı→kalıcı
   kolon indirgemesi (temp/@tablo/view/TVF/proc sonuç kümesi/`IN:@p`, çağıran↔çağrılan paylaşılan temp'ler dahil).

## Bilinen sınırlar

- Kolon düzeyi prosedürler arası akış yalnız `IN:@p` / `OUT:@p` / `RETURN` / `RS1` düğümleri ve paylaşılan temp'ler
  üzerinden; TVP argümanları tablo düzeyinde.
- Config tablosundan gelen tablo adları (`FETCH … INTO @tbl` → `EXEC`) varsayılan canlı sorgulanmaz; `DynamicSql` sayfasında
  kökeni (`Column:dbo.ETL_Config.HedefTablo`) ile `Unresolved` olarak listelenir. `--config-tables`: kaynak kolondan
  `SELECT DISTINCT TOP 50` okunur ve her değer dinamik batch olarak analiz edilir (canlı, salt okunur, NOLOCK, 20 s; güven Medium).
- `WITH ENCRYPTION` ve SQLCLR modüller: metin yok; yalnız katalog bağımlılıkları.
- SSIS/SSRS/uygulama gömülü SQL kapsam dışı.
- Excel: toplam 3 milyondan fazla satırda lineage sayfaları yalnız CSV'de; Excel'e özet sayfalar (`lineage-ozet-<RunId>.xlsx`).
  Tam veri için `--sql-target`. Excel yazımı satırları belleğe alır (ClosedXML); büyük ortamda `--no-xlsx` + SQL hedefi önerilir.
- `archiveDatabasePatterns` (lineage.json): `["^(RAPOR)_(\\d{4}|@YEAR)$"]` gibi regex'ler; 1. yakalama grubu kanonik ad.
  Eşleşen arşiv DB'leri taranmaz (`skipArchiveDatabases`), `RAPOR_2024.dbo.X` ve dinamik `'RAPOR_' + @YEAR + '.dbo.X'`
  referansları `RAPOR.dbo.X`'e bağlanır. `excludeModuleNameContains`: `["_old","_sil"]` gibi ad parçaları içeren modüller taranmaz.
- XML: `col.modify('… sql:column("a") …')`, `col.value/query/exist(...)` → kolon + `sql:column`/`sql:variable` referansları (Expression, yaklaşık).
- Parse öncesi normalizasyon: eski `RAISERROR 60101 'mesaj'` biçimi, `%%physloc%%`, job adımlarındaki HTML kaçışları (`&gt;`).
- `EXEC @sql` (parantezsiz) ile çalıştırılan ifadeler dinamik SQL sayılır; `RAPOR_<yıl>.sys.sp_executesql` gibi DB deseni
  taranan DB adlarına açılır. Modül içinde literal satırlarla doldurulan `#temp` / `@tablo` komut kuyrukları (`INSERT … VALUES ('EXEC …')`)
  çözülür. Kod tarafından yaratılan kalıcı tablolar (`SELECT INTO` / `CREATE TABLE`, başka modülde) ön geçişte sanal kataloğa alınır.

## Kaynak düzeni

| Dosya | İçerik |
|---|---|
| `Program.cs` | giriş: `App.RunAsync(args)` |
| `src/01-Config.cs` | `App` (alt komutlar), `Log`, `LineageConfig`, `Cli` |
| `src/02-Model.cs` | `ObjRef`, `ColRef`, `Deps`, çıktı satır sınıfları |
| `src/03-Catalog.cs` | tembel katalog (`Catalog`, `ServerCatalog`, `DbCatalog`, `CatalogLoader`, `FileCatalog`) |
| `src/04-Parser.cs` | parser merdiveni |
| `src/05a-Binder.cs`, `src/05b-Statements.cs` | bağlayıcı ve ifade işleyicileri (`ModuleAnalyzer`) |
| `src/06-DynamicSql.cs` | string-akış analizi, şablonlar |
| `src/07-Run.cs` | `DbTaskRunner`: tek DB ön geçiş / analiz, `PrepassResult`, `TaskMeta` |
| `src/08-Output.cs` | CSV (akışlı yazma/okuma), `PartWriter`, Excel, DDL, SqlBulkCopy |
| `src/09-IncrementalQueryGraph.cs` | artımlı koşu, `--query`, `--graph` |
| `src/10-InfaLineage.cs` | Informatica modu (`namespace InfaLineage`) |
| `src/11-Pipeline.cs` | `PlanFile`/`PlanTask`, `PlanStore`, **`LineagePipeline`** (Plan / RunTask / ClaimNext / RunPending / Merge / RunAll) |
| `src/12-Merge.cs` | `Merger`: birleştirme, prosedürler arası yayılım, indirgeme, özetler |

v1.3.x tek dosyalı sürüm `v1.3.0` etiketinde/release'inde durur.

---

# Informatica PowerCenter XML export lineage (`infa` alt komutu)

`src/10-InfaLineage.cs` (`namespace InfaLineage`), Designer / Repository Manager "Export Objects" ya da `pmrep objectexport` ile alınan
POWERMART XML dosyalarından (folder, source/target, mapping, mapplet, reusable transformation, shortcut,
workflow/session) kaynak → hedef lineage üretir. SqlLineage ile aynı çıktı mantığı; `App.RunAsync` ilk argüman `infa` ise buraya yönlendirir
(yalnız Informatica isteniyorsa `Program.cs`'te `return await InfaLineage.App.RunAsync(args);`).

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
