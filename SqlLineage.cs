#:package Microsoft.SqlServer.TransactSql.ScriptDom@170.*
#:package Microsoft.Data.SqlClient@6.*
#:package ClosedXML@0.105.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property InvariantGlobalization=false
// ============================================================================
//  SqlLineage.cs  —  MSSQL veri soyağacı (lineage) çıkarıcı, tek dosya, .NET 10
//
//  Çalıştırma:   dotnet run SqlLineage.cs -- --config lineage.json
//                dotnet run SqlLineage.cs -- --init            (örnek lineage.json üretir)
//                dotnet run SqlLineage.cs -- --files ./sql     (bağlantısız: .sql dosyalarından)
//  Yayınlama:    dotnet publish SqlLineage.cs -c Release -o ./bin  (tek exe)
//
//  Bölümler:  1 Giriş/Config  2 Model  3 Katalog  4 Parser  5 Binder/DataFlow
//             6 Dinamik SQL  7 Son geçişler (Collapsed, Interprocedural)  8 Çıktı
// ============================================================================
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Xml.Linq;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
return await App.RunAsync(args);

// ============================================================================
#region 1. Giriş / Config
// ============================================================================

static class App
{
    public const string EngineVersion = "1.3.1";
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("infa", StringComparison.OrdinalIgnoreCase)) return await InfaLineage.App.RunAsync(args[1..]);
        try
        {
            var cfg = Cli.Parse(args);
            if (cfg == null) return 1;
            var run = new LineageRun(cfg);
            await run.ExecuteAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("FATAL: " + ex);
            return 2;
        }
    }
}

static class Log
{
    public static bool Verbose;
    static readonly object _lock = new();
    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) { if (Verbose) Write("DEBUG", msg); }
    static void Write(string lvl, string msg)
    {
        lock (_lock) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {lvl} {msg}");
    }
}

sealed class LineageConfig
{
    public List<ConnectionConfig> Connections { get; set; } = new();
    /// <summary>"*" = tüm kullanıcı DB'leri; aksi halde ad listesi.</summary>
    public List<string> Databases { get; set; } = new() { "*" };
    public List<string> Schemas { get; set; } = new() { "*" };
    public bool IncludeTriggers { get; set; } = true;
    public bool IncludeJobSteps { get; set; } = true;
    public bool IncludeSystemObjects { get; set; } = false;
    /// <summary>Bağlantısız mod: .sql dosyaları/klasörleri. Katalog dosyalardaki CREATE TABLE'lardan kurulur.</summary>
    public List<string> SqlFiles { get; set; } = new();
    public string FileModeServer { get; set; } = "FILES";
    public string FileModeDatabase { get; set; } = "FILES";
    public OutputConfig Output { get; set; } = new();
    public int Parallelism { get; set; } = Math.Max(2, Environment.ProcessorCount);
    public int ModuleTimeoutSeconds { get; set; } = 60;
    public int MaxDynamicAlternatives { get; set; } = 64;
    public int MaxPatternMatches { get; set; } = 50;
    public int MaxCollapseDepth { get; set; } = 32;
    /// <summary>Dinamik SQL delikleri için kaynak tablodan SELECT DISTINCT TOP (MaxConfigValues) okur (canlı sorgu, salt okunur).</summary>
    public bool ConfigTableLookup { get; set; } = false;
    /// <summary>Arşiv DB desenleri (regex, 1 yakalama grubu = kanonik ad). Eşleşen DB'ler taranmaz, referansları kanonik DB'ye bağlanır. Örn. "^(RAPOR)_(\\d{4}|@YEAR)$".</summary>
    public List<string> ArchiveDatabasePatterns { get; set; } = new();
    public bool SkipArchiveDatabases { get; set; } = true;
    /// <summary>Adı bu parçaları içeren modüller taranmaz (örn. "_old", "_sil", "deleted").</summary>
    public List<string> ExcludeModuleNameContains { get; set; } = new();
    /// <summary>Adı bu parçaları içeren nesnelere giden/gelen satırlar çıktıdan düşülür (politika: eski/silinecek kopyalar).</summary>
    public List<string> ExcludeObjectNameContains { get; set; } = new();
    public List<string> ExcludeDatabases { get; set; } = new();
    /// <summary>Global linked server / DNS alias → taranan sunucu adı.</summary>
    public Dictionary<string, string> ServerAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bu boyutu (karakter) aşan tanımlar analiz edilmez (DefinitionSizeLimit).</summary>
    public int MaxDefinitionChars { get; set; } = 5_000_000;
    /// <summary>Statements tablosu: her ifadenin metni (≤4000 karakter). Büyük ortamda satır sayısını çok artırır.</summary>
    public bool IncludeStatementText { get; set; } = false;
    /// <summary>--sql-target ile birlikte: önceki koşuda tanımı değişmeyen modüller yeniden analiz edilmez, satırları hedef DB'den geri yüklenir.</summary>
    public bool Incremental { get; set; } = false;
    public QueryConfig? Query { get; set; }
    public GraphConfig? Graph { get; set; }
    public int MaxConfigValues { get; set; } = 50;
    public int MaxCallDepth { get; set; } = 8;
    public bool Verbose { get; set; } = false;
}

sealed class ConnectionConfig
{
    public string Name { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    /// <summary>Boşsa global "databases" geçerli.</summary>
    public List<string> Databases { get; set; } = new();
    public List<string> ExcludeDatabases { get; set; } = new();
    /// <summary>Bu sunucuda tanımlı linked server adı → taranan sunucunun adı (sys.servers.data_source eşleşmiyorsa).</summary>
    public Dictionary<string, string> ServerAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class QueryConfig
{
    /// <summary>"db.schema.object" ya da "db.schema.object.column" (sunucu isteğe bağlı: "server.db.schema.object").</summary>
    public string Node { get; set; } = "";
    public string Direction { get; set; } = "up";   // up | down | both
    public int MaxHops { get; set; } = 10;
    public int MaxRows { get; set; } = 500_000;
}
sealed class GraphConfig
{
    public string Node { get; set; } = "";
    public int MaxHops { get; set; } = 3;
    public string Format { get; set; } = "dot";     // dot | graphml | json
    public string Level { get; set; } = "object";   // object | column
}

sealed class OutputConfig
{
    public string Directory { get; set; } = "out";
    public bool Xlsx { get; set; } = true;
    public bool Csv { get; set; } = true;
    public string CsvSeparator { get; set; } = ",";
    public bool SqlTableDdl { get; set; } = true;
    /// <summary>Doluysa sonuçlar SqlBulkCopy ile bu bağlantıdaki lineage.* tablolarına yazılır.</summary>
    public string? SqlBulkTarget { get; set; }
    public string SqlSchema { get; set; } = "lineage";
    public int MaxRowsPerSheet { get; set; } = 900_000;
}

static class Cli
{
    const string Usage = """
        SqlLineage — MSSQL lineage çıkarıcı

          --config <lineage.json>      yapılandırma dosyası (varsayılan: ./lineage.json varsa)
          --init                       örnek lineage.json yazar ve çıkar
          --server "<name>=<connstr>"  bağlantı ekler (tekrarlanabilir; name isteğe bağlı)
          --db <name>                  yalnız bu DB'ler (tekrarlanabilir)
          --files <path>               .sql dosyası/klasörü (bağlantısız mod; tekrarlanabilir)
          --out <dir>                  çıktı klasörü (varsayılan out)
          --sql-target "<connstr>"     sonuçları bu DB'de lineage.* tablolarına bulk yükler
          --no-xlsx / --no-csv         ilgili çıktıyı kapatır
          --parallel <n>               paralel modül analizi
          --config-tables              dinamik SQL delikleri için kaynak tablodan DISTINCT değer okur (canlı, salt okunur)
          --exclude-db <ad>            bu DB'yi tarama (tekrarlanabilir)
          --incremental                --sql-target ile: değişmeyen modülleri önceki koşudan geri yükle
          --statements                 her ifadenin metnini Statements tablosuna yaz
          --query "<db.schema.obj[.col]>" [--direction up|down|both] [--max-hops N]   çok sekmeli kolon soyağacı (Query sayfası/CSV)
          --graph "<db.schema.obj[.col]>" [--graph-hops N] [--graph-format dot|graphml|json] [--graph-level object|column]
          infa ...                     Informatica PowerCenter XML export modu (infa --help)
          --verbose
        """;

    public static LineageConfig? Parse(string[] args)
    {
        string? cfgPath = null;
        var overrides = new List<Action<LineageConfig>>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} için değer eksik");
            switch (a)
            {
                case "--config": cfgPath = Next(); break;
                case "--init": WriteSample(); return null;
                case "--server":
                    {
                        var v = Next();
                        int eq = v.IndexOf('=');
                        // "name=Server=..;" biçimi: ilk '=' öncesi ';' veya boşluk içermiyorsa ad kabul edilir
                        string name, cs;
                        if (eq > 0 && !v[..eq].Contains(';') && !v[..eq].Contains(' ') && !v[..eq].Equals("Server", StringComparison.OrdinalIgnoreCase) && !v[..eq].Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                        { name = v[..eq]; cs = v[(eq + 1)..]; }
                        else { name = ""; cs = v; }
                        overrides.Add(c => c.Connections.Add(new ConnectionConfig { Name = name, ConnectionString = cs }));
                        break;
                    }
                case "--db": { var v = Next(); overrides.Add(c => { if (c.Databases.Count == 1 && c.Databases[0] == "*") c.Databases.Clear(); c.Databases.Add(v); }); break; }
                case "--files": { var v = Next(); overrides.Add(c => c.SqlFiles.Add(v)); break; }
                case "--out": { var v = Next(); overrides.Add(c => c.Output.Directory = v); break; }
                case "--sql-target": { var v = Next(); overrides.Add(c => c.Output.SqlBulkTarget = v); break; }
                case "--no-xlsx": overrides.Add(c => c.Output.Xlsx = false); break;
                case "--no-csv": overrides.Add(c => c.Output.Csv = false); break;
                case "--parallel": { var v = int.Parse(Next()); overrides.Add(c => c.Parallelism = v); break; }
                case "--verbose": overrides.Add(c => c.Verbose = true); break;
                case "--config-tables": overrides.Add(c => c.ConfigTableLookup = true); break;
                case "--incremental": overrides.Add(c => c.Incremental = true); break;
                case "--statements": overrides.Add(c => c.IncludeStatementText = true); break;
                case "--exclude-db": { var v = Next(); overrides.Add(c => c.ExcludeDatabases.Add(v)); break; }
                case "--query": { var v = Next(); overrides.Add(c => { c.Query ??= new QueryConfig(); c.Query.Node = v; }); break; }
                case "--direction": { var v = Next(); overrides.Add(c => { c.Query ??= new QueryConfig(); c.Query.Direction = v; }); break; }
                case "--max-hops": { var v = int.Parse(Next()); overrides.Add(c => { c.Query ??= new QueryConfig(); c.Query.MaxHops = v; if (c.Graph != null) c.Graph.MaxHops = v; }); break; }
                case "--graph": { var v = Next(); overrides.Add(c => { c.Graph ??= new GraphConfig(); c.Graph.Node = v; }); break; }
                case "--graph-format": { var v = Next(); overrides.Add(c => { c.Graph ??= new GraphConfig(); c.Graph.Format = v; }); break; }
                case "--graph-level": { var v = Next(); overrides.Add(c => { c.Graph ??= new GraphConfig(); c.Graph.Level = v; }); break; }
                case "--graph-hops": { var v = int.Parse(Next()); overrides.Add(c => { c.Graph ??= new GraphConfig(); c.Graph.MaxHops = v; }); break; }
                case "-h": case "--help": Console.WriteLine(Usage); return null;
                default: Console.WriteLine("Bilinmeyen argüman: " + a); Console.WriteLine(Usage); return null;
            }
        }
        LineageConfig cfg;
        cfgPath ??= File.Exists("lineage.json") ? "lineage.json" : null;
        if (cfgPath != null)
        {
            var json = File.ReadAllText(cfgPath);
            cfg = JsonSerializer.Deserialize<LineageConfig>(json, JsonOpts) ?? new LineageConfig();
            Log.Info($"Config: {Path.GetFullPath(cfgPath)}");
        }
        else cfg = new LineageConfig();
        foreach (var o in overrides) o(cfg);
        Log.Verbose = cfg.Verbose;
        if (cfg.Connections.Count == 0 && cfg.SqlFiles.Count == 0)
        {
            Console.WriteLine(Usage);
            Console.WriteLine("\nNe bağlantı ne de --files verildi. `--init` ile örnek lineage.json üretin.");
            return null;
        }
        for (int i = 0; i < cfg.Connections.Count; i++)
            if (string.IsNullOrWhiteSpace(cfg.Connections[i].Name)) cfg.Connections[i].Name = "SRV" + (i + 1);
        return cfg;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static void WriteSample()
    {
        var sample = new LineageConfig
        {
            Connections = { new ConnectionConfig { Name = "SRV-ETL01", ConnectionString = "Server=etl01;Database=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=True" } },
            Databases = new() { "*" },
        };
        File.WriteAllText("lineage.json", JsonSerializer.Serialize(sample, JsonOpts));
        Console.WriteLine("lineage.json yazıldı. Bağlantı dizesini düzenleyip: dotnet run SqlLineage.cs -- --config lineage.json");
        Console.WriteLine("Not: Microsoft.Data.SqlClient varsayılan olarak Encrypt=True; sertifika yoksa TrustServerCertificate=True ekleyin.");
    }
}

#endregion

// ============================================================================
#region 2. Model
// ============================================================================

/// <summary>Türkçe collation'a hoşgörülü, kültürden bağımsız ad karşılaştırıcı (I/ı/İ/i tek sınıf).</summary>
sealed class NameComparer : IEqualityComparer<string>, IComparer<string>
{
    public static readonly NameComparer Instance = new();
    public static string Norm(string s)
    {
        if (s.IndexOf('ı') < 0 && s.IndexOf('İ') < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch == 'ı' ? 'i' : ch == 'İ' ? 'i' : ch);
        return sb.ToString();
    }
    public bool Equals(string? x, string? y) => x == null ? y == null : y != null && string.Equals(Norm(x), Norm(y), StringComparison.OrdinalIgnoreCase);
    public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(Norm(s));
    public int Compare(string? x, string? y) => string.Compare(x == null ? null : Norm(x), y == null ? null : Norm(y), StringComparison.OrdinalIgnoreCase);
    public static bool Eq(string? x, string? y) => Instance.Equals(x, y);
}

enum ObjType
{
    Table, View, Procedure, ScalarFunction, InlineTvf, MsTvf, ClrModule, Trigger, Synonym, TableType, Sequence,
    TempTable, TableVariable, Tvp, Cursor, ResultSet, File, External, Unresolved, JobStep, Script, System
}

/// <summary>Nesne kimliği: (sunucu, db, şema, ad, tür). Şema, modül-yerel nesnelerde (temp, @tablo) sahip modülü taşır.</summary>
sealed class ObjRef : IEquatable<ObjRef>
{
    public string Server { get; }
    public string Database { get; }
    public string Schema { get; }
    public string Name { get; }
    public ObjType Type { get; }
    public ObjRef(string server, string database, string schema, string name, ObjType type)
    { Server = server ?? ""; Database = database ?? ""; Schema = schema ?? ""; Name = name ?? ""; Type = type; }
    public bool IsPersistent => Type is ObjType.Table or ObjType.View;
    public bool IsModuleLocal => Type is ObjType.TempTable or ObjType.TableVariable or ObjType.Tvp or ObjType.Cursor;
    public string Fqn => $"{Server}.{Database}.{Schema}.{Name}";
    public string Key => NameComparer.Norm(Fqn).ToUpperInvariant() + "|" + Type;
    public ObjRef With(ObjType t) => new(Server, Database, Schema, Name, t);
    public bool Equals(ObjRef? o) => o != null && Type == o.Type && NameComparer.Eq(Name, o.Name) && NameComparer.Eq(Schema, o.Schema) && NameComparer.Eq(Database, o.Database) && NameComparer.Eq(Server, o.Server);
    public override bool Equals(object? obj) => Equals(obj as ObjRef);
    public override int GetHashCode() => HashCode.Combine(NameComparer.Instance.GetHashCode(Name), NameComparer.Instance.GetHashCode(Schema), NameComparer.Instance.GetHashCode(Database), Type);
    public override string ToString() => Fqn;
}

enum FlowKind { Direct, Positional, Expression, Aggregate, Indirect }
enum Provenance { Static, Interprocedural, Trigger, DynamicStatic, DynamicPartial, Catalog, Collapsed }
enum Confidence { Exact, High, Medium, Low }

/// <summary>Kolon kaynağı: nesne + kolon adı (+ bağlama güveni, eşitliğe dahil değil).</summary>
sealed class ColRef : IEquatable<ColRef>
{
    public ObjRef Obj { get; }
    public string Column { get; }
    public Confidence Conf { get; }
    public ColRef(ObjRef obj, string column, Confidence conf = Confidence.Exact) { Obj = obj; Column = column ?? "*"; Conf = conf; }
    public bool Equals(ColRef? o) => o != null && Obj.Equals(o.Obj) && NameComparer.Eq(Column, o.Column);
    public override bool Equals(object? obj) => Equals(obj as ColRef);
    public override int GetHashCode() => HashCode.Combine(Obj.GetHashCode(), NameComparer.Instance.GetHashCode(Column));
    public override string ToString() => $"{Obj}.{Column}";
}

/// <summary>Bir ifadenin bağımlılık kümesi: veri kaynakları (tür ile) + dolaylı (kontrol) kaynaklar.</summary>
sealed class Deps
{
    public static readonly Deps None = new();
    readonly Dictionary<ColRef, FlowKind> _data = new();
    readonly HashSet<ColRef> _indirect = new();
    public IReadOnlyDictionary<ColRef, FlowKind> Data => _data;
    public IReadOnlyCollection<ColRef> Indirect => _indirect;
    public bool IsEmpty => _data.Count == 0 && _indirect.Count == 0;
    public bool HasData => _data.Count > 0;

    static int Rank(FlowKind k) => k switch { FlowKind.Direct => 0, FlowKind.Positional => 1, FlowKind.Expression => 2, FlowKind.Aggregate => 3, _ => -1 };
    public static FlowKind Stronger(FlowKind a, FlowKind b) => Rank(a) >= Rank(b) ? a : b;

    public static Deps Direct(ColRef c) { var d = new Deps(); d._data[c] = FlowKind.Direct; return d; }
    public static Deps Of(ColRef c, FlowKind k) { var d = new Deps(); if (k == FlowKind.Indirect) d._indirect.Add(c); else d._data[c] = k; return d; }
    public static Deps Union(params Deps[] xs) => Union((IEnumerable<Deps>)xs);
    public static Deps Union(IEnumerable<Deps> xs)
    {
        var d = new Deps();
        foreach (var x in xs) { if (x == null) continue; foreach (var kv in x._data) d.Add(kv.Key, kv.Value); foreach (var c in x._indirect) d._indirect.Add(c); }
        return d;
    }
    void Add(ColRef c, FlowKind k) { if (_data.TryGetValue(c, out var old)) _data[c] = Stronger(old, k); else _data[c] = k; }
    public Deps Union(Deps other) => Union(this, other);
    public Deps Map(Func<FlowKind, FlowKind> f)
    {
        var d = new Deps();
        foreach (var kv in _data) d.Add(kv.Key, f(kv.Value));
        foreach (var c in _indirect) d._indirect.Add(c);
        return d;
    }
    /// <summary>Tüm veri kaynaklarını en az Expression yapar (Direct → Expression).</summary>
    public Deps AsExpression() => Map(k => Stronger(k, FlowKind.Expression));
    public Deps AsAggregate() => Map(k => Stronger(k, FlowKind.Aggregate));
    public Deps AsPositional() => Map(k => Stronger(k, FlowKind.Positional));
    /// <summary>Tüm veri kaynaklarını dolaylı (kontrol) yapar.</summary>
    public Deps AsIndirect()
    {
        var d = new Deps();
        foreach (var kv in _data) d._indirect.Add(kv.Key);
        foreach (var c in _indirect) d._indirect.Add(c);
        return d;
    }
    public ColRef? SingleDirect() => _data.Count == 1 && _data.First().Value is FlowKind.Direct or FlowKind.Positional ? _data.First().Key : null;
}

// ---- Çıktı satırları (sayfa/tablo başına bir sınıf; property sırası = kolon sırası) ----

sealed class ColumnLineageRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ModuleType { get; set; } = "";
    public int StatementNo { get; set; }
    public string StatementType { get; set; } = "";
    public int Line { get; set; }
    public string SourceServer { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string SourceSchema { get; set; } = "";
    public string SourceObject { get; set; } = "";
    public string SourceObjectType { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public string TargetServer { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string TargetSchema { get; set; } = "";
    public string TargetObject { get; set; } = "";
    public string TargetObjectType { get; set; } = "";
    public string TargetColumn { get; set; } = "";
    public string FlowKind { get; set; } = "";
    public string Expression { get; set; } = "";
    public string Provenance { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Note { get; set; } = "";
}

sealed class ObjectLineageRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ModuleType { get; set; } = "";
    public int StatementNo { get; set; }
    public string StatementType { get; set; } = "";
    public int Line { get; set; }
    public string Action { get; set; } = "";
    public string ObjectServer { get; set; } = "";
    public string ObjectDatabase { get; set; } = "";
    public string ObjectSchema { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public string Provenance { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Note { get; set; } = "";
}

sealed class CollapsedRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ModuleType { get; set; } = "";
    public string SourceServer { get; set; } = "";
    public string SourceDatabase { get; set; } = "";
    public string SourceSchema { get; set; } = "";
    public string SourceObject { get; set; } = "";
    public string SourceObjectType { get; set; } = "";
    public string SourceColumn { get; set; } = "";
    public string TargetServer { get; set; } = "";
    public string TargetDatabase { get; set; } = "";
    public string TargetSchema { get; set; } = "";
    public string TargetObject { get; set; } = "";
    public string TargetColumn { get; set; } = "";
    public string FlowKind { get; set; } = "";
    public int Hops { get; set; }
    public string Path { get; set; } = "";
    public string Confidence { get; set; } = "";
}

sealed class ModuleRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ModuleType { get; set; } = "";
    public string ParseStatus { get; set; } = "";
    public string ParserUsed { get; set; } = "";
    public int DefinitionLength { get; set; }
    public int Statements { get; set; }
    public int DynamicSites { get; set; }
    public int ColumnEdges { get; set; }
    public int ObjectEdges { get; set; }
    public int UnresolvedNames { get; set; }
    public int ElapsedMs { get; set; }
    public DateTime? ModifyDate { get; set; }
    public string DefinitionHash { get; set; } = "";
    public bool Reused { get; set; }
    public string Errors { get; set; } = "";
    public string Holes { get; set; } = "";
}

sealed class DynamicSqlRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int StatementNo { get; set; }
    public int Line { get; set; }
    public string SinkKind { get; set; } = "";
    public string Status { get; set; } = "";
    public int Alternatives { get; set; }
    public string Template { get; set; } = "";
    public string Holes { get; set; } = "";
    public int EdgesProduced { get; set; }
    public string ParseError { get; set; } = "";
}

sealed class UnresolvedRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int StatementNo { get; set; }
    public int Line { get; set; }
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Name { get; set; } = "";
    public string Note { get; set; } = "";
}

sealed class StatementRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public int StatementNo { get; set; }
    public int Line { get; set; }
    public string StatementType { get; set; } = "";
    public bool Dynamic { get; set; }
    public string SqlText { get; set; } = "";
}

sealed class CatalogDepRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ReferencedServer { get; set; } = "";
    public string ReferencedDatabase { get; set; } = "";
    public string ReferencedSchema { get; set; } = "";
    public string ReferencedName { get; set; } = "";
    public bool FoundInAnalysis { get; set; }
}

sealed class IssueSummaryRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ModuleSchema { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string ParseStatus { get; set; } = "";
    public int Issues { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public string TopKinds { get; set; } = "";
}

sealed class QueryRow
{
    public string RunId { get; set; } = "";
    public string Direction { get; set; } = "";
    public string StartNode { get; set; } = "";
    public string StartColumn { get; set; } = "";
    public string EndServer { get; set; } = "";
    public string EndDatabase { get; set; } = "";
    public string EndSchema { get; set; } = "";
    public string EndObject { get; set; } = "";
    public string EndObjectType { get; set; } = "";
    public string EndColumn { get; set; } = "";
    public int Hops { get; set; }
    public string FlowKind { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string TerminalReason { get; set; } = "";
    public string Path { get; set; } = "";
    public string Modules { get; set; } = "";
}

sealed class UnresolvedKindRow
{
    public string RunId { get; set; } = ""; public string Server { get; set; } = ""; public string Database { get; set; } = ""; public string Kind { get; set; } = ""; public int Count { get; set; } public int DistinctNames { get; set; } public string Example { get; set; } = "";
}

sealed class SummaryRow
{
    public string RunId { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public int Modules { get; set; }
    public int Parsed { get; set; }
    public int ParsedWithHoles { get; set; }
    public int Quarantined { get; set; }
    public int NoDefinition { get; set; }
    public double ParseRate { get; set; }
    public int ObjectRefs { get; set; }
    public int ObjectRefsResolved { get; set; }
    public double BindRate { get; set; }
    public int DynamicSites { get; set; }
    public int DynamicResolved { get; set; }
    public double DynamicResolutionRate { get; set; }
    public int ColumnLineageRows { get; set; }
    public int ObjectLineageRows { get; set; }
    public int CollapsedRows { get; set; }
    public int CatalogDepsMissed { get; set; }
    public int ReusedModules { get; set; }
    public string EngineSignature { get; set; } = "";
    public int ElapsedMs { get; set; }
}

#endregion

// ============================================================================
#region 3. Katalog
// ============================================================================

sealed class ColInfo
{
    public string Name = ""; public int Ordinal; public string TypeName = "";
    public bool IsIdentity, IsComputed, IsRowVersion;
}

sealed class ParamInfo
{
    public string Name = ""; public int Ordinal; public string TypeName = ""; public bool IsOutput; public bool IsTableType;
    public string? DefaultLiteral;   // parse edilen CREATE PROC'tan doldurulur (sys.parameters T-SQL default'unu vermez)
}

sealed class ObjInfo
{
    public ObjRef Ref = null!;
    public int ObjectId;
    public string TypeCode = "";           // U V P PC FN IF TF FS FT TR SN TT SO X ...
    public DateTime? ModifyDate;
    public List<ColInfo> Columns = new();
    public string? SynonymBase;
    public string? Definition;
    public bool DefinitionMissing;         // WITH ENCRYPTION / CLR
    public bool QuotedIdentifier = true;
    public bool AnsiNulls = true;
    public List<ParamInfo> Params = new();
    public int? ParentObjectId;            // trigger
    public bool IsDisabled, IsInsteadOf;
    public string TriggerEvents = "";
    public string DefaultSchema = "dbo";   // sahibin default şeması (şemasız EXEC çözümü için)
    public int? ProcedureNumber;
    public bool IsMsShipped;
    public string ModuleTypeName => TypeCode switch
    {
        "P" => "Procedure", "PC" => "ClrProcedure", "V" => "View", "FN" => "ScalarFunction", "IF" => "InlineTvf",
        "TF" => "MsTvf", "FS" => "ClrScalarFunction", "FT" => "ClrTvf", "TR" => "Trigger", "JOB" => "JobStep", "FILE" => "Script",
        _ => TypeCode
    };
    public bool IsModule => TypeCode is "P" or "V" or "FN" or "IF" or "TF" or "TR" or "JOB" or "FILE" or "PC" or "FS" or "FT";
    public ColInfo? Column(string name) => Columns.FirstOrDefault(c => NameComparer.Eq(c.Name, name));
}

sealed class DepInfo
{
    public int ReferencingId; public string? Server, Database, Schema; public string Name = "";
}

sealed class DbCatalog
{
    public string Server = ""; public string Name = ""; public int Compat = 150; public string Collation = "";
    public Dictionary<string, ObjInfo> Objects = new(NameComparer.Instance);      // "schema.name"
    public Dictionary<int, ObjInfo> ById = new();
    public Dictionary<string, ObjInfo> TableTypes = new(NameComparer.Instance);   // "schema.name"
    public List<ObjInfo> Modules = new();
    public List<DepInfo> Deps = new();
    public ConcurrentDictionary<string, ObjInfo> Overlay = new(NameComparer.Instance); // SELECT INTO / CREATE TABLE ile tarama sırasında yaratılanlar
    public bool IsTurkish => Collation.StartsWith("Turkish", StringComparison.OrdinalIgnoreCase);

    public ObjInfo? Find(string schema, string name)
    {
        if (Objects.TryGetValue(schema + "." + name, out var o)) return o;
        if (Overlay.TryGetValue(schema + "." + name, out o)) return o;
        return null;
    }
    public IEnumerable<ObjInfo> FindByName(string name) => Objects.Values.Where(o => NameComparer.Eq(o.Ref.Name, name));
    public void Add(ObjInfo o)
    {
        Objects[o.Ref.Schema + "." + o.Ref.Name] = o;
        if (o.ObjectId != 0) ById[o.ObjectId] = o;
    }
}

sealed class LinkedServerInfo { public string Name = ""; public string DataSource = ""; public string Catalog = ""; public string Provider = ""; }

sealed class JobStepInfo
{
    public string JobName = ""; public int StepId; public string StepName = ""; public string Subsystem = ""; public string Command = ""; public string DatabaseName = ""; public bool Enabled;
}

sealed class ServerCatalog
{
    public string Name = "";            // SERVERPROPERTY('ServerName')
    public string ConfigName = "";
    public string MachineName = "";
    public int Major = 15;
    public string Version = "";
    public Dictionary<string, DbCatalog> Dbs = new(NameComparer.Instance);
    public Dictionary<string, LinkedServerInfo> Linked = new(NameComparer.Instance);
    public List<JobStepInfo> JobSteps = new();
}

sealed class Catalog
{
    public List<ServerCatalog> Servers = new();
    public List<Regex> ArchivePatterns = new();
    /// <summary>Arşiv DB adını kanonik ada çevirir (RAPOR_2024 → RAPOR); desen yoksa aynen döner. "%"/yer tutucu içeren adlar "2000" ile denenir.</summary>
    public string Canonical(string db)
    {
        if (ArchivePatterns.Count == 0 || string.IsNullOrEmpty(db)) return db;
        string probe = Regex.Replace(db, @"LH_[IV]\d+|%|⟨[^⟩]*⟩", "2000");
        foreach (var rx in ArchivePatterns) { var m = rx.Match(probe); if (m.Success) return m.Groups.Count > 1 ? m.Groups[1].Value : probe; }
        return db;
    }
    public bool IsArchive(string db) => ArchivePatterns.Count > 0 && !NameComparer.Eq(Canonical(db), db);
    readonly Dictionary<string, ServerCatalog> _byName = new(NameComparer.Instance);

    public void AddAlias(string alias, string serverName) { if (_byName.TryGetValue(serverName, out var s)) _byName.TryAdd(alias, s); }
    public void Add(ServerCatalog s)
    {
        Servers.Add(s);
        _byName[s.Name] = s;
        if (!string.IsNullOrEmpty(s.ConfigName)) _byName.TryAdd(s.ConfigName, s);
        if (!string.IsNullOrEmpty(s.MachineName)) _byName.TryAdd(s.MachineName, s);
        // "HOST\INSTANCE" → "HOST" eşlemesi (varsayılan instance)
        int bs = s.Name.IndexOf('\\');
        if (bs > 0) _byName.TryAdd(s.Name[..bs], s);
    }
    public ServerCatalog? FindServer(string name) => _byName.TryGetValue(name, out var s) ? s : null;
    public DbCatalog? FindDb(string server, string db)
    {
        var srv = FindServer(server); if (srv == null) return null;
        if (srv.Dbs.TryGetValue(db, out var d)) return d;
        var canon = Canonical(db);
        return !NameComparer.Eq(canon, db) && srv.Dbs.TryGetValue(canon, out d) ? d : null;
    }

    /// <summary>Linked server adını taranan bir sunucuya eşler; bulunamazsa data_source (ya da ad) döner, taranmamış işaretiyle.</summary>
    public (string serverName, bool scanned) ResolveLinked(string fromServer, string linkedName)
    {
        var from = FindServer(fromServer);
        if (from != null && from.Linked.TryGetValue(linkedName, out var ls))
        {
            var ds = ls.DataSource;
            if (!string.IsNullOrEmpty(ds))
            {
                var t = FindServer(ds) ?? FindServer(ds.Split(',')[0]);
                if (t != null) return (t.Name, true);
                return (ds, false);
            }
        }
        var direct = FindServer(linkedName);
        return direct != null ? (direct.Name, true) : (linkedName, false);
    }
}

static class ConfigTableReader
{
    public static readonly ConcurrentDictionary<string, string> Connections = new(NameComparer.Instance);   // sunucu adı → bağlantı dizesi
    static readonly ConcurrentDictionary<string, List<string>> _cache = new();
    public static List<string> Values(ColRef c, LineageConfig cfg)
    {
        string key = c.Obj.Key + "|" + c.Column;
        return _cache.GetOrAdd(key, _ =>
        {
            var list = new List<string>();
            if (!Connections.TryGetValue(c.Obj.Server, out var cs)) return list;
            try
            {
                using var cn = new SqlConnection(cs); cn.Open();
                string q = $"SELECT DISTINCT TOP ({cfg.MaxConfigValues}) CAST([{c.Column}] AS nvarchar(max)) FROM [{c.Obj.Database}].[{c.Obj.Schema}].[{c.Obj.Name}] WITH (NOLOCK) WHERE [{c.Column}] IS NOT NULL";
                using var cmd = new SqlCommand(q, cn) { CommandTimeout = 20 };
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var v = r.GetString(0).Trim(); if (v.Length > 0 && v.Length < 200_000) list.Add(v); }
                Log.Debug($"config-tables: {c} → {list.Count} değer");
            }
            catch (Exception ex) { Log.Debug($"config-tables {c}: {ex.Message}"); }
            return list;
        });
    }
}

static class CatalogLoader
{
    static Catalog? ArchiveFilter;
    public static async Task<Catalog> LoadAsync(LineageConfig cfg)
    {
        var cat = new Catalog();
        foreach (var p in cfg.ArchiveDatabasePatterns) { try { cat.ArchivePatterns.Add(new Regex(p, RegexOptions.IgnoreCase)); } catch (Exception ex) { Log.Warn($"ArchiveDatabasePatterns geçersiz regex '{p}': {ex.Message}"); } }
        ArchiveFilter = cat;
        var tasks = cfg.Connections.Select(c => LoadServerAsync(cfg, c)).ToList();
        var loaded = new List<(ServerCatalog s, ConnectionConfig cc)>();
        for (int i = 0; i < tasks.Count; i++)
        {
            var s = await tasks[i];
            if (s != null) { cat.Add(s); loaded.Add((s, cfg.Connections[i])); }
        }
        foreach (var (s, cc) in loaded) foreach (var kv in cc.ServerAliases) cat.AddAlias(kv.Key, kv.Value);
        foreach (var kv in cfg.ServerAliases) cat.AddAlias(kv.Key, kv.Value);
        if (cfg.SqlFiles.Count > 0) cat.Add(FileCatalog.Build(cfg));
        return cat;
    }

    static async Task<ServerCatalog?> LoadServerAsync(LineageConfig cfg, ConnectionConfig cc)
    {
        var sw = Stopwatch.StartNew();
        var s = new ServerCatalog { ConfigName = cc.Name };
        try
        {
            using var cn = new SqlConnection(cc.ConnectionString);
            await cn.OpenAsync();
            using (var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)), CAST(SERVERPROPERTY('MachineName') AS nvarchar(256))", cn))
            using (var r = await cmd.ExecuteReaderAsync())
            {
                await r.ReadAsync();
                s.Name = r.GetString(0); s.Version = r.GetString(1); s.MachineName = r.IsDBNull(2) ? "" : r.GetString(2);
                s.Major = int.TryParse(s.Version.Split('.')[0], out var mj) ? mj : 15;
            }
            Log.Info($"[{cc.Name}] bağlandı: {s.Name} (v{s.Version})");
            ConfigTableReader.Connections[s.Name] = cc.ConnectionString;

            // linked servers
            try
            {
                using var cmd = new SqlCommand("SELECT name, ISNULL(data_source,''), ISNULL(catalog,''), ISNULL(provider,'') FROM sys.servers WHERE is_linked = 1", cn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    s.Linked[r.GetString(0)] = new LinkedServerInfo { Name = r.GetString(0), DataSource = r.GetString(1), Catalog = r.GetString(2), Provider = r.GetString(3) };
            }
            catch (Exception ex) { Log.Warn($"[{cc.Name}] sys.servers okunamadı: {ex.Message}"); }

            // job steps
            if (cfg.IncludeJobSteps)
            {
                try
                {
                    using var cmd = new SqlCommand("SELECT j.name, st.step_id, st.step_name, st.subsystem, ISNULL(st.command,''), ISNULL(st.database_name,'master'), j.enabled FROM msdb.dbo.sysjobs j JOIN msdb.dbo.sysjobsteps st ON st.job_id = j.job_id", cn);
                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        s.JobSteps.Add(new JobStepInfo { JobName = r.GetString(0), StepId = r.GetInt32(1), StepName = r.GetString(2), Subsystem = r.GetString(3), Command = r.GetString(4), DatabaseName = r.GetString(5), Enabled = r.GetByte(6) == 1 });
                    Log.Info($"[{cc.Name}] {s.JobSteps.Count} job adımı");
                }
                catch (Exception ex) { Log.Warn($"[{cc.Name}] msdb job adımları okunamadı: {ex.Message}"); }
            }

            // databases
            var dbs = new List<(string name, int compat, string coll)>();
            using (var cmd = new SqlCommand("SELECT name, compatibility_level, ISNULL(collation_name,''), database_id FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", cn))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    var name = r.GetString(0); int id = r.GetInt32(3);
                    var dbList = cc.Databases.Count > 0 ? cc.Databases : cfg.Databases;
                    bool wanted = dbList.Contains("*") ? id > 4 && !NameComparer.Eq(name, "distribution") : dbList.Any(d => NameComparer.Eq(d, name));
                    if (wanted && (cc.ExcludeDatabases.Any(x => NameComparer.Eq(x, name)) || cfg.ExcludeDatabases.Any(x => NameComparer.Eq(x, name)))) { Log.Info($"[{cc.Name}] {name} hariç tutuldu (excludeDatabases)"); wanted = false; }
                    if (wanted && cfg.SkipArchiveDatabases && ArchiveFilter != null && ArchiveFilter.IsArchive(name)) { Log.Info($"[{cc.Name}] {name} arşiv DB (→ {ArchiveFilter.Canonical(name)}), taranmıyor"); wanted = false; }
                    if (wanted) dbs.Add((name, Convert.ToInt32(r.GetValue(1)), r.GetString(2)));
                }
            foreach (var (name, compat, coll) in dbs)
            {
                try
                {
                    var db = await LoadDbAsync(cfg, cn, s, name, compat, coll);
                    s.Dbs[name] = db;
                }
                catch (Exception ex) { Log.Error($"[{cc.Name}] {name} yüklenemedi: {ex.Message}"); }
            }
            Log.Info($"[{cc.Name}] katalog: {s.Dbs.Count} DB, {s.Dbs.Values.Sum(d => d.Modules.Count)} modül, {sw.ElapsedMilliseconds} ms");
            return s;
        }
        catch (Exception ex)
        {
            Log.Error($"[{cc.Name}] bağlantı/katalog hatası: {ex.Message}" + (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? "  → bağlantı dizesine TrustServerCertificate=True ekleyin" : ""));
            return null;
        }
    }

    static async Task<DbCatalog> LoadDbAsync(LineageConfig cfg, SqlConnection cn, ServerCatalog s, string dbName, int compat, string coll)
    {
        var db = new DbCatalog { Server = s.Name, Name = dbName, Compat = compat, Collation = coll };
        await cn.ChangeDatabaseAsync(dbName);
        string sysFilter = cfg.IncludeSystemObjects ? "" : " AND o.is_ms_shipped = 0";
        string schemaFilter = cfg.Schemas.Contains("*") ? "" : " AND s.name IN (" + string.Join(",", cfg.Schemas.Select(x => "'" + x.Replace("'", "''") + "'")) + ")";

        // objects (+ default schema of owner principal)
        var q = $@"SELECT o.object_id, s.name, o.name, o.type, o.modify_date, o.parent_object_id, o.is_ms_shipped,
                          ISNULL(dp.default_schema_name, ISNULL(sp.default_schema_name, 'dbo'))
                   FROM sys.objects o
                   JOIN sys.schemas s ON s.schema_id = o.schema_id
                   LEFT JOIN sys.database_principals dp ON dp.principal_id = o.principal_id
                   LEFT JOIN sys.database_principals sp ON sp.principal_id = s.principal_id
                   WHERE o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT','TR','SN','SO','ET','X'){sysFilter}{schemaFilter}";
        using (var cmd = new SqlCommand(q, cn) { CommandTimeout = 0 })
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var type = r.GetString(3).Trim();
                var ot = type switch { "U" or "ET" => ObjType.Table, "V" => ObjType.View, "P" or "X" => ObjType.Procedure, "PC" => ObjType.ClrModule, "FN" => ObjType.ScalarFunction, "IF" => ObjType.InlineTvf, "TF" => ObjType.MsTvf, "FS" or "FT" => ObjType.ClrModule, "TR" => ObjType.Trigger, "SN" => ObjType.Synonym, "SO" => ObjType.Sequence, _ => ObjType.External };
                var o = new ObjInfo
                {
                    ObjectId = r.GetInt32(0), TypeCode = type, ModifyDate = r.GetDateTime(4),
                    Ref = new ObjRef(s.Name, dbName, r.GetString(1), r.GetString(2), ot),
                    ParentObjectId = r.GetInt32(5) == 0 ? null : r.GetInt32(5), IsMsShipped = r.GetBoolean(6), DefaultSchema = r.GetString(7),
                };
                db.Add(o);
            }

        // columns
        using (var cmd = new SqlCommand(@"SELECT c.object_id, c.column_id, c.name, ISNULL(t.name,''), c.is_identity, c.is_computed, c.system_type_id
                                          FROM sys.columns c LEFT JOIN sys.types t ON t.user_type_id = c.user_type_id ORDER BY c.object_id, c.column_id", cn) { CommandTimeout = 0 })
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                if (!db.ById.TryGetValue(r.GetInt32(0), out var o)) continue;
                o.Columns.Add(new ColInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3), IsIdentity = r.GetBoolean(4), IsComputed = r.GetBoolean(5), IsRowVersion = r.GetByte(6) == 189 });
            }

        // table types (TVP)
        using (var cmd = new SqlCommand(@"SELECT tt.type_table_object_id, s.name, tt.name FROM sys.table_types tt JOIN sys.schemas s ON s.schema_id = tt.schema_id", cn))
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var o = new ObjInfo { ObjectId = r.GetInt32(0), TypeCode = "TT", Ref = new ObjRef(s.Name, dbName, r.GetString(1), r.GetString(2), ObjType.TableType) };
                db.TableTypes[o.Ref.Schema + "." + o.Ref.Name] = o;
                db.ById[o.ObjectId] = o;
            }
        using (var cmd = new SqlCommand(@"SELECT c.object_id, c.column_id, c.name, ISNULL(t.name,'') FROM sys.columns c JOIN sys.table_types tt ON tt.type_table_object_id = c.object_id LEFT JOIN sys.types t ON t.user_type_id = c.user_type_id ORDER BY c.object_id, c.column_id", cn))
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o) && o.TypeCode == "TT")
                    o.Columns.Add(new ColInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3) });

        // synonyms
        using (var cmd = new SqlCommand("SELECT object_id, base_object_name FROM sys.synonyms", cn))
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o)) o.SynonymBase = r.GetString(1);

        // modules
        using (var cmd = new SqlCommand("SELECT m.object_id, m.definition, m.uses_quoted_identifier, m.uses_ansi_nulls FROM sys.sql_modules m", cn) { CommandTimeout = 0 })
        using (var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
            while (await r.ReadAsync())
            {
                int id = r.GetInt32(0);
                string? def = r.IsDBNull(1) ? null : r.GetString(1);
                bool qi = r.GetBoolean(2), an = r.GetBoolean(3);
                if (!db.ById.TryGetValue(id, out var o)) continue;
                o.Definition = def; o.DefinitionMissing = def == null; o.QuotedIdentifier = qi; o.AnsiNulls = an;
            }
        // numbered procedures
        try
        {
            using var cmd = new SqlCommand("SELECT object_id, procedure_number, definition FROM sys.numbered_procedures", cn);
            using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            while (await r.ReadAsync())
            {
                int id = r.GetInt32(0); int num = Convert.ToInt32(r.GetValue(1)); string? def = r.IsDBNull(2) ? null : r.GetString(2);
                if (!db.ById.TryGetValue(id, out var parent)) continue;
                var o = new ObjInfo { ObjectId = 0, TypeCode = "P", Ref = new ObjRef(s.Name, dbName, parent.Ref.Schema, parent.Ref.Name + ";" + num, ObjType.Procedure), Definition = def, DefinitionMissing = def == null, ProcedureNumber = num, DefaultSchema = parent.DefaultSchema, ModifyDate = parent.ModifyDate };
                db.Add(o);
            }
        }
        catch (Exception ex) { Log.Debug($"numbered_procedures: {ex.Message}"); }

        // parameters
        using (var cmd = new SqlCommand("SELECT p.object_id, p.parameter_id, p.name, ISNULL(t.name,''), p.is_output, ISNULL(t.is_table_type,0) FROM sys.parameters p LEFT JOIN sys.types t ON t.user_type_id = p.user_type_id WHERE p.parameter_id > 0 ORDER BY p.object_id, p.parameter_id", cn) { CommandTimeout = 0 })
        using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o))
                    o.Params.Add(new ParamInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3), IsOutput = r.GetBoolean(4), IsTableType = r.GetBoolean(5) });

        // triggers
        if (cfg.IncludeTriggers)
        {
            using var cmd = new SqlCommand(@"SELECT t.object_id, t.parent_id, t.is_disabled, t.is_instead_of_trigger,
                                             STUFF((SELECT ','+te.type_desc FROM sys.trigger_events te WHERE te.object_id = t.object_id FOR XML PATH('')),1,1,'')
                                             FROM sys.triggers t WHERE t.parent_class = 1", cn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o))
                { o.ParentObjectId = r.GetInt32(1); o.IsDisabled = r.GetBoolean(2); o.IsInsteadOf = r.GetBoolean(3); o.TriggerEvents = r.IsDBNull(4) ? "" : r.GetString(4); }
        }
        else
            foreach (var t in db.Objects.Values.Where(o => o.TypeCode == "TR").ToList()) { db.Objects.Remove(t.Ref.Schema + "." + t.Ref.Name); }

        // catalog dependencies (çapraz kontrol)
        try
        {
            using var cmd = new SqlCommand("SELECT referencing_id, referenced_server_name, referenced_database_name, referenced_schema_name, referenced_entity_name FROM sys.sql_expression_dependencies WHERE referenced_minor_id = 0", cn) { CommandTimeout = 0 };
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                db.Deps.Add(new DepInfo { ReferencingId = r.GetInt32(0), Server = r.IsDBNull(1) ? null : r.GetString(1), Database = r.IsDBNull(2) ? null : r.GetString(2), Schema = r.IsDBNull(3) ? null : r.GetString(3), Name = r.GetString(4) });
        }
        catch (Exception ex) { Log.Debug($"sql_expression_dependencies: {ex.Message}"); }

        db.Modules = db.Objects.Values.Where(o => o.IsModule && (o.Definition != null || o.DefinitionMissing || o.TypeCode is "PC" or "FS" or "FT")).ToList();
        Log.Info($"  {s.Name}.{dbName}: {db.Objects.Count} nesne, {db.Modules.Count} modül, compat {compat}");
        return db;
    }
}

/// <summary>Bağlantısız mod: .sql dosyalarından modüller ve (CREATE TABLE'lardan) sentetik katalog.</summary>
static class FileCatalog
{
    public static ServerCatalog Build(LineageConfig cfg)
    {
        var s = new ServerCatalog { Name = cfg.FileModeServer, ConfigName = cfg.FileModeServer, Major = 16 };
        var db = new DbCatalog { Server = s.Name, Name = cfg.FileModeDatabase, Compat = 160, Collation = "Turkish_CI_AS" };
        s.Dbs[db.Name] = db;
        var files = new List<string>();
        foreach (var p in cfg.SqlFiles)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.sql", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
            else Log.Warn("Dosya/klasör yok: " + p);
        }
        int synthetic = 0;
        var goRx = new Regex(@"^\s*GO(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(f);
            int batchNo = 0; bool quoted = true;
            foreach (var btextRaw in goRx.Split(text))
            {
                var btext = btextRaw.Trim();
                if (btext.Length == 0) continue;
                batchNo++;
                var qm = Regex.Match(btext, @"SET\s+QUOTED_IDENTIFIER\s+(ON|OFF)\s*;?\s*$", RegexOptions.IgnoreCase);
                if (qm.Success && !Regex.IsMatch(btext, @"\bCREATE\b", RegexOptions.IgnoreCase)) { quoted = qm.Groups[1].Value.Equals("ON", StringComparison.OrdinalIgnoreCase); continue; }
                int before = db.Objects.Count;
                var pr = ParserLadder.Parse(btext, quoted, 16, 160);
                var heads = (pr.Fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).ToList();
                var head = heads != null && heads.Count == 1 ? heads[0] : null;
                switch (head)
                {
                    case ProcedureStatementBody p:
                        AddModule(db, "P", p.ProcedureReference.Name, btext, ObjType.Procedure, p.Parameters); break;
                    case ViewStatementBody v:
                        AddModule(db, "V", v.SchemaObjectName, btext, ObjType.View, null); break;
                    case FunctionStatementBody fn:
                        {
                            var (code, ot) = fn.ReturnType switch { ScalarFunctionReturnType => ("FN", ObjType.ScalarFunction), SelectFunctionReturnType => ("IF", ObjType.InlineTvf), _ => ("TF", ObjType.MsTvf) };
                            AddModule(db, code, fn.Name, btext, ot, fn.Parameters); break;
                        }
                    case TriggerStatementBody tr:
                        {
                            var o = AddModule(db, "TR", tr.Name, btext, ObjType.Trigger, null);
                            var parentName = tr.TriggerObject?.Name;
                            if (parentName != null)
                            {
                                var parent = db.Find(parentName.SchemaIdentifier?.Value ?? "dbo", parentName.BaseIdentifier.Value) ?? AddTable(db, parentName.SchemaIdentifier?.Value ?? "dbo", parentName.BaseIdentifier.Value);
                                o.ParentObjectId = parent.ObjectId;
                                o.TriggerEvents = string.Join(",", tr.TriggerActions.Select(a => a.TriggerActionType.ToString().ToUpperInvariant()));
                                o.IsInsteadOf = tr.TriggerType == TriggerType.InsteadOf;
                            }
                            break;
                        }
                    default:
                        {
                            if (heads == null)
                            {
                                // parse edilemeyen batch: başlıktan ad/tür tahmin et, analizde merdiven + delik açma devreye girer
                                var m = Regex.Match(btext, @"CREATE\s+(OR\s+ALTER\s+)?(PROC(EDURE)?|VIEW|FUNCTION|TRIGGER)\s+([\w\[\]\.]+)", RegexOptions.IgnoreCase);
                                if (m.Success)
                                {
                                    var parts = ModuleAnalyzer.SplitName(m.Groups[4].Value);
                                    string kind = m.Groups[2].Value.ToUpperInvariant();
                                    var (code, ot) = kind.StartsWith("PROC") ? ("P", ObjType.Procedure) : kind == "VIEW" ? ("V", ObjType.View) : kind == "FUNCTION" ? ("FN", ObjType.ScalarFunction) : ("TR", ObjType.Trigger);
                                    db.Add(new ObjInfo { ObjectId = ++_nextId, TypeCode = code, Ref = new ObjRef(db.Server, db.Name, parts.Length > 1 ? parts[^2] : "dbo", parts[^1], ot), Definition = btext });
                                }
                                else db.Add(new ObjInfo { ObjectId = ++_nextId, TypeCode = "FILE", Ref = new ObjRef(s.Name, db.Name, "file", Path.GetFileName(f) + "#" + batchNo, ObjType.Script), Definition = btext });
                                break;
                            }
                            // Tablolar sentetik kataloğa; kalan her şey "script" modülü
                            bool onlyDdl = true;
                            foreach (var st in heads)
                            {
                                if (st is CreateTableStatement ct && !ct.SchemaObjectName.BaseIdentifier.Value.StartsWith('#'))
                                {
                                    var t = AddTable(db, ct.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo", ct.SchemaObjectName.BaseIdentifier.Value);
                                    t.Columns.Clear();
                                    int i = 0;
                                    foreach (var cd in ct.Definition.ColumnDefinitions)
                                        t.Columns.Add(new ColInfo { Name = cd.ColumnIdentifier.Value, Ordinal = ++i, TypeName = cd.DataType is SqlDataTypeReference sd ? sd.SqlDataTypeOption.ToString() : "", IsIdentity = cd.IdentityOptions != null, IsComputed = cd.ComputedColumnExpression != null, IsRowVersion = cd.DataType is SqlDataTypeReference sd2 && sd2.SqlDataTypeOption is SqlDataTypeOption.Rowversion or SqlDataTypeOption.Timestamp });
                                    synthetic++;
                                }
                                else if (st is not (CreateIndexStatement or AlterTableStatement or UseStatement or PredicateSetStatement or SetTransactionIsolationLevelStatement)) onlyDdl = false;
                            }
                            if (!onlyDdl)
                                db.Add(new ObjInfo { ObjectId = ++_nextId, TypeCode = "FILE", Ref = new ObjRef(s.Name, db.Name, "file", Path.GetFileName(f) + "#" + batchNo, ObjType.Script), Definition = btext });
                            break;
                        }
                }
                if (!quoted) foreach (var o in db.Objects.Values.Skip(before)) o.QuotedIdentifier = false;
            }
        }
        db.Modules = db.Objects.Values.Where(o => o.IsModule).ToList();
        Log.Info($"[FILES] {files.Count} dosya, {db.Modules.Count} modül, {synthetic} sentetik tablo");
        return s;
    }

    static int _nextId = 1_000_000;
    static ObjInfo AddModule(DbCatalog db, string code, SchemaObjectName name, string text, ObjType ot, IList<ProcedureParameter>? ps)
    {
        var o = new ObjInfo { ObjectId = ++_nextId, TypeCode = code, Ref = new ObjRef(db.Server, db.Name, name.SchemaIdentifier?.Value ?? "dbo", name.BaseIdentifier.Value, ot), Definition = text };
        if (ps != null)
        {
            int i = 0;
            foreach (var p in ps) o.Params.Add(new ParamInfo { Name = p.VariableName.Value, Ordinal = ++i, IsOutput = p.Modifier == Microsoft.SqlServer.TransactSql.ScriptDom.ParameterModifier.Output, IsTableType = p.DataType is UserDataTypeReference ud && db.TableTypes.Values.Any(t => NameComparer.Eq(t.Ref.Name, ud.Name.BaseIdentifier.Value)), TypeName = p.DataType is SqlDataTypeReference sd ? sd.SqlDataTypeOption.ToString() : (p.DataType is UserDataTypeReference ud2 ? ud2.Name.BaseIdentifier.Value : "") });
        }
        db.Add(o);
        return o;
    }
    static ObjInfo AddTable(DbCatalog db, string schema, string name)
    {
        var existing = db.Find(schema, name);
        if (existing != null) return existing;
        var o = new ObjInfo { ObjectId = ++_nextId, TypeCode = "U", Ref = new ObjRef(db.Server, db.Name, schema, name, ObjType.Table) };
        db.Add(o);
        return o;
    }
}

#endregion

// ============================================================================
#region 4. Parser merdiveni
// ============================================================================

sealed class ParseResult
{
    public TSqlFragment? Fragment;
    public int ParserUsed;
    public IList<ParseError> Errors = new List<ParseError>();
    public List<string> Holes = new();
    public string Status = "Parsed";      // Parsed | ParsedWithHoles | Quarantined | NoDefinition
    public string Text = "";
    public string ErrorText => string.Join(" | ", Errors.Take(5).Select(e => $"L{e.Line}:{e.Column} {e.Message}"));
}

static class ParserLadder
{
    static readonly ConcurrentDictionary<(int, bool), ConcurrentBag<TSqlParser>> _pool = new();
    static readonly Regex StmtStart = new(@"^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|SET|DECLARE|IF|ELSE|WHILE|BEGIN|END|EXEC|EXECUTE|WITH|CREATE|ALTER|DROP|TRUNCATE|PRINT|RETURN|FETCH|OPEN|CLOSE|DEALLOCATE|GO|RAISERROR|THROW|COMMIT|ROLLBACK|GOTO|WAITFOR|BULK|USE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int FromServerMajor(int m) => m switch { <= 8 => 80, 9 => 90, 10 => 100, 11 => 110, 12 => 120, 13 => 130, 14 => 140, 15 => 150, 16 => 160, _ => 170 };
    public static int FromCompat(int c) => c <= 80 ? 80 : c >= 170 ? 170 : (c / 10) * 10;

    public static TSqlParser Create(int v, bool q) => v switch
    {
        80 => new TSql80Parser(q), 90 => new TSql90Parser(q), 100 => new TSql100Parser(q), 110 => new TSql110Parser(q),
        120 => new TSql120Parser(q), 130 => new TSql130Parser(q), 140 => new TSql140Parser(q), 150 => new TSql150Parser(q),
        160 => new TSql160Parser(q), _ => new TSql170Parser(q)
    };
    public static TSqlParser Rent(int v, bool q)
    {
        var bag = _pool.GetOrAdd((v, q), _ => new ConcurrentBag<TSqlParser>());
        return bag.TryTake(out var p) ? p : Create(v, q);
    }
    public static void Return(int v, bool q, TSqlParser p) => _pool.GetOrAdd((v, q), _ => new ConcurrentBag<TSqlParser>()).Add(p);

    public static TSqlFragment? ParseOnce(int v, bool q, string text, out IList<ParseError> errors)
    {
        var p = Rent(v, q);
        try { return p.Parse(new StringReader(text), out errors); }
        finally { Return(v, q, p); }
    }

    static readonly Regex LegacyRaiserror = new(@"\bRAISERROR\s+(\d+)\s+('(?:[^']|'')*'|@\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PhysLoc = new(@"%%(physloc|lockres)%%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Eski/bozuk sözdizimini parse edilebilir eşdeğerine çevirir (lineage anlamı değişmez).</summary>
    public static string Normalize(string text)
    {
        if (text.IndexOf("RAISERROR", StringComparison.OrdinalIgnoreCase) >= 0) text = LegacyRaiserror.Replace(text, "RAISERROR($2, 16, 1)");
        if (text.Contains("%%")) text = PhysLoc.Replace(text, "__$1__");
        if ((text.Contains("&gt;") || text.Contains("&lt;") || text.Contains("&amp;") || text.Contains("&quot;")) && !text.Contains('<') && !text.Contains('>'))
            text = System.Net.WebUtility.HtmlDecode(text);   // HTML-kaçışlı job adımı metni
        return text;
    }

    public static ParseResult Parse(string text, bool quoted, int serverMajor, int compat)
    {
        text = Normalize(text);
        var res = new ParseResult { Text = text };
        int pServer = FromServerMajor(serverMajor), pCompat = FromCompat(compat);
        var ladder = new List<int> { compat <= 80 ? 80 : pServer, 170, pCompat, 80 }.Distinct().ToList();
        TSqlFragment? best = null; IList<ParseError> bestErr = new List<ParseError>(); int bestV = ladder[0]; int bestCount = int.MaxValue;
        foreach (var v in ladder)
        {
            var f = ParseOnce(v, quoted, text, out var errs);
            if (errs.Count == 0) { res.Fragment = f; res.ParserUsed = v; return res; }
            if (errs.Count < bestCount) { bestCount = errs.Count; best = f; bestErr = errs; bestV = v; }
        }
        // Delik açma: hatalı ifade aralığını boşlukla değiştir (offset'ler sabit kalır), en fazla 5 kez.
        string cur = text;
        var holes = new List<string>();
        var errList = bestErr;
        for (int round = 0; round < 5 && errList.Count > 0; round++)
        {
            var e = errList[0];
            var (start, end) = HoleSpan(cur, e.Offset);
            if (end <= start) break;
            holes.Add($"L{e.Line}: {Snippet(cur.Substring(start, end - start))} ← {e.Message}");
            var sb = new StringBuilder(cur);
            for (int i = start; i < end; i++) if (sb[i] != '\n' && sb[i] != '\r') sb[i] = ' ';
            cur = sb.ToString();
            var f = ParseOnce(bestV, quoted, cur, out errList);
            if (errList.Count == 0)
            {
                res.Fragment = f; res.ParserUsed = bestV; res.Holes = holes; res.Status = "ParsedWithHoles"; res.Text = cur; res.Errors = bestErr;
                return res;
            }
        }
        res.Fragment = null; res.ParserUsed = bestV; res.Errors = bestErr; res.Holes = holes; res.Status = "Quarantined";
        return res;
    }

    static (int, int) HoleSpan(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length) offset = Math.Max(0, text.Length - 1);
        int start = text.LastIndexOf('\n', offset) + 1;
        int pos = text.IndexOf('\n', offset);
        int lines = 0;
        int end = text.Length;
        while (pos >= 0 && pos < text.Length && lines < 40)
        {
            int next = text.IndexOf('\n', pos + 1);
            string lineText = text.Substring(pos + 1, (next < 0 ? text.Length : next) - pos - 1);
            if (StmtStart.IsMatch(lineText)) { end = pos + 1; break; }
            pos = next; lines++;
        }
        if (pos < 0) end = text.Length;
        return (start, end);
    }
    public static string Snippet(string s, int max = 160)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

#endregion

// ============================================================================
#region 5a. Binder: kapsam, range var, ad çözümleme, sorgu/ifade bağlama
// ============================================================================

sealed class OutCol
{
    public string Name; public Deps Deps; public string Expr = "";
    public OutCol(string name, Deps deps) { Name = name; Deps = deps; }
}

sealed class RangeVar
{
    public string? Alias; public string BaseName = ""; public ObjRef Obj = null!;
    public List<OutCol>? Columns;          // null = şekil bilinmiyor
    public Deps? Fallback;                 // bilinmeyen şekilde kolon istendiğinde (TVF argümanları vb.)
    public Confidence Conf = Confidence.Exact;
    public bool Transparent;               // CTE / türetilmiş tablo: düğüm değil, bağımlılıklar geçer
    public string Note = "";
    public bool Matches(string key) => Alias != null ? NameComparer.Eq(Alias, key) : NameComparer.Eq(BaseName, key);
    public bool HasColumn(string c) => Columns != null && Columns.Any(x => NameComparer.Eq(x.Name, c));
    public Deps Column(string c)
    {
        if (Columns != null)
        {
            var oc = Columns.FirstOrDefault(x => NameComparer.Eq(x.Name, c));
            if (oc != null) return Transparent ? oc.Deps : Deps.Direct(new ColRef(Obj, oc.Name, Conf));
            if (Transparent) return Fallback ?? Deps.None;
            return Deps.Direct(new ColRef(Obj, c, Confidence.High));   // katalogda yok ama ad belli (bayat şekil)
        }
        if (Fallback != null) return Fallback;
        return Deps.Direct(new ColRef(Obj, c, Conf == Confidence.Exact ? Confidence.High : Conf));
    }
    public List<OutCol> Expand()
    {
        if (Columns != null) return Transparent ? Columns : Columns.Select(x => new OutCol(x.Name, Deps.Direct(new ColRef(Obj, x.Name, Conf)))).ToList();
        return new List<OutCol> { new OutCol("*", Fallback ?? Deps.Direct(new ColRef(Obj, "*", Confidence.Low))) };
    }
}

sealed class Scope
{
    public Scope? Parent;
    public List<RangeVar> Vars = new();
    public Dictionary<string, List<OutCol>>? Ctes;
    public Scope(Scope? parent) { Parent = parent; }
    public RangeVar? FindVar(string key)
    {
        for (var s = this; s != null; s = s.Parent)
            foreach (var v in s.Vars) if (v.Matches(key)) return v;
        return null;
    }
    public List<OutCol>? FindCte(string name)
    {
        for (var s = this; s != null; s = s.Parent)
            if (s.Ctes != null && s.Ctes.TryGetValue(name, out var c)) return c;
        return null;
    }
}

sealed class QueryResult
{
    public List<OutCol> Columns = new();
    public Deps Control = Deps.None;
}

sealed class TempDef
{
    public string Name = ""; public List<string>? Columns; public int Ordinal; public ObjRef Ref = null!; public bool Ambient;
    public Dictionary<string, List<string>> Literals = new(NameComparer.Instance);   // kolon → literal değerler (INSERT VALUES / FROM'suz SELECT)
}

sealed class TempRegistry
{
    readonly Dictionary<string, TempDef> _cur = new(NameComparer.Instance);
    int _seq;
    public TempDef Define(string name, List<string>? cols, ObjRef owner)
    {
        var d = new TempDef { Name = name, Columns = cols, Ordinal = ++_seq, Ref = MakeRef(name, owner) };
        _cur[name] = d; return d;
    }
    public TempDef GetOrAmbient(string name, ObjRef owner)
    {
        if (_cur.TryGetValue(name, out var d)) return d;
        d = new TempDef { Name = name, Columns = null, Ordinal = ++_seq, Ref = MakeRef(name, owner), Ambient = true };
        _cur[name] = d; return d;
    }
    public TempDef? Get(string name) => _cur.TryGetValue(name, out var d) ? d : null;
    public List<string> Names() => _cur.Keys.ToList();
    public void Drop(string name) => _cur.Remove(name);
    public void AddColumns(string name, IEnumerable<string> cols, ObjRef owner)
    {
        var d = GetOrAmbient(name, owner);
        d.Columns ??= new List<string>();
        d.Columns.AddRange(cols);
    }
    static ObjRef MakeRef(string name, ObjRef owner) =>
        name.StartsWith("##") ? new ObjRef(owner.Server, owner.Database, "##", name, ObjType.TempTable)
                              : new ObjRef(owner.Server, owner.Database, owner.Schema + "." + owner.Name, name, ObjType.TempTable);
}

sealed class ExecResult
{
    public ObjRef? Callee; public List<OutCol>? ResultSet; public bool Dynamic; public Dictionary<string, Deps>? InnerVars;
}

sealed class AnalysisResult
{
    public List<ColumnLineageRow> ColumnRows = new();
    public List<ObjectLineageRow> ObjectRows = new();
    public List<DynamicSqlRow> DynamicRows = new();
    public List<UnresolvedRow> UnresolvedRows = new();
    public List<List<string>> ResultSetNames = new();
    public List<StatementRow> StatementRows = new();
    public int Statements, ObjectRefs, ObjectRefsResolved, DynamicSites, DynamicResolved;
}

sealed partial class ModuleAnalyzer
{
    readonly Catalog cat; readonly LineageConfig cfg; readonly string runId;
    public readonly ObjInfo Mod; readonly DbCatalog db; readonly ServerCatalog srv;
    public readonly AnalysisResult Result = new();
    string curServer, curDb, curText = "";
    int stmtNo; string stmtType = ""; int line;
    Provenance prov = Provenance.Static; string provNote = "";
    readonly Dictionary<string, Deps> vars = new(NameComparer.Instance);
    readonly Dictionary<string, string> varTypes = new(NameComparer.Instance);
    readonly Dictionary<string, RangeVar> tableVars = new(NameComparer.Instance);
    readonly Dictionary<string, Dictionary<string, List<string>>> tableVarLiterals = new(NameComparer.Instance);
    /// <summary>Modül içinde literal satırlarla doldurulmuş temp/@tablo kolonunun değerleri.</summary>
    public List<string>? TempLiterals(ColRef c)
    {
        if (c.Obj.Type == ObjType.TempTable) return temps.Get(c.Obj.Name)?.Literals.GetValueOrDefault(c.Column);
        if (c.Obj.Type == ObjType.TableVariable) return tableVarLiterals.GetValueOrDefault(c.Obj.Name)?.GetValueOrDefault(c.Column);
        return null;
    }
    void RecordLiterals(RangeVar target, List<string>? targetCols, InsertSource src)
    {
        if (targetCols == null || target.Obj.Type is not (ObjType.TempTable or ObjType.TableVariable)) return;
        var rows = new List<IList<ScalarExpression>>();
        switch (src)
        {
            case ValuesInsertSource v: foreach (var rv in v.RowValues) rows.Add(rv.ColumnValues); break;
            case SelectInsertSource sel: CollectNoFromRows(sel.Select, rows); break;
        }
        if (rows.Count == 0) return;
        Dictionary<string, List<string>> store;
        if (target.Obj.Type == ObjType.TempTable) { var td = temps.Get(target.Obj.Name); if (td == null) return; store = td.Literals; }
        else store = tableVarLiterals.TryGetValue(target.Obj.Name, out var d) ? d : tableVarLiterals[target.Obj.Name] = new(NameComparer.Instance);
        for (int i = 0; i < targetCols.Count; i++)
            foreach (var row in rows)
                if (i < row.Count && row[i] is StringLiteral sl)
                {
                    var l = store.TryGetValue(targetCols[i], out var ll) ? ll : store[targetCols[i]] = new();
                    if (l.Count < 64 && !l.Contains(sl.Value)) l.Add(sl.Value);
                }
    }
    static void CollectNoFromRows(QueryExpression q, List<IList<ScalarExpression>> rows)
    {
        switch (q)
        {
            case QuerySpecification qs when qs.FromClause == null: rows.Add(qs.SelectElements.OfType<SelectScalarExpression>().Select(e => e.Expression).ToList()); break;
            case BinaryQueryExpression bq: CollectNoFromRows(bq.FirstQueryExpression, rows); CollectNoFromRows(bq.SecondQueryExpression, rows); break;
            case QueryParenthesisExpression qp: CollectNoFromRows(qp.QueryExpression, rows); break;
        }
    }
    readonly Dictionary<string, List<OutCol>> cursors = new(NameComparer.Instance);
    readonly TempRegistry temps = new();
    readonly List<ParamInfo> parms = new();
    Deps returnDeps = Deps.None;
    readonly HashSet<string> objKeys = new(), colKeys = new();
    readonly DateTime deadline;
    int dynDepth, outerLine;
    readonly StringFlow dyn;
    int RowLine => dynDepth > 0 ? outerLine : line;
    public bool SuppressEmit { get => suppressEmit; set => suppressEmit = value; }
    readonly Scope moduleScope = new(null);
    ObjInfo? triggerParent;
    static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase) { "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "STRING_AGG", "STDEV", "STDEVP", "VAR", "VARP", "CHECKSUM_AGG", "GROUPING", "GROUPING_ID", "APPROX_COUNT_DISTINCT", "APPROX_PERCENTILE_CONT", "APPROX_PERCENTILE_DISC" };
    static readonly HashSet<string> WindowFns = new(StringComparer.OrdinalIgnoreCase) { "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "PERCENT_RANK", "CUME_DIST", "PERCENTILE_CONT", "PERCENTILE_DISC" };

    public ModuleAnalyzer(Catalog cat, LineageConfig cfg, string runId, ServerCatalog srv, DbCatalog db, ObjInfo mod)
    {
        this.cat = cat; this.cfg = cfg; this.runId = runId; this.srv = srv; this.db = db; Mod = mod;
        curServer = db.Server; curDb = db.Name;
        deadline = DateTime.UtcNow.AddSeconds(cfg.ModuleTimeoutSeconds);
        dyn = new StringFlow(this, cfg);
        if (mod.ParentObjectId is int pid && db.ById.TryGetValue(pid, out var parent)) triggerParent = parent;
        foreach (var p in mod.Params) parms.Add(p);
    }

    public string ModuleKey => Mod.Ref.Key;

    // ------------------------------------------------------------ yardımcılar
    void CheckDeadline() { if (DateTime.UtcNow > deadline) throw new TimeoutException($"modül zaman aşımı ({cfg.ModuleTimeoutSeconds}s)"); }
    public string Src(TSqlFragment? f, int max = 400)
    {
        if (f == null || f.StartOffset < 0 || f.StartOffset + f.FragmentLength > curText.Length || f.FragmentLength <= 0) return "";
        return ParserLadder.Snippet(curText.Substring(f.StartOffset, f.FragmentLength), max);
    }
    static string Id(Identifier? i) => i?.Value ?? "";
    static string Last(MultiPartIdentifier? m) => m == null || m.Identifiers.Count == 0 ? "" : m.Identifiers[^1].Value;
    string TypeName(ObjType t) => t.ToString();

    Confidence Combine(Confidence a, FlowKind k) => k == FlowKind.Positional && a == Confidence.Exact ? Confidence.High : a;

    public void EmitObj(ObjRef o, string action, Confidence conf = Confidence.Exact, string note = "")
    {
        if (suppressEmit) return;
        string key = $"{stmtNo}|{action}|{o.Key}";
        if (!objKeys.Add(key)) return;
        Result.ObjectRows.Add(new ObjectLineageRow
        {
            RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name, ModuleType = Mod.ModuleTypeName,
            StatementNo = stmtNo, StatementType = stmtType, Line = RowLine, Action = action,
            ObjectServer = o.Server, ObjectDatabase = o.Database, ObjectSchema = o.Schema, ObjectName = o.Name, ObjectType = TypeName(o.Type),
            Provenance = prov.ToString(), Confidence = (conf == Confidence.Exact && prov == Provenance.DynamicPartial ? Confidence.Low : conf).ToString(), Note = Join(provNote, note)
        });
    }

    static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "; " + b;

    public void EmitCol(Deps deps, ObjRef target, string targetCol, string expr = "", string note = "")
    {
        if (suppressEmit) return;
        foreach (var kv in deps.Data)
        {
            var s = kv.Key;
            string key = $"{stmtNo}|{s.Obj.Key}|{s.Column}|{target.Key}|{targetCol}|{kv.Value}";
            if (!colKeys.Add(key)) continue;
            var conf = Combine(s.Conf, kv.Value);
            if (prov == Provenance.DynamicPartial) conf = Confidence.Low;
            Result.ColumnRows.Add(new ColumnLineageRow
            {
                RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name, ModuleType = Mod.ModuleTypeName,
                StatementNo = stmtNo, StatementType = stmtType, Line = RowLine,
                SourceServer = s.Obj.Server, SourceDatabase = s.Obj.Database, SourceSchema = s.Obj.Schema, SourceObject = s.Obj.Name, SourceObjectType = TypeName(s.Obj.Type), SourceColumn = s.Column,
                TargetServer = target.Server, TargetDatabase = target.Database, TargetSchema = target.Schema, TargetObject = target.Name, TargetObjectType = TypeName(target.Type), TargetColumn = targetCol,
                FlowKind = kv.Value.ToString(), Expression = kv.Value == FlowKind.Direct ? "" : expr, Provenance = prov.ToString(), Confidence = conf.ToString(), Note = Join(provNote, note)
            });
        }
        EmitIndirect(deps, target, expr);
    }

    public void EmitIndirect(Deps deps, ObjRef target, string expr = "")
    {
        if (suppressEmit) return;
        foreach (var s in deps.Indirect)
        {
            string key = $"{stmtNo}|{s.Obj.Key}|{s.Column}|{target.Key}|*|Indirect";
            if (!colKeys.Add(key)) continue;
            Result.ColumnRows.Add(new ColumnLineageRow
            {
                RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name, ModuleType = Mod.ModuleTypeName,
                StatementNo = stmtNo, StatementType = stmtType, Line = RowLine,
                SourceServer = s.Obj.Server, SourceDatabase = s.Obj.Database, SourceSchema = s.Obj.Schema, SourceObject = s.Obj.Name, SourceObjectType = TypeName(s.Obj.Type), SourceColumn = s.Column,
                TargetServer = target.Server, TargetDatabase = target.Database, TargetSchema = target.Schema, TargetObject = target.Name, TargetObjectType = TypeName(target.Type), TargetColumn = "*",
                FlowKind = "Indirect", Expression = expr, Provenance = prov.ToString(), Confidence = (prov == Provenance.DynamicPartial ? Confidence.Low : s.Conf).ToString(), Note = provNote
            });
        }
    }

    static string SeverityOf(string kind) => kind switch
    {
        "StatementError" or "DynamicAssignError" or "RemoteQueryNotParsed" => "error",
        "SchemaAssumed" or "ViewColumnsMissing" or "DynamicPattern" or "CursorNotFound" => "info",
        _ => "warning"
    };
    public void Unresolved(string kind, string name, string note = "")
    {
        if (suppressEmit) return;
        Result.UnresolvedRows.Add(new UnresolvedRow { RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name, StatementNo = stmtNo, Line = RowLine, Kind = kind, Severity = SeverityOf(kind), Name = name, Note = Join(provNote, note) });
    }

    // ------------------------------------------------------------ nesne adı çözümleme
    public ObjRef OwnerRef => Mod.Ref;

    /// <summary>Şema nesnesi adını (1-4 parça) nesneye çözer. Temp/tablo değişkeni/CTE burada değil, BindTableRef'te ele alınır.</summary>
    public (ObjRef Ref, ObjInfo? Info, Confidence Conf) ResolveObject(SchemaObjectName n, bool forWrite = false)
    {
        string? server = Id(n.ServerIdentifier).NullIfEmpty(), dbName = Id(n.DatabaseIdentifier).NullIfEmpty(), schema = Id(n.SchemaIdentifier).NullIfEmpty();
        string name = Id(n.BaseIdentifier);
        return ResolveName(server, dbName, schema, name, forWrite);
    }

    public (ObjRef Ref, ObjInfo? Info, Confidence Conf) ResolveName(string? server, string? dbName, string? schema, string name, bool forWrite = false, int synonymDepth = 0)
    {
        if (!suppressEmit) Result.ObjectRefs++;
        if (name.Contains("LH_", StringComparison.Ordinal))
        {
            var disp = dyn.Display(name);
            // 'dbo.Sales_' + @yil gibi parça delikleri: katalogda desenle genişlet
            string pattern = Regex.Replace(name, @"LH_[IV]\d+", "%");
            if (pattern != "%" && server == null)
            {
                var saveDbCtx = curDb; if (dbName != null) curDb = dbName;
                var matches = MatchPattern((schema != null ? schema + "." : "") + pattern, ObjType.Table);
                curDb = saveDbCtx;
                if (matches.Count == 1) { if (!suppressEmit) Result.ObjectRefsResolved++; return (matches[0].Ref, matches[0], Confidence.Medium); }
                if (matches.Count > 1)
                {
                    foreach (var m in matches.Skip(1)) EmitObj(m.Ref, forWrite ? "Writes" : "Reads", Confidence.Medium, $"desen {pattern} ({matches.Count} eşleşme)");
                    Unresolved("DynamicPattern", disp, $"desen {pattern}: {matches.Count} eşleşme, ilkine bağlandı");
                    return (matches[0].Ref, matches[0], Confidence.Medium);
                }
            }
            Unresolved("DynamicPlaceholder", disp, "dinamik SQL'de nesne adı deliği");
            return (new ObjRef(curServer, dbName ?? curDb, schema ?? "", disp, ObjType.Unresolved), null, Confidence.Low);
        }
        string srvName = curServer; bool scanned = true;
        if (server != null)
        {
            (srvName, scanned) = cat.ResolveLinked(curServer, server);
            if (!scanned)
            {
                Unresolved("LinkedServerNotScanned", $"{server}.{dbName}.{schema}.{name}", "linked server taramada yok");
                return (new ObjRef(srvName, dbName ?? "", schema ?? "", name, ObjType.External), null, Confidence.Medium);
            }
            if (dbName == null && srv.Linked.TryGetValue(server, out var ls) && !string.IsNullOrEmpty(ls.Catalog)) dbName = ls.Catalog;
        }
        if (dbName != null && (dbName.Contains("LH_", StringComparison.Ordinal) || dbName.Contains('⟨')))
        {
            var canon = cat.Canonical(dbName);
            if (NameComparer.Eq(canon, dbName)) { Unresolved("DynamicDatabaseName", dyn.Display(dbName) + "." + (schema ?? "?") + "." + name, "veritabanı adı dinamik; ArchiveDatabasePatterns ile kanonikleştirilebilir"); return (new ObjRef(srvName, dyn.Display(dbName), schema ?? "", name, ObjType.Unresolved), null, Confidence.Low); }
            dbName = canon;
        }
        if (dbName != null) dbName = cat.Canonical(dbName);
        var targetDb = dbName == null ? (NameComparer.Eq(srvName, curServer) ? cat.FindDb(curServer, curDb) : null) : cat.FindDb(srvName, dbName);
        if (targetDb == null)
        {
            if (dbName != null && (NameComparer.Eq(dbName, "master") || NameComparer.Eq(dbName, "msdb") || NameComparer.Eq(dbName, "tempdb")) )
                return (new ObjRef(srvName, dbName, schema ?? "dbo", name, ObjType.System), null, Confidence.Medium);
            Unresolved("DatabaseNotScanned", $"{srvName}.{dbName ?? curDb}.{schema}.{name}", "veritabanı taramada yok");
            return (new ObjRef(srvName, dbName ?? curDb, schema ?? "", name, ObjType.External), null, Confidence.Medium);
        }
        // trigger sözde tabloları
        if (schema == null && triggerParent != null && (NameComparer.Eq(name, "inserted") || NameComparer.Eq(name, "deleted")))
        { if (!suppressEmit) Result.ObjectRefsResolved++; return (triggerParent.Ref, triggerParent, Confidence.Exact); }
        if (schema != null && (NameComparer.Eq(schema, "sys") || NameComparer.Eq(schema, "INFORMATION_SCHEMA")))
        { if (!suppressEmit) Result.ObjectRefsResolved++; return (new ObjRef(targetDb.Server, targetDb.Name, schema, name, ObjType.System), null, Confidence.Exact); }
        if (schema == null && name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) && targetDb.Find(Mod.Ref.Schema, name) == null && targetDb.Find("dbo", name) == null)
        { if (!suppressEmit) Result.ObjectRefsResolved++; return (new ObjRef(targetDb.Server, "master", "sys", name, ObjType.System), null, Confidence.Exact); }

        ObjInfo? info = null; Confidence conf = Confidence.Exact; string note = "";
        if (schema != null) info = targetDb.Find(schema, name);
        else
        {
            string modSchema = Mod.TypeCode is "JOB" or "FILE" ? Mod.DefaultSchema : Mod.Ref.Schema;
            info = targetDb.Find(modSchema, name) ?? targetDb.Find("dbo", name);
            if (info == null)
            {
                var cands = targetDb.FindByName(name).ToList();
                if (cands.Count == 1) { info = cands[0]; conf = Confidence.Medium; note = "şema varsayıldı: " + info.Ref.Schema; }
                else if (cands.Count > 1) { info = cands[0]; conf = Confidence.Medium; note = "birden çok şemada var: " + string.Join(",", cands.Select(c => c.Ref.Schema)); }
            }
        }
        if (info == null)
        {
            Unresolved("ObjectNotFound", $"{targetDb.Server}.{targetDb.Name}.{schema ?? "?"}.{name}", forWrite ? "yazma hedefi katalogda yok (kod tarafından yaratılıyor olabilir)" : "");
            return (new ObjRef(targetDb.Server, targetDb.Name, schema ?? "", name, ObjType.Unresolved), null, Confidence.Low);
        }
        if (info.TypeCode == "SN" && info.SynonymBase != null && synonymDepth < 4)
        {
            var parts = SplitName(info.SynonymBase);
            var (r, i2, c2) = ResolveName(parts.Length > 3 ? parts[^4] : null, parts.Length > 2 ? parts[^3] : null, parts.Length > 1 ? parts[^2] : null, parts[^1], forWrite, synonymDepth + 1);
            EmitObj(info.Ref, "ResolvesTo", Confidence.Exact, "synonym → " + r.Fqn);
            return (r, i2, c2 == Confidence.Exact ? conf : c2);
        }
        if (!suppressEmit) Result.ObjectRefsResolved++;
        if (note != "") Unresolved("SchemaAssumed", info.Ref.Fqn, note);
        if (info.ObjectId == 0 && info.TypeCode == "U" && !targetDb.Objects.ContainsKey(info.Ref.Schema + "." + info.Ref.Name)) return (info.Ref, info, conf == Confidence.Exact ? Confidence.High : conf);   // overlay: kod yaratıyor
        return (info.Ref, info, conf);
    }

    static readonly ConcurrentDictionary<string, List<string>?> _derivedCols = new();
    [ThreadStatic] static HashSet<string>? _deriving;

    /// <summary>Katalogda kolon listesi olmayan view / TVF için çıktı kolon adlarını tanımdan türetir (satır üretmeden).</summary>
    List<string>? DerivedColumns(ObjInfo info)
    {
        if (info.Definition == null) return null;
        string key = info.Ref.Key;
        if (_derivedCols.TryGetValue(key, out var cached)) return cached;
        _deriving ??= new HashSet<string>();
        if (!_deriving.Add(key)) return null;   // döngüsel view
        try
        {
            var vsrv = cat.FindServer(info.Ref.Server) ?? srv; var vdb = cat.FindDb(info.Ref.Server, info.Ref.Database) ?? db;
            var pr = ParserLadder.Parse(info.Definition, info.QuotedIdentifier, vsrv.Major, vdb.Compat);
            List<string>? names = null;
            if (pr.Fragment is TSqlScript sc)
            {
                var an = new ModuleAnalyzer(cat, cfg, runId, vsrv, vdb, info) { SuppressEmit = true };
                an.curText = pr.Text;
                foreach (var st in sc.Batches.SelectMany(b => b.Statements))
                {
                    SelectStatement? sel = st switch { ViewStatementBody v => v.SelectStatement, FunctionStatementBody { ReturnType: SelectFunctionReturnType sf } => sf.SelectStatement, _ => null };
                    if (st is FunctionStatementBody { ReturnType: TableValuedFunctionReturnType tv }) { names = tv.DeclareTableVariableBody.Definition?.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToList(); break; }
                    if (sel == null) continue;
                    if (st is ViewStatementBody vb && vb.Columns.Count > 0) { names = vb.Columns.Select(c => c.Value).ToList(); break; }
                    if (st is FunctionStatementBody fb) an.RegisterParams(fb.Parameters);
                    var scope = new Scope(an.moduleScope) { Ctes = an.RegisterCtes(sel.WithCtesAndXmlNamespaces, an.moduleScope) };
                    names = an.BindQuery(sel.QueryExpression, scope).Columns.Select(c => c.Name).ToList();
                    break;
                }
            }
            _derivedCols[key] = names;
            return names;
        }
        catch (Exception ex) { Log.Debug($"DerivedColumns {info.Ref}: {ex.Message}"); _derivedCols[key] = null; return null; }
        finally { _deriving.Remove(key); }
    }

    /// <summary>"[db].[schema].[name]" → parçalar (köşeli parantez farkında).</summary>
    public static string[] SplitName(string s)
    {
        var parts = new List<string>(); var sb = new StringBuilder(); bool inBr = false, inQ = false;
        foreach (var ch in s)
        {
            if (inBr) { if (ch == ']') inBr = false; else sb.Append(ch); }
            else if (inQ) { if (ch == '"') inQ = false; else sb.Append(ch); }
            else if (ch == '[') inBr = true;
            else if (ch == '"') inQ = true;
            else if (ch == '.') { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    // ------------------------------------------------------------ tablo referansları
    /// <summary>FROM içindeki bir tablo referansını kapsama ekler; join koşullarından gelen kontrol bağımlılıklarını döner.</summary>
    Deps BindTableRef(TableReference tr, Scope scope)
    {
        CheckDeadline();
        switch (tr)
        {
            case NamedTableReference nt:
                scope.Vars.Add(MakeRangeVar(nt.SchemaObject, Id(nt.Alias).NullIfEmpty(), scope, false));
                return Deps.None;
            case QueryDerivedTable qd:
                {
                    var qr = BindQuery(qd.QueryExpression, scope);
                    var cols = Rename(qr.Columns, qd.Columns);
                    scope.Vars.Add(new RangeVar { Alias = Id(qd.Alias).NullIfEmpty() ?? "derived", BaseName = "derived", Obj = new ObjRef(curServer, curDb, "", "derived", ObjType.Unresolved), Columns = cols, Transparent = true });
                    return qr.Control;
                }
            case QualifiedJoin qj:
                {
                    var c1 = BindTableRef(qj.FirstTableReference, scope);
                    var c2 = BindTableRef(qj.SecondTableReference, scope);
                    var on = qj.SearchCondition == null ? Deps.None : BindBool(qj.SearchCondition, scope).AsIndirect();
                    return Deps.Union(c1, c2, on);
                }
            case UnqualifiedJoin uj:
                return Deps.Union(BindTableRef(uj.FirstTableReference, scope), BindTableRef(uj.SecondTableReference, scope));
            case JoinParenthesisTableReference jp:
                return BindTableRef(jp.Join, scope);
            case VariableTableReference vt:
                {
                    var rv = TableVar(vt.Variable.Name);
                    scope.Vars.Add(new RangeVar { Alias = Id(vt.Alias).NullIfEmpty(), BaseName = vt.Variable.Name, Obj = rv.Obj, Columns = rv.Columns, Conf = rv.Conf });
                    EmitObj(rv.Obj, "Reads", rv.Conf);
                    return Deps.None;
                }
            case SchemaObjectFunctionTableReference sf:
                {
                    var args = Deps.Union(sf.Parameters.Select(p => BindScalar(p, scope)));
                    string fname = Id(sf.SchemaObject.BaseIdentifier);
                    if (sf.SchemaObject.SchemaIdentifier == null && (NameComparer.Eq(fname, "STRING_SPLIT") || NameComparer.Eq(fname, "OPENJSON") || NameComparer.Eq(fname, "GENERATE_SERIES")))
                    {
                        scope.Vars.Add(new RangeVar { Alias = Id(sf.Alias).NullIfEmpty() ?? fname, BaseName = fname, Obj = new ObjRef(curServer, curDb, "", fname, ObjType.System), Columns = null, Fallback = args.AsExpression(), Transparent = true });
                        return Deps.None;
                    }
                    var (r, info, conf) = ResolveObject(sf.SchemaObject);
                    EmitObj(r, "Reads", conf, "TVF");
                    var cols = info?.Columns.Count > 0 ? info.Columns.Select(c => new OutCol(c.Name, Deps.None)).ToList() : null;
                    if (cols == null && info != null && info.TypeCode is "IF" or "TF") { var dcols = DerivedColumns(info); if (dcols != null) cols = dcols.Select(c => new OutCol(c, Deps.None)).ToList(); }
                    {
                        int ai = 0;
                        foreach (var pexpr in sf.Parameters) { ai++; var pd = BindScalar(pexpr, scope); if (pd.HasData) EmitCol(pd, r, "IN:" + (info != null && ai - 1 < info.Params.Count ? info.Params[ai - 1].Name : "#" + ai), Src(pexpr, 200), "TVF argümanı"); }
                    }
                    var rvf = new RangeVar { Alias = Id(sf.Alias).NullIfEmpty() ?? fname, BaseName = fname, Obj = r, Columns = cols, Conf = conf, Fallback = cols == null ? Deps.Direct(new ColRef(r, "*", conf)).Union(args.AsExpression()) : null };
                    if (sf.Columns.Count > 0 && cols != null) rvf.Columns = Rename(cols, sf.Columns);
                    scope.Vars.Add(rvf);
                    return args.AsIndirect();
                }
            case PivotedTableReference pv:
                {
                    var inner = new Scope(scope);
                    var ctrl = BindTableRef(pv.TableReference, inner);
                    var pivotCol = ResolveColumn(pv.PivotColumn.MultiPartIdentifier, inner);
                    var valDeps = Deps.Union(pv.ValueColumns.Select(v => ResolveColumn(v.MultiPartIdentifier, inner)));
                    var cols = new List<OutCol>();
                    var excluded = new HashSet<string>(NameComparer.Instance) { Last(pv.PivotColumn.MultiPartIdentifier) };
                    foreach (var v in pv.ValueColumns) excluded.Add(Last(v.MultiPartIdentifier));
                    foreach (var rv in inner.Vars) foreach (var oc in rv.Expand()) if (!excluded.Contains(oc.Name)) cols.Add(new OutCol(oc.Name, oc.Deps));
                    foreach (var ic in pv.InColumns) cols.Add(new OutCol(ic.Value, valDeps.AsAggregate().Union(pivotCol.AsIndirect())));
                    scope.Vars.Add(new RangeVar { Alias = Id(pv.Alias).NullIfEmpty() ?? "pvt", BaseName = "pvt", Obj = new ObjRef(curServer, curDb, "", "pivot", ObjType.Unresolved), Columns = cols, Transparent = true });
                    return ctrl;
                }
            case UnpivotedTableReference up:
                {
                    var inner = new Scope(scope);
                    var ctrl = BindTableRef(up.TableReference, inner);
                    var inDeps = Deps.Union(up.InColumns.Select(c => ResolveColumn(c.MultiPartIdentifier, inner)));
                    var excluded = new HashSet<string>(up.InColumns.Select(c => Last(c.MultiPartIdentifier)), NameComparer.Instance);
                    var cols = new List<OutCol>();
                    foreach (var rv in inner.Vars) foreach (var oc in rv.Expand()) if (!excluded.Contains(oc.Name)) cols.Add(new OutCol(oc.Name, oc.Deps));
                    cols.Add(new OutCol(up.ValueColumn.Value, inDeps));
                    cols.Add(new OutCol(up.PivotColumn.Value, inDeps.AsExpression()));
                    scope.Vars.Add(new RangeVar { Alias = Id(up.Alias).NullIfEmpty() ?? "unpvt", BaseName = "unpvt", Obj = new ObjRef(curServer, curDb, "", "unpivot", ObjType.Unresolved), Columns = cols, Transparent = true });
                    return ctrl;
                }
            case InlineDerivedTable idt:
                {
                    var cols = new List<OutCol>();
                    int n = idt.RowValues.Count > 0 ? idt.RowValues[0].ColumnValues.Count : 0;
                    for (int i = 0; i < n; i++)
                    {
                        var d = Deps.Union(idt.RowValues.Select(rw => i < rw.ColumnValues.Count ? BindScalar(rw.ColumnValues[i], scope) : Deps.None));
                        cols.Add(new OutCol(i < idt.Columns.Count ? idt.Columns[i].Value : "Col" + (i + 1), d));
                    }
                    scope.Vars.Add(new RangeVar { Alias = Id(idt.Alias).NullIfEmpty() ?? "values", BaseName = "values", Obj = new ObjRef(curServer, curDb, "", "values", ObjType.Unresolved), Columns = cols, Transparent = true });
                    return Deps.None;
                }
            case OpenQueryTableReference oq:
                {
                    var (srvName, scanned) = cat.ResolveLinked(curServer, oq.LinkedServer.Value);
                    return BindRemoteQuery(oq.Query?.Value, srvName, scanned ? null : oq.LinkedServer.Value, Id(oq.Alias).NullIfEmpty() ?? "openquery", scope, "OPENQUERY " + oq.LinkedServer.Value);
                }
            case OpenRowsetTableReference orw:
                {
                    string ds = orw.DataSource?.Value ?? orw.ProviderString?.Value ?? "OPENROWSET";
                    if (orw.Object != null)
                    {
                        var ext = new ObjRef(ds, Id(orw.Object.DatabaseIdentifier), Id(orw.Object.SchemaIdentifier), Id(orw.Object.BaseIdentifier), ObjType.External);
                        EmitObj(ext, "Reads", Confidence.Medium, "OPENROWSET nesne");
                        scope.Vars.Add(new RangeVar { Alias = Id(orw.Alias).NullIfEmpty() ?? "openrowset", BaseName = "openrowset", Obj = ext, Columns = null, Conf = Confidence.Medium });
                        return Deps.None;
                    }
                    return BindRemoteQuery(orw.Query?.Value, ds, ds, Id(orw.Alias).NullIfEmpty() ?? "openrowset", scope, "OPENROWSET " + ds);
                }
            case BulkOpenRowset bk:
                {
                    var file = new ObjRef("FILE", "", "", bk.DataFiles.FirstOrDefault()?.Value ?? "?", ObjType.File);
                    EmitObj(file, "Reads", Confidence.High, "OPENROWSET(BULK)");
                    scope.Vars.Add(new RangeVar { Alias = Id(bk.Alias).NullIfEmpty() ?? "bulk", BaseName = "bulk", Obj = file, Columns = null, Conf = Confidence.High });
                    return Deps.None;
                }
            case AdHocTableReference ah:
                {
                    var son2 = ah.Object?.SchemaObjectName;
                    var ext = new ObjRef(ah.DataSource?.InitString?.Value ?? "OPENDATASOURCE", Id(son2?.DatabaseIdentifier), Id(son2?.SchemaIdentifier), Id(son2?.BaseIdentifier), ObjType.External);
                    EmitObj(ext, "Reads", Confidence.Medium, "OPENDATASOURCE");
                    scope.Vars.Add(new RangeVar { Alias = Id(ah.Alias).NullIfEmpty() ?? "opends", BaseName = "opends", Obj = ext, Columns = null, Conf = Confidence.Medium });
                    return Deps.None;
                }
            case OpenJsonTableReference oj:
                {
                    var d = BindScalar(oj.Variable, scope).AsExpression();
                    var cols = oj.SchemaDeclarationItems.Count > 0 ? oj.SchemaDeclarationItems.Select(s => new OutCol(Id(s.ColumnDefinition?.ColumnIdentifier), d)).ToList() : null;
                    scope.Vars.Add(new RangeVar { Alias = Id(oj.Alias).NullIfEmpty() ?? "openjson", BaseName = "openjson", Obj = new ObjRef(curServer, curDb, "", "OPENJSON", ObjType.System), Columns = cols, Fallback = d, Transparent = true });
                    return Deps.None;
                }
            case GlobalFunctionTableReference gf:
                {
                    var d = Deps.Union(gf.Parameters.Select(p => BindScalar(p, scope))).AsExpression();
                    scope.Vars.Add(new RangeVar { Alias = Id(gf.Alias).NullIfEmpty() ?? gf.Name.Value, BaseName = gf.Name.Value, Obj = new ObjRef(curServer, curDb, "", gf.Name.Value, ObjType.System), Columns = null, Fallback = d, Transparent = true });
                    return Deps.None;
                }
            case BuiltInFunctionTableReference bf:
                {
                    var d = Deps.Union(bf.Parameters.Select(p => BindScalar(p, scope))).AsExpression();
                    scope.Vars.Add(new RangeVar { Alias = Id(bf.Alias).NullIfEmpty() ?? bf.Name.Value, BaseName = bf.Name.Value, Obj = new ObjRef(curServer, curDb, "", bf.Name.Value, ObjType.System), Columns = null, Fallback = d, Transparent = true });
                    return Deps.None;
                }
            case VariableMethodCallTableReference vm:
                {
                    var d = BindScalar(vm.Variable, scope).AsExpression();
                    scope.Vars.Add(new RangeVar { Alias = Id(vm.Alias).NullIfEmpty() ?? "method", BaseName = "method", Obj = new ObjRef(curServer, curDb, "", vm.Variable.Name, ObjType.System), Columns = null, Fallback = d, Transparent = true });
                    return Deps.None;
                }
            case DataModificationTableReference dm:
                {
                    // FROM (INSERT/UPDATE/DELETE ... OUTPUT ...) AS x — nadir; DML'i normal işleyip OUTPUT kolonlarını türetilmiş tablo yapar
                    var outCols = HandleDml(dm.DataModificationSpecification, nested: true);
                    scope.Vars.Add(new RangeVar { Alias = Id(dm.Alias).NullIfEmpty() ?? "dml", BaseName = "dml", Obj = new ObjRef(curServer, curDb, "", "dml", ObjType.Unresolved), Columns = outCols, Transparent = true });
                    return Deps.None;
                }
            default:
                {
                    var alias = (tr as TableReferenceWithAlias)?.Alias?.Value ?? tr.GetType().Name;
                    scope.Vars.Add(new RangeVar { Alias = alias, BaseName = alias, Obj = new ObjRef(curServer, curDb, "", tr.GetType().Name, ObjType.Unresolved), Columns = null, Conf = Confidence.Low });
                    Unresolved("UnsupportedTableReference", tr.GetType().Name, Src(tr, 120));
                    return Deps.None;
                }
        }
    }

    Deps BindRemoteQuery(string? sql, string serverName, string? externalServer, string alias, Scope scope, string note)
    {
        var rvObj = new ObjRef(serverName, "", "", alias, ObjType.External);
        if (string.IsNullOrWhiteSpace(sql))
        {
            scope.Vars.Add(new RangeVar { Alias = alias, BaseName = alias, Obj = rvObj, Columns = null, Conf = Confidence.Low });
            return Deps.None;
        }
        // uzak sorguyu aynı merdivenle parse et; uzak sunucu taranmışsa katalogda çözülür
        string saveS = curServer, saveDb = curDb, saveText = curText; var saveNote = provNote;
        try
        {
            if (externalServer == null)
            {
                curServer = serverName;
                var lsInfo = srv.Linked.Values.FirstOrDefault(l => NameComparer.Eq(cat.ResolveLinked(saveS, l.Name).serverName, serverName));
                if (lsInfo != null && !string.IsNullOrEmpty(lsInfo.Catalog)) curDb = lsInfo.Catalog;
                else curDb = cat.FindServer(serverName)?.Dbs.Keys.FirstOrDefault() ?? curDb;
            }
            else curServer = externalServer;
            provNote = Join(provNote, note);
            var pr = ParserLadder.Parse(sql, true, srv.Major, db.Compat);
            curText = sql;
            if (pr.Fragment is TSqlScript sc)
            {
                var sel = sc.Batches.SelectMany(b => b.Statements).OfType<SelectStatement>().FirstOrDefault();
                if (sel != null)
                {
                    var qscope = new Scope(scope) { Ctes = RegisterCtes(sel.WithCtesAndXmlNamespaces, scope) };
                    var qr = BindQuery(sel.QueryExpression, qscope);
                    scope.Vars.Add(new RangeVar { Alias = alias, BaseName = alias, Obj = rvObj, Columns = qr.Columns, Transparent = true });
                    return qr.Control;
                }
            }
            Unresolved("RemoteQueryNotParsed", ParserLadder.Snippet(sql, 120), pr.ErrorText);
            scope.Vars.Add(new RangeVar { Alias = alias, BaseName = alias, Obj = rvObj, Columns = null, Conf = Confidence.Low });
            return Deps.None;
        }
        finally { curServer = saveS; curDb = saveDb; curText = saveText; provNote = saveNote; }
    }

    static List<OutCol> Rename(List<OutCol> cols, IList<Identifier> names)
    {
        if (names == null || names.Count == 0) return cols;
        var r = new List<OutCol>();
        for (int i = 0; i < cols.Count; i++) r.Add(new OutCol(i < names.Count ? names[i].Value : cols[i].Name, cols[i].Deps));
        return r;
    }

    RangeVar TableVar(string name)
    {
        if (tableVars.TryGetValue(name, out var rv)) return rv;
        var p = parms.FirstOrDefault(x => NameComparer.Eq(x.Name, name));
        if (p != null && p.IsTableType)
        {
            var tt = db.TableTypes.Values.FirstOrDefault(t => NameComparer.Eq(t.Ref.Name, p.TypeName));
            var r = new ObjRef(Mod.Ref.Server, Mod.Ref.Database, Mod.Ref.Schema + "." + Mod.Ref.Name, name, ObjType.Tvp);
            rv = new RangeVar { BaseName = name, Obj = r, Columns = tt?.Columns.Select(c => new OutCol(c.Name, Deps.None)).ToList(), Conf = Confidence.Exact };
        }
        else
        {
            var r = new ObjRef(Mod.Ref.Server, Mod.Ref.Database, Mod.Ref.Schema + "." + Mod.Ref.Name, name, ObjType.TableVariable);
            rv = new RangeVar { BaseName = name, Obj = r, Columns = null, Conf = Confidence.High };
            Unresolved("TableVariableUndeclared", name);
        }
        tableVars[name] = rv;
        return rv;
    }

    /// <summary>Adlandırılmış tablo referansı → range var (CTE, temp, katalog nesnesi).</summary>
    RangeVar MakeRangeVar(SchemaObjectName son, string? alias, Scope scope, bool forWrite)
    {
        string name = Id(son.BaseIdentifier);
        if (son.SchemaIdentifier == null && son.DatabaseIdentifier == null && son.ServerIdentifier == null)
        {
            var cte = scope.FindCte(name);
            if (cte != null) return new RangeVar { Alias = alias, BaseName = name, Obj = new ObjRef(curServer, curDb, "", name, ObjType.Unresolved), Columns = cte, Transparent = true };
        }
        if (name.StartsWith('#'))
        {
            var td = temps.GetOrAmbient(name, OwnerRef);
            if (td.Ambient && td.Columns == null) { Unresolved("AmbientTempTable", name, "bu modülde yaratılmamış temp tablo (çağıran yaratmış olabilir)"); td.Columns = null; }
            if (!forWrite) EmitObj(td.Ref, "Reads", td.Ambient ? Confidence.Medium : Confidence.Exact, td.Ambient ? "ambient temp" : "");
            return new RangeVar { Alias = alias, BaseName = name, Obj = td.Ref, Columns = td.Columns?.Select(c => new OutCol(c, Deps.None)).ToList(), Conf = td.Ambient ? Confidence.Medium : Confidence.Exact };
        }
        var (r, info, conf) = ResolveObject(son, forWrite);
        if (!forWrite) EmitObj(r, "Reads", conf);
        List<OutCol>? cols = info != null && info.Columns.Count > 0 ? info.Columns.Select(c => new OutCol(c.Name, Deps.None)).ToList() : null;
        if (info != null && info.TypeCode is "V" or "IF" && cols == null)
        {
            var derived = DerivedColumns(info);
            if (derived != null) cols = derived.Select(c => new OutCol(c, Deps.None)).ToList();
            else Unresolved("ViewColumnsMissing", r.Fqn, "view/TVF kolon listesi katalogda boş ve tanımdan türetilemedi");
        }
        return new RangeVar { Alias = alias, BaseName = name, Obj = r, Columns = cols, Conf = conf };
    }

    Dictionary<string, List<OutCol>>? RegisterCtes(WithCtesAndXmlNamespaces? with, Scope scope)
    {
        if (with == null || with.CommonTableExpressions.Count == 0) return null;
        var dict = new Dictionary<string, List<OutCol>>(NameComparer.Instance);
        var cteScope = new Scope(scope) { Ctes = dict };
        foreach (var cte in with.CommonTableExpressions)
        {
            // özyinelemeli CTE: kendi adını (boş kolon listesiyle) önce kaydet ki iç referans çözülsün
            var names = cte.Columns.Select(c => c.Value).ToList();
            if (names.Count > 0) dict[cte.ExpressionName.Value] = names.Select(n => new OutCol(n, Deps.None)).ToList();
            var qr = BindQuery(cte.QueryExpression, cteScope);
            var cols = Rename(qr.Columns, cte.Columns);
            foreach (var c in cols) c.Deps = c.Deps.Union(qr.Control.AsIndirect());
            dict[cte.ExpressionName.Value] = cols;
        }
        return dict;
    }

    // ------------------------------------------------------------ kolon çözümleme
    public Deps ResolveColumn(MultiPartIdentifier? id, Scope scope)
    {
        if (id == null || id.Identifiers.Count == 0) return Deps.None;
        var parts = id.Identifiers;
        string col = parts[^1].Value;
        if (parts.Count >= 2)
        {
            string q = parts[^2].Value;
            var rv = scope.FindVar(q);
            if (rv == null && parts.Count >= 3) rv = scope.FindVar(parts[^2].Value);   // schema.table.col → table
            if (rv != null) return rv.Column(col);
            // nitelenmiş ama kapsamda yok (nadir: UPDATE hedefi FROM'da değil vb.)
            Unresolved("ColumnQualifierNotFound", q + "." + col);
            return Deps.Direct(new ColRef(new ObjRef(curServer, curDb, "", q, ObjType.Unresolved), col, Confidence.Low));
        }
        for (var s = scope; s != null; s = s.Parent)
        {
            var known = s.Vars.Where(v => v.HasColumn(col)).ToList();
            if (known.Count == 1) return known[0].Column(col);
            if (known.Count > 1)
            {
                Unresolved("AmbiguousColumn", col, string.Join(",", known.Select(k => k.Alias ?? k.BaseName)));
                return Deps.Union(known.Select(k => k.Column(col)));
            }
            var unknown = s.Vars.Where(v => v.Columns == null).ToList();
            if (unknown.Count == 1) return unknown[0].Column(col);
            if (unknown.Count > 1) return Deps.Union(unknown.Select(u => u.Column(col)));
        }
        // hiçbir range var yok (örn. SELECT @x = 1 gibi FROM'suz ya da bağlama hatası)
        if (scope.Vars.Count == 0 && scope.Parent == null) return Deps.None;
        Unresolved("ColumnNotFound", col);
        return Deps.None;
    }

    // ------------------------------------------------------------ sorgu bağlama
    public QueryResult BindQuery(QueryExpression q, Scope parent)
    {
        CheckDeadline();
        switch (q)
        {
            case QueryParenthesisExpression qp:
                {
                    var r = BindQuery(qp.QueryExpression, parent);
                    ApplyOrderOffset(q, r, parent);
                    return r;
                }
            case BinaryQueryExpression bq:
                {
                    var l = BindQuery(bq.FirstQueryExpression, parent);
                    var r = BindQuery(bq.SecondQueryExpression, parent);
                    var res = new QueryResult { Control = l.Control.Union(r.Control) };
                    for (int i = 0; i < l.Columns.Count; i++)
                    {
                        var d = l.Columns[i].Deps;
                        if (i < r.Columns.Count)
                            d = bq.BinaryQueryExpressionType == BinaryQueryExpressionType.Union ? d.Union(r.Columns[i].Deps) : d.Union(r.Columns[i].Deps.AsIndirect());
                        res.Columns.Add(new OutCol(l.Columns[i].Name, d));
                    }
                    ApplyOrderOffset(q, res, parent);
                    return res;
                }
            case QuerySpecification qs:
                return BindSpec(qs, parent);
            default:
                Unresolved("UnsupportedQuery", q.GetType().Name);
                return new QueryResult();
        }
    }

    void ApplyOrderOffset(QueryExpression q, QueryResult r, Scope scope)
    {
        if (q.OffsetClause != null)
        {
            r.Control = r.Control.Union(Deps.Union(BindScalar(q.OffsetClause.OffsetExpression, scope), BindScalar(q.OffsetClause.FetchExpression, scope)).AsIndirect());
            if (q.OrderByClause != null) r.Control = r.Control.Union(OrderDeps(q.OrderByClause, scope, r).AsIndirect());
        }
    }

    Deps OrderDeps(OrderByClause ob, Scope scope, QueryResult? r)
    {
        var list = new List<Deps>();
        foreach (var e in ob.OrderByElements)
        {
            // ORDER BY alias / ordinal → çıktı kolonuna göre
            if (r != null && e.Expression is ColumnReferenceExpression c && c.MultiPartIdentifier.Identifiers.Count == 1)
            {
                var oc = r.Columns.FirstOrDefault(x => NameComparer.Eq(x.Name, c.MultiPartIdentifier.Identifiers[0].Value));
                if (oc != null && scope.Vars.All(v => !v.HasColumn(oc.Name))) { list.Add(oc.Deps); continue; }
            }
            if (r != null && e.Expression is IntegerLiteral il && int.TryParse(il.Value, out int ord) && ord >= 1 && ord <= r.Columns.Count) { list.Add(r.Columns[ord - 1].Deps); continue; }
            list.Add(BindScalar(e.Expression, scope));
        }
        return Deps.Union(list);
    }

    QueryResult BindSpec(QuerySpecification qs, Scope parent)
    {
        var scope = new Scope(parent);
        var control = new List<Deps>();
        if (qs.FromClause != null)
            foreach (var tr in qs.FromClause.TableReferences) control.Add(BindTableRef(tr, scope));
        if (qs.WhereClause != null) control.Add(BindBool(qs.WhereClause.SearchCondition, scope).AsIndirect());
        if (qs.GroupByClause != null) control.Add(Deps.Union(qs.GroupByClause.GroupingSpecifications.Select(g => GroupDeps(g, scope))).AsIndirect());
        if (qs.HavingClause != null) control.Add(BindBool(qs.HavingClause.SearchCondition, scope).AsIndirect());
        if (qs.TopRowFilter != null) control.Add(BindScalar(qs.TopRowFilter.Expression, scope).AsIndirect());

        var res = new QueryResult();
        int i = 0;
        foreach (var se in qs.SelectElements)
        {
            i++;
            switch (se)
            {
                case SelectScalarExpression sse:
                    {
                        string name = sse.ColumnName?.Value ?? (sse.Expression is ColumnReferenceExpression cr ? Last(cr.MultiPartIdentifier) : "Col" + i);
                        var d = BindScalar(sse.Expression, scope);
                        res.Columns.Add(new OutCol(name, d) { Expr = sse.Expression is ColumnReferenceExpression ? "" : Src(sse.Expression, 200) });
                        break;
                    }
                case SelectStarExpression star:
                    {
                        if (star.Qualifier != null)
                        {
                            var rv = scope.FindVar(Last(star.Qualifier));
                            if (rv != null) res.Columns.AddRange(rv.Expand().Select(c => new OutCol(c.Name, c.Deps)));
                            else { Unresolved("StarQualifierNotFound", Last(star.Qualifier)); }
                        }
                        else foreach (var rv in scope.Vars) res.Columns.AddRange(rv.Expand().Select(c => new OutCol(c.Name, c.Deps)));
                        break;
                    }
                case SelectSetVariable ssv:
                    {
                        var d = BindScalar(ssv.Expression, scope);
                        var ctrlNow = Deps.Union(control).AsIndirect();
                        var full = d.Union(ctrlNow);
                        if (ssv.AssignmentKind != AssignmentKind.Equals && vars.TryGetValue(ssv.Variable.Name, out var old)) full = full.Union(old);
                        vars[ssv.Variable.Name] = full;
                        dyn.AssignFromSelect(ssv.Variable.Name, ssv.Expression, d, ssv.AssignmentKind != AssignmentKind.Equals, scope.Vars.Count > 0);
                        break;
                    }
            }
        }
        if (qs.OrderByClause != null && (qs.TopRowFilter != null || qs.OffsetClause != null))
            control.Add(OrderDeps(qs.OrderByClause, scope, res).AsIndirect());
        if (qs.OffsetClause != null) control.Add(Deps.Union(BindScalar(qs.OffsetClause.OffsetExpression, scope), BindScalar(qs.OffsetClause.FetchExpression, scope)).AsIndirect());
        if (qs.ForClause is XmlForClause or JsonForClause)
        {
            // FOR XML/JSON: tek çıktı kolonu, tüm kolonlar Expression
            var all = Deps.Union(res.Columns.Select(c => c.Deps)).AsExpression();
            res.Columns = new List<OutCol> { new OutCol("XML_JSON", all) };
        }
        res.Control = Deps.Union(control);
        return res;
    }

    Deps GroupDeps(GroupingSpecification g, Scope scope) => g switch
    {
        ExpressionGroupingSpecification e => BindScalar(e.Expression, scope),
        CompositeGroupingSpecification c => Deps.Union(c.Items.Select(x => GroupDeps(x, scope))),
        RollupGroupingSpecification r => Deps.Union(r.Arguments.Select(x => GroupDeps(x, scope))),
        CubeGroupingSpecification cu => Deps.Union(cu.Arguments.Select(x => GroupDeps(x, scope))),
        GroupingSetsGroupingSpecification gs => Deps.Union(gs.Sets.Select(x => GroupDeps(x, scope))),
        _ => Deps.None
    };

    // ------------------------------------------------------------ ifade bağlama
    public Deps BindScalar(ScalarExpression? e, Scope scope)
    {
        if (e == null) return Deps.None;
        switch (e)
        {
            case ColumnReferenceExpression c:
                if (c.ColumnType != ColumnType.Regular && c.MultiPartIdentifier == null) return Deps.None;   // $IDENTITY, $ROWGUID, $action
                return ResolveColumn(c.MultiPartIdentifier, scope);
            case VariableReference v:
                return vars.TryGetValue(v.Name, out var vd) ? vd : Deps.None;
            case Literal:
            case GlobalVariableExpression:
                return Deps.None;
            case ParenthesisExpression p: return BindScalar(p.Expression, scope);
            case UnaryExpression u: return BindScalar(u.Expression, scope).AsExpression();
            case BinaryExpression b: return Deps.Union(BindScalar(b.FirstExpression, scope), BindScalar(b.SecondExpression, scope)).AsExpression();
            case CastCall cc: return BindScalar(cc.Parameter, scope).AsExpression();
            case ConvertCall cv: return BindScalar(cv.Parameter, scope).AsExpression();
            case TryCastCall tc: return BindScalar(tc.Parameter, scope).AsExpression();
            case TryConvertCall tv: return BindScalar(tv.Parameter, scope).AsExpression();
            case ParseCall pc: return BindScalar(pc.StringValue, scope).AsExpression();
            case TryParseCall tp: return BindScalar(tp.StringValue, scope).AsExpression();
            case CoalesceExpression co: return Deps.Union(co.Expressions.Select(x => BindScalar(x, scope))).AsExpression();
            case NullIfExpression ni: return Deps.Union(BindScalar(ni.FirstExpression, scope), BindScalar(ni.SecondExpression, scope)).AsExpression();
            case IIfCall ii: return Deps.Union(BindBool(ii.Predicate, scope).AsIndirect(), BindScalar(ii.ThenExpression, scope), BindScalar(ii.ElseExpression, scope)).AsExpression();
            case SearchedCaseExpression sc:
                return Deps.Union(sc.WhenClauses.Select(w => Deps.Union(BindBool(w.WhenExpression, scope).AsIndirect(), BindScalar(w.ThenExpression, scope)))).Union(BindScalar(sc.ElseExpression, scope)).AsExpression();
            case SimpleCaseExpression sm:
                return Deps.Union(BindScalar(sm.InputExpression, scope).AsIndirect(), Deps.Union(sm.WhenClauses.Select(w => Deps.Union(BindScalar(w.WhenExpression, scope).AsIndirect(), BindScalar(w.ThenExpression, scope)))), BindScalar(sm.ElseExpression, scope)).AsExpression();
            case ScalarSubquery sq:
                {
                    var r = BindQuery(sq.QueryExpression, scope);
                    var d = r.Columns.Count > 0 ? r.Columns[0].Deps : Deps.None;
                    return d.AsExpression().Union(r.Control.AsIndirect());
                }
            case LeftFunctionCall lf: return Deps.Union(lf.Parameters.Select(x => BindScalar(x, scope))).AsExpression();
            case RightFunctionCall rf: return Deps.Union(rf.Parameters.Select(x => BindScalar(x, scope))).AsExpression();
            case NextValueForExpression nv:
                {
                    var (r, _, conf) = ResolveObject(nv.SequenceName);
                    EmitObj(r.With(ObjType.Sequence), "Reads", conf, "NEXT VALUE FOR");
                    return Deps.Of(new ColRef(r.With(ObjType.Sequence), "VALUE", conf), FlowKind.Expression);
                }
            case FunctionCall f: return BindFunction(f, scope);
            case OdbcFunctionCall of: return Deps.Union(of.Parameters.Select(x => BindScalar(x, scope))).AsExpression();
            case ExtractFromExpression ef: return BindScalar(ef.Expression, scope).AsExpression();
            case AtTimeZoneCall at: return Deps.Union(BindScalar(at.DateValue, scope), BindScalar(at.TimeZone, scope)).AsExpression();
            case UserDefinedTypePropertyAccess up: return BindScalar(up.CallTarget is ExpressionCallTarget ect ? ect.Expression : null, scope).AsExpression();
            case IdentityFunctionCall: return Deps.None;
            default:
                return Deps.None;
        }
    }

    static readonly HashSet<string> XmlMethods = new(StringComparer.OrdinalIgnoreCase) { "value", "query", "exist", "nodes", "modify" };
    static readonly Regex XmlColRx = new(@"sql:column\(\s*""([^""]+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex XmlVarRx = new(@"sql:variable\(\s*""(@\w+)""\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>XQuery/XML DML metnindeki sql:column("x") ve sql:variable("@v") referansları.</summary>
    Deps XmlRefs(IList<ScalarExpression> ps, Scope scope)
    {
        var list = new List<Deps>();
        foreach (var sl in ps.OfType<StringLiteral>())
        {
            foreach (Match m in XmlColRx.Matches(sl.Value))
            {
                var parts = SplitName(m.Groups[1].Value);
                var mpi = new MultiPartIdentifier(); foreach (var pp in parts) mpi.Identifiers.Add(new Identifier { Value = pp });
                list.Add(ResolveColumn(mpi, scope).AsExpression());
            }
            foreach (Match m in XmlVarRx.Matches(sl.Value)) if (vars.TryGetValue(m.Groups[1].Value, out var vd)) list.Add(vd.AsExpression());
        }
        return Deps.Union(list);
    }
    static readonly HashSet<string> DatePartFns = new(StringComparer.OrdinalIgnoreCase) { "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC", "DATE_BUCKET", "DATEFROMPARTS" };

    Deps BindFunction(FunctionCall f, Scope scope)
    {
        string name = f.FunctionName.Value;
        IEnumerable<ScalarExpression> ps = f.Parameters;
        if (DatePartFns.Contains(name) && f.Parameters.Count > 0 && f.Parameters[0] is ColumnReferenceExpression dp && dp.MultiPartIdentifier?.Identifiers.Count == 1)
            ps = f.Parameters.Skip(1);   // DATEADD(year, ...) → 'year' kolon değil datepart
        var args = Deps.Union(ps.Select(p => BindScalar(p, scope)));
        if ((NameComparer.Eq(name, "COUNT") || NameComparer.Eq(name, "COUNT_BIG")) && !args.HasData && f.CallTarget == null)
        {
            // COUNT(*): satır sayısı kapsamdaki tablolara bağlıdır
            var tbls = new List<Deps>();
            for (var sc = scope; sc != null; sc = sc.Parent) { foreach (var rv in sc.Vars) tbls.Add(rv.Transparent ? Deps.Union(rv.Expand().Select(c => c.Deps)) : Deps.Direct(new ColRef(rv.Obj, "*", rv.Conf))); if (sc.Vars.Count > 0) break; }
            args = Deps.Union(tbls);
        }
        Deps over = Deps.None;
        if (f.OverClause != null)
        {
            var parts = new List<Deps>();
            if (f.OverClause.Partitions != null) parts.AddRange(f.OverClause.Partitions.Select(p => BindScalar(p, scope)));
            if (f.OverClause.OrderByClause != null) parts.Add(OrderDeps(f.OverClause.OrderByClause, scope, null));
            over = Deps.Union(parts).AsIndirect();
        }
        if (f.WithinGroupClause?.OrderByClause != null) over = over.Union(OrderDeps(f.WithinGroupClause.OrderByClause, scope, null).AsIndirect());
        if (f.CallTarget is MultiPartIdentifierCallTarget xmp && XmlMethods.Contains(name))
        {
            // xml kolon metodu: col.value('xpath','tip'), col.query(...), col.exist(...), col.nodes(...) → kolon + sql:column/sql:variable referansları
            var colDeps = ResolveColumn(xmp.MultiPartIdentifier, scope);
            return colDeps.AsExpression().Union(XmlRefs(f.Parameters, scope)).Union(over);
        }
        if (f.CallTarget is MultiPartIdentifierCallTarget mp)
        {
            // kullanıcı tanımlı skaler fonksiyon: schema.fn(...)
            var ids = mp.MultiPartIdentifier.Identifiers;
            string? server = ids.Count >= 4 ? ids[^4].Value : null, dbn = ids.Count >= 3 ? ids[^3].Value : null, schema = ids.Count >= 1 ? ids[^1].Value : null;
            var (r, info, conf) = ResolveName(server, dbn, schema, name);
            var fnRef = r.Type == ObjType.Unresolved || r.Type == ObjType.External ? r : r.With(info?.Ref.Type ?? ObjType.ScalarFunction);
            EmitObj(fnRef, "Calls", conf, "skaler UDF");
            int ai = 0;
            foreach (var pexpr in f.Parameters)
            {
                ai++;
                var pd = BindScalar(pexpr, scope);
                if (pd.HasData) EmitCol(pd, fnRef, "IN:" + (info != null && ai - 1 < info.Params.Count ? info.Params[ai - 1].Name : "#" + ai), Src(pexpr, 200), "UDF argümanı");
            }
            return Deps.Of(new ColRef(fnRef, "RETURN", conf), FlowKind.Expression).Union(args.AsIndirect()).Union(over);
        }
        if (Aggregates.Contains(name) || (f.OverClause != null && (WindowFns.Contains(name) || Aggregates.Contains(name))))
            return args.AsAggregate().Union(over);
        if (WindowFns.Contains(name)) return args.AsAggregate().Union(over);
        return args.AsExpression().Union(over);
    }

    public Deps BindBool(BooleanExpression? b, Scope scope)
    {
        if (b == null) return Deps.None;
        switch (b)
        {
            case BooleanBinaryExpression bb: return Deps.Union(BindBool(bb.FirstExpression, scope), BindBool(bb.SecondExpression, scope));
            case BooleanComparisonExpression bc: return Deps.Union(BindScalar(bc.FirstExpression, scope), BindScalar(bc.SecondExpression, scope));
            case BooleanNotExpression bn: return BindBool(bn.Expression, scope);
            case BooleanParenthesisExpression bp: return BindBool(bp.Expression, scope);
            case BooleanIsNullExpression bi: return BindScalar(bi.Expression, scope);
            case BooleanTernaryExpression bt: return Deps.Union(BindScalar(bt.FirstExpression, scope), BindScalar(bt.SecondExpression, scope), BindScalar(bt.ThirdExpression, scope));
            case InPredicate ip:
                {
                    var d = BindScalar(ip.Expression, scope);
                    if (ip.Values != null) d = d.Union(Deps.Union(ip.Values.Select(v => BindScalar(v, scope))));
                    if (ip.Subquery != null) { var r = BindQuery(ip.Subquery.QueryExpression, scope); d = d.Union(Deps.Union(r.Columns.Select(c => c.Deps))).Union(r.Control); }
                    return d;
                }
            case LikePredicate lp: return Deps.Union(BindScalar(lp.FirstExpression, scope), BindScalar(lp.SecondExpression, scope));
            case ExistsPredicate ep: { var r = BindQuery(ep.Subquery.QueryExpression, scope); return Deps.Union(r.Columns.Select(c => c.Deps)).Union(r.Control); }
            case SubqueryComparisonPredicate sp: { var r = BindQuery(sp.Subquery.QueryExpression, scope); return Deps.Union(BindScalar(sp.Expression, scope), Deps.Union(r.Columns.Select(c => c.Deps)), r.Control); }
            case FullTextPredicate ft: return Deps.Union(ft.Columns.Select(c => ResolveColumn(c.MultiPartIdentifier, scope))).Union(BindScalar(ft.Value, scope));
            case DistinctPredicate dp: return Deps.Union(BindScalar(dp.FirstExpression, scope), BindScalar(dp.SecondExpression, scope));
            case UpdateCall uc: return uc.Identifier != null ? ResolveColumn(new MultiPartIdentifier { Identifiers = { uc.Identifier } }, scope) : Deps.None;
            default: return Deps.None;
        }
    }
}

static class StrExt
{
    public static string? NullIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;
}

#endregion

// ============================================================================
#region 5b. İfade işleyicileri
// ============================================================================

sealed partial class ModuleAnalyzer
{
    bool suppressEmit;
    List<OutCol>? lastResultSet;

    public void Analyze(ParseResult pr)
    {
        curText = pr.Text;
        if (pr.Fragment is not TSqlScript script) return;
        foreach (var batch in script.Batches)
            foreach (var st in batch.Statements) AnalyzeTop(st);
        Finish();
    }

    void AnalyzeTop(TSqlStatement st)
    {
        switch (st)
        {
            case ProcedureStatementBody p:
                RegisterParams(p.Parameters);
                Block(p.StatementList);
                break;
            case ViewStatementBody v:
                stmtNo++; Result.Statements++; stmtType = "VIEW"; line = v.StartLine;
                SelectAsOutput(v.SelectStatement, Mod.Ref, v.Columns.Count > 0 ? v.Columns.Select(c => c.Value).ToList() : null);
                break;
            case FunctionStatementBody f:
                RegisterParams(f.Parameters);
                switch (f.ReturnType)
                {
                    case SelectFunctionReturnType s:
                        stmtNo++; Result.Statements++; stmtType = "FUNCTION"; line = f.StartLine;
                        SelectAsOutput(s.SelectStatement, Mod.Ref, null);
                        break;
                    case TableValuedFunctionReturnType t:
                        DeclareTableVar(t.DeclareTableVariableBody);
                        Block(f.StatementList);
                        stmtNo++; stmtType = "FUNCTION"; line = f.StartLine;
                        if (tableVars.TryGetValue(t.DeclareTableVariableBody.VariableName.Value, out var rv) && rv.Columns != null)
                            foreach (var c in rv.Columns) EmitCol(Deps.Direct(new ColRef(rv.Obj, c.Name)), Mod.Ref, c.Name);
                        break;
                    default:
                        Block(f.StatementList);
                        break;
                }
                break;
            case TriggerStatementBody tr:
                Block(tr.StatementList);
                break;
            default:
                Stmt(st);
                break;
        }
    }

    void Finish()
    {
        stmtNo++; stmtType = "RETURN"; 
        foreach (var p in parms.Where(x => x.IsOutput))
            if (vars.TryGetValue(p.Name, out var d) && !d.IsEmpty) EmitCol(d, Mod.Ref, "OUT:" + p.Name);
        if (!returnDeps.IsEmpty) EmitCol(returnDeps, Mod.Ref, "RETURN");
    }

    void RegisterParams(IList<ProcedureParameter> ps)
    {
        int i = 0;
        foreach (var p in ps)
        {
            i++;
            string name = p.VariableName.Value;
            var info = parms.FirstOrDefault(x => NameComparer.Eq(x.Name, name));
            if (info == null)
            {
                info = new ParamInfo { Name = name, Ordinal = i, TypeName = p.DataType is SqlDataTypeReference sd ? sd.SqlDataTypeOption.ToString() : (p.DataType is UserDataTypeReference ud ? Id(ud.Name.BaseIdentifier) : ""), IsOutput = p.Modifier == Microsoft.SqlServer.TransactSql.ScriptDom.ParameterModifier.Output };
                info.IsTableType = p.DataType is UserDataTypeReference ud2 && db.TableTypes.Values.Any(t => NameComparer.Eq(t.Ref.Name, Id(ud2.Name.BaseIdentifier)));
                parms.Add(info);
            }
            if (p.Value is Literal lit) info.DefaultLiteral = lit is NullLiteral ? null : lit.Value;
            varTypes[name] = p.DataType is SqlDataTypeReference sd2 ? sd2.SqlDataTypeOption + (sd2.Parameters.Count > 0 ? "(" + string.Join(",", sd2.Parameters.Select(x => x.Value)) + ")" : "") : info.TypeName;
            vars[name] = info.IsTableType ? Deps.None : Deps.Direct(new ColRef(Mod.Ref, "IN:" + name));   // parametre değeri: çağırandan gelen akış düğümü
            dyn.RegisterParam(name, info.DefaultLiteral, p.Value);
        }
    }

    void Block(StatementList? sl)
    {
        if (sl == null) return;
        foreach (var st in sl.Statements) Stmt(st);
    }

    static string StmtTypeName(TSqlStatement st)
    {
        var n = st.GetType().Name;
        if (n.EndsWith("Statement")) n = n[..^9];
        return n.ToUpperInvariant();
    }

    void Stmt(TSqlStatement st)
    {
        CheckDeadline();
        stmtNo++; Result.Statements++; line = st.StartLine; stmtType = StmtTypeName(st);
        if (cfg.IncludeStatementText && !suppressEmit && st is not (BeginEndBlockStatement or IfStatement or WhileStatement or TryCatchStatement))
            Result.StatementRows.Add(new StatementRow { RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name, StatementNo = stmtNo, Line = RowLine, StatementType = stmtType, Dynamic = dynDepth > 0, SqlText = Src(st, 4000) });
        try
        {
            switch (st)
            {
                case BeginEndBlockStatement be: Block(be.StatementList); break;
                case IfStatement ifs: HandleIf(ifs); break;
                case WhileStatement wh: HandleWhile(wh); break;
                case TryCatchStatement tc: Block(tc.TryStatements); Block(tc.CatchStatements); break;
                case SelectStatement sel: HandleSelect(sel); break;
                case InsertStatement ins: HandleInsert(ins.InsertSpecification, ins.WithCtesAndXmlNamespaces); break;
                case UpdateStatement upd: HandleUpdate(upd.UpdateSpecification, upd.WithCtesAndXmlNamespaces); break;
                case DeleteStatement del: HandleDelete(del.DeleteSpecification, del.WithCtesAndXmlNamespaces); break;
                case MergeStatement mg: HandleMerge(mg.MergeSpecification, mg.WithCtesAndXmlNamespaces); break;
                case TruncateTableStatement tt: HandleTruncate(tt); break;
                case DropTableStatement dt: foreach (var o in dt.Objects) HandleDrop(o); break;
                case CreateTableStatement ct: HandleCreateTable(ct); break;
                case AlterTableAddTableElementStatement at: HandleAlterAdd(at); break;
                case DeclareTableVariableStatement dtv: DeclareTableVar(dtv.Body); break;
                case DeclareVariableStatement dv: HandleDeclare(dv); break;
                case SetVariableStatement sv: HandleSet(sv); break;
                case DeclareCursorStatement dc: HandleDeclareCursor(dc); break;
                case FetchCursorStatement fc: HandleFetch(fc); break;
                case ExecuteStatement ex: HandleExecStatement(ex); break;
                case ReturnStatement rt: if (rt.Expression != null) returnDeps = returnDeps.Union(BindScalar(rt.Expression, moduleScope)); break;
                case BulkInsertStatement bi: HandleBulkInsert(bi); break;
                case UseStatement us: HandleUse(us); break;
                case CreateSynonymStatement cs:
                    {
                        var (r, _, conf) = ResolveObject(cs.Name, true);
                        var tgt = cs.ForName;
                        var (tr, tinfo, tconf) = ResolveName(Id(tgt.ServerIdentifier).NullIfEmpty(), Id(tgt.DatabaseIdentifier).NullIfEmpty(), Id(tgt.SchemaIdentifier).NullIfEmpty(), Id(tgt.BaseIdentifier));
                        EmitObj(r.With(ObjType.Synonym), "Create", conf, "synonym → " + tr.Fqn);
                        EmitObj(tr, "ResolvesTo", tconf, "synonym hedefi: " + r.Fqn);
                        break;
                    }
                case PrintStatement pr: dyn.Touch(pr.Expression); break;
                case RaiseErrorStatement: case ThrowStatement: case WaitForStatement: case GoToStatement: case LabelStatement: case BreakStatement: case ContinueStatement:
                case BeginTransactionStatement: case CommitTransactionStatement: case RollbackTransactionStatement: case SaveTransactionStatement:
                case PredicateSetStatement: case SetTransactionIsolationLevelStatement: case SetRowCountStatement: case SetIdentityInsertStatement: case SetCommandStatement:
                case OpenCursorStatement: case CloseCursorStatement: case DeallocateCursorStatement: case ExecuteAsStatement: case RevertStatement:
                case CreateIndexStatement: case DropIndexStatement: case UpdateStatisticsStatement: case CreateStatisticsStatement: case DbccStatement: case CheckpointStatement:
                case KillStatement: case SendStatement: case ReceiveStatement: case TSqlStatementSnippet: case SetErrorLevelStatement: case SetOffsetsStatement: case SetStatisticsStatement: case SetTextSizeStatement:
                    break;
                case ProcedureStatementBody or ViewStatementBody or FunctionStatementBody or TriggerStatementBody:
                    AnalyzeTop(st); break;   // job/script içinde CREATE ... 
                default:
                    // diğer DDL (CREATE VIEW içinde vs.) — lineage etkisi yok; ad görünsün diye kaydet
                    if (st is not (DropObjectsStatement or AlterTableStatement or CreateSchemaStatement or CreateSequenceStatement or GrantStatement or DenyStatement or RevokeStatement or CreateSynonymStatement or CreateTypeStatement or CreateDatabaseStatement))
                        Unresolved("UnhandledStatement", stmtType, Src(st, 120));
                    break;
            }
        }
        catch (TimeoutException) { throw; }
        catch (Exception ex)
        {
            Unresolved("StatementError", stmtType, ex.GetType().Name + ": " + ex.Message + " @ " + Src(st, 120));
        }
    }

    // ------------------------------------------------------------ kontrol akışı
    void HandleIf(IfStatement ifs)
    {
        BindBool(ifs.Predicate, moduleScope);
        var snapVars = new Dictionary<string, Deps>(vars, NameComparer.Instance);
        var snapDyn = dyn.Snapshot();
        Stmt(ifs.ThenStatement);
        var thenVars = new Dictionary<string, Deps>(vars, NameComparer.Instance);
        var thenDyn = dyn.Snapshot();
        vars.Clear(); foreach (var kv in snapVars) vars[kv.Key] = kv.Value;
        dyn.Restore(snapDyn);
        if (ifs.ElseStatement != null) Stmt(ifs.ElseStatement);
        foreach (var kv in thenVars) vars[kv.Key] = vars.TryGetValue(kv.Key, out var cur) ? cur.Union(kv.Value) : kv.Value;
        dyn.Join(thenDyn);
    }

    void HandleWhile(WhileStatement wh)
    {
        BindBool(wh.Predicate, moduleScope);
        if (!suppressEmit)
        {
            // 1. geçiş: yalnız değişken durumu (satır üretme), 2. geçiş: gerçek
            int saveStmt = stmtNo; suppressEmit = true;
            try { Stmt(wh.Statement); } finally { suppressEmit = false; stmtNo = saveStmt; }
        }
        dyn.MarkLoop();
        Stmt(wh.Statement);
    }

    // ------------------------------------------------------------ SELECT
    void HandleSelect(SelectStatement sel)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(sel.WithCtesAndXmlNamespaces, moduleScope) };
        var qr = BindQuery(sel.QueryExpression, scope);
        bool setsVar = HasSetVariable(sel.QueryExpression);
        if (sel.Into != null)
        {
            stmtType = "SELECT INTO";
            var names = qr.Columns.Select(c => c.Name).ToList();
            var target = ResolveCreateTarget(sel.Into, names);
            WriteColumns(target, names, qr.Columns, qr.Control, "Insert", false);
        }
        else if (setsVar) { /* atamalar BindSpec içinde yapıldı */ }
        else lastResultSet = EmitResultSet(qr);
    }

    static bool HasSetVariable(QueryExpression q) => q switch
    {
        QuerySpecification qs => qs.SelectElements.Any(e => e is SelectSetVariable),
        QueryParenthesisExpression qp => HasSetVariable(qp.QueryExpression),
        BinaryQueryExpression bq => HasSetVariable(bq.FirstQueryExpression) || HasSetVariable(bq.SecondQueryExpression),
        _ => false
    };

    List<OutCol> EmitResultSet(QueryResult qr)
    {
        int n = Result.ResultSetNames.Count + 1;
        if (!suppressEmit) Result.ResultSetNames.Add(qr.Columns.Select(c => c.Name).ToList());
        stmtType = "SELECT(RS)";
        for (int i = 0; i < qr.Columns.Count; i++) EmitCol(qr.Columns[i].Deps, Mod.Ref, $"RS{n}:{qr.Columns[i].Name}", qr.Columns[i].Expr, $"result set {n}, ordinal {i + 1}");
        EmitIndirect(qr.Control, Mod.Ref);
        return qr.Columns;
    }

    void SelectAsOutput(SelectStatement sel, ObjRef target, List<string>? names)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(sel.WithCtesAndXmlNamespaces, moduleScope) };
        var qr = BindQuery(sel.QueryExpression, scope);
        for (int i = 0; i < qr.Columns.Count; i++)
        {
            string name = names != null && i < names.Count ? names[i] : qr.Columns[i].Name;
            EmitCol(qr.Columns[i].Deps, target, name, qr.Columns[i].Expr);
        }
        EmitIndirect(qr.Control, target);
    }

    // ------------------------------------------------------------ yazma hedefleri
    /// <summary>SELECT INTO / CREATE TABLE hedefi: temp → registry; kalıcı → katalog overlay.</summary>
    ObjRef ResolveCreateTarget(SchemaObjectName son, List<string>? cols)
    {
        string name = Id(son.BaseIdentifier);
        if (name.StartsWith('#'))
        {
            var td = temps.Define(name, cols, OwnerRef);
            EmitObj(td.Ref, "Create");
            return td.Ref;
        }
        var (r, info, conf) = ResolveObject(son, true);
        if (info == null && r.Type == ObjType.Unresolved)
        {
            // kod tarafından yaratılan kalıcı tablo: overlay'e ekle ki sonraki ifadeler çözülsün
            var tdb = cat.FindDb(r.Server, r.Database);
            var schema = r.Schema.NullIfEmpty() ?? (Mod.TypeCode is "JOB" or "FILE" ? Mod.DefaultSchema : Mod.Ref.Schema);
            var nr = new ObjRef(r.Server, r.Database, schema, r.Name, ObjType.Table);
            if (tdb != null)
            {
                var oi = new ObjInfo { TypeCode = "U", Ref = nr, Columns = cols?.Select((c, i) => new ColInfo { Name = c, Ordinal = i + 1 }).ToList() ?? new() };
                tdb.Overlay.TryAdd(schema + "." + r.Name, oi);
            }
            EmitObj(nr, "Create", Confidence.High, "katalogda yok; kod yaratıyor");
            return nr;
        }
        EmitObj(r, "Create", conf);
        return r;
    }

    RangeVar ResolveTarget(TableReference tr, Scope scope)
    {
        switch (tr)
        {
            case NamedTableReference nt:
                {
                    string name = Id(nt.SchemaObject.BaseIdentifier);
                    if (nt.SchemaObject.SchemaIdentifier == null && nt.SchemaObject.DatabaseIdentifier == null)
                    {
                        var existing = scope.FindVar(name);
                        if (existing != null && !existing.Transparent) return existing;
                        if (existing != null && existing.Transparent)
                        {
                            // güncellenebilir CTE/türetilmiş tablo: altındaki tek kalıcı kaynağı bul
                            var srcs = existing.Columns?.SelectMany(c => c.Deps.Data.Keys.Select(k => k.Obj)).Distinct().ToList() ?? new();
                            if (srcs.Count == 1) { var rv = new RangeVar { Alias = name, BaseName = name, Obj = srcs[0], Columns = existing.Columns, Transparent = true }; return rv; }
                            Unresolved("UpdatableCteAmbiguous", name);
                            return existing;
                        }
                    }
                    var made = MakeRangeVar(nt.SchemaObject, Id(nt.Alias).NullIfEmpty(), scope, true);
                    scope.Vars.Add(made);
                    return made;
                }
            case VariableTableReference vt:
                {
                    var rv = TableVar(vt.Variable.Name);
                    var r2 = new RangeVar { Alias = Id(vt.Alias).NullIfEmpty(), BaseName = vt.Variable.Name, Obj = rv.Obj, Columns = rv.Columns, Conf = rv.Conf };
                    scope.Vars.Add(r2);
                    return r2;
                }
            default:
                {
                    Unresolved("UnsupportedWriteTarget", tr.GetType().Name, Src(tr, 120));
                    var r = new RangeVar { BaseName = "?", Obj = new ObjRef(curServer, curDb, "", tr.GetType().Name, ObjType.Unresolved), Conf = Confidence.Low };
                    scope.Vars.Add(r);
                    return r;
                }
        }
    }

    List<string>? DefaultInsertCols(RangeVar target)
    {
        if (target.Columns == null) return null;
        if (target.Obj.IsPersistent)
        {
            var info = cat.FindDb(target.Obj.Server, target.Obj.Database)?.Find(target.Obj.Schema, target.Obj.Name);
            if (info != null && info.Columns.Count > 0)
                return info.Columns.Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion).Select(c => c.Name).ToList();
        }
        return target.Columns.Select(c => c.Name).ToList();
    }

    void WriteColumns(ObjRef target, List<string>? targetCols, List<OutCol> src, Deps control, string action, bool positional, string note = "")
    {
        EmitObj(target, action);
        if (targetCols == null)
        {
            targetCols = src.Select(c => c.Name).ToList();
            note = Join(note, "hedef kolon listesi bilinmiyor; kaynak adları varsayıldı");
        }
        if (src.Count == 1 && src[0].Name == "*" && targetCols.Count != 1)
        {
            foreach (var tc in targetCols) EmitCol(src[0].Deps.AsPositional(), target, tc, "", Join(note, "kaynak * açılamadı"));
        }
        else
        {
            int n = Math.Min(targetCols.Count, src.Count);
            string mism = targetCols.Count != src.Count ? $"kolon sayısı uyuşmuyor: hedef {targetCols.Count}, kaynak {src.Count}" : "";
            for (int i = 0; i < n; i++)
                EmitCol(positional ? src[i].Deps.AsPositional() : src[i].Deps, target, targetCols[i], src[i].Expr, Join(note, mism));
        }
        EmitIndirect(control, target);
    }

    List<OutCol> ValuesCols(ValuesInsertSource v, Scope scope)
    {
        var cols = new List<OutCol>();
        int n = v.RowValues.Count > 0 ? v.RowValues.Max(r => r.ColumnValues.Count) : 0;
        for (int i = 0; i < n; i++)
        {
            var d = Deps.Union(v.RowValues.Select(r => i < r.ColumnValues.Count ? BindScalar(r.ColumnValues[i], scope) : Deps.None));
            cols.Add(new OutCol("Val" + (i + 1), d) { Expr = v.RowValues.Count > 0 && i < v.RowValues[0].ColumnValues.Count ? Src(v.RowValues[0].ColumnValues[i], 200) : "" });
        }
        return cols;
    }

    // ------------------------------------------------------------ INSERT / UPDATE / DELETE / MERGE
    List<OutCol> HandleDml(DataModificationSpecification spec, bool nested)
    {
        switch (spec)
        {
            case InsertSpecification i: HandleInsert(i, null); break;
            case UpdateSpecification u: HandleUpdate(u, null); break;
            case DeleteSpecification d: HandleDelete(d, null); break;
            case MergeSpecification m: HandleMerge(m, null); break;
        }
        return new List<OutCol>();
    }

    void HandleInsert(InsertSpecification spec, WithCtesAndXmlNamespaces? with)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(with, moduleScope) };
        var target = ResolveTarget(spec.Target, new Scope(scope));
        var targetCols = spec.Columns.Count > 0 ? spec.Columns.Select(c => Last(c.MultiPartIdentifier)).ToList() : DefaultInsertCols(target);
        if (target.Obj.Type == ObjType.TempTable && target.Columns == null && spec.Columns.Count > 0) temps.AddColumns(target.Obj.Name, targetCols!, OwnerRef);
        List<OutCol> src = new(); var control = new List<Deps>();
        switch (spec.InsertSource)
        {
            case SelectInsertSource s:
                {
                    stmtType = "INSERT SELECT";
                    var qr = BindQuery(s.Select, scope);
                    src = qr.Columns; control.Add(qr.Control);
                    break;
                }
            case ValuesInsertSource v:
                stmtType = "INSERT VALUES";
                src = ValuesCols(v, scope);
                break;
            case ExecuteInsertSource x:
                {
                    stmtType = "INSERT EXEC";
                    var er = HandleExecSpec(x.Execute);
                    if (er.ResultSet != null) src = er.ResultSet;
                    else if (er.Callee != null)
                    {
                        int n = targetCols?.Count ?? 1;
                        for (int i = 0; i < n; i++) src.Add(new OutCol("#" + (i + 1), Deps.Of(new ColRef(er.Callee, $"RS1:#{i + 1}", Confidence.High), FlowKind.Positional)));
                    }
                    break;
                }
        }
        if (spec.TopRowFilter != null) control.Add(BindScalar(spec.TopRowFilter.Expression, scope).AsIndirect());
        RecordLiterals(target, targetCols, spec.InsertSource);
        WriteColumns(target.Obj, targetCols, src, Deps.Union(control), "Insert", spec.Columns.Count == 0);
        if (spec.OutputIntoClause != null)
        {
            var newVals = new Dictionary<string, Deps>(NameComparer.Instance);
            if (targetCols != null) for (int i = 0; i < Math.Min(targetCols.Count, src.Count); i++) newVals[targetCols[i]] = src[i].Deps;
            HandleOutputInto(spec.OutputIntoClause, target, newVals);
        }
    }

    void HandleUpdate(UpdateSpecification spec, WithCtesAndXmlNamespaces? with)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(with, moduleScope) };
        var control = new List<Deps>();
        if (spec.FromClause != null) foreach (var tr in spec.FromClause.TableReferences) control.Add(BindTableRef(tr, scope));
        var target = ResolveTarget(spec.Target, scope);
        if (spec.WhereClause != null) control.Add(BindBool(spec.WhereClause.SearchCondition, scope).AsIndirect());
        if (spec.TopRowFilter != null) control.Add(BindScalar(spec.TopRowFilter.Expression, scope).AsIndirect());
        var newVals = new Dictionary<string, Deps>(NameComparer.Instance);
        EmitObj(target.Obj, "Update", target.Conf);
        ApplySetClauses(spec.SetClauses, target, scope, newVals);
        EmitIndirect(Deps.Union(control), target.Obj);
        if (spec.OutputIntoClause != null) HandleOutputInto(spec.OutputIntoClause, target, newVals);
    }

    void ApplySetClauses(IList<SetClause> setClauses, RangeVar target, Scope scope, Dictionary<string, Deps> newVals)
    {
        foreach (var sc in setClauses)
        {
            switch (sc)
            {
                case AssignmentSetClause a:
                    {
                        var d = BindScalar(a.NewValue, scope);
                        if (a.Column != null)
                        {
                            string col = Last(a.Column.MultiPartIdentifier);
                            if (a.AssignmentKind != AssignmentKind.Equals) d = d.Union(target.Column(col)).AsExpression();
                            EmitCol(d, target.Obj, col, Src(a.NewValue, 200));
                            newVals[col] = d;
                            if (a.Variable != null) { vars[a.Variable.Name] = d.Union(target.Column(col)); }
                        }
                        else if (a.Variable != null) { vars[a.Variable.Name] = d; dyn.Assign(a.Variable.Name, a.NewValue, false); }
                        break;
                    }
                case FunctionCallSetClause fc:
                    {
                        // col.WRITE(expr, off, len) / xmlcol.modify('insert ... sql:column("a") ...')
                        var args = Deps.Union(fc.MutatorFunction.Parameters.Select(p => BindScalar(p, scope))).AsExpression();
                        if (NameComparer.Eq(fc.MutatorFunction.FunctionName.Value, "modify")) args = args.Union(XmlRefs(fc.MutatorFunction.Parameters, scope));
                        string col = fc.MutatorFunction.CallTarget is MultiPartIdentifierCallTarget mp ? Last(mp.MultiPartIdentifier) : "?";
                        EmitCol(args.Union(target.Column(col).AsExpression()), target.Obj, col, Src(fc, 200), NameComparer.Eq(fc.MutatorFunction.FunctionName.Value, "modify") ? "XML DML (yaklaşık)" : "");
                        newVals[col] = args;
                        break;
                    }
            }
        }
    }

    void HandleDelete(DeleteSpecification spec, WithCtesAndXmlNamespaces? with)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(with, moduleScope) };
        var control = new List<Deps>();
        if (spec.FromClause != null) foreach (var tr in spec.FromClause.TableReferences) control.Add(BindTableRef(tr, scope));
        var target = ResolveTarget(spec.Target, scope);
        if (spec.WhereClause != null) control.Add(BindBool(spec.WhereClause.SearchCondition, scope).AsIndirect());
        if (spec.TopRowFilter != null) control.Add(BindScalar(spec.TopRowFilter.Expression, scope).AsIndirect());
        EmitObj(target.Obj, "Delete", target.Conf);
        EmitIndirect(Deps.Union(control), target.Obj);
        if (spec.OutputIntoClause != null) HandleOutputInto(spec.OutputIntoClause, target, new Dictionary<string, Deps>(NameComparer.Instance));
    }

    void HandleMerge(MergeSpecification spec, WithCtesAndXmlNamespaces? with)
    {
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(with, moduleScope) };
        var target = ResolveTarget(spec.Target, scope);
        if (spec.TableAlias != null) target.Alias = spec.TableAlias.Value;
        var control = new List<Deps> { BindTableRef(spec.TableReference, scope) };
        if (spec.SearchCondition != null) control.Add(BindBool(spec.SearchCondition, scope).AsIndirect());
        var newVals = new Dictionary<string, Deps>(NameComparer.Instance);
        EmitObj(target.Obj, "Merge", target.Conf);
        foreach (var ac in spec.ActionClauses)
        {
            if (ac.SearchCondition != null) control.Add(BindBool(ac.SearchCondition, scope).AsIndirect());
            switch (ac.Action)
            {
                case UpdateMergeAction um:
                    stmtType = "MERGE UPDATE"; EmitObj(target.Obj, "Update", target.Conf, "MERGE WHEN " + ac.Condition);
                    ApplySetClauses(um.SetClauses, target, scope, newVals);
                    break;
                case InsertMergeAction im:
                    {
                        stmtType = "MERGE INSERT";
                        var cols = im.Columns.Count > 0 ? im.Columns.Select(c => Last(c.MultiPartIdentifier)).ToList() : DefaultInsertCols(target);
                        var src = im.Source != null ? ValuesCols(im.Source, scope) : new List<OutCol>();
                        WriteColumns(target.Obj, cols, src, Deps.None, "Insert", im.Columns.Count == 0, "MERGE WHEN " + ac.Condition);
                        if (cols != null) for (int i = 0; i < Math.Min(cols.Count, src.Count); i++) newVals[cols[i]] = src[i].Deps;
                        break;
                    }
                case DeleteMergeAction:
                    stmtType = "MERGE DELETE"; EmitObj(target.Obj, "Delete", target.Conf, "MERGE WHEN " + ac.Condition);
                    break;
            }
        }
        stmtType = "MERGE";
        EmitIndirect(Deps.Union(control), target.Obj);
        if (spec.OutputIntoClause != null) HandleOutputInto(spec.OutputIntoClause, target, newVals);
    }

    void HandleOutputInto(OutputIntoClause oc, RangeVar target, Dictionary<string, Deps> newVals)
    {
        var save = stmtType; stmtType = "OUTPUT INTO";
        var oscope = new Scope(moduleScope);
        var tcols = target.Expand();
        var inserted = new RangeVar { Alias = "inserted", BaseName = "inserted", Obj = target.Obj, Transparent = true, Columns = tcols.Select(c => new OutCol(c.Name, newVals.TryGetValue(c.Name, out var d) ? d : Deps.Direct(new ColRef(target.Obj, c.Name)))).ToList() };
        var deleted = new RangeVar { Alias = "deleted", BaseName = "deleted", Obj = target.Obj, Transparent = true, Columns = tcols.Select(c => new OutCol(c.Name, Deps.Direct(new ColRef(target.Obj, c.Name)))).ToList() };
        if (target.Columns == null) { inserted.Columns = null; inserted.Fallback = Deps.Union(newVals.Values).Union(Deps.Direct(new ColRef(target.Obj, "*"))); deleted.Columns = null; deleted.Fallback = Deps.Direct(new ColRef(target.Obj, "*")); }
        oscope.Vars.Add(inserted); oscope.Vars.Add(deleted);
        var into = ResolveTarget(oc.IntoTable, new Scope(moduleScope));
        var cols = oc.IntoTableColumns.Count > 0 ? oc.IntoTableColumns.Select(c => Last(c.MultiPartIdentifier)).ToList() : DefaultInsertCols(into);
        var src = new List<OutCol>();
        int i = 0;
        foreach (var se in oc.SelectColumns)
        {
            i++;
            switch (se)
            {
                case SelectScalarExpression sse:
                    src.Add(new OutCol(sse.ColumnName?.Value ?? (sse.Expression is ColumnReferenceExpression cr && cr.MultiPartIdentifier != null ? Last(cr.MultiPartIdentifier) : "Col" + i), BindScalar(sse.Expression, oscope)) { Expr = Src(sse.Expression, 200) });
                    break;
                case SelectStarExpression star:
                    {
                        var rv = star.Qualifier != null ? oscope.FindVar(Last(star.Qualifier)) : null;
                        if (rv != null) src.AddRange(rv.Expand()); else foreach (var v in oscope.Vars) src.AddRange(v.Expand());
                        break;
                    }
            }
        }
        WriteColumns(into.Obj, cols, src, Deps.None, "Insert", oc.IntoTableColumns.Count == 0, "OUTPUT INTO");
        stmtType = save;
    }

    // ------------------------------------------------------------ DDL benzeri
    void HandleTruncate(TruncateTableStatement tt)
    {
        string name = Id(tt.TableName.BaseIdentifier);
        if (name.StartsWith('#')) { EmitObj(temps.GetOrAmbient(name, OwnerRef).Ref, "Truncate"); return; }
        var (r, _, conf) = ResolveObject(tt.TableName, true);
        EmitObj(r, "Truncate", conf);
    }

    void HandleDrop(SchemaObjectName son)
    {
        string name = Id(son.BaseIdentifier);
        if (name.StartsWith('#')) { var td = temps.Get(name); if (td != null) { EmitObj(td.Ref, "Drop"); temps.Drop(name); } return; }
        var (r, _, conf) = ResolveObject(son, true);
        EmitObj(r, "Drop", conf);
    }

    void HandleCreateTable(CreateTableStatement ct)
    {
        string name = Id(ct.SchemaObjectName.BaseIdentifier);
        var cols = ct.Definition?.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToList() ?? new List<string>();
        ObjRef target;
        if (name.StartsWith('#')) { target = temps.Define(name, cols, OwnerRef).Ref; EmitObj(target, "Create"); }
        else target = ResolveCreateTarget(ct.SchemaObjectName, cols);
        // computed kolonlar: aynı tablonun kolonlarından Expression
        if (ct.Definition != null)
        {
            var self = new Scope(null); self.Vars.Add(new RangeVar { BaseName = name, Obj = target, Columns = cols.Select(c => new OutCol(c, Deps.None)).ToList() });
            foreach (var cd in ct.Definition.ColumnDefinitions.Where(c => c.ComputedColumnExpression != null))
                EmitCol(BindScalar(cd.ComputedColumnExpression, self).AsExpression(), target, cd.ColumnIdentifier.Value, Src(cd.ComputedColumnExpression, 200), "computed column");
        }
    }

    void HandleAlterAdd(AlterTableAddTableElementStatement at)
    {
        string name = Id(at.SchemaObjectName.BaseIdentifier);
        var cols = at.Definition?.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToList() ?? new();
        if (name.StartsWith('#')) temps.AddColumns(name, cols, OwnerRef);
        else
        {
            var (r, _, conf) = ResolveObject(at.SchemaObjectName, true);
            EmitObj(r, "Alter", conf);
        }
    }

    void DeclareTableVar(DeclareTableVariableBody body)
    {
        string name = body.VariableName.Value;
        var cols = body.Definition?.ColumnDefinitions.Select(c => new OutCol(c.ColumnIdentifier.Value, Deps.None)).ToList() ?? new();
        var r = new ObjRef(Mod.Ref.Server, Mod.Ref.Database, Mod.Ref.Schema + "." + Mod.Ref.Name, name, ObjType.TableVariable);
        tableVars[name] = new RangeVar { BaseName = name, Obj = r, Columns = cols, Conf = Confidence.Exact };
    }

    void HandleDeclare(DeclareVariableStatement dv)
    {
        foreach (var d in dv.Declarations)
        {
            string name = d.VariableName.Value;
            varTypes[name] = d.DataType is SqlDataTypeReference sd ? sd.SqlDataTypeOption + (sd.Parameters.Count > 0 ? "(" + string.Join(",", sd.Parameters.Select(x => x.Value)) + ")" : "") : "";
            if (d.Value != null) { vars[name] = BindScalar(d.Value, moduleScope); try { dyn.Assign(name, d.Value, false); } catch (Exception ex) { Unresolved("DynamicAssignError", name, ex.Message); } }
            else { vars[name] = Deps.None; dyn.Clear(name); }
        }
    }

    void HandleSet(SetVariableStatement sv)
    {
        string name = sv.Variable.Name;
        if (sv.CursorDefinition != null)
        {
            var sel = sv.CursorDefinition.Select;
            var scope = new Scope(moduleScope) { Ctes = RegisterCtes(sel.WithCtesAndXmlNamespaces, moduleScope) };
            cursors[name] = BindQuery(sel.QueryExpression, scope).Columns;
            return;
        }
        if (sv.Expression == null) return;
        var d = BindScalar(sv.Expression, moduleScope);
        bool append = sv.AssignmentKind != AssignmentKind.Equals;
        if (append && vars.TryGetValue(name, out var old)) d = d.Union(old).AsExpression();
        vars[name] = d;
        try { dyn.Assign(name, sv.Expression, append); } catch (Exception ex) { Unresolved("DynamicAssignError", name, ex.Message); }
    }

    void HandleDeclareCursor(DeclareCursorStatement dc)
    {
        var sel = dc.CursorDefinition?.Select;
        if (sel == null) return;
        var scope = new Scope(moduleScope) { Ctes = RegisterCtes(sel.WithCtesAndXmlNamespaces, moduleScope) };
        var qr = BindQuery(sel.QueryExpression, scope);
        foreach (var c in qr.Columns) c.Deps = c.Deps.Union(qr.Control.AsIndirect());
        cursors[dc.Name.Value] = qr.Columns;
    }

    void HandleFetch(FetchCursorStatement fc)
    {
        string? cname = fc.Cursor?.Name?.Value ?? fc.Cursor?.Name?.Identifier?.Value;
        if (cname == null || !cursors.TryGetValue(cname, out var cols)) { if (fc.IntoVariables.Count > 0) Unresolved("CursorNotFound", cname ?? "?"); return; }
        for (int i = 0; i < fc.IntoVariables.Count; i++)
        {
            string v = fc.IntoVariables[i].Name;
            var d = i < cols.Count ? cols[i].Deps.AsPositional() : Deps.None;
            vars[v] = d;
            dyn.AssignFromDeps(v, d, "cursor " + cname);
        }
    }

    void HandleBulkInsert(BulkInsertStatement bi)
    {
        var (r, info, conf) = ResolveObject(bi.To, true);
        var file = new ObjRef("FILE", "", "", bi.From?.Value ?? "?", ObjType.File);
        EmitObj(file, "Reads", Confidence.High, "BULK INSERT kaynağı");
        var cols = info?.Columns.Where(c => !c.IsIdentity && !c.IsComputed).Select(c => c.Name).ToList();
        WriteColumns(r, cols, new List<OutCol> { new OutCol("*", Deps.Direct(new ColRef(file, "*", Confidence.High))) }, Deps.None, "Insert", true, "BULK INSERT");
    }

    void HandleUse(UseStatement us)
    {
        string name = us.DatabaseName.Value;
        if (cat.FindDb(curServer, name) != null) curDb = name;
        else { Unresolved("DatabaseNotScanned", name, "USE"); curDb = name; }
    }

    // ------------------------------------------------------------ EXEC
    void HandleExecStatement(ExecuteStatement es)
    {
        var er = HandleExecSpec(es.ExecuteSpecification);
        if (es.ExecuteSpecification.Variable != null && er.Callee != null)
            vars[es.ExecuteSpecification.Variable.Name] = Deps.Direct(new ColRef(er.Callee, "RETURN", Confidence.High));
    }

    ExecResult HandleExecSpec(ExecuteSpecification spec)
    {
        stmtType = "EXEC";
        switch (spec.ExecutableEntity)
        {
            case ExecutableProcedureReference epr:
                {
                    var prn = epr.ProcedureReference;
                    if (prn.ProcedureVariable != null) return ExecDynamicProcName(prn.ProcedureVariable.Name, epr.Parameters, spec);
                    var son = prn.ProcedureReference.Name;
                    string baseName = Id(son.BaseIdentifier);
                    if (NameComparer.Eq(baseName, "sp_executesql")) return ExecSpExecuteSql(epr.Parameters, spec, Id(son.DatabaseIdentifier).NullIfEmpty());
                    if (NameComparer.Eq(baseName, "sp_MSforeachtable") || NameComparer.Eq(baseName, "sp_MSforeachdb")) return ExecForEach(baseName, epr.Parameters);
                    if (NameComparer.Eq(baseName, "xp_cmdshell") || NameComparer.Eq(baseName, "sp_send_dbmail") || NameComparer.Eq(baseName, "sp_start_job") || baseName.StartsWith("sp_OA", StringComparison.OrdinalIgnoreCase))
                    {
                        var lit = epr.Parameters.Select(p => p.ParameterValue).OfType<StringLiteral>().Select(s => s.Value).FirstOrDefault() ?? "";
                        var ext = new ObjRef(curServer, NameComparer.Eq(baseName, "sp_start_job") ? "msdb" : "", "", NameComparer.Eq(baseName, "sp_start_job") ? "JOB:" + lit : baseName, NameComparer.Eq(baseName, "sp_start_job") ? ObjType.JobStep : ObjType.External);
                        EmitObj(ext, "Calls", Confidence.High, ParserLadder.Snippet(lit, 120));
                        if (NameComparer.Eq(baseName, "sp_send_dbmail"))
                        {
                            var q = epr.Parameters.FirstOrDefault(p => p.Variable != null && NameComparer.Eq(p.Variable.Name, "@query"));
                            if (q != null) RunDynamic(dyn.Eval(q.ParameterValue), "sp_send_dbmail @query", null, null);
                        }
                        return new ExecResult { Callee = ext };
                    }
                    var (r, info, conf) = ResolveObject(son);
                    if (spec.LinkedServer != null)
                    {
                        var (srvName, scanned) = cat.ResolveLinked(curServer, spec.LinkedServer.Value);
                        if (!scanned) r = new ObjRef(srvName, r.Database, r.Schema, r.Name, ObjType.External);
                    }
                    var callee = r.Type is ObjType.Unresolved or ObjType.External or ObjType.System ? r : r.With(info?.Ref.Type ?? ObjType.Procedure);
                    EmitObj(callee, "Calls", conf);
                    BindExecArgs(epr.Parameters, callee, info);
                    return new ExecResult { Callee = callee };
                }
            case ExecutableStringList esl:
                {
                    stmtType = "EXEC DYNAMIC";
                    var templates = dyn.EvalConcat(esl.Strings);
                    string? linked = spec.LinkedServer?.Value;
                    return RunDynamic(templates, linked != null ? "EXEC() AT " + linked : "EXEC()", null, linked);
                }
            default:
                return new ExecResult();
        }
    }

    void BindExecArgs(IList<ExecuteParameter> ps, ObjRef callee, ObjInfo? info)
    {
        int i = 0;
        foreach (var p in ps)
        {
            i++;
            string pname = p.Variable?.Name ?? (info != null && i - 1 < info.Params.Count ? info.Params[i - 1].Name : "#" + i);
            var d = p.ParameterValue is VariableReference vr && tableVars.TryGetValue(vr.Name, out var tv)
                ? Deps.Union(tv.Expand().Select(c => c.Deps)).Union(Deps.Direct(new ColRef(tv.Obj, "*")))
                : BindScalar(p.ParameterValue, moduleScope);
            if (!d.IsEmpty) EmitCol(d, callee, "IN:" + pname, Src(p.ParameterValue, 200));
            if (p.IsOutput && p.ParameterValue is VariableReference ov)
                vars[ov.Name] = Deps.Direct(new ColRef(callee, "OUT:" + pname, Confidence.High));
        }
    }

    static readonly Regex StmtLikeRx = new(@"^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|IF|WHILE|BEGIN|DECLARE|SET|WITH|CREATE|ALTER|DROP|TRUNCATE|EXEC|EXECUTE|USE|BULK|PRINT|RAISERROR)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DbSpExecRx = new(@"^\s*(.+?)\.(sys\.)?sp_executesql\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    ExecResult ExecDynamicProcName(string varName, IList<ExecuteParameter> ps, ExecuteSpecification spec)
    {
        stmtType = "EXEC @PROC";
        var alts = dyn.Get(varName);
        // EXEC @sql (parantezsiz) ile çalıştırılan tam ifadeler: prosedür adı değil, dinamik SQL
        if (alts.Count > 0 && alts.All(t => StmtLikeRx.IsMatch(t.Display()) || t.Display().Trim().Contains(' ')))
            return RunDynamic(alts, "EXEC @var (ifade)", null, spec.LinkedServer?.Value);
        // RAPOR_<yıl>.sys.sp_executesql: DB bağlamı desenli sp_executesql
        var mDb = alts.Count == 1 ? DbSpExecRx.Match(alts[0].Display()) : Match.Empty;
        if (mDb.Success)
        {
            string pattern = Regex.Replace(mDb.Groups[1].Value.Trim('[', ']'), @"⟨[^⟩]*⟩", "%");
            var rx = new Regex("^" + Regex.Escape(pattern).Replace("%", ".*") + "$", RegexOptions.IgnoreCase);
            var dbs = (cat.FindServer(curServer)?.Dbs.Keys ?? Enumerable.Empty<string>()).Where(d => rx.IsMatch(d)).Take(12).ToList();
            if (dbs.Count == 0 && !NameComparer.Eq(cat.Canonical(pattern), pattern)) dbs.Add(cat.Canonical(pattern));
            if (dbs.Count == 0) dbs.Add(curDb);
            ExecResult? last = null;
            foreach (var dbn in dbs) { var save = provNote; provNote = Join(provNote, "DB deseni " + pattern + " → " + dbn); try { last = ExecSpExecuteSql(ps, spec, dbn); } finally { provNote = save; } }
            return last ?? new ExecResult { Dynamic = true };
        }
        var site = NewDynamicRow("EXEC @proc", alts);
        ObjRef? first = null; int edges = 0;
        foreach (var t in alts.Take(cfg.MaxDynamicAlternatives))
        {
            var lit = t.LiteralOrNull();
            if (lit != null)
            {
                var parts = SplitName(lit.Trim());
                var (r, info, conf) = ResolveName(parts.Length > 3 ? parts[^4] : null, parts.Length > 2 ? parts[^3] : null, parts.Length > 1 ? parts[^2] : null, parts[^1]);
                var callee = r.Type is ObjType.Unresolved or ObjType.External ? r : r.With(info?.Ref.Type ?? ObjType.Procedure);
                EmitObj(callee, "Calls", conf, "dinamik prosedür adı: " + varName);
                BindExecArgs(ps, callee, info); first ??= callee; edges++;
            }
            else
            {
                var pattern = t.LikePattern();
                var matches = MatchPattern(pattern, ObjType.Procedure);
                foreach (var m in matches) { EmitObj(m.Ref, "Calls", Confidence.Medium, $"desen {pattern} ({varName})"); first ??= m.Ref; edges++; }
                if (matches.Count == 0) { EmitObj(new ObjRef(curServer, curDb, "", t.Display(), ObjType.Unresolved), "Calls", Confidence.Low, "çözülemeyen prosedür adı"); Unresolved("DynamicProcName", t.Display()); }
            }
        }
        site.EdgesProduced = edges; site.Status = edges > 0 && alts.All(a => a.LiteralOrNull() != null) ? "Exact" : edges > 0 ? "Partial" : "Unresolved";
        if (site.Status != "Unresolved" && !suppressEmit) Result.DynamicResolved++;
        return new ExecResult { Callee = first, Dynamic = true };
    }

    ExecResult ExecSpExecuteSql(IList<ExecuteParameter> ps, ExecuteSpecification spec, string? dbContext)
    {
        stmtType = "EXEC sp_executesql";
        if (ps.Count == 0) return new ExecResult { Dynamic = true };
        var templates = dyn.Eval(ps[0].ParameterValue);
        // @params bildirimi → iç parametre adları
        var innerNames = new List<string>();
        if (ps.Count > 1)
        {
            var decl = dyn.Eval(ps[1].ParameterValue).FirstOrDefault()?.LiteralOrNull() ?? "";
            foreach (Match m in Regex.Matches(decl, @"@\w+")) innerNames.Add(m.Value);
        }
        var inner = new Dictionary<string, Deps>(NameComparer.Instance);
        var outputs = new List<(string inner, string outer)>();
        for (int i = 2; i < ps.Count; i++)
        {
            string name = ps[i].Variable?.Name ?? (i - 2 < innerNames.Count ? innerNames[i - 2] : "@p" + (i - 1));
            inner[name] = BindScalar(ps[i].ParameterValue, moduleScope);
            dyn.SetInnerValue(name, ps[i].ParameterValue);
            if (ps[i].IsOutput && ps[i].ParameterValue is VariableReference vr) outputs.Add((name, vr.Name));
        }
        string? saveDb = null;
        if (dbContext != null) { saveDb = curDb; curDb = dbContext; }
        try
        {
            var er = RunDynamic(templates, "sp_executesql", inner, spec.LinkedServer?.Value);
            foreach (var (iname, oname) in outputs) if (er.InnerVars != null && er.InnerVars.TryGetValue(iname, out var d)) vars[oname] = d;
            return er;
        }
        finally { if (saveDb != null) curDb = saveDb; dyn.ClearInnerValues(); }
    }

    ExecResult ExecForEach(string proc, IList<ExecuteParameter> ps)
    {
        stmtType = "EXEC " + proc;
        var cmd = ps.FirstOrDefault(p => p.Variable == null || NameComparer.Eq(p.Variable.Name, "@command1")) ?? ps.FirstOrDefault();
        var templates = cmd == null ? new List<Template>() : dyn.Eval(cmd.ParameterValue);
        // '?' → tüm kullanıcı tabloları (sp_MSforeachtable) / tüm DB'ler: desen deliği
        foreach (var t in templates) t.ReplaceQuestionMark(NameComparer.Eq(proc, "sp_MSforeachtable") ? "ObjectName" : "Database");
        return RunDynamic(templates, proc, null, null);
    }

    List<ObjInfo> MatchPattern(string likePattern, ObjType? type)
    {
        var tdb = cat.FindDb(curServer, curDb);
        if (tdb == null) return new();
        var rx = new Regex("^" + Regex.Escape(likePattern).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.IgnoreCase);
        var q = tdb.Objects.Values.Where(o => rx.IsMatch(o.Ref.Name) || rx.IsMatch(o.Ref.Schema + "." + o.Ref.Name));
        if (type == ObjType.Procedure) q = q.Where(o => o.TypeCode is "P" or "PC");
        else if (type == ObjType.Table) q = q.Where(o => o.TypeCode is "U" or "V" or "SN");
        return q.Take(cfg.MaxPatternMatches).ToList();
    }

    DynamicSqlRow NewDynamicRow(string sink, List<Template> alts)
    {
        if (!suppressEmit) Result.DynamicSites++;
        var row = new DynamicSqlRow
        {
            RunId = runId, Server = Mod.Ref.Server, Database = Mod.Ref.Database, ModuleSchema = Mod.Ref.Schema, ModuleName = Mod.Ref.Name,
            StatementNo = stmtNo, Line = RowLine, SinkKind = sink, Alternatives = alts.Count,
            Template = string.Join("\n---\n", alts.Take(4).Select(a => ParserLadder.Snippet(a.Display(), 1500))),
            Holes = string.Join("; ", alts.SelectMany(a => a.Parts.OfType<Hole>()).Select(h => h.Display + "←" + h.Origin + (h.Values.Count > 0 ? $" [{h.Values.Count} değer: {h.ValuesNote}]" : "")).Distinct().Take(20)),
        };
        if (!suppressEmit) Result.DynamicRows.Add(row);
        return row;
    }
}

#endregion

// ============================================================================
#region 6. Dinamik SQL: string-akış analizi ve şablon somutlaştırma
// ============================================================================

abstract class Part { }
sealed class Lit : Part { public string Text; public Lit(string t) { Text = t; } }
sealed class Hole : Part
{
    public int Id; public string Origin = ""; public string Display = ""; public string Kind = "Unknown"; // Parameter | Column | Cursor | Unknown | Question
    public bool Bracket, Quote; public List<string> Values = new(); public string Placeholder = ""; public ColRef? Source; public string ValuesNote = "";
}

sealed class Template
{
    public List<Part> Parts = new();
    public bool Approximate;
    public static Template OfLit(string s) { var t = new Template(); if (s.Length > 0) t.Parts.Add(new Lit(s)); return t; }
    public static Template OfHole(Hole h) { var t = new Template(); t.Parts.Add(h); return t; }
    public Template Concat(Template o)
    {
        var t = new Template { Approximate = Approximate || o.Approximate };
        t.Parts.AddRange(Parts);
        foreach (var p in o.Parts)
        {
            if (p is Lit l && t.Parts.Count > 0 && t.Parts[^1] is Lit pl) t.Parts[^1] = new Lit(pl.Text + l.Text);
            else t.Parts.Add(p);
        }
        return t;
    }
    public string? LiteralOrNull() => Parts.All(p => p is Lit) ? string.Concat(Parts.Cast<Lit>().Select(l => l.Text)) : null;
    public string Display() => string.Concat(Parts.Select(p => p is Lit l ? l.Text : "⟨" + ((Hole)p).Display + "⟩"));
    public string LikePattern() => string.Concat(Parts.Select(p => p is Lit l ? l.Text.Trim() : "%"));
    public bool HasHoles => Parts.Any(p => p is Hole);
    public Template Map(Func<Part, Part> f) { var t = new Template { Approximate = Approximate }; foreach (var p in Parts) t.Parts.Add(f(p)); return t; }
    public void ReplaceQuestionMark(string kind)
    {
        var np = new List<Part>();
        foreach (var p in Parts)
        {
            if (p is Lit l && l.Text.Contains('?'))
            {
                var segs = l.Text.Split('?');
                for (int i = 0; i < segs.Length; i++)
                {
                    if (segs[i].Length > 0) np.Add(new Lit(segs[i]));
                    if (i < segs.Length - 1) np.Add(new Hole { Id = -1, Origin = "sp_MSforeach", Display = "?", Kind = kind });
                }
            }
            else np.Add(p);
        }
        Parts = np;
    }
}

sealed class StringFlow
{
    readonly ModuleAnalyzer a; readonly LineageConfig cfg;
    Dictionary<string, List<Template>> vars = new(NameComparer.Instance);
    readonly Dictionary<string, (string? def, ScalarExpression? expr)> parms = new(NameComparer.Instance);
    readonly Dictionary<string, List<Template>> innerValues = new(NameComparer.Instance);
    readonly Dictionary<int, Hole> holes = new();
    int seq; bool inLoop;
    public static ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>> CallerArgs = new();
    static readonly Regex PlaceholderRx = new(@"LH_[IV](\d+)", RegexOptions.Compiled);

    public StringFlow(ModuleAnalyzer a, LineageConfig cfg) { this.a = a; this.cfg = cfg; }

    // ---------------- durum
    public void RegisterParam(string name, string? def, ScalarExpression? expr) { parms[name] = (def, expr); vars.Remove(name); }
    public void Clear(string name) => vars.Remove(name);
    public Dictionary<string, List<Template>> Snapshot() => new(vars, NameComparer.Instance);
    public void Restore(Dictionary<string, List<Template>> s) => vars = new(s, NameComparer.Instance);
    public void Join(Dictionary<string, List<Template>> other)
    {
        foreach (var kv in other)
        {
            if (!vars.TryGetValue(kv.Key, out var cur)) { vars[kv.Key] = kv.Value; continue; }
            var merged = new List<Template>(cur);
            foreach (var t in kv.Value) if (!merged.Any(m => m.Display() == t.Display())) merged.Add(t);
            vars[kv.Key] = Cap(merged);
        }
    }
    public void MarkLoop() => inLoop = true;
    public void Touch(ScalarExpression? e) { }
    public void SetInnerValue(string name, ScalarExpression e) => innerValues[name] = Eval(e);
    public void ClearInnerValues() => innerValues.Clear();

    List<Template> Cap(List<Template> l)
    {
        if (l.Count <= cfg.MaxDynamicAlternatives) return l;
        var t = l.Take(cfg.MaxDynamicAlternatives).ToList();
        foreach (var x in t) x.Approximate = true;
        return t;
    }

    Hole NewHole(string origin, string display, string kind)
    {
        var h = new Hole { Id = ++seq, Origin = origin, Display = display, Kind = kind };
        holes[h.Id] = h;
        return h;
    }

    public string Display(string placeholderText) => PlaceholderRx.Replace(placeholderText, m => holes.TryGetValue(int.Parse(m.Groups[1].Value), out var h) ? "⟨" + h.Display + "⟩" : "⟨?⟩");
    public Hole? HoleOf(string placeholderText) { var m = PlaceholderRx.Match(placeholderText); return m.Success && holes.TryGetValue(int.Parse(m.Groups[1].Value), out var h) ? h : null; }

    // ---------------- atamalar
    public void Assign(string name, ScalarExpression expr, bool append)
    {
        var v = Eval(expr);
        if (append) v = Combine(Get(name), v);
        if (inLoop && append) foreach (var t in v) t.Approximate = true;
        vars[name] = Cap(v);
    }
    public void AssignFromSelect(string name, ScalarExpression expr, Deps deps, bool append, bool hasFrom)
    {
        if (!hasFrom) { Assign(name, expr, append); return; }
        // SELECT @v = col FROM t: satır değeri → delik (köken: kolon)
        var src = deps.SingleDirect();
        var h = NewHole(src != null ? "Column:" + src : "Query", name + (src != null ? "=" + src.Obj.Name + "." + src.Column : ""), "Column");
        h.Source = src; FillFromSource(h);
        var v = new List<Template> { Template.OfHole(h) };
        if (deps.Data.Count == 0 && expr is not ColumnReferenceExpression) v = Eval(expr);   // SELECT @v = 'lit' FROM t
        if (append) v = Combine(Get(name), v);
        vars[name] = Cap(v);
    }
    public void AssignFromDeps(string name, Deps deps, string originNote)
    {
        var src = deps.SingleDirect();
        var h = NewHole(src != null ? "Column:" + src : originNote, name + (src != null ? "=" + src.Obj.Name + "." + src.Column : ""), "Cursor");
        h.Source = src; FillFromSource(h);
        vars[name] = new List<Template> { Template.OfHole(h) };
    }

    /// <summary>Delik kaynağı bir kolon ise değerleri doldur: modül içi literal satırlar (temp/@tablo) ya da --config-tables ile canlı DISTINCT.</summary>
    void FillFromSource(Hole h)
    {
        if (h.Source == null) return;
        var lits = a.TempLiterals(h.Source);
        if (lits != null && lits.Count > 0) { h.Values.AddRange(lits.Take(64)); h.ValuesNote = "literal satırlar"; return; }
        if (cfg.ConfigTableLookup && h.Source.Obj.IsPersistent)
        {
            var vals = ConfigTableReader.Values(h.Source, cfg);
            if (vals.Count > 0) { h.Values.AddRange(vals); h.ValuesNote = "config tablosu DISTINCT (superset)"; }
        }
    }
    public List<Template> Get(string name)
    {
        if (innerValues.TryGetValue(name, out var iv)) return iv;
        if (vars.TryGetValue(name, out var v)) return v;
        if (parms.TryGetValue(name, out var p))
        {
            var h = NewHole("Parameter", name, "Parameter");
            if (p.def != null) { h.Values.Add(p.def); h.ValuesNote = "parametre default'u"; }
            // statik çağıranların literal argümanları
            if (CallerArgs.TryGetValue(a.ModuleKey, out var byParam) && byParam.TryGetValue(name, out var vals))
                foreach (var val in vals.Keys.Take(32)) if (!h.Values.Contains(val)) { h.Values.Add(val); h.ValuesNote = h.ValuesNote == "" ? "çağıran literal argümanları" : h.ValuesNote + " + çağıran literal"; }
            return new List<Template> { Template.OfHole(h) };
        }
        return new List<Template> { Template.OfHole(NewHole("Variable", name, "Unknown")) };
    }

    // ---------------- değerlendirme
    static List<Template> One(Template t) => new() { t };
    List<Template> Combine(List<Template> x, List<Template> y)
    {
        var r = new List<Template>();
        foreach (var t1 in x) foreach (var t2 in y) { r.Add(t1.Concat(t2)); if (r.Count >= cfg.MaxDynamicAlternatives) { foreach (var t in r) t.Approximate = true; return r; } }
        return r;
    }
    List<Template> Union(params List<Template>[] xs)
    {
        var r = new List<Template>();
        foreach (var x in xs) foreach (var t in x) if (!r.Any(m => m.Display() == t.Display())) r.Add(t);
        return Cap(r);
    }

    public List<Template> EvalConcat(IList<ValueExpression> strings)
    {
        var cur = One(Template.OfLit(""));
        foreach (var s in strings) cur = Combine(cur, Eval(s));
        return cur;
    }

    public List<Template> Eval(ScalarExpression? e)
    {
        switch (e)
        {
            case null: return One(Template.OfLit(""));
            case StringLiteral sl: return One(Template.OfLit(sl.Value));
            case IntegerLiteral il: return One(Template.OfLit(il.Value));
            case NumericLiteral nl: return One(Template.OfLit(nl.Value));
            case NullLiteral: return One(Template.OfLit(""));
            case Literal l: return One(Template.OfLit(l.Value ?? ""));
            case VariableReference v: return Get(v.Name);
            case GlobalVariableExpression g:
                return NameComparer.Eq(g.Name, "@@SERVERNAME") ? One(Template.OfLit(a.CurServer)) : One(Template.OfHole(NewHole("Global", g.Name, "Value")));
            case ParenthesisExpression p: return Eval(p.Expression);
            case BinaryExpression b when b.BinaryExpressionType == BinaryExpressionType.Add: return Combine(Eval(b.FirstExpression), Eval(b.SecondExpression));
            case BinaryExpression b: return One(Template.OfHole(NewHole("Expression", a.Src(b, 60), "Value")));
            case CastCall c: return Eval(c.Parameter);
            case ConvertCall c: return Eval(c.Parameter);
            case TryCastCall c: return Eval(c.Parameter);
            case TryConvertCall c: return Eval(c.Parameter);
            case CoalesceExpression co: return Union(co.Expressions.Select(Eval).ToArray());
            case NullIfExpression ni: return Eval(ni.FirstExpression);
            case IIfCall ii: return Union(Eval(ii.ThenExpression), Eval(ii.ElseExpression));
            case SearchedCaseExpression sc: return Union(sc.WhenClauses.Select(w => Eval(w.ThenExpression)).Append(Eval(sc.ElseExpression)).ToArray());
            case SimpleCaseExpression sm: return Union(sm.WhenClauses.Select(w => Eval(w.ThenExpression)).Append(Eval(sm.ElseExpression)).ToArray());
            case ScalarSubquery sq:
                {
                    var r = a.BindQuery(sq.QueryExpression, new Scope(null));
                    var src = r.Columns.Count > 0 ? r.Columns[0].Deps.SingleDirect() : null;
                    return One(Template.OfHole(NewHole(src != null ? "Column:" + src : "Subquery", src != null ? src.Obj.Name + "." + src.Column : "subquery", "Column")));
                }
            case LeftFunctionCall lf: return Approx(Eval(lf.Parameters.FirstOrDefault()));
            case RightFunctionCall rf: return Approx(Eval(rf.Parameters.FirstOrDefault()));
            case FunctionCall f: return EvalFunction(f);
            default: return One(Template.OfHole(NewHole("Expression", a.Src(e, 60), "Value")));
        }
    }

    static List<Template> Approx(List<Template> l) { foreach (var t in l) t.Approximate = true; return l; }

    List<Template> EvalFunction(FunctionCall f)
    {
        string n = f.FunctionName.Value.ToUpperInvariant();
        var ps = f.Parameters;
        if (f.CallTarget is MultiPartIdentifierCallTarget)
            return One(Template.OfHole(NewHole("Function:" + a.Src(f, 60), f.FunctionName.Value + "()", "Value")));
        switch (n)
        {
            case "CONCAT": { var cur = One(Template.OfLit("")); foreach (var p in ps) cur = Combine(cur, Eval(p)); return cur; }
            case "CONCAT_WS":
                {
                    var sep = ps.Count > 0 ? Eval(ps[0]) : One(Template.OfLit(""));
                    var cur = One(Template.OfLit(""));
                    for (int i = 1; i < ps.Count; i++) { if (i > 1) cur = Combine(cur, sep); cur = Combine(cur, Eval(ps[i])); }
                    return cur;
                }
            case "QUOTENAME":
                {
                    var inner = Eval(ps.Count > 0 ? ps[0] : null);
                    string q = ps.Count > 1 && ps[1] is StringLiteral ql ? ql.Value : "[";
                    return inner.Select(t =>
                    {
                        var lit = t.LiteralOrNull();
                        if (lit != null) return Template.OfLit(q == "'" ? "'" + lit.Replace("'", "''") + "'" : q == "\"" ? "\"" + lit + "\"" : "[" + lit.Replace("]", "]]") + "]");
                        return t.Map(p => p is Hole h ? new Hole { Id = h.Id, Origin = h.Origin, Display = h.Display, Kind = q == "'" ? "Value" : "Identifier", Bracket = q != "'", Quote = q == "'", Values = h.Values } : p);
                    }).ToList();
                }
            case "REPLACE":
                {
                    var src = Eval(ps.Count > 0 ? ps[0] : null); var from = ps.Count > 1 ? Eval(ps[1]).FirstOrDefault()?.LiteralOrNull() : null; var to = ps.Count > 2 ? Eval(ps[2]).FirstOrDefault()?.LiteralOrNull() : null;
                    if (from == null || to == null || from.Length == 0) return Approx(src);
                    return src.Select(t => t.Map(p => p is Lit l ? new Lit(l.Text.Replace(from, to, StringComparison.OrdinalIgnoreCase)) : p)).ToList();
                }
            case "UPPER": return Eval(ps.FirstOrDefault()).Select(t => t.Map(p => p is Lit l ? new Lit(l.Text.ToUpperInvariant()) : p)).ToList();
            case "LOWER": return Eval(ps.FirstOrDefault()).Select(t => t.Map(p => p is Lit l ? new Lit(l.Text.ToLowerInvariant()) : p)).ToList();
            case "LTRIM": case "RTRIM": case "TRIM": return Eval(ps.LastOrDefault()).Select(t => { var lit = t.LiteralOrNull(); return lit != null ? Template.OfLit(lit.Trim()) : t; }).ToList();
            case "CHAR": case "NCHAR": return ps.Count > 0 && ps[0] is IntegerLiteral il && int.TryParse(il.Value, out var code) ? One(Template.OfLit(((char)code).ToString())) : One(Template.OfLit(" "));
            case "SPACE": return One(Template.OfLit(" "));
            case "REPLICATE": { var x = Eval(ps.FirstOrDefault()).FirstOrDefault()?.LiteralOrNull(); return One(Template.OfLit(x ?? " ")); }
            case "ISNULL": case "COALESCE": return Union(ps.Select(Eval).ToArray());
            case "DB_NAME": return ps.Count == 0 ? One(Template.OfLit(a.CurDb)) : One(Template.OfHole(NewHole("DB_NAME()", "db", "Database")));
            case "SCHEMA_NAME": return ps.Count == 0 ? One(Template.OfLit(a.Mod.Ref.Schema)) : One(Template.OfHole(NewHole("SCHEMA_NAME()", "schema", "Identifier")));
            case "OBJECT_NAME": return ps.Count == 1 && ps[0] is GlobalVariableExpression ge && NameComparer.Eq(ge.Name, "@@PROCID") ? One(Template.OfLit(a.Mod.Ref.Name)) : One(Template.OfHole(NewHole("OBJECT_NAME()", "object", "Identifier")));
            case "OBJECT_SCHEMA_NAME": return ps.Count == 1 && ps[0] is GlobalVariableExpression ge2 && NameComparer.Eq(ge2.Name, "@@PROCID") ? One(Template.OfLit(a.Mod.Ref.Schema)) : One(Template.OfHole(NewHole("OBJECT_SCHEMA_NAME()", "schema", "Identifier")));
            case "FORMATMESSAGE":
                {
                    var fmt = Eval(ps.FirstOrDefault()).FirstOrDefault()?.LiteralOrNull();
                    if (fmt == null) return One(Template.OfHole(NewHole("FORMATMESSAGE", "formatmessage", "Value")));
                    var cur = One(Template.OfLit("")); int argi = 1; int last = 0;
                    foreach (Match m in Regex.Matches(fmt, @"%[sdi]|%%"))
                    {
                        cur = Combine(cur, One(Template.OfLit(fmt[last..m.Index])));
                        cur = m.Value == "%%" ? Combine(cur, One(Template.OfLit("%"))) : Combine(cur, argi < ps.Count ? Eval(ps[argi++]) : One(Template.OfHole(NewHole("FORMATMESSAGE", "arg", "Value"))));
                        last = m.Index + m.Length;
                    }
                    return Combine(cur, One(Template.OfLit(fmt[last..])));
                }
            case "STRING_AGG":
                {
                    var r = Eval(ps.FirstOrDefault());
                    foreach (var t in r) t.Approximate = true;
                    return r;
                }
            case "SUBSTRING": case "STUFF": case "LEFT": case "RIGHT": case "REVERSE": return Approx(Eval(ps.FirstOrDefault()));
            case "STR": case "FORMAT": case "DATENAME": case "DATEPART": case "YEAR": case "MONTH": case "DAY": case "GETDATE": case "SYSDATETIME": case "NEWID": case "LEN": case "DATALENGTH": case "ABS": case "ROUND": case "FLOOR": case "CEILING":
                return One(Template.OfHole(NewHole("Function:" + n, n + "()", "Value")));
            default:
                return One(Template.OfHole(NewHole("Function:" + n, n + "()", "Value")));
        }
    }

    // ---------------- somutlaştırma
    static readonly HashSet<string> IdentAfter = new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "EXEC", "EXECUTE", "MERGE", "INSERT", "DELETE", "TRUNCATE", "USING", "APPLY", "VIEW", "PROC", "PROCEDURE", "BY", "SELECT", "DISTINCT", "OBJECT_ID", "ALTER", "CREATE", "INDEX" };
    static readonly HashSet<string> CondAfter = new(StringComparer.OrdinalIgnoreCase) { "WHERE", "AND", "OR", "ON", "HAVING", "WHEN", "IF", "WHILE" };
    static readonly HashSet<string> StmtEnd = new(StringComparer.OrdinalIgnoreCase) { "BEGIN", "ELSE", "THEN", "END", "GO", "AS" };

    /// <summary>Şablonu parse edilebilir metne çevirir: delikler bağlama göre yer tutucu/dolgu alır.</summary>
    public (string Text, bool Partial) Materialize(Template t)
    {
        var sb = new StringBuilder(); bool partial = false; bool inQ = false, inB = false;
        foreach (var p in t.Parts)
        {
            if (p is Lit l)
            {
                foreach (var ch in l.Text)
                {
                    if (inQ) { if (ch == '\'') inQ = false; }
                    else if (inB) { if (ch == ']') inB = false; }
                    else if (ch == '\'') inQ = true;
                    else if (ch == '[') inB = true;
                    sb.Append(ch);
                }
                continue;
            }
            var h = (Hole)p;
            string before = sb.ToString();
            string ph;
            if (inQ) ph = "LH_V" + h.Id;
            else if (inB) ph = "LH_I" + h.Id;
            else if (h.Bracket) ph = "[LH_I" + h.Id + "]";
            else if (h.Quote) ph = "'LH_V" + h.Id + "'";
            else if (h.Kind is "Question" or "ObjectName" or "Database") ph = "LH_I" + Math.Max(0, h.Id);
            else
            {
                string trimmed = before.TrimEnd();
                bool glued = before.Length > 0 && (char.IsLetterOrDigit(before[^1]) || before[^1] == '_' || before[^1] == '.');
                string lastWord = LastWord(trimmed);
                if (glued) ph = "LH_I" + h.Id;
                else if (IdentAfter.Contains(lastWord) || trimmed.EndsWith(',')) ph = "LH_I" + h.Id;
                else if (CondAfter.Contains(lastWord)) { ph = "1=1"; partial = true; }
                else if (NameComparer.Eq(lastWord, "SET")) { ph = "LH_I" + h.Id + " = 0"; partial = true; }
                else if (trimmed.Length == 0 || trimmed.EndsWith(';') || StmtEnd.Contains(lastWord)) { ph = ""; partial = true; }
                else if (trimmed.EndsWith('=') || trimmed.EndsWith('<') || trimmed.EndsWith('>') || trimmed.EndsWith('(') || trimmed.EndsWith('+') || trimmed.EndsWith('-') || trimmed.EndsWith('*') || trimmed.EndsWith('/')
                         || NameComparer.Eq(lastWord, "TOP") || NameComparer.Eq(lastWord, "IN") || NameComparer.Eq(lastWord, "LIKE") || NameComparer.Eq(lastWord, "VALUES") || NameComparer.Eq(lastWord, "BETWEEN"))
                    ph = h.Kind == "Identifier" ? "LH_I" + h.Id : "0";
                else if (h.Kind == "Identifier" || h.Kind == "Column" || h.Kind == "Cursor" || h.Kind == "Parameter") ph = "LH_I" + h.Id;
                else { ph = "0"; }
            }
            if (h.Id > 0) holes[h.Id].Placeholder = ph;
            sb.Append(ph);
        }
        return (sb.ToString(), partial || t.Approximate);
    }

    static string LastWord(string s)
    {
        int i = s.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
        int end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '@' || s[i] == '#')) i--;
        return s[(i + 1)..end];
    }

    /// <summary>Delikleri bilinen değerlerle (parametre default'u, çağıran literal'leri) doldurup alternatifleri çoğaltır.</summary>
    public List<Template> ExpandValues(List<Template> templates)
    {
        var result = new List<Template>();
        foreach (var t in templates)
        {
            var work = new List<Template> { t };
            foreach (var h in t.Parts.OfType<Hole>().Where(x => x.Values.Count > 0).DistinctBy(x => x.Id).ToList())
            {
                var next = new List<Template>();
                foreach (var w in work)
                    foreach (var v in h.Values)
                    {
                        next.Add(w.Map(p => p is Hole ph && ph.Id == h.Id ? new Lit(v) : p));
                        if (next.Count >= cfg.MaxDynamicAlternatives) break;
                    }
                if (next.Count > 0) work = next;
                if (work.Count >= cfg.MaxDynamicAlternatives) break;
            }
            result.AddRange(work);
            if (result.Count >= cfg.MaxDynamicAlternatives) break;
        }
        return result.Count == 0 ? templates : result;
    }
}

sealed partial class ModuleAnalyzer
{
    public string CurServer => curServer;
    public string CurDb => curDb;

    static readonly Regex SplitRx = new(@"(?im)(?:;|^)(?=\s*(?:SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|TRUNCATE|WITH|CREATE|ALTER|DROP|IF|DECLARE|SET|WHILE|BEGIN|USE|BULK)\b)", RegexOptions.Compiled);
    static readonly Regex ObjRx = new(@"\b(FROM|JOIN|INTO|UPDATE|TRUNCATE\s+TABLE|MERGE\s+INTO|MERGE|DELETE\s+FROM|DELETE|EXEC|EXECUTE|CREATE\s+TABLE|DROP\s+TABLE)\s+(?!SELECT\b|OPENQUERY\b|OPENROWSET\b|\()([\w\[\]\.#@]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    ExecResult RunDynamic(List<Template> templates, string sinkKind, Dictionary<string, Deps>? innerVars, string? linked)
    {
        var site = NewDynamicRow(sinkKind, templates);
        var er = new ExecResult { Dynamic = true };
        if (dynDepth >= 2) { site.Status = "Unresolved"; site.ParseError = "iç içe dinamik SQL derinliği aşıldı"; return er; }
        string saveServer = curServer, saveDb = curDb, saveNote = provNote;
        if (linked != null)
        {
            var (srvName, scanned) = cat.ResolveLinked(curServer, linked);
            curServer = srvName;
            if (srv.Linked.TryGetValue(linked, out var ls) && !string.IsNullOrEmpty(ls.Catalog)) curDb = ls.Catalog;
            else curDb = cat.FindServer(srvName)?.Dbs.Keys.FirstOrDefault() ?? curDb;
            provNote = Join(provNote, "EXEC AT " + linked + (scanned ? "" : " (taranmamış)"));
        }
        int before = Result.ColumnRows.Count + Result.ObjectRows.Count;
        string status = "Unresolved"; var errors = new List<string>();
        var alts = dyn.ExpandValues(templates);
        int alternativesRun = 0;
        int baseStmtNo = stmtNo;   // her alternatif aynı ifade numarasından başlar: aynı kenar tekrar üretilmez (colKeys/objKeys)
        foreach (var alt in alts.Take(cfg.MaxDynamicAlternatives))
        {
            stmtNo = baseStmtNo;
            var (text, partial) = dyn.Materialize(alt);
            if (string.IsNullOrWhiteSpace(text)) continue;
            alternativesRun++;
            string note = $"dinamik site @L{line} {sinkKind}";
            var pr = ParserLadder.Parse(text, true, srv.Major, db.Compat);
            if (pr.Fragment != null)
            {
                var rs = AnalyzeDynamicBatch(pr, partial ? Provenance.DynamicPartial : Provenance.DynamicStatic, note, innerVars, out var innerSnap);
                er.ResultSet ??= rs; er.InnerVars ??= innerSnap;
                status = Better(status, partial ? "Partial" : "Exact");
                if (pr.Holes.Count > 0) errors.Add("delik: " + string.Join(" | ", pr.Holes.Take(2)));
                continue;
            }
            errors.Add(pr.ErrorText);
            // yedek 1: parçalara böl
            bool any = false;
            foreach (var piece in SplitRx.Split(text).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var pp = ParserLadder.Parse(piece, true, srv.Major, db.Compat);
                if (pp.Fragment != null) { AnalyzeDynamicBatch(pp, Provenance.DynamicPartial, note + " (parça)", innerVars, out _); any = true; }
                else any |= RegexObjects(piece, note);
            }
            if (any) status = Better(status, "Partial");
        }
        site.EdgesProduced = Result.ColumnRows.Count + Result.ObjectRows.Count - before;
        site.Status = status; site.ParseError = string.Join(" || ", errors.Distinct().Take(3));
        if (alternativesRun == 0)
        {
            var only = templates.Count == 1 && templates[0].Parts.Count == 1 && templates[0].Parts[0] is Hole h0 ? h0 : null;
            if (only != null && only.Kind == "Parameter") { site.Status = "Executor"; site.ParseError = "parametre olduğu gibi çalıştırılıyor; değer çağıranlardan gelir (literal argüman ön geçişi)"; }
            else if (only != null && only.Origin == "Variable") { site.ParseError = "değişken ataması izlenemedi (SET/SELECT dışı bir yol)"; Unresolved("DynamicVarUntracked", only.Display); }
            else if (only != null && only.Source != null) site.ParseError = "değer tabloda: " + only.Source + (cfg.ConfigTableLookup ? " (config-tables ile değer bulunamadı)" : " (--config-tables ile okunabilir)");
            else site.ParseError = Join(site.ParseError, "şablon boş (tamamen bilinmeyen değişken)");
        }
        if (status != "Unresolved" && !suppressEmit) Result.DynamicResolved++;
        curServer = saveServer; curDb = saveDb; provNote = saveNote;
        return er;
    }

    static string Better(string a, string b) => a == "Exact" || b == "Exact" ? "Exact" : a == "Partial" || b == "Partial" ? "Partial" : "Unresolved";

    /// <summary>Regex ile yalnız nesne düzeyi çıkarım (parse edilemeyen parçalar için).</summary>
    bool RegexObjects(string text, string note)
    {
        bool any = false;
        var saveProv = prov; var saveNote = provNote; prov = Provenance.DynamicPartial; provNote = Join(provNote, note + " (regex)");
        foreach (Match m in ObjRx.Matches(text))
        {
            string kw = Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", " "); string name = m.Groups[2].Value;
            if (name.StartsWith("LH_", StringComparison.Ordinal) || name.StartsWith('@')) continue;
            string action = kw switch { "FROM" or "JOIN" => "Reads", "EXEC" or "EXECUTE" => "Calls", "DELETE" or "DELETE FROM" => "Delete", "UPDATE" => "Update", "INTO" or "MERGE" or "MERGE INTO" => "Insert", "TRUNCATE TABLE" => "Truncate", "CREATE TABLE" => "Create", "DROP TABLE" => "Drop", _ => "Reads" };
            var parts = SplitName(name);
            ObjRef r;
            if (parts[^1].StartsWith('#')) r = temps.GetOrAmbient(parts[^1], OwnerRef).Ref;
            else (r, _, _) = ResolveName(parts.Length > 3 ? parts[^4] : null, parts.Length > 2 ? parts[^3] : null, parts.Length > 1 ? parts[^2] : null, parts[^1], action != "Reads");
            EmitObj(r, action, Confidence.Low, "regex çıkarımı");
            any = true;
        }
        prov = saveProv; provNote = saveNote;
        return any;
    }

    List<OutCol>? AnalyzeDynamicBatch(ParseResult pr, Provenance p, string note, Dictionary<string, Deps>? innerVars, out Dictionary<string, Deps>? innerSnapshot)
    {
        innerSnapshot = null;
        if (pr.Fragment is not TSqlScript script) return null;
        if (dynDepth == 0) outerLine = line;
        dynDepth++;
        string saveText = curText; var saveProv = prov; var saveNote = provNote; string saveDb = curDb; var saveType = stmtType; int saveLine = line;
        var saveVars = new Dictionary<string, Deps>(vars, NameComparer.Instance);
        var tempsBefore = new HashSet<string>(temps.Names(), NameComparer.Instance);
        var saveRs = lastResultSet; lastResultSet = null;
        try
        {
            curText = pr.Text; prov = p; provNote = Join(saveNote, note);
            if (innerVars != null) foreach (var kv in innerVars) vars[kv.Key] = kv.Value;
            foreach (var batch in script.Batches) foreach (var st in batch.Statements) Stmt(st);
            innerSnapshot = new Dictionary<string, Deps>(vars, NameComparer.Instance);
            return lastResultSet;
        }
        finally
        {
            dynDepth--;
            curText = saveText; prov = saveProv; provNote = saveNote; curDb = saveDb; stmtType = saveType; line = saveLine;
            vars.Clear(); foreach (var kv in saveVars) vars[kv.Key] = kv.Value;
            foreach (var n in temps.Names()) if (!tempsBefore.Contains(n)) temps.Drop(n);   // iç batch'te yaratılan temp'ler batch sonunda düşer
            lastResultSet = saveRs ?? lastResultSet;
        }
    }
}

#endregion

// ============================================================================
#region 7. Orkestrasyon ve son geçişler
// ============================================================================

sealed class ModuleStats
{
    public string Server = "", Database = ""; public string Status = ""; public AnalysisResult? Result;
}

sealed class LineageData
{
    public List<ColumnLineageRow> ColumnRows = new();
    public List<ObjectLineageRow> ObjectRows = new();
    public List<CollapsedRow> CollapsedRows = new();
    public List<ModuleRow> ModuleRows = new();
    public List<DynamicSqlRow> DynamicRows = new();
    public List<UnresolvedRow> UnresolvedRows = new();
    public List<SummaryRow> SummaryRows = new();
    public List<StatementRow> StatementRows = new();
    public List<CatalogDepRow> CatalogDepRows = new();
    public List<IssueSummaryRow> IssueSummaryRows = new();
    public List<QueryRow> QueryRows = new();
}

sealed class LineageRun
{
    readonly LineageConfig cfg; readonly string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    readonly LineageData data = new();
    readonly object gate = new();
    Catalog cat = null!;
    readonly List<ModuleStats> stats = new();
    readonly Dictionary<string, List<string>> resultSetNames = new();   // modül Fqn key → RS1 kolon adları
    Incremental? incremental;
    readonly Dictionary<string, string> engineSignatures = new(NameComparer.Instance);   // "server|db" → imza

    public LineageRun(LineageConfig cfg) { this.cfg = cfg; }

    public async Task ExecuteAsync()
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"RunId {runId}; paralellik {cfg.Parallelism}");
        cat = await CatalogLoader.LoadAsync(cfg);
        foreach (var s in cat.Servers) foreach (var db in s.Dbs.Values) engineSignatures[s.Name + "|" + db.Name] = Incremental.Signature(cfg, db);
        var modules = CollectModules();
        Log.Info($"Toplam {modules.Count} modül analiz edilecek");
        if (cfg.Incremental)
        {
            if (string.IsNullOrWhiteSpace(cfg.Output.SqlBulkTarget)) Log.Warn("--incremental için --sql-target gerekli; tam koşu yapılıyor");
            else { incremental = Incremental.Load(cfg, engineSignatures); if (incremental == null) Log.Warn("Önceki koşu bulunamadı; tam koşu"); }
        }
        PrePassCallerArgs(modules);
        AnalyzeAll(modules);
        Log.Info($"Analiz bitti: {data.ColumnRows.Count} kolon satırı, {data.ObjectRows.Count} nesne satırı, {sw.Elapsed.TotalSeconds:F0} s" + (incremental != null ? $" ({data.ModuleRows.Count(m => m.Reused)} modül önceki koşudan)" : ""));
        ApplyObjectPolicy();
        CatalogCrossCheck();
        Interprocedural();
        Collapse();
        BuildIssueSummary();
        if (cfg.Query != null && cfg.Query.Node != "") data.QueryRows = LineageQuery.Run(cfg, runId, data, cat);
        BuildSummary((int)sw.ElapsedMilliseconds);
        Output.Write(cfg, runId, data);
        if (cfg.Graph != null && cfg.Graph.Node != "") GraphExport.Write(cfg, runId, data, cat);
        Log.Info($"Bitti: {sw.Elapsed.TotalSeconds:F0} s");
    }

    // ---------------- modül listesi
    List<(ServerCatalog srv, DbCatalog db, ObjInfo mod)> CollectModules()
    {
        var list = new List<(ServerCatalog, DbCatalog, ObjInfo)>();
        int excluded = 0;
        foreach (var s in cat.Servers)
        {
            foreach (var db in s.Dbs.Values) foreach (var m in db.Modules)
            {
                if (cfg.ExcludeModuleNameContains.Any(x => m.Ref.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) { excluded++; continue; }
                list.Add((s, db, m));
            }
            foreach (var js in s.JobSteps.Where(j => NameComparer.Eq(j.Subsystem, "TSQL")))
            {
                if (!s.Dbs.TryGetValue(js.DatabaseName, out var db)) db = new DbCatalog { Server = s.Name, Name = js.DatabaseName, Compat = 150 };
                var o = new ObjInfo { TypeCode = "JOB", Ref = new ObjRef(s.Name, db.Name, "job", $"{js.JobName}#{js.StepId}", ObjType.JobStep), Definition = js.Command, DefaultSchema = "dbo" };
                list.Add((s, db, o));
            }
        }
        if (excluded > 0) Log.Info($"{excluded} modül ad filtresiyle (ExcludeModuleNameContains) dışlandı");
        return list;
    }

    // ---------------- ön geçiş: EXEC proc literal argümanları (dinamik deliklerin doldurulması için)
    void PrePassCallerArgs(List<(ServerCatalog srv, DbCatalog db, ObjInfo mod)> modules)
    {
        var sw = Stopwatch.StartNew();
        int found = 0, created = 0;
        Parallel.ForEach(modules.Where(m => m.mod.Definition != null && m.mod.TypeCode is "P" or "TR" or "JOB" or "FILE"), new ParallelOptions { MaxDegreeOfParallelism = cfg.Parallelism }, m =>
        {
            try
            {
                var pr = ParserLadder.Parse(m.mod.Definition!, m.mod.QuotedIdentifier, m.srv.Major, m.db.Compat);
                if (pr.Fragment == null) return;
                var v = new CallerArgVisitor();
                pr.Fragment.Accept(v);
                if (v.Calls.Count == 0 && v.Created.Count == 0) return;
                var resolver = new ModuleAnalyzer(cat, cfg, runId, m.srv, m.db, m.mod) { SuppressEmit = true };
                // kod tarafından yaratılan kalıcı tablolar: DB overlay'ine (diğer modüllerden okunabilsin)
                foreach (var (son, cols) in v.Created)
                {
                    string dbName = son.DatabaseIdentifier?.Value.NullIfEmpty() ?? m.db.Name;
                    string srvName = son.ServerIdentifier?.Value.NullIfEmpty() == null ? m.srv.Name : cat.ResolveLinked(m.srv.Name, son.ServerIdentifier!.Value).serverName;
                    var tdb = cat.FindDb(srvName, dbName);
                    if (tdb == null) continue;
                    string schema = son.SchemaIdentifier?.Value.NullIfEmpty() ?? (m.mod.TypeCode is "JOB" or "FILE" ? m.mod.DefaultSchema : m.mod.Ref.Schema);
                    string name = son.BaseIdentifier.Value;
                    if (tdb.Find(schema, name) != null) continue;
                    var oi = new ObjInfo { TypeCode = "U", Ref = new ObjRef(tdb.Server, tdb.Name, schema, name, ObjType.Table), Columns = cols.Select((c, i) => new ColInfo { Name = c, Ordinal = i + 1 }).ToList() };
                    if (tdb.Overlay.TryAdd(schema + "." + name, oi)) Interlocked.Increment(ref created);
                }
                foreach (var (son, args) in v.Calls)
                {
                    var (r, info, _) = resolver.ResolveObject(son);
                    if (info == null) continue;
                    var byParam = StringFlow.CallerArgs.GetOrAdd(r.With(info.Ref.Type).Key, _ => new(NameComparer.Instance));
                    int i = 0;
                    foreach (var (pname, lit) in args)
                    {
                        i++;
                        string name = pname ?? (i - 1 < info.Params.Count ? info.Params[i - 1].Name : "");
                        if (name == "") continue;
                        byParam.GetOrAdd(name, _ => new()).TryAdd(lit, 0);
                        Interlocked.Increment(ref found);
                    }
                }
            }
            catch (Exception ex) { Log.Debug($"pre-pass {m.mod.Ref}: {ex.Message}"); }
        });
        Log.Info($"Ön geçiş: {found} çağıran literal argümanı, {created} kod tarafından yaratılan kalıcı tablo (overlay), {sw.ElapsedMilliseconds} ms");
    }

    sealed class CallerArgVisitor : TSqlConcreteFragmentVisitor
    {
        public List<(SchemaObjectName, List<(string? name, string lit)>)> Calls = new();
        public List<(SchemaObjectName name, List<string> cols)> Created = new();
        public override void Visit(SelectStatement node) { if (node.Into != null && !node.Into.BaseIdentifier.Value.StartsWith('#')) Created.Add((node.Into, new())); }
        public override void Visit(CreateTableStatement node)
        {
            if (node.SchemaObjectName.BaseIdentifier.Value.StartsWith('#')) return;
            Created.Add((node.SchemaObjectName, node.Definition?.ColumnDefinitions.Select(c => c.ColumnIdentifier.Value).ToList() ?? new()));
        }
        public override void Visit(ExecuteStatement node)
        {
            if (node.ExecuteSpecification.ExecutableEntity is not ExecutableProcedureReference epr || epr.ProcedureReference.ProcedureReference == null) return;
            var args = new List<(string?, string)>();
            foreach (var p in epr.Parameters)
                if (p.ParameterValue is StringLiteral sl) args.Add((p.Variable?.Name, sl.Value));
                else if (p.ParameterValue is IntegerLiteral il) args.Add((p.Variable?.Name, il.Value));
            if (args.Count > 0) Calls.Add((epr.ProcedureReference.ProcedureReference.Name, args));
        }
    }

    // ---------------- paralel analiz (büyük stack'li işçi thread'ler)
    void AnalyzeAll(List<(ServerCatalog srv, DbCatalog db, ObjInfo mod)> modules)
    {
        var queue = new ConcurrentQueue<(ServerCatalog, DbCatalog, ObjInfo)>(modules);
        int done = 0, total = modules.Count;
        var workers = new List<Thread>();
        for (int w = 0; w < Math.Max(1, cfg.Parallelism); w++)
        {
            var t = new Thread(() =>
            {
                while (queue.TryDequeue(out var item))
                {
                    AnalyzeOne(item.Item1, item.Item2, item.Item3);
                    int d = Interlocked.Increment(ref done);
                    if (d % 500 == 0 || d == total) Log.Info($"  {d}/{total} modül");
                }
            }, 64 * 1024 * 1024) { IsBackground = true, Name = "lineage-" + w };
            workers.Add(t); t.Start();
        }
        foreach (var t in workers) t.Join();
    }

    void AnalyzeOne(ServerCatalog srv, DbCatalog db, ObjInfo mod)
    {
        var sw = Stopwatch.StartNew();
        var row = new ModuleRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, ModuleType = mod.ModuleTypeName, ModifyDate = mod.ModifyDate, DefinitionLength = mod.Definition?.Length ?? 0, DefinitionHash = Incremental.Hash(mod.Definition) };
        var st = new ModuleStats { Server = mod.Ref.Server, Database = mod.Ref.Database };
        AnalysisResult? res = null;
        if (incremental != null && incremental.TryReuse(mod, row, out var reused))
        {
            row.Reused = true; row.ParseStatus = reused.ParseStatus; row.ParserUsed = reused.ParserUsed; row.Errors = reused.Errors; row.Holes = reused.Holes;
            row.Statements = reused.Statements; row.DynamicSites = reused.DynamicSites; row.ColumnEdges = reused.ColumnEdges; row.ObjectEdges = reused.ObjectEdges; row.UnresolvedNames = reused.UnresolvedNames;
            st.Status = row.ParseStatus; st.Result = incremental.ResultFor(mod.Ref, runId);
            lock (gate)
            {
                data.ColumnRows.AddRange(st.Result.ColumnRows); data.ObjectRows.AddRange(st.Result.ObjectRows); data.DynamicRows.AddRange(st.Result.DynamicRows); data.UnresolvedRows.AddRange(st.Result.UnresolvedRows); data.StatementRows.AddRange(st.Result.StatementRows);
                if (st.Result.ResultSetNames.Count > 0) resultSetNames[FqnKey(mod.Ref)] = st.Result.ResultSetNames[0];
                data.ModuleRows.Add(row); stats.Add(st);
            }
            return;
        }
        try
        {
            if (mod.Definition != null && mod.Definition.Length > cfg.MaxDefinitionChars)
            {
                row.ParseStatus = "DefinitionSizeLimit"; row.Errors = $"tanım {mod.Definition.Length:N0} karakter > MaxDefinitionChars {cfg.MaxDefinitionChars:N0}";
                res = CatalogFallback(srv, db, mod, row.ParseStatus);
            }
            else if (mod.Definition == null)
            {
                row.ParseStatus = mod.TypeCode is "PC" or "FS" or "FT" ? "NoDefinition(CLR)" : "NoDefinition(Encrypted)";
                res = CatalogFallback(srv, db, mod, row.ParseStatus);
            }
            else
            {
                var pr = ParserLadder.Parse(mod.Definition, mod.QuotedIdentifier, srv.Major, db.Compat);
                row.ParseStatus = pr.Status; row.ParserUsed = "TSql" + pr.ParserUsed; row.Errors = pr.ErrorText; row.Holes = string.Join(" | ", pr.Holes);
                if (pr.Fragment == null) res = CatalogFallback(srv, db, mod, "Quarantined");
                else
                {
                    var an = new ModuleAnalyzer(cat, cfg, runId, srv, db, mod);
                    try { an.Analyze(pr); }
                    catch (TimeoutException tex) { row.ParseStatus = "Timeout"; row.Errors = Join(row.Errors, tex.Message); }
                    res = an.Result;
                }
            }
        }
        catch (Exception ex)
        {
            row.ParseStatus = "Error"; row.Errors = Join(row.Errors, ex.GetType().Name + ": " + ex.Message);
            Log.Warn($"{mod.Ref}: {ex.GetType().Name}: {ex.Message}");
        }
        row.ElapsedMs = (int)sw.ElapsedMilliseconds;
        st.Status = row.ParseStatus;
        if (res != null)
        {
            row.Statements = res.Statements; row.DynamicSites = res.DynamicSites; row.ColumnEdges = res.ColumnRows.Count; row.ObjectEdges = res.ObjectRows.Count; row.UnresolvedNames = res.UnresolvedRows.Count;
            st.Result = res;
            lock (gate)
            {
                data.ColumnRows.AddRange(res.ColumnRows); data.ObjectRows.AddRange(res.ObjectRows); data.DynamicRows.AddRange(res.DynamicRows); data.UnresolvedRows.AddRange(res.UnresolvedRows); data.StatementRows.AddRange(res.StatementRows);
                if (res.ResultSetNames.Count > 0) resultSetNames[FqnKey(mod.Ref)] = res.ResultSetNames[0];
            }
        }
        lock (gate) { data.ModuleRows.Add(row); stats.Add(st); }
    }

    static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "; " + b;
    static string FqnKey(ObjRef r) => NameComparer.Norm($"{r.Server}.{r.Database}.{r.Schema}.{r.Name}").ToUpperInvariant();
    static string FqnKey(string server, string db, string schema, string name) => NameComparer.Norm($"{server}.{db}.{schema}.{name}").ToUpperInvariant();

    /// <summary>Şifreli/CLR/karantina modüller: sys.sql_expression_dependencies'ten nesne düzeyi satırlar.</summary>
    AnalysisResult CatalogFallback(ServerCatalog srv, DbCatalog db, ObjInfo mod, string reason)
    {
        var res = new AnalysisResult();
        foreach (var d in db.Deps.Where(x => x.ReferencingId == mod.ObjectId && mod.ObjectId != 0))
        {
            res.ObjectRows.Add(new ObjectLineageRow
            {
                RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, ModuleType = mod.ModuleTypeName,
                StatementNo = 0, StatementType = "CATALOG", Action = "References",
                ObjectServer = d.Server ?? mod.Ref.Server, ObjectDatabase = d.Database ?? mod.Ref.Database, ObjectSchema = d.Schema ?? "", ObjectName = d.Name,
                ObjectType = db.Find(d.Schema ?? "dbo", d.Name)?.Ref.Type.ToString() ?? "Unresolved", Provenance = "Catalog", Confidence = "Medium", Note = reason
            });
        }
        return res;
    }

    // ---------------- politika: eski/silinecek nesnelere giden satırları düş
    void ApplyObjectPolicy()
    {
        if (cfg.ExcludeObjectNameContains.Count == 0) return;
        bool Ex(string n) => cfg.ExcludeObjectNameContains.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
        int c1 = data.ColumnRows.RemoveAll(r => Ex(r.SourceObject) || Ex(r.TargetObject));
        int c2 = data.ObjectRows.RemoveAll(r => Ex(r.ObjectName));
        Log.Info($"Politika (ExcludeObjectNameContains): {c1} kolon, {c2} nesne satırı düşüldü");
    }

    void BuildIssueSummary()
    {
        data.IssueSummaryRows = data.UnresolvedRows.GroupBy(u => (u.Server, u.Database, u.ModuleSchema, u.ModuleName))
            .Select(g => new IssueSummaryRow
            {
                RunId = runId, Server = g.Key.Server, Database = g.Key.Database, ModuleSchema = g.Key.ModuleSchema, ModuleName = g.Key.ModuleName,
                ParseStatus = data.ModuleRows.FirstOrDefault(m => NameComparer.Eq(m.Server, g.Key.Server) && NameComparer.Eq(m.Database, g.Key.Database) && NameComparer.Eq(m.ModuleSchema, g.Key.ModuleSchema) && NameComparer.Eq(m.ModuleName, g.Key.ModuleName))?.ParseStatus ?? "",
                Issues = g.Count(), Errors = g.Count(x => x.Severity == "error"), Warnings = g.Count(x => x.Severity == "warning"),
                TopKinds = string.Join("; ", g.GroupBy(x => x.Kind).OrderByDescending(k => k.Count()).Take(5).Select(k => $"{k.Key}={k.Count()}"))
            }).OrderByDescending(x => x.Errors).ThenByDescending(x => x.Issues).Take(5000).ToList();
    }

    // ---------------- katalog çapraz kontrolü: SQL Server'ın gördüğü ama bizim görmediğimiz referanslar
    void CatalogCrossCheck()
    {
        var seen = new HashSet<string>();
        foreach (var r in data.ObjectRows) seen.Add(FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName) + "→" + NameComparer.Norm(r.ObjectName).ToUpperInvariant());
        int missed = 0;
        foreach (var s in cat.Servers) foreach (var db in s.Dbs.Values)
            foreach (var d in db.Deps)
            {
                if (!db.ById.TryGetValue(d.ReferencingId, out var mod) || !mod.IsModule) continue;
                string key = FqnKey(mod.Ref) + "→" + NameComparer.Norm(d.Name).ToUpperInvariant();
                bool found = seen.Contains(key);
                data.CatalogDepRows.Add(new CatalogDepRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, ReferencedServer = d.Server ?? mod.Ref.Server, ReferencedDatabase = d.Database ?? db.Name, ReferencedSchema = d.Schema ?? "", ReferencedName = d.Name, FoundInAnalysis = found });
                if (mod.Definition == null || d.Server != null) continue;
                if (found) continue;
                // temp/CTE/alias adları katalogda referans olarak görünmez; buraya düşenler gerçek eksiklerdir
                missed++;
                data.UnresolvedRows.Add(new UnresolvedRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, Kind = "CatalogDepMissed", Severity = "warning", Name = $"{d.Database ?? db.Name}.{d.Schema ?? "?"}.{d.Name}", Note = "sys.sql_expression_dependencies'te var, analizde yok" });
            }
        Log.Info($"Katalog çapraz kontrolü: {missed} kaçırılmış referans (Unresolved sayfasında CatalogDepMissed)");
    }

    // ---------------- prosedürler arası (nesne düzeyi) + trigger yayılımı
    void Interprocedural()
    {
        var sw = Stopwatch.StartNew();
        static bool IsEffect(string a) => a is "Reads" or "Insert" or "Update" or "Delete" or "Merge" or "Truncate" or "Create" or "Drop" or "Writes" or "References";
        static bool Persistent(string t) => t is "Table" or "View" or "External" or "Unresolved" or "System" or "File" or "Sequence" or "Synonym";
        var own = new Dictionary<string, List<ObjectLineageRow>>();
        var calls = new Dictionary<string, List<ObjectLineageRow>>();
        var modInfo = new Dictionary<string, ObjectLineageRow>();
        foreach (var r in data.ObjectRows)
        {
            if (r.Provenance is "Interprocedural" or "Trigger") continue;
            string k = FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            modInfo.TryAdd(k, r);
            if (r.Action == "Calls" && r.ObjectType is "Procedure" or "ClrModule" or "Unresolved" or "External") (calls.TryGetValue(k, out var l) ? l : calls[k] = new()).Add(r);
            else if (IsEffect(r.Action) && Persistent(r.ObjectType)) (own.TryGetValue(k, out var l2) ? l2 : own[k] = new()).Add(r);
        }
        // trigger'lar: tablo Fqn → (trigger modül key, olaylar)
        var triggers = new Dictionary<string, List<(string key, string events, ObjInfo trg)>>();
        foreach (var s in cat.Servers) foreach (var db in s.Dbs.Values) foreach (var t in db.Modules.Where(m => m.TypeCode == "TR" && !m.IsDisabled && m.ParentObjectId != null))
            if (db.ById.TryGetValue(t.ParentObjectId!.Value, out var parent))
                (triggers.TryGetValue(FqnKey(parent.Ref), out var l) ? l : triggers[FqnKey(parent.Ref)] = new()).Add((FqnKey(t.Ref), t.TriggerEvents, t));
        int added = 0;
        var newRows = new List<ObjectLineageRow>();
        foreach (var start in own.Keys.Union(calls.Keys).ToList())
        {
            var visited = new HashSet<string> { start };
            var q = new Queue<(string key, int depth, string path, ObjectLineageRow via)>();
            void Enqueue(string key, int depth, string path, ObjectLineageRow via) { if (visited.Add(key)) q.Enqueue((key, depth, path, via)); }
            void Expand(string key, int depth, string path, ObjectLineageRow via)
            {
                if (depth >= cfg.MaxCallDepth) return;
                if (calls.TryGetValue(key, out var cl))
                    foreach (var c in cl) Enqueue(FqnKey(c.ObjectServer, c.ObjectDatabase, c.ObjectSchema, c.ObjectName), depth + 1, path + " > " + c.ObjectSchema + "." + c.ObjectName, via ?? c);
                if (own.TryGetValue(key, out var ol))
                    foreach (var e in ol.Where(x => x.Action is "Insert" or "Update" or "Delete" or "Merge"))
                        if (triggers.TryGetValue(FqnKey(e.ObjectServer, e.ObjectDatabase, e.ObjectSchema, e.ObjectName), out var tl))
                            foreach (var (tk, ev, trg) in tl)
                                if (ev.Contains(e.Action == "Merge" ? "INSERT" : e.Action.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) || ev == "")
                                    Enqueue(tk, depth + 1, path + " > [trg]" + trg.Ref.Schema + "." + trg.Ref.Name, via ?? e);
            }
            var startRow = modInfo[start];
            Expand(start, 0, startRow.ModuleSchema + "." + startRow.ModuleName, null!);
            var emitted = new HashSet<string>();
            while (q.Count > 0)
            {
                var (key, depth, path, via) = q.Dequeue();
                if (own.TryGetValue(key, out var effects))
                    foreach (var e in effects)
                    {
                        string dk = e.Action + "|" + FqnKey(e.ObjectServer, e.ObjectDatabase, e.ObjectSchema, e.ObjectName);
                        if (!emitted.Add(dk)) continue;
                        newRows.Add(new ObjectLineageRow
                        {
                            RunId = runId, Server = startRow.Server, Database = startRow.Database, ModuleSchema = startRow.ModuleSchema, ModuleName = startRow.ModuleName, ModuleType = startRow.ModuleType,
                            StatementNo = via?.StatementNo ?? 0, StatementType = via?.StatementType ?? "", Line = via?.Line ?? 0, Action = e.Action,
                            ObjectServer = e.ObjectServer, ObjectDatabase = e.ObjectDatabase, ObjectSchema = e.ObjectSchema, ObjectName = e.ObjectName, ObjectType = e.ObjectType,
                            Provenance = path.Contains("[trg]") ? "Trigger" : "Interprocedural", Confidence = e.Confidence == "Exact" ? "High" : e.Confidence, Note = path
                        });
                        added++;
                    }
                Expand(key, depth, path, via);
            }
        }
        data.ObjectRows.AddRange(newRows);
        Log.Info($"Prosedürler arası yayılım: {added} satır, {sw.ElapsedMilliseconds} ms");
    }

    // ---------------- kalıcıdan kalıcıya indirgeme (temp/@tablo/view/fonksiyon/proc sonuç kümesi üzerinden)
    sealed class Edge { public string SrcServer = "", SrcDb = "", SrcSchema = "", SrcObj = "", SrcType = "", SrcCol = ""; public FlowKind Kind; public string Conf = ""; public string Module = ""; }

    static bool Transient(string t) => t is "TempTable" or "TableVariable" or "Tvp" or "View" or "ScalarFunction" or "InlineTvf" or "MsTvf" or "Procedure";
    static string NodeKey(string server, string db, string schema, string obj, string col) => FqnKey(server, db, schema, obj) + "|" + NameComparer.Norm(col).ToUpperInvariant();

    void Collapse()
    {
        var sw = Stopwatch.StartNew();
        // hedef düğüm → gelen kenarlar (modül-yerel düğümler için modül anahtarıyla)
        var incoming = new Dictionary<string, List<Edge>>();
        foreach (var r in data.ColumnRows)
        {
            if (r.FlowKind == "Indirect") continue;
            string modKey = FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            string tk = NodeKey(r.TargetServer, r.TargetDatabase, r.TargetSchema, r.TargetObject, r.TargetColumn);
            if (r.TargetObjectType is "TempTable" or "TableVariable" or "Tvp") tk = modKey + "#" + tk;
            (incoming.TryGetValue(tk, out var l) ? l : incoming[tk] = new()).Add(new Edge { SrcServer = r.SourceServer, SrcDb = r.SourceDatabase, SrcSchema = r.SourceSchema, SrcObj = r.SourceObject, SrcType = r.SourceObjectType, SrcCol = r.SourceColumn, Kind = Enum.Parse<FlowKind>(r.FlowKind), Conf = r.Confidence, Module = modKey });
        }
        // çağrı grafı: paylaşılan (#temp) tablolar için çağıran ↔ çağrılan geçişi
        var callees = new Dictionary<string, List<(string key, string schemaDot, string server, string db)>>();
        var callers = new Dictionary<string, List<(string key, string schemaDot, string server, string db)>>();
        foreach (var r in data.ObjectRows.Where(x => x.Action == "Calls" && x.Provenance is "Static" or "DynamicStatic"))
        {
            string caller = FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName), callee = FqnKey(r.ObjectServer, r.ObjectDatabase, r.ObjectSchema, r.ObjectName);
            (callees.TryGetValue(caller, out var l1) ? l1 : callees[caller] = new()).Add((callee, r.ObjectSchema + "." + r.ObjectName, r.ObjectServer, r.ObjectDatabase));
            (callers.TryGetValue(callee, out var l2) ? l2 : callers[callee] = new()).Add((caller, r.ModuleSchema + "." + r.ModuleName, r.Server, r.Database));
        }
        var outRows = new List<CollapsedRow>();
        var seen = new HashSet<string>();
        foreach (var r in data.ColumnRows)
        {
            if (r.FlowKind == "Indirect") continue;
            if (!(r.TargetObjectType is "Table" or "External" or "Unresolved" or "System")) continue;
            string modKey = FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            var startEdge = new Edge { SrcServer = r.SourceServer, SrcDb = r.SourceDatabase, SrcSchema = r.SourceSchema, SrcObj = r.SourceObject, SrcType = r.SourceObjectType, SrcCol = r.SourceColumn, Kind = Enum.Parse<FlowKind>(r.FlowKind), Conf = r.Confidence, Module = modKey };
            int budget = 5000;
            Walk(startEdge, modKey, 1, r.SourceSchema + "." + r.SourceObject + "." + r.SourceColumn, startEdge.Kind, Worst("Exact", r.Confidence), new HashSet<string>(), (src, hops, path, kind, conf) =>
            {
                if (budget-- <= 0) return;
                string key = $"{modKey}|{NodeKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj, src.SrcCol)}→{NodeKey(r.TargetServer, r.TargetDatabase, r.TargetSchema, r.TargetObject, r.TargetColumn)}|{kind}";
                if (!seen.Add(key)) return;
                outRows.Add(new CollapsedRow
                {
                    RunId = runId, Server = r.Server, Database = r.Database, ModuleSchema = r.ModuleSchema, ModuleName = r.ModuleName, ModuleType = r.ModuleType,
                    SourceServer = src.SrcServer, SourceDatabase = src.SrcDb, SourceSchema = src.SrcSchema, SourceObject = src.SrcObj, SourceObjectType = src.SrcType, SourceColumn = src.SrcCol,
                    TargetServer = r.TargetServer, TargetDatabase = r.TargetDatabase, TargetSchema = r.TargetSchema, TargetObject = r.TargetObject, TargetColumn = r.TargetColumn,
                    FlowKind = kind.ToString(), Hops = hops, Path = path + " → " + r.TargetSchema + "." + r.TargetObject + "." + r.TargetColumn, Confidence = conf
                });
            });
        }
        data.CollapsedRows = outRows;
        Log.Info($"İndirgeme: {outRows.Count} kalıcı→kalıcı satır, {sw.ElapsedMilliseconds} ms");

        void Walk(Edge src, string modKey, int hops, string path, FlowKind kind, string conf, HashSet<string> visited, Action<Edge, int, string, FlowKind, string> emit)
        {
            if (!Transient(src.SrcType) || hops > cfg.MaxCollapseDepth) { emit(src, hops, path, kind, conf); return; }
            string nk = NodeKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj, src.SrcCol);
            bool local = src.SrcType is "TempTable" or "TableVariable" or "Tvp";
            string lookup = local ? modKey + "#" + nk : nk;
            // proc sonuç kümesi pozisyonel: RS1:#i → RS1:<ad>
            if (src.SrcType == "Procedure" && src.SrcCol.StartsWith("RS1:#") && int.TryParse(src.SrcCol[5..], out int ord) && resultSetNames.TryGetValue(FqnKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj), out var names) && ord - 1 < names.Count)
                lookup = NodeKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj, "RS1:" + names[ord - 1]);
            if (!visited.Add(lookup)) return;
            var edges = incoming.TryGetValue(lookup, out var l) ? l : null;
            if (edges == null && src.SrcCol != "*")
            {
                // '*' ile yazılmış temp'ler: her kolon '*' kenarına bağlanır
                string star = local ? modKey + "#" + NodeKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj, "*") : NodeKey(src.SrcServer, src.SrcDb, src.SrcSchema, src.SrcObj, "*");
                incoming.TryGetValue(star, out edges);
            }
            string edgesMod = modKey;
            if ((edges == null || edges.Count == 0) && src.SrcType == "TempTable" && !src.SrcObj.StartsWith("##"))
            {
                // bu modülde doldurulmamış temp: çağrılan (ya da çağıran) modülün aynı adlı temp'ine bak
                foreach (var (nk2, sd, sv, sdb) in (callees.TryGetValue(modKey, out var cl) ? cl : new()).Concat(callers.TryGetValue(modKey, out var cr) ? cr : new()))
                {
                    string other = nk2 + "#" + NodeKey(sv, sdb, sd, src.SrcObj, src.SrcCol);
                    if (!incoming.TryGetValue(other, out edges)) incoming.TryGetValue(nk2 + "#" + NodeKey(sv, sdb, sd, src.SrcObj, "*"), out edges);
                    if (edges != null && edges.Count > 0) { edgesMod = nk2; break; }
                }
            }
            if (edges == null || edges.Count == 0) { emit(src, hops, path, kind, conf); visited.Remove(lookup); return; }
            foreach (var e in edges)
                Walk(e, edgesMod, hops + 1, e.SrcSchema + "." + e.SrcObj + "." + e.SrcCol + " → " + path, Deps.Stronger(kind, e.Kind), Worst(conf, e.Conf), visited, emit);
            visited.Remove(lookup);
        }
    }

    static string Worst(string a, string b)
    {
        static int R(string c) => c switch { "Exact" => 0, "High" => 1, "Medium" => 2, _ => 3 };
        return R(a) >= R(b) ? a : b;
    }

    // ---------------- özet
    void BuildSummary(int elapsedMs)
    {
        foreach (var g in data.ModuleRows.GroupBy(m => NameComparer.Norm(m.Server + "|" + m.Database).ToUpperInvariant()).Select(x => (Key: (Server: x.First().Server, Database: x.First().Database), Rows: x.ToList())))
        {
            var st = stats.Where(s => NameComparer.Eq(s.Server, g.Key.Server) && NameComparer.Eq(s.Database, g.Key.Database) && s.Result != null).Select(s => s.Result!).ToList();
            var row = new SummaryRow
            {
                RunId = runId, Server = g.Key.Server, Database = g.Key.Database, Modules = g.Rows.Count,
                Parsed = g.Rows.Count(m => m.ParseStatus == "Parsed"), ParsedWithHoles = g.Rows.Count(m => m.ParseStatus == "ParsedWithHoles"),
                Quarantined = g.Rows.Count(m => m.ParseStatus is "Quarantined" or "Timeout" or "Error"), NoDefinition = g.Rows.Count(m => m.ParseStatus.StartsWith("NoDefinition")),
                ObjectRefs = st.Sum(s => s.ObjectRefs), ObjectRefsResolved = st.Sum(s => s.ObjectRefsResolved),
                DynamicSites = st.Sum(s => s.DynamicSites), DynamicResolved = st.Sum(s => s.DynamicResolved),
                ColumnLineageRows = data.ColumnRows.Count(r => NameComparer.Eq(r.Server, g.Key.Server) && NameComparer.Eq(r.Database, g.Key.Database)),
                ObjectLineageRows = data.ObjectRows.Count(r => NameComparer.Eq(r.Server, g.Key.Server) && NameComparer.Eq(r.Database, g.Key.Database)),
                CollapsedRows = data.CollapsedRows.Count(r => NameComparer.Eq(r.Server, g.Key.Server) && NameComparer.Eq(r.Database, g.Key.Database)),
                CatalogDepsMissed = data.UnresolvedRows.Count(r => r.Kind == "CatalogDepMissed" && NameComparer.Eq(r.Server, g.Key.Server) && NameComparer.Eq(r.Database, g.Key.Database)),
                ReusedModules = g.Rows.Count(m => m.Reused), EngineSignature = engineSignatures.GetValueOrDefault(g.Key.Server + "|" + g.Key.Database) ?? "",
                ElapsedMs = elapsedMs,
            };
            int withDef = row.Modules - row.NoDefinition;
            row.ParseRate = withDef == 0 ? 1 : Math.Round((row.Parsed + row.ParsedWithHoles) / (double)withDef, 4);
            row.BindRate = row.ObjectRefs == 0 ? 1 : Math.Round(row.ObjectRefsResolved / (double)row.ObjectRefs, 4);
            row.DynamicResolutionRate = row.DynamicSites == 0 ? 1 : Math.Round(row.DynamicResolved / (double)row.DynamicSites, 4);
            data.SummaryRows.Add(row);
            Log.Info($"  {row.Server}.{row.Database}: modül {row.Modules}, parse {row.ParseRate:P1}, bind {row.BindRate:P1}, dinamik {row.DynamicSites} site / {row.DynamicResolutionRate:P0}, kolon satırı {row.ColumnLineageRows}");
        }
    }
}

#endregion

// ============================================================================
#region 8. Çıktı: Excel, CSV, DDL, SqlBulkCopy
// ============================================================================

static class Output
{
    static readonly HashSet<string> LongCols = new(StringComparer.OrdinalIgnoreCase) { "Expression", "Note", "Template", "Holes", "Errors", "Path", "ParseError", "Name" };

    public static void Write(LineageConfig cfg, string runId, LineageData d)
    {
        var dir = cfg.Output.Directory;
        Directory.CreateDirectory(dir);
        long totalRows = d.ColumnRows.Count + d.ObjectRows.Count + d.CollapsedRows.Count + d.ModuleRows.Count + d.DynamicRows.Count + d.UnresolvedRows.Count;
        bool big = totalRows > 3_000_000;
        if (cfg.Output.Xlsx && big)
        {
            Log.Warn($"Büyük çıktı: toplam {totalRows:N0} satır (> 3.000.000). Lineage sayfaları yalnız CSV'ye yazılır; Excel'e özet sayfalar (Summary, Modules, DynamicSql, Unresolved) konur. Tam yükleme için --sql-target kullanın.");
            cfg.Output.Csv = true;
        }
        if (cfg.Output.Xlsx)
        {
            var path = Path.Combine(dir, big ? $"lineage-ozet-{runId}.xlsx" : $"lineage-{runId}.xlsx");
            var sw = Stopwatch.StartNew();
            using var wb = new XLWorkbook();
            AddSheet(wb, "Summary", d.SummaryRows, cfg);
            if (!big) { AddSheet(wb, "ColumnLineage", d.ColumnRows, cfg); AddSheet(wb, "ObjectLineage", d.ObjectRows, cfg); AddSheet(wb, "Collapsed", d.CollapsedRows, cfg); }
            AddSheet(wb, "Modules", d.ModuleRows, cfg);
            AddSheet(wb, "DynamicSql", d.DynamicRows, cfg);
            AddSheet(wb, "Unresolved", big ? d.UnresolvedRows.Take(cfg.Output.MaxRowsPerSheet).ToList() : d.UnresolvedRows, cfg);
            AddSheet(wb, "TopIssues", d.IssueSummaryRows, cfg);
            if (d.QueryRows.Count > 0) AddSheet(wb, "Query", d.QueryRows, cfg);
            if (!big) AddSheet(wb, "CatalogDeps", d.CatalogDepRows, cfg);
            if (!big && d.StatementRows.Count > 0) AddSheet(wb, "Statements", d.StatementRows, cfg);
            AddSheet(wb, "UnresolvedByKind", d.UnresolvedRows.GroupBy(u => (u.Server, u.Database, u.Kind)).Select(g => new UnresolvedKindRow { RunId = runId, Server = g.Key.Server, Database = g.Key.Database, Kind = g.Key.Kind, Count = g.Count(), DistinctNames = g.Select(x => x.Name).Distinct(NameComparer.Instance).Count(), Example = g.First().Name }).OrderByDescending(x => x.Count).ToList(), cfg);
            wb.SaveAs(path);
            Log.Info($"Excel: {path} ({sw.ElapsedMilliseconds} ms)" + (big ? " — yalnız özet sayfalar" : ""));
        }
        if (cfg.Output.Csv)
        {
            var csvDir = Path.Combine(dir, "csv-" + runId);
            Directory.CreateDirectory(csvDir);
            WriteCsv(Path.Combine(csvDir, "Summary.csv"), d.SummaryRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "ColumnLineage.csv"), d.ColumnRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "ObjectLineage.csv"), d.ObjectRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "Collapsed.csv"), d.CollapsedRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "Modules.csv"), d.ModuleRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "DynamicSql.csv"), d.DynamicRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "Unresolved.csv"), d.UnresolvedRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "TopIssues.csv"), d.IssueSummaryRows, cfg.Output.CsvSeparator);
            WriteCsv(Path.Combine(csvDir, "CatalogDeps.csv"), d.CatalogDepRows, cfg.Output.CsvSeparator);
            if (d.StatementRows.Count > 0) WriteCsv(Path.Combine(csvDir, "Statements.csv"), d.StatementRows, cfg.Output.CsvSeparator);
            if (d.QueryRows.Count > 0) WriteCsv(Path.Combine(csvDir, "Query.csv"), d.QueryRows, cfg.Output.CsvSeparator);
            Log.Info($"CSV: {csvDir}");
        }
        if (cfg.Output.SqlTableDdl)
        {
            var path = Path.Combine(dir, "lineage_tables.sql");
            File.WriteAllText(path, Ddl(cfg.Output.SqlSchema), new UTF8Encoding(true));
            Log.Info($"DDL: {path}");
        }
        if (!string.IsNullOrWhiteSpace(cfg.Output.SqlBulkTarget))
        {
            try { BulkLoad(cfg, d); }
            catch (Exception ex) { Log.Error("SqlBulkCopy hatası: " + ex.Message); }
        }
    }

    static void AddSheet<T>(XLWorkbook wb, string name, List<T> rows, LineageConfig cfg)
    {
        int max = Math.Max(1000, cfg.Output.MaxRowsPerSheet);
        int part = 0;
        for (int start = 0; start == 0 || start < rows.Count; start += max)
        {
            part++;
            var ws = wb.Worksheets.Add(part == 1 ? name : $"{name}_{part}");
            var slice = rows.Skip(start).Take(max).ToList();
            var props = typeof(T).GetProperties();
            for (int c = 0; c < props.Length; c++) ws.Cell(1, c + 1).Value = props[c].Name;
            ws.Row(1).Style.Font.Bold = true;
            if (slice.Count > 0)
            {
                if (slice.Count <= 100_000) ws.Cell(1, 1).InsertTable(slice, name + (part > 1 ? part.ToString() : ""), true);
                else ws.Cell(2, 1).InsertData(slice);
            }
            ws.SheetView.FreezeRows(1);
            if (slice.Count <= 20_000) ws.Columns().AdjustToContents(1, Math.Min(slice.Count + 1, 200), 8, 60);
        }
    }

    static void WriteCsv<T>(string path, List<T> rows, string sep)
    {
        var props = typeof(T).GetProperties();
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(sep, props.Select(p => p.Name)));
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Clear();
            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = props[i].GetValue(r);
                string s = v switch { null => "", DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"), double db => db.ToString(CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
                if (s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(s);
            }
            w.WriteLine(sb.ToString());
        }
    }

    static string SqlType(PropertyInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (t == typeof(int)) return "int";
        if (t == typeof(double)) return "float";
        if (t == typeof(bool)) return "bit";
        if (t == typeof(DateTime)) return "datetime2(3)";
        return LongCols.Contains(p.Name) ? "nvarchar(max)" : "nvarchar(512)";
    }

    static readonly (string table, Type type)[] Tables =
    {
        ("Summary", typeof(SummaryRow)), ("ColumnLineage", typeof(ColumnLineageRow)), ("ObjectLineage", typeof(ObjectLineageRow)), ("Collapsed", typeof(CollapsedRow)),
        ("Modules", typeof(ModuleRow)), ("DynamicSql", typeof(DynamicSqlRow)), ("Unresolved", typeof(UnresolvedRow)),
        ("TopIssues", typeof(IssueSummaryRow)), ("CatalogDeps", typeof(CatalogDepRow)), ("Statements", typeof(StatementRow)), ("Query", typeof(QueryRow)),
    };

    public static string Ddl(string schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');");
        sb.AppendLine("GO");
        foreach (var (table, type) in Tables)
        {
            sb.AppendLine($"IF OBJECT_ID(N'[{schema}].[{table}]') IS NULL");
            sb.AppendLine($"CREATE TABLE [{schema}].[{table}] (");
            sb.AppendLine("    [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            var props = type.GetProperties();
            for (int i = 0; i < props.Length; i++)
                sb.AppendLine($"    [{props[i].Name}] {SqlType(props[i])} NULL{(i < props.Length - 1 ? "," : "")}");
            sb.AppendLine(");");
            sb.AppendLine("GO");
            // şema evrimi: sonradan eklenen kolonlar
            foreach (var p in props) sb.AppendLine($"IF COL_LENGTH(N'[{schema}].[{table}]', N'{p.Name}') IS NULL ALTER TABLE [{schema}].[{table}] ADD [{p.Name}] {SqlType(p)} NULL;");
            sb.AppendLine("GO");
        }
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Collapsed_Target') CREATE INDEX IX_Collapsed_Target ON [{schema}].[Collapsed](RunId, TargetDatabase, TargetSchema, TargetObject, TargetColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Collapsed_Source') CREATE INDEX IX_Collapsed_Source ON [{schema}].[Collapsed](RunId, SourceDatabase, SourceSchema, SourceObject, SourceColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ColumnLineage_Target') CREATE INDEX IX_ColumnLineage_Target ON [{schema}].[ColumnLineage](RunId, TargetDatabase, TargetSchema, TargetObject, TargetColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ObjectLineage_Object') CREATE INDEX IX_ObjectLineage_Object ON [{schema}].[ObjectLineage](RunId, ObjectDatabase, ObjectSchema, ObjectName);");
        sb.AppendLine("GO");
        sb.Append(Views(schema));
        return sb.ToString();
    }

    /// <summary>Sorgulanabilir katman: son koşu view'leri ve özyinelemeli upstream/downstream fonksiyonları.</summary>
    public static string Views(string s)
    {
        string latest = $"(SELECT TOP 1 RunId FROM [{s}].[Summary] ORDER BY Id DESC)";
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_LatestRun] AS SELECT TOP 1 RunId FROM [{s}].[Summary] ORDER BY Id DESC;").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ColumnEdges] AS SELECT c.* FROM [{s}].[Collapsed] c WHERE c.RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ColumnEdgesDetail] AS SELECT c.* FROM [{s}].[ColumnLineage] c WHERE c.RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ObjectEdges] AS SELECT DISTINCT RunId, Server, [Database], ModuleSchema, ModuleName, ModuleType, Action, ObjectServer, ObjectDatabase, ObjectSchema, ObjectName, ObjectType, Provenance FROM [{s}].[ObjectLineage] WHERE RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_Nodes] AS SELECT DISTINCT SourceServer AS Server, SourceDatabase AS [Database], SourceSchema AS [Schema], SourceObject AS [Object], SourceObjectType AS ObjectType, SourceColumn AS [Column] FROM [{s}].[Collapsed] WHERE RunId = {latest} UNION SELECT DISTINCT TargetServer, TargetDatabase, TargetSchema, TargetObject, 'Table', TargetColumn FROM [{s}].[Collapsed] WHERE RunId = {latest};").AppendLine("GO");
        foreach (var (fn, fromSide, toSide) in new[] { ("fn_Upstream", "Target", "Source"), ("fn_Downstream", "Source", "Target") })
        {
            sb.AppendLine($@"CREATE OR ALTER FUNCTION [{s}].[{fn}](@Database nvarchar(256), @Schema nvarchar(256), @Object nvarchar(256), @Column nvarchar(256), @MaxHops int)
RETURNS TABLE AS RETURN
WITH r AS (
  SELECT c.{toSide}Server AS Server, c.{toSide}Database AS [Database], c.{toSide}Schema AS [Schema], c.{toSide}Object AS [Object], c.{toSide}Column AS [Column],
         c.{fromSide}Column AS StartColumn, 1 AS Hops, c.FlowKind, c.Confidence,
         CAST(c.SourceSchema + '.' + c.SourceObject + '.' + c.SourceColumn + ' -> ' + c.TargetSchema + '.' + c.TargetObject + '.' + c.TargetColumn AS nvarchar(max)) AS Path,
         CAST('|' + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|' AS nvarchar(max)) AS Visited,
         CAST(c.ModuleSchema + '.' + c.ModuleName AS nvarchar(max)) AS Modules
  FROM [{s}].[Collapsed] c
  WHERE c.RunId = {latest} AND c.{fromSide}Database = @Database AND c.{fromSide}Schema = @Schema AND c.{fromSide}Object = @Object AND (@Column IS NULL OR c.{fromSide}Column = @Column)
  UNION ALL
  SELECT c.{toSide}Server, c.{toSide}Database, c.{toSide}Schema, c.{toSide}Object, c.{toSide}Column, r.StartColumn, r.Hops + 1, c.FlowKind, c.Confidence,
         {(fromSide == "Target" ? "c.SourceSchema + '.' + c.SourceObject + '.' + c.SourceColumn + ' -> ' + r.Path" : "r.Path + ' -> ' + c.TargetSchema + '.' + c.TargetObject + '.' + c.TargetColumn")},
         r.Visited + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|',
         {(fromSide == "Target" ? "c.ModuleSchema + '.' + c.ModuleName + ' > ' + r.Modules" : "r.Modules + ' > ' + c.ModuleSchema + '.' + c.ModuleName")}
  FROM r JOIN [{s}].[Collapsed] c ON c.RunId = {latest} AND c.{fromSide}Database = r.[Database] AND c.{fromSide}Schema = r.[Schema] AND c.{fromSide}Object = r.[Object] AND c.{fromSide}Column = r.[Column]
  WHERE r.Hops < @MaxHops AND r.Visited NOT LIKE '%|' + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|%'
)
SELECT Server, [Database], [Schema], [Object], [Column], StartColumn, Hops, FlowKind, Confidence, Path, Modules FROM r;").AppendLine("GO");
        }
        sb.AppendLine($"-- Örnek: SELECT * FROM [{s}].[fn_Upstream](N'dw_production', N'dbo', N'MUSTERI_OZET', NULL, 10) ORDER BY StartColumn, Hops;");
        sb.AppendLine($"-- Örnek: SELECT * FROM [{s}].[fn_Downstream](N'dw_production', N'dbo', N'MUSTERI', N'AD', 10);");
        return sb.ToString();
    }

    static void BulkLoad(LineageConfig cfg, LineageData d)
    {
        var sw = Stopwatch.StartNew();
        using var cn = new SqlConnection(cfg.Output.SqlBulkTarget);
        cn.Open();
        foreach (var batch in Regex.Split(Ddl(cfg.Output.SqlSchema), @"^\s*GO\s*$", RegexOptions.Multiline))
        {
            var text = batch.Trim();
            if (text.Length == 0 || text.Split('\n').All(l => l.TrimStart().StartsWith("--") || l.Trim().Length == 0)) continue;
            using var cmd = new SqlCommand(text, cn) { CommandTimeout = 300 };
            cmd.ExecuteNonQuery();
        }
        Load(cn, cfg.Output.SqlSchema, "Summary", d.SummaryRows);
        Load(cn, cfg.Output.SqlSchema, "ColumnLineage", d.ColumnRows);
        Load(cn, cfg.Output.SqlSchema, "ObjectLineage", d.ObjectRows);
        Load(cn, cfg.Output.SqlSchema, "Collapsed", d.CollapsedRows);
        Load(cn, cfg.Output.SqlSchema, "Modules", d.ModuleRows);
        Load(cn, cfg.Output.SqlSchema, "DynamicSql", d.DynamicRows);
        Load(cn, cfg.Output.SqlSchema, "Unresolved", d.UnresolvedRows);
        Load(cn, cfg.Output.SqlSchema, "TopIssues", d.IssueSummaryRows);
        Load(cn, cfg.Output.SqlSchema, "CatalogDeps", d.CatalogDepRows);
        Load(cn, cfg.Output.SqlSchema, "Statements", d.StatementRows);
        Load(cn, cfg.Output.SqlSchema, "Query", d.QueryRows);
        Log.Info($"SqlBulkCopy: {cfg.Output.SqlSchema}.* yüklendi ({sw.ElapsedMilliseconds} ms); sorgu: SELECT * FROM [{cfg.Output.SqlSchema}].[fn_Upstream](db, schema, tablo, kolon|NULL, 10)");
    }

    static void Load<T>(SqlConnection cn, string schema, string table, List<T> rows)
    {
        var props = typeof(T).GetProperties();
        var dt = new DataTable();
        foreach (var p in props) dt.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
        foreach (var r in rows)
        {
            var vals = new object[props.Length];
            for (int i = 0; i < props.Length; i++) vals[i] = props[i].GetValue(r) ?? DBNull.Value;
            dt.Rows.Add(vals);
        }
        using var bc = new SqlBulkCopy(cn) { DestinationTableName = $"[{schema}].[{table}]", BatchSize = 10_000, BulkCopyTimeout = 0 };
        foreach (var p in props) bc.ColumnMappings.Add(p.Name, p.Name);
        bc.WriteToServer(dt);
    }
}

#endregion

// ============================================================================
#region 9. Artımlı koşu, çok sekmeli sorgu, graf dışa aktarımı
// ============================================================================

/// <summary>--incremental: önceki koşuda (SQL hedefi) tanım hash'i ve motor imzası aynı olan modüller yeniden analiz edilmez; satırları geri yüklenir.</summary>
sealed class Incremental
{
    readonly Dictionary<string, ModuleRow> prev = new();
    readonly Dictionary<string, AnalysisResult> rows = new();
    public string PrevRunId = "";

    public static string Hash(string? text) => text == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    /// <summary>Motor sürümü + analiz ayarları + katalog şekli (tablo/kolon/synonym). Değişirse o DB'nin tüm modülleri yeniden analiz edilir.</summary>
    public static string Signature(LineageConfig cfg, DbCatalog db)
    {
        var sb = new StringBuilder();
        sb.Append(App.EngineVersion).Append('|').Append(cfg.MaxDynamicAlternatives).Append('|').Append(cfg.ConfigTableLookup).Append('|').Append(cfg.IncludeStatementText).Append('|')
          .Append(string.Join(",", cfg.ArchiveDatabasePatterns)).Append('|').Append(string.Join(",", cfg.ExcludeObjectNameContains)).Append('|');
        foreach (var o in db.Objects.Values.Where(o => o.TypeCode is "U" or "V" or "SN" or "TT" or "IF" or "TF").OrderBy(o => o.Ref.Schema + "." + o.Ref.Name, StringComparer.Ordinal))
        {
            sb.Append(o.Ref.Schema).Append('.').Append(o.Ref.Name).Append(':');
            foreach (var c in o.Columns) sb.Append(c.Name).Append(',');
            sb.Append(o.SynonymBase).Append(';');
        }
        return Hash(sb.ToString());
    }

    static string Key(string server, string db, string schema, string name) => NameComparer.Norm($"{server}.{db}.{schema}.{name}").ToUpperInvariant();

    public static Incremental? Load(LineageConfig cfg, Dictionary<string, string> signatures)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var inc = new Incremental();
            string s = cfg.Output.SqlSchema;
            using var cn = new SqlConnection(cfg.Output.SqlBulkTarget); cn.Open();
            using (var cmd = new SqlCommand($"SELECT TOP 1 RunId FROM [{s}].[Summary] ORDER BY Id DESC", cn)) inc.PrevRunId = cmd.ExecuteScalar() as string ?? "";
            if (inc.PrevRunId == "") return null;
            var eligible = new HashSet<string>(NameComparer.Instance);
            using (var cmd = new SqlCommand($"SELECT Server, [Database], EngineSignature FROM [{s}].[Summary] WHERE RunId = @r", cn))
            {
                cmd.Parameters.AddWithValue("@r", inc.PrevRunId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string k = r.GetString(0) + "|" + r.GetString(1);
                    if (signatures.TryGetValue(k, out var cur) && cur == (r.IsDBNull(2) ? "" : r.GetString(2))) eligible.Add(k);
                    else Log.Info($"  artımlı: {k} imzası değişti → tam analiz");
                }
            }
            if (eligible.Count == 0) return null;
            foreach (var m in Read<ModuleRow>(cn, $"SELECT * FROM [{s}].[Modules] WHERE RunId = @r", inc.PrevRunId))
                if (eligible.Contains(m.Server + "|" + m.Database)) inc.prev[Key(m.Server, m.Database, m.ModuleSchema, m.ModuleName)] = m;
            AnalysisResult Res(string server, string db, string schema, string name)
            {
                string k = Key(server, db, schema, name);
                return inc.rows.TryGetValue(k, out var a) ? a : inc.rows[k] = new AnalysisResult();
            }
            foreach (var x in Read<ColumnLineageRow>(cn, $"SELECT * FROM [{s}].[ColumnLineage] WHERE RunId = @r AND Provenance NOT IN ('Interprocedural','Trigger')", inc.PrevRunId)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).ColumnRows.Add(x);
            foreach (var x in Read<ObjectLineageRow>(cn, $"SELECT * FROM [{s}].[ObjectLineage] WHERE RunId = @r AND Provenance NOT IN ('Interprocedural','Trigger')", inc.PrevRunId)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).ObjectRows.Add(x);
            foreach (var x in Read<DynamicSqlRow>(cn, $"SELECT * FROM [{s}].[DynamicSql] WHERE RunId = @r", inc.PrevRunId)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).DynamicRows.Add(x);
            foreach (var x in Read<UnresolvedRow>(cn, $"SELECT * FROM [{s}].[Unresolved] WHERE RunId = @r AND Kind <> 'CatalogDepMissed'", inc.PrevRunId)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).UnresolvedRows.Add(x);
            if (cfg.IncludeStatementText)
                foreach (var x in Read<StatementRow>(cn, $"SELECT * FROM [{s}].[Statements] WHERE RunId = @r", inc.PrevRunId)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).StatementRows.Add(x);
            Log.Info($"Artımlı: önceki koşu {inc.PrevRunId}, {inc.prev.Count} modül aday, {eligible.Count} DB imzası eşleşti, {sw.ElapsedMilliseconds} ms");
            return inc;
        }
        catch (Exception ex) { Log.Warn("Artımlı yükleme başarısız, tam koşu: " + ex.Message); return null; }
    }

    static IEnumerable<T> Read<T>(SqlConnection cn, string sql, string runId) where T : new()
    {
        var props = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        cmd.Parameters.AddWithValue("@r", runId);
        using var r = cmd.ExecuteReader();
        var map = new List<(int i, PropertyInfo p)>();
        for (int i = 0; i < r.FieldCount; i++) if (props.TryGetValue(r.GetName(i), out var p) && p.CanWrite) map.Add((i, p));
        while (r.Read())
        {
            var t = new T();
            foreach (var (i, p) in map)
            {
                if (r.IsDBNull(i)) continue;
                var v = r.GetValue(i); var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                p.SetValue(t, pt == typeof(int) ? Convert.ToInt32(v) : pt == typeof(bool) ? Convert.ToBoolean(v) : pt == typeof(double) ? Convert.ToDouble(v) : pt == typeof(DateTime) ? Convert.ToDateTime(v) : v.ToString()!);
            }
            yield return t;
        }
    }

    public bool TryReuse(ObjInfo mod, ModuleRow row, out ModuleRow prevRow)
    {
        prevRow = null!;
        if (!prev.TryGetValue(Key(mod.Ref.Server, mod.Ref.Database, mod.Ref.Schema, mod.Ref.Name), out var p)) return false;
        if (p.DefinitionHash != row.DefinitionHash || p.ParseStatus is "Error" or "Timeout") return false;
        prevRow = p; return true;
    }

    public AnalysisResult ResultFor(ObjRef r, string newRunId)
    {
        var res = rows.TryGetValue(Key(r.Server, r.Database, r.Schema, r.Name), out var a) ? a : new AnalysisResult();
        foreach (var x in res.ColumnRows) x.RunId = newRunId;
        foreach (var x in res.ObjectRows) x.RunId = newRunId;
        foreach (var x in res.DynamicRows) x.RunId = newRunId;
        foreach (var x in res.UnresolvedRows) x.RunId = newRunId;
        foreach (var x in res.StatementRows) x.RunId = newRunId;
        // sonuç kümesi kolon adları: "RS1:<ad>" hedefli satırlardan (Note: "result set 1, ordinal i")
        var rs = res.ColumnRows.Where(x => x.TargetColumn.StartsWith("RS1:") && NameComparer.Eq(x.TargetObject, r.Name) && x.Note.Contains("ordinal"))
            .Select(x => (name: x.TargetColumn[4..], ord: int.TryParse(Regex.Match(x.Note, @"ordinal (\d+)").Groups[1].Value, out var o) ? o : 0)).Where(x => x.ord > 0).DistinctBy(x => x.ord).OrderBy(x => x.ord).Select(x => x.name).ToList();
        if (rs.Count > 0) res.ResultSetNames.Add(rs);
        res.Statements = 0;
        return res;
    }
}

/// <summary>--query: indirgenmiş (kalıcı→kalıcı) kenarlar üzerinden modüller arası çok sekmeli kolon soyağacı.</summary>
static class LineageQuery
{
    sealed class E { public string Server = "", Db = "", Schema = "", Obj = "", Type = "", Col = "", Kind = "", Conf = "", Module = ""; }
    public static string NK(string server, string db, string schema, string obj, string col) => NameComparer.Norm($"{db}.{schema}.{obj}.{col}").ToUpperInvariant();

    public static (string? server, string db, string schema, string obj, string? col) ParseNode(string node, Catalog cat)
    {
        var p = ModuleAnalyzer.SplitName(node.Trim());
        if (p.Length == 5) return (p[0], p[1], p[2], p[3], p[4]);
        if (p.Length == 4) return cat.FindDb(p[0], p[1]) != null ? (p[0], p[1], p[2], p[3], null) : (null, p[0], p[1], p[2], p[3]);
        if (p.Length == 3) return (null, p[0], p[1], p[2], null);
        if (p.Length == 2) return (null, "", p[0], p[1], null);
        throw new ArgumentException("düğüm biçimi: db.schema.obje[.kolon] ya da server.db.schema.obje[.kolon]");
    }

    public static List<QueryRow> Run(LineageConfig cfg, string runId, LineageData data, Catalog cat)
    {
        var q = cfg.Query!;
        var sw = Stopwatch.StartNew();
        var (server, db, schema, obj, col) = ParseNode(q.Node, cat);
        // adjacency
        var incoming = new Dictionary<string, List<(E src, E tgt)>>(); var outgoing = new Dictionary<string, List<(E src, E tgt)>>();
        foreach (var r in data.CollapsedRows)
        {
            var s = new E { Server = r.SourceServer, Db = r.SourceDatabase, Schema = r.SourceSchema, Obj = r.SourceObject, Type = r.SourceObjectType, Col = r.SourceColumn, Kind = r.FlowKind, Conf = r.Confidence, Module = r.ModuleSchema + "." + r.ModuleName };
            var t = new E { Server = r.TargetServer, Db = r.TargetDatabase, Schema = r.TargetSchema, Obj = r.TargetObject, Type = "Table", Col = r.TargetColumn, Kind = r.FlowKind, Conf = r.Confidence, Module = s.Module };
            var tk = NK(t.Server, t.Db, t.Schema, t.Obj, t.Col); var sk = NK(s.Server, s.Db, s.Schema, s.Obj, s.Col);
            (incoming.TryGetValue(tk, out var l1) ? l1 : incoming[tk] = new()).Add((s, t));
            (outgoing.TryGetValue(sk, out var l2) ? l2 : outgoing[sk] = new()).Add((s, t));
        }
        var outRows = new List<QueryRow>();
        var dirs = q.Direction.Equals("both", StringComparison.OrdinalIgnoreCase) ? new[] { "up", "down" } : new[] { q.Direction.ToLowerInvariant() };
        foreach (var dir in dirs)
        {
            bool up = dir == "up";
            var idx = up ? incoming : outgoing;
            var startCols = col != null ? new List<string> { col }
                : (up ? data.CollapsedRows.Where(r => NameComparer.Eq(r.TargetDatabase, db) && NameComparer.Eq(r.TargetSchema, schema) && NameComparer.Eq(r.TargetObject, obj)).Select(r => r.TargetColumn)
                      : data.CollapsedRows.Where(r => NameComparer.Eq(r.SourceDatabase, db) && NameComparer.Eq(r.SourceSchema, schema) && NameComparer.Eq(r.SourceObject, obj)).Select(r => r.SourceColumn)).Distinct(NameComparer.Instance).ToList();
            if (startCols.Count == 0) Log.Warn($"--query: {q.Node} için {(up ? "gelen" : "giden")} kenar yok");
            foreach (var sc in startCols)
            {
                string startKey = NK(server ?? "", db, schema, obj, sc);
                void Walk(string key, int hops, string path, string mods, string kind, string conf, HashSet<string> onPath, E? last)
                {
                    if (outRows.Count >= q.MaxRows) return;
                    var edges = idx.GetValueOrDefault(key);
                    string reason = "";
                    if (edges == null || edges.Count == 0) reason = last == null ? "NO_EDGES" : last.Type is "Unresolved" or "External" ? "UNRESOLVED" : "PHYSICAL_SOURCE";
                    else if (hops >= q.MaxHops) reason = "MAX_HOPS";
                    if (reason != "" || edges == null)
                    {
                        if (last != null) outRows.Add(new QueryRow { RunId = runId, Direction = dir, StartNode = $"{db}.{schema}.{obj}", StartColumn = sc, EndServer = last.Server, EndDatabase = last.Db, EndSchema = last.Schema, EndObject = last.Obj, EndObjectType = last.Type, EndColumn = last.Col, Hops = hops, FlowKind = kind, Confidence = conf, TerminalReason = reason, Path = path, Modules = mods });
                        return;
                    }
                    foreach (var (s, t) in edges)
                    {
                        var next = up ? s : t;
                        string nk = NK(next.Server, next.Db, next.Schema, next.Obj, next.Col);
                        string np = up ? $"{next.Db}.{next.Schema}.{next.Obj}.{next.Col} → {path}" : $"{path} → {next.Db}.{next.Schema}.{next.Obj}.{next.Col}";
                        string nm = mods == "" ? s.Module : (up ? s.Module + " > " + mods : mods + " > " + s.Module);
                        string nkind = Stronger(kind, s.Kind); string nconf = Worst(conf, s.Conf);
                        if (onPath.Contains(nk)) { outRows.Add(new QueryRow { RunId = runId, Direction = dir, StartNode = $"{db}.{schema}.{obj}", StartColumn = sc, EndServer = next.Server, EndDatabase = next.Db, EndSchema = next.Schema, EndObject = next.Obj, EndObjectType = next.Type, EndColumn = next.Col, Hops = hops + 1, FlowKind = nkind, Confidence = nconf, TerminalReason = "CYCLE", Path = np, Modules = nm }); continue; }
                        onPath.Add(nk);
                        Walk(nk, hops + 1, np, nm, nkind, nconf, onPath, next);
                        onPath.Remove(nk);
                    }
                }
                Walk(startKey, 0, $"{db}.{schema}.{obj}.{sc}", "", "Direct", "Exact", new HashSet<string> { startKey }, null);
            }
        }
        Log.Info($"--query {q.Node} ({q.Direction}, ≤{q.MaxHops} sekme): {outRows.Count} yol, {sw.ElapsedMilliseconds} ms");
        return outRows;
    }
    static string Stronger(string a, string b) { static int R(string k) => k switch { "Direct" => 0, "Positional" => 1, "Expression" => 2, "Aggregate" => 3, _ => 4 }; return R(a) >= R(b) ? a : b; }
    static string Worst(string a, string b) { static int R(string c) => c switch { "Exact" => 0, "High" => 1, "Medium" => 2, _ => 3 }; return R(a) >= R(b) ? a : b; }
}

/// <summary>--graph: bir düğüm çevresindeki alt-grafı DOT / GraphML / JSON olarak yazar (Graphviz: dot -Tsvg graph.dot -o graph.svg).</summary>
static class GraphExport
{
    public static void Write(LineageConfig cfg, string runId, LineageData data, Catalog cat)
    {
        var g = cfg.Graph!;
        var (server, db, schema, obj, col) = LineageQuery.ParseNode(g.Node, cat);
        bool colLevel = g.Level.Equals("column", StringComparison.OrdinalIgnoreCase);
        string Id(string d, string s, string o, string c) => colLevel ? $"{d}.{s}.{o}.{c}" : $"{d}.{s}.{o}";
        var edges = new Dictionary<string, (string from, string to, string kind, string module)>();
        var adjOut = new Dictionary<string, List<string>>(NameComparer.Instance); var adjIn = new Dictionary<string, List<string>>(NameComparer.Instance);
        foreach (var r in data.CollapsedRows)
        {
            string f = Id(r.SourceDatabase, r.SourceSchema, r.SourceObject, r.SourceColumn), t = Id(r.TargetDatabase, r.TargetSchema, r.TargetObject, r.TargetColumn);
            if (f.Equals(t, StringComparison.OrdinalIgnoreCase)) continue;
            string k = (f + "→" + t).ToUpperInvariant();
            if (!edges.ContainsKey(k)) { edges[k] = (f, t, r.FlowKind, r.ModuleSchema + "." + r.ModuleName); (adjOut.TryGetValue(f, out var l1) ? l1 : adjOut[f] = new()).Add(t); (adjIn.TryGetValue(t, out var l2) ? l2 : adjIn[t] = new()).Add(f); }
        }
        string start = colLevel && col != null ? $"{db}.{schema}.{obj}.{col}" : $"{db}.{schema}.{obj}";
        var keep = new HashSet<string>(NameComparer.Instance);
        var frontier = new List<string>();
        if (colLevel && col == null) frontier.AddRange(adjIn.Keys.Concat(adjOut.Keys).Where(k => k.StartsWith(start + ".", StringComparison.OrdinalIgnoreCase)).Distinct(NameComparer.Instance));
        else frontier.Add(start);
        foreach (var f in frontier) keep.Add(f);
        for (int h = 0; h < g.MaxHops; h++)
        {
            var next = new List<string>();
            foreach (var n in frontier) { foreach (var m in adjIn.GetValueOrDefault(n) ?? new()) if (keep.Add(m)) next.Add(m); foreach (var m in adjOut.GetValueOrDefault(n) ?? new()) if (keep.Add(m)) next.Add(m); }
            frontier = next; if (frontier.Count == 0) break;
        }
        var sub = edges.Values.Where(e => keep.Contains(e.from) && keep.Contains(e.to)).ToList();
        Directory.CreateDirectory(cfg.Output.Directory);
        string ext = g.Format.ToLowerInvariant() switch { "graphml" => "graphml", "json" => "json", _ => "dot" };
        string path = Path.Combine(cfg.Output.Directory, $"graph-{runId}.{ext}");
        var sb = new StringBuilder();
        if (ext == "dot")
        {
            sb.AppendLine("digraph lineage {").AppendLine("  rankdir=LR; node [shape=box, fontsize=10];");
            foreach (var n in keep) sb.AppendLine($"  \"{n}\" [{(n.Equals(start, StringComparison.OrdinalIgnoreCase) || n.StartsWith(start + ".", StringComparison.OrdinalIgnoreCase) ? "style=filled, fillcolor=lightyellow" : "")}];");
            foreach (var e in sub) sb.AppendLine($"  \"{e.from}\" -> \"{e.to}\" [label=\"{e.kind}\\n{e.module}\", fontsize=8{(e.kind == "Indirect" ? ", style=dashed" : "")}];");
            sb.AppendLine("}");
        }
        else if (ext == "graphml")
        {
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\"><key id=\"kind\" for=\"edge\" attr.name=\"kind\" attr.type=\"string\"/><key id=\"module\" for=\"edge\" attr.name=\"module\" attr.type=\"string\"/><graph edgedefault=\"directed\">");
            foreach (var n in keep) sb.AppendLine($"  <node id=\"{System.Security.SecurityElement.Escape(n)}\"/>");
            int i = 0; foreach (var e in sub) sb.AppendLine($"  <edge id=\"e{i++}\" source=\"{System.Security.SecurityElement.Escape(e.from)}\" target=\"{System.Security.SecurityElement.Escape(e.to)}\"><data key=\"kind\">{e.kind}</data><data key=\"module\">{System.Security.SecurityElement.Escape(e.module)}</data></edge>");
            sb.AppendLine("</graph></graphml>");
        }
        else
        {
            sb.Append(JsonSerializer.Serialize(new { start, level = g.Level, hops = g.MaxHops, nodes = keep.ToList(), edges = sub.Select(e => new { e.from, e.to, e.kind, e.module }) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Log.Info($"--graph {g.Node}: {keep.Count} düğüm, {sub.Count} kenar → {path}");
    }
}

#endregion

// ============================================================================
#region 10. Informatica PowerCenter XML export lineage (alt komut: infa)
// ============================================================================
namespace InfaLineage
{// ============================================================================
//  InfaLineage.cs — Informatica PowerCenter XML export (POWERMART) lineage çıkarıcı
//
//  Kullanım (klasik konsol projesi):  Program.cs → return await InfaLineage.App.RunAsync(args);
//      dotnet run -- --xml export.xml [--xml klasor] --out out [--no-xlsx] [--no-csv] [--sql-target "<connstr>"]
//  Paketler: ClosedXML 0.105.*, Microsoft.Data.SqlClient 6.*, Microsoft.SqlServer.TransactSql.ScriptDom 170.*
//  (SQL override'ları T-SQL parser ile denenir; parse olmazsa regex ile nesne düzeyi çıkarım.)
//
//  Çıktı sayfaları: Summary, ColumnLineage (kaynak tablo.kolon → hedef tablo.kolon, indirgenmiş, Path ile),
//  PortLineage (mapping içi tüm port kenarları), ObjectLineage (mapping/session × nesne × eylem),
//  Mappings, Sessions, Unresolved.
// ============================================================================


public static class App
{
    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var cfg = InfaConfig.Parse(args);
            if (cfg == null) return 1;
            await Task.Run(() => new InfaRun(cfg).Execute());
            return 0;
        }
        catch (Exception ex) { InfaLog.Error("FATAL: " + ex); return 2; }
    }
}

static class InfaLog
{
    public static bool Verbose;
    static readonly object _l = new();
    public static void Info(string m) => W("INFO ", m);
    public static void Warn(string m) => W("WARN ", m);
    public static void Error(string m) => W("ERROR", m);
    public static void Debug(string m) { if (Verbose) W("DEBUG", m); }
    static void W(string l, string m) { lock (_l) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {l} {m}"); }
}

public sealed class InfaConfig
{
    public List<string> XmlPaths = new();
    public string OutDir = "out";
    public bool Xlsx = true, Csv = true, Ddl = true;
    public string CsvSeparator = ",";
    public string? SqlBulkTarget;
    public string SqlSchema = "infa_lineage";
    public int MaxRowsPerSheet = 900_000;
    public int MaxCollapseDepth = 200;
    public bool PerSession = true;   // her session için ayrı fiziksel satır (bağlantı/owner override'lı)

    const string Usage = """
        InfaLineage — Informatica PowerCenter XML export lineage çıkarıcı
          --xml <dosya|klasör>     POWERMART XML (tekrarlanabilir; klasörde *.xml)
          --out <dir>              çıktı klasörü (varsayılan out)
          --no-xlsx / --no-csv / --no-ddl
          --sql-target "<connstr>" sonuçları infa_lineage.* tablolarına SqlBulkCopy ile yükler
          --sql-schema <ad>        (varsayılan infa_lineage)
          --mapping-only           session bazlı çoğaltma yapma, yalnız tasarım (mapping) düzeyi
          --verbose
        """;

    public static InfaConfig? Parse(string[] args)
    {
        var c = new InfaConfig();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " için değer eksik");
            switch (a)
            {
                case "--xml": c.XmlPaths.Add(Next()); break;
                case "--out": c.OutDir = Next(); break;
                case "--no-xlsx": c.Xlsx = false; break;
                case "--no-csv": c.Csv = false; break;
                case "--no-ddl": c.Ddl = false; break;
                case "--sql-target": c.SqlBulkTarget = Next(); break;
                case "--sql-schema": c.SqlSchema = Next(); break;
                case "--mapping-only": c.PerSession = false; break;
                case "--verbose": InfaLog.Verbose = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return null;
                default: Console.WriteLine("Bilinmeyen argüman: " + a); Console.WriteLine(Usage); return null;
            }
        }
        if (c.XmlPaths.Count == 0) { Console.WriteLine(Usage); return null; }
        return c;
    }
}

// ---------------------------------------------------------------- satır sınıfları
public sealed class ColumnLineageRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Workflow { get; set; } = ""; public string Session { get; set; } = ""; public string Mapping { get; set; } = "";
    public string SourceConnection { get; set; } = ""; public string SourceDbType { get; set; } = ""; public string SourceSchema { get; set; } = ""; public string SourceObject { get; set; } = ""; public string SourceObjectType { get; set; } = ""; public string SourceColumn { get; set; } = "";
    public string TargetConnection { get; set; } = ""; public string TargetDbType { get; set; } = ""; public string TargetSchema { get; set; } = ""; public string TargetObject { get; set; } = ""; public string TargetObjectType { get; set; } = ""; public string TargetColumn { get; set; } = "";
    public string FlowKind { get; set; } = ""; public int Hops { get; set; } public string Path { get; set; } = ""; public string Expression { get; set; } = ""; public string Confidence { get; set; } = ""; public string Note { get; set; } = "";
}
public sealed class PortLineageRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Mapping { get; set; } = "";
    public string FromInstance { get; set; } = ""; public string FromInstanceType { get; set; } = ""; public string FromPort { get; set; } = "";
    public string ToInstance { get; set; } = ""; public string ToInstanceType { get; set; } = ""; public string ToPort { get; set; } = "";
    public string FlowKind { get; set; } = ""; public string Expression { get; set; } = ""; public string Note { get; set; } = "";
}
public sealed class ObjectLineageRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Workflow { get; set; } = ""; public string Session { get; set; } = ""; public string Mapping { get; set; } = ""; public string Instance { get; set; } = ""; public string InstanceType { get; set; } = "";
    public string Action { get; set; } = ""; public string Connection { get; set; } = ""; public string DbType { get; set; } = ""; public string ObjectSchema { get; set; } = ""; public string ObjectName { get; set; } = ""; public string ObjectType { get; set; } = ""; public string Confidence { get; set; } = ""; public string Note { get; set; } = "";
}
public sealed class MappingRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Mapping { get; set; } = ""; public string Status { get; set; } = "";
    public int Instances { get; set; } public int Sources { get; set; } public int Targets { get; set; } public int Lookups { get; set; } public int Mapplets { get; set; } public int Connectors { get; set; } public int PortEdges { get; set; } public int SqlOverrides { get; set; } public int SqlOverridesParsed { get; set; } public int UnresolvedCount { get; set; } public int Sessions { get; set; } public string Errors { get; set; } = "";
}
public sealed class SessionRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Workflow { get; set; } = ""; public string Session { get; set; } = ""; public string Mapping { get; set; } = ""; public bool Reusable { get; set; }
    public string Instance { get; set; } = ""; public string ReaderWriter { get; set; } = ""; public string Connection { get; set; } = ""; public string ConnectionType { get; set; } = ""; public string OwnerOverride { get; set; } = ""; public string TableOverride { get; set; } = ""; public string SqlOverride { get; set; } = ""; public string FileName { get; set; } = "";
}
public sealed class UnresolvedRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public string Mapping { get; set; } = ""; public string Instance { get; set; } = ""; public string Kind { get; set; } = ""; public string Name { get; set; } = ""; public string Note { get; set; } = "";
}
public sealed class SummaryRow
{
    public string RunId { get; set; } = ""; public string Folder { get; set; } = ""; public int Mappings { get; set; } public int MappingsOk { get; set; } public int MappingsWithErrors { get; set; } public double SuccessRate { get; set; }
    public int Workflows { get; set; } public int Sessions { get; set; } public int Instances { get; set; } public int Connectors { get; set; } public int PortEdges { get; set; }
    public int SqlOverrides { get; set; } public int SqlOverridesParsed { get; set; } public double SqlOverrideParseRate { get; set; }
    public int ColumnLineageRows { get; set; } public int ObjectLineageRows { get; set; } public int UnresolvedNames { get; set; } public int TargetColumnsTotal { get; set; } public int TargetColumnsWithLineage { get; set; } public double TargetCoverage { get; set; }
}

public enum FlowKind { Direct, Expression, Aggregate, Indirect, Positional }

// ---------------------------------------------------------------- repo modeli
sealed class Folder
{
    public string Name = "";
    public Dictionary<string, XElement> Sources = new(StringComparer.OrdinalIgnoreCase);       // NAME (DBDNAME.NAME de eklenir)
    public Dictionary<string, XElement> Targets = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, XElement> Transformations = new(StringComparer.OrdinalIgnoreCase); // reusable
    public Dictionary<string, XElement> Mapplets = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, XElement> Mappings = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, XElement> Sessions = new(StringComparer.OrdinalIgnoreCase);       // reusable sessions
    public List<XElement> Workflows = new();
    public Dictionary<string, XElement> Shortcuts = new(StringComparer.OrdinalIgnoreCase);
}

sealed class Port
{
    public string Name = ""; public string PortType = ""; public string Expression = ""; public string Group = ""; public string RefField = ""; public string DataType = ""; public int Number;
    public bool IsInput => PortType.Contains("INPUT", StringComparison.OrdinalIgnoreCase);
    public bool IsOutput => PortType.Contains("OUTPUT", StringComparison.OrdinalIgnoreCase) || PortType.Contains("RETURN", StringComparison.OrdinalIgnoreCase);
    public bool IsLookup => PortType.Contains("LOOKUP", StringComparison.OrdinalIgnoreCase);
    public bool IsVariable => PortType.Contains("VARIABLE", StringComparison.OrdinalIgnoreCase);
}

sealed class Instance
{
    public string Name = ""; public string Type = ""; /* SOURCE TARGET TRANSFORMATION MAPPLET LOOKUPTABLE SQLTABLE PROCEDURE */ public string TransType = ""; public string DefName = ""; public string DbdName = ""; public bool Reusable;
    public XElement? Def; public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase); public List<Port> Ports = new();
    public string DbType = ""; public string Owner = ""; public string ObjectName = ""; public string Connection = "";
    public string LookupTable = ""; public List<string> AssocSources = new(); public string ViaInstance = "";
    public bool IsLookup => TransType.StartsWith("Lookup", StringComparison.OrdinalIgnoreCase);
    public bool IsSourceQualifier => TransType.Contains("Qualifier", StringComparison.OrdinalIgnoreCase);
    public Port? PortByName(string n) => Ports.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
}

sealed class Edge
{
    public string FromInst = "", FromPort = "", ToInst = "", ToPort = ""; public FlowKind Kind; public string Expr = ""; public string Note = "";
}

sealed class MappingGraph
{
    public string Folder = "", Name = "";
    public Dictionary<string, Instance> Instances = new(StringComparer.OrdinalIgnoreCase);
    public List<Edge> Edges = new();
    public List<(string kind, string name, string note)> Unresolved = new();
    public int Connectors, SqlOverrides, SqlOverridesParsed, Mapplets;
    public string Errors = "";
    public Dictionary<string, List<Edge>> Incoming = new(StringComparer.OrdinalIgnoreCase);
    public static string Key(string inst, string port) => inst + "|" + port;
}

sealed class SessionInfo
{
    public string Folder = "", Workflow = "", Name = "", Mapping = ""; public bool Reusable;
    public Dictionary<string, Dictionary<string, string>> InstanceAttrs = new(StringComparer.OrdinalIgnoreCase);   // instance → attr → value
    public Dictionary<string, (string conn, string type, string rw)> Connections = new(StringComparer.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------- yükleme
static class RepoLoader
{
    public static List<Folder> Load(InfaConfig cfg)
    {
        var folders = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var p in cfg.XmlPaths)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.xml", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
            else InfaLog.Warn("Yok: " + p);
        }
        foreach (var f in files)
        {
            var sw = Stopwatch.StartNew();
            XDocument doc;
            try { doc = XDocument.Load(f, System.Xml.Linq.LoadOptions.None); }
            catch (Exception ex) { InfaLog.Error($"{f}: {ex.Message}"); continue; }
            int n = 0;
            foreach (var fe in doc.Descendants("FOLDER"))
            {
                string name = (string?)fe.Attribute("NAME") ?? "(folder)";
                if (!folders.TryGetValue(name, out var folder)) folders[name] = folder = new Folder { Name = name };
                foreach (var e in fe.Elements())
                {
                    string en = e.Name.LocalName; string on = (string?)e.Attribute("NAME") ?? "";
                    switch (en)
                    {
                        case "SOURCE": folder.Sources[on] = e; var dbd = (string?)e.Attribute("DBDNAME"); if (!string.IsNullOrEmpty(dbd)) folder.Sources.TryAdd(dbd + "." + on, e); break;
                        case "TARGET": folder.Targets[on] = e; break;
                        case "TRANSFORMATION": folder.Transformations[on] = e; break;
                        case "MAPPLET": folder.Mapplets[on] = e; break;
                        case "MAPPING": folder.Mappings[on] = e; n++; break;
                        case "SESSION": folder.Sessions[on] = e; break;
                        case "WORKFLOW": case "WORKLET": folder.Workflows.Add(e); break;
                        case "SHORTCUT": folder.Shortcuts[on] = e; break;
                    }
                }
            }
            InfaLog.Info($"{Path.GetFileName(f)}: {n} mapping, {sw.ElapsedMilliseconds} ms");
        }
        return folders.Values.ToList();
    }
}

// ---------------------------------------------------------------- mapping analizi
sealed class MappingAnalyzer
{
    readonly List<Folder> folders; readonly Folder folder; readonly MappingGraph g = new();
    static readonly Regex IdentRx = new(@"(?<![\w$:.])([A-Za-z_][A-Za-z0-9_]*)(?!\s*\()", RegexOptions.Compiled);
    static readonly Regex AggRx = new(@"\b(SUM|AVG|COUNT|MIN|MAX|FIRST|LAST|MEDIAN|PERCENTILE|STDDEV|VARIANCE)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LkpRx = new(@":LKP\.(\w+)\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SpRx = new(@":SP\.(\w+)\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex SqlObjRx = new(@"\b(FROM|JOIN|INTO|UPDATE|DELETE\s+FROM|MERGE\s+INTO)\s+(?!SELECT\b|\()([\w\$\.\[\]""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LiteralOnlyRx = new(@"^\s*('[^']*'|-?[\d.]+|\$\$?\w+|NULL|SYSDATE|SESSSTARTTIME|NEXTVAL|CURRVAL)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "IIF", "DECODE", "IN", "ISNULL", "IS", "THEN", "ELSE", "WHEN", "CASE", "END", "TO_CHAR", "TO_DATE", "TO_INTEGER", "TO_DECIMAL", "LTRIM", "RTRIM", "TRIM", "UPPER", "LOWER", "SUBSTR", "INSTR", "LENGTH", "NEXTVAL", "CURRVAL", "SYSDATE", "SESSSTARTTIME", "DD_INSERT", "DD_UPDATE", "DD_DELETE", "DD_REJECT", "ERROR", "ABORT" };

    public MappingAnalyzer(List<Folder> folders, Folder folder) { this.folders = folders; this.folder = folder; }

    public MappingGraph Analyze(XElement mapping)
    {
        g.Folder = folder.Name; g.Name = (string?)mapping.Attribute("NAME") ?? "";
        try
        {
            BuildInstances(mapping, "", folder);
            foreach (var c in mapping.Elements("CONNECTOR")) AddConnector(c, "");
            foreach (var inst in g.Instances.Values.ToList()) InternalEdges(inst);
            foreach (var e in g.Edges)
            {
                var k = MappingGraph.Key(e.ToInst, e.ToPort);
                if (!g.Incoming.TryGetValue(k, out var l)) g.Incoming[k] = l = new List<Edge>();
                l.Add(e);
            }
        }
        catch (Exception ex) { g.Errors = ex.GetType().Name + ": " + ex.Message; }
        return g;
    }

    // ---- tanım çözümleme (folder → shortcut → diğer folder'lar)
    XElement? FindDef(Folder f, string kind, string name, string? dbd)
    {
        var dict = Dict(f, kind);
        if (dict == null) return null;
        if (kind == "SOURCE" && !string.IsNullOrEmpty(dbd) && dict.TryGetValue(dbd + "." + name, out var s1)) return s1;
        if (dict.TryGetValue(name, out var e)) return e;
        if (f.Shortcuts.TryGetValue(name, out var sc))
        {
            string refName = (string?)sc.Attribute("REFOBJECTNAME") ?? name; string refFolder = (string?)sc.Attribute("FOLDERNAME") ?? "";
            var of = folders.FirstOrDefault(x => x.Name.Equals(refFolder, StringComparison.OrdinalIgnoreCase));
            if (of != null && of != f) { var r = FindDef(of, kind, refName, (string?)sc.Attribute("REFDBDNAME") ?? dbd); if (r != null) return r; }
        }
        foreach (var of in folders) if (of != f) { var d2 = Dict(of, kind); if (d2 != null && d2.TryGetValue(name, out var e2)) return e2; }
        return null;
    }
    static Dictionary<string, XElement>? Dict(Folder f, string kind) => kind switch { "SOURCE" => f.Sources, "TARGET" => f.Targets, "TRANSFORMATION" => f.Transformations, "MAPPLET" => f.Mapplets, _ => null };

    static Dictionary<string, string> ReadAttrs(XElement? e, Dictionary<string, string>? into = null)
    {
        into ??= new(StringComparer.OrdinalIgnoreCase);
        if (e == null) return into;
        foreach (var ta in e.Elements("TABLEATTRIBUTE")) into[(string?)ta.Attribute("NAME") ?? ""] = (string?)ta.Attribute("VALUE") ?? "";
        foreach (var ta in e.Elements("ATTRIBUTE")) into[(string?)ta.Attribute("NAME") ?? ""] = (string?)ta.Attribute("VALUE") ?? "";
        return into;
    }

    static List<Port> ReadPorts(XElement? def, string fieldElem)
    {
        var l = new List<Port>(); if (def == null) return l; int i = 0;
        foreach (var f in def.Elements(fieldElem))
            l.Add(new Port { Name = (string?)f.Attribute("NAME") ?? "", PortType = (string?)f.Attribute("PORTTYPE") ?? "INPUT/OUTPUT", Expression = (string?)f.Attribute("EXPRESSION") ?? "", Group = (string?)f.Attribute("GROUP") ?? "", RefField = (string?)f.Attribute("REF_FIELD") ?? "", DataType = (string?)f.Attribute("DATATYPE") ?? "", Number = int.TryParse((string?)f.Attribute("FIELDNUMBER"), out var n) ? n : ++i });
        return l;
    }

    void BuildInstances(XElement container, string prefix, Folder f)
    {
        foreach (var ie in container.Elements("INSTANCE"))
        {
            var inst = new Instance
            {
                Name = prefix + ((string?)ie.Attribute("NAME") ?? ""), Type = ((string?)ie.Attribute("TYPE") ?? "").ToUpperInvariant(),
                TransType = (string?)ie.Attribute("TRANSFORMATION_TYPE") ?? "", DefName = (string?)ie.Attribute("TRANSFORMATION_NAME") ?? "", DbdName = (string?)ie.Attribute("DBDNAME") ?? "",
                Reusable = string.Equals((string?)ie.Attribute("REUSABLE"), "YES", StringComparison.OrdinalIgnoreCase)
            };
            switch (inst.Type)
            {
                case "SOURCE":
                    inst.Def = FindDef(f, "SOURCE", inst.DefName, inst.DbdName);
                    inst.Ports = ReadPorts(inst.Def, "SOURCEFIELD");
                    inst.DbType = (string?)inst.Def?.Attribute("DATABASETYPE") ?? ""; inst.Owner = (string?)inst.Def?.Attribute("OWNERNAME") ?? ""; inst.ObjectName = (string?)inst.Def?.Attribute("NAME") ?? inst.DefName; inst.Connection = inst.DbdName;
                    if (inst.Def == null) g.Unresolved.Add(("SourceDefinitionNotFound", inst.DefName, inst.Name));
                    break;
                case "TARGET":
                    inst.Def = FindDef(f, "TARGET", inst.DefName, null);
                    inst.Ports = ReadPorts(inst.Def, "TARGETFIELD");
                    inst.DbType = (string?)inst.Def?.Attribute("DATABASETYPE") ?? ""; inst.ObjectName = (string?)inst.Def?.Attribute("NAME") ?? inst.DefName;
                    ReadAttrs(inst.Def, inst.Attrs); ReadAttrs(ie, inst.Attrs);
                    if (inst.Def == null) g.Unresolved.Add(("TargetDefinitionNotFound", inst.DefName, inst.Name));
                    break;
                case "MAPPLET":
                    {
                        var mp = FindDef(f, "MAPPLET", inst.DefName, null);
                        g.Mapplets++;
                        inst.TransType = "Mapplet";
                        g.Instances[inst.Name] = inst;
                        if (mp == null) { g.Unresolved.Add(("MappletNotFound", inst.DefName, inst.Name)); continue; }
                        // mapplet içi instance'lar "MappletInst.Inner" adıyla açılır
                        BuildInstances(mp, inst.Name + ".", f);
                        foreach (var c in mp.Elements("CONNECTOR")) AddConnector(c, inst.Name + ".");
                        continue;
                    }
                default: // TRANSFORMATION
                    {
                        XElement? def = inst.Reusable ? FindDef(f, "TRANSFORMATION", inst.DefName, null) : container.Elements("TRANSFORMATION").FirstOrDefault(t => string.Equals((string?)t.Attribute("NAME"), inst.DefName, StringComparison.OrdinalIgnoreCase));
                        def ??= FindDef(f, "TRANSFORMATION", inst.DefName, null);
                        inst.Def = def;
                        if (def == null) g.Unresolved.Add(("TransformationNotFound", inst.DefName, inst.Name));
                        if (string.IsNullOrEmpty(inst.TransType)) inst.TransType = (string?)def?.Attribute("TYPE") ?? "";
                        inst.Ports = ReadPorts(def, "TRANSFORMFIELD");
                        ReadAttrs(def, inst.Attrs); ReadAttrs(ie, inst.Attrs);
                        foreach (var asi in ie.Elements("ASSOCIATED_SOURCE_INSTANCE")) inst.AssocSources.Add(prefix + ((string?)asi.Attribute("NAME") ?? ""));
                        if (inst.IsLookup) { inst.LookupTable = inst.Attrs.GetValueOrDefault("Lookup table name") ?? ""; inst.ObjectName = inst.LookupTable; }
                        break;
                    }
            }
            g.Instances[inst.Name] = inst;
        }
    }

    // mapplet dış bağlantısı: mapplet instance portu → içerideki Input/Output Transformation portu
    (string inst, string port) RedirectMapplet(string instName, string port, bool incoming)
    {
        if (!g.Instances.TryGetValue(instName, out var inst) || inst.Type != "MAPPLET") return (instName, port);
        string wanted = incoming ? "Input Transformation" : "Output Transformation";
        foreach (var inner in g.Instances.Values.Where(x => x.Name.StartsWith(instName + ".", StringComparison.OrdinalIgnoreCase) && x.TransType.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            if (inner.PortByName(port) != null) return (inner.Name, port);
        return (instName, port);
    }

    void AddConnector(XElement c, string prefix)
    {
        g.Connectors++;
        string fi = prefix + ((string?)c.Attribute("FROMINSTANCE") ?? ""), fp = (string?)c.Attribute("FROMFIELD") ?? "";
        string ti = prefix + ((string?)c.Attribute("TOINSTANCE") ?? ""), tp = (string?)c.Attribute("TOFIELD") ?? "";
        (fi, fp) = RedirectMapplet(fi, fp, false);
        (ti, tp) = RedirectMapplet(ti, tp, true);
        g.Edges.Add(new Edge { FromInst = fi, FromPort = fp, ToInst = ti, ToPort = tp, Kind = FlowKind.Direct, Note = "connector" });
    }

    void AddEdge(string fi, string fp, string ti, string tp, FlowKind k, string expr = "", string note = "") => g.Edges.Add(new Edge { FromInst = fi, FromPort = fp, ToInst = ti, ToPort = tp, Kind = k, Expr = expr, Note = note });

    // ---- transformation içi kurallar
    void InternalEdges(Instance inst)
    {
        if (inst.Type is "SOURCE" or "TARGET" or "MAPPLET" or "LOOKUPTABLE" or "SQLTABLE" or "PROCEDURE") return;
        string tt = inst.TransType;
        var inputs = inst.Ports.Where(p => p.IsInput || p.IsVariable).ToList();
        var outputs = inst.Ports.Where(p => p.IsOutput || p.IsVariable).ToList();

        // 1) Source Qualifier: SQL override / filtre / join → connector'lar kaynağa bağlar, burada ek kenarlar
        if (inst.IsSourceQualifier)
        {
            string sql = inst.Attrs.GetValueOrDefault("Sql Query") ?? "";
            if (!string.IsNullOrWhiteSpace(sql)) SqlOverride(inst, sql, outputs, "Sql Query");
            foreach (var key in new[] { "Source Filter", "User Defined Join" })
            {
                var v = inst.Attrs.GetValueOrDefault(key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                var srcs = inst.AssocSources.Count > 0 ? inst.AssocSources : new List<string> { inst.Name };
                foreach (var o in outputs) foreach (var sname in srcs) AddEdge(sname, "*", inst.Name, o.Name, FlowKind.Indirect, Clip(v), key);
            }
            return;
        }
        // 2) Lookup: lookup tablosu kolonları → LOOKUP portları; koşul → Indirect
        if (inst.IsLookup)
        {
            string lkpSql = inst.Attrs.GetValueOrDefault("Lookup Sql Override") ?? "";
            var lkpInst = "LKP:" + inst.Name;
            g.Instances.TryAdd(lkpInst, new Instance { Name = lkpInst, Type = "LOOKUPTABLE", TransType = "Lookup Table", ObjectName = inst.LookupTable, Connection = inst.Attrs.GetValueOrDefault("Connection Information") ?? "", Attrs = inst.Attrs });
            foreach (var p in inst.Ports.Where(p => p.IsLookup)) AddEdge(lkpInst, p.Name, inst.Name, p.Name, FlowKind.Direct, "", "lookup column");
            if (!string.IsNullOrWhiteSpace(lkpSql)) { g.SqlOverrides++; if (TryParseTsql(lkpSql) != null) g.SqlOverridesParsed++; else g.Unresolved.Add(("LookupSqlOverrideNotParsed", Clip(lkpSql, 100), inst.Name)); }
            string cond = inst.Attrs.GetValueOrDefault("Lookup condition") ?? "";
            var condRefs = Refs(cond, inst).ToList();
            foreach (var o in outputs) foreach (var r in condRefs) AddEdge(inst.Name, r, inst.Name, o.Name, FlowKind.Indirect, Clip(cond), "lookup condition");
        }
        // 3) Stored Procedure transformation
        if (tt.Contains("Stored Procedure", StringComparison.OrdinalIgnoreCase))
        {
            string proc = inst.Attrs.GetValueOrDefault("Stored Procedure Name") ?? inst.DefName;
            var spInst = "SP:" + proc;
            g.Instances.TryAdd(spInst, new Instance { Name = spInst, Type = "PROCEDURE", TransType = "Stored Procedure", ObjectName = proc, Connection = inst.Attrs.GetValueOrDefault("Connection Information") ?? "" });
            foreach (var p in inputs) AddEdge(inst.Name, p.Name, spInst, "IN:" + p.Name, FlowKind.Direct, "", "proc argümanı");
            foreach (var p in outputs.Where(p => !p.IsInput)) AddEdge(spInst, "OUT:" + p.Name, inst.Name, p.Name, FlowKind.Direct, "", "proc çıktısı");
            return;
        }
        // 4) Router: grup portları REF_FIELD ile giriş portuna bağlı; grup koşulu Indirect
        if (tt.Contains("Router", StringComparison.OrdinalIgnoreCase))
        {
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (inst.Def != null) foreach (var ge in inst.Def.Elements("GROUP")) groups[(string?)ge.Attribute("NAME") ?? ""] = (string?)ge.Attribute("EXPRESSION") ?? "";
            foreach (var o in inst.Ports.Where(p => !string.IsNullOrEmpty(p.RefField)))
            {
                AddEdge(inst.Name, o.RefField, inst.Name, o.Name, FlowKind.Direct, "", "router group " + o.Group);
                if (groups.TryGetValue(o.Group, out var gx) && !string.IsNullOrWhiteSpace(gx))
                    foreach (var r in Refs(gx, inst)) AddEdge(inst.Name, r, inst.Name, o.Name, FlowKind.Indirect, Clip(gx), "router condition");
            }
            return;
        }
        // 5) Filtre / Update Strategy / Joiner koşulları → tüm çıkışlara Indirect
        string? cond2 = tt.Contains("Filter", StringComparison.OrdinalIgnoreCase) ? inst.Attrs.GetValueOrDefault("Filter Condition")
                      : tt.Contains("Update Strategy", StringComparison.OrdinalIgnoreCase) ? inst.Attrs.GetValueOrDefault("Update Strategy Expression")
                      : tt.Contains("Joiner", StringComparison.OrdinalIgnoreCase) ? inst.Attrs.GetValueOrDefault("Join Condition") : null;
        if (!string.IsNullOrWhiteSpace(cond2))
            foreach (var o in outputs) foreach (var r in Refs(cond2, inst)) AddEdge(inst.Name, r, inst.Name, o.Name, FlowKind.Indirect, Clip(cond2), tt + " condition");
        bool isAgg = tt.Contains("Aggregator", StringComparison.OrdinalIgnoreCase);
        if (isAgg || tt.Contains("Rank", StringComparison.OrdinalIgnoreCase) || tt.Contains("Sorter", StringComparison.OrdinalIgnoreCase))
        {
            var keys = new List<string>();
            if (inst.Def != null) foreach (var fe in inst.Def.Elements("TRANSFORMFIELD"))
                if (string.Equals((string?)fe.Attribute("GROUPBY"), "YES", StringComparison.OrdinalIgnoreCase) || string.Equals((string?)fe.Attribute("SORTKEY"), "YES", StringComparison.OrdinalIgnoreCase)) keys.Add((string?)fe.Attribute("NAME") ?? "");
            foreach (var o in outputs) foreach (var k in keys) if (!k.Equals(o.Name, StringComparison.OrdinalIgnoreCase)) AddEdge(inst.Name, k, inst.Name, o.Name, FlowKind.Indirect, "", isAgg ? "group by" : "sort/rank key");
        }
        // 6) Genel: çıkış portu ifadesi → giriş/değişken portlarına Expression/Aggregate; ifadesiz çıkış → aynı adlı giriş (Union/Normalizer/Joiner) Direct
        foreach (var o in inst.Ports.Where(p => (p.IsOutput || p.IsVariable) && !p.IsInput))
        {
            string ex = o.Expression;
            if (string.IsNullOrWhiteSpace(ex) || ex.Trim().Equals(o.Name, StringComparison.OrdinalIgnoreCase))
            {
                var same = inst.Ports.Where(p => p.IsInput && !ReferenceEquals(p, o) && (p.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase) || (p.Group != "" && StripGroup(p.Name, p.Group).Equals(o.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
                foreach (var p in same) AddEdge(inst.Name, p.Name, inst.Name, o.Name, FlowKind.Direct, "", same.Count > 1 ? "union group " + p.Group : "passthrough");
                if (same.Count == 0 && !o.IsLookup && !tt.Contains("Sequence", StringComparison.OrdinalIgnoreCase) && inst.Ports.Any(p => p.IsInput))
                    g.Unresolved.Add(("OutputPortWithoutSource", inst.Name + "." + o.Name, tt));
                continue;
            }
            var kind = AggRx.IsMatch(ex) ? FlowKind.Aggregate : FlowKind.Expression;
            int n = 0;
            foreach (var r in Refs(ex, inst)) { AddEdge(inst.Name, r, inst.Name, o.Name, kind, Clip(ex)); n++; }
            foreach (Match m in LkpRx.Matches(ex))
            {
                string lkpName = m.Groups[1].Value;
                var lkp = g.Instances.Values.FirstOrDefault(x => x.IsLookup && (x.Name.Equals(lkpName, StringComparison.OrdinalIgnoreCase) || x.DefName.Equals(lkpName, StringComparison.OrdinalIgnoreCase)));
                string lkpInst = lkp != null ? "LKP:" + lkp.Name : "LKP:" + lkpName;
                if (lkp == null)
                {
                    var ld = FindDef(folder, "TRANSFORMATION", lkpName, null);
                    g.Instances.TryAdd(lkpInst, new Instance { Name = lkpInst, Type = "LOOKUPTABLE", TransType = "Lookup Table", ObjectName = ld != null ? (ReadAttrs(ld).GetValueOrDefault("Lookup table name") ?? lkpName) : lkpName });
                }
                var retPort = lkp?.Ports.FirstOrDefault(p => p.PortType.Contains("RETURN", StringComparison.OrdinalIgnoreCase))?.Name ?? "RETURN";
                AddEdge(lkpInst, retPort, inst.Name, o.Name, FlowKind.Expression, Clip(ex), "unconnected lookup :LKP." + lkpName); n++;
                foreach (var r in Refs(m.Groups[2].Value, inst)) AddEdge(inst.Name, r, lkpInst, "IN:" + lkpName, FlowKind.Indirect, "", "lookup input");
            }
            foreach (Match m in SpRx.Matches(ex))
            {
                string spName = m.Groups[1].Value; string spInst = "SP:" + spName;
                g.Instances.TryAdd(spInst, new Instance { Name = spInst, Type = "PROCEDURE", TransType = "Stored Procedure", ObjectName = spName });
                AddEdge(spInst, "RETURN", inst.Name, o.Name, FlowKind.Expression, Clip(ex), "unconnected :SP." + spName); n++;
                foreach (var r in Refs(m.Groups[2].Value, inst)) AddEdge(inst.Name, r, spInst, "IN:" + spName, FlowKind.Direct, "", "proc argümanı");
            }
            if (n == 0 && !LiteralOnlyRx.IsMatch(ex)) g.Unresolved.Add(("ExpressionRefsNotFound", inst.Name + "." + o.Name, Clip(ex, 120)));
        }
        // Pre/Post SQL, Update Override: nesne düzeyi
        foreach (var key in new[] { "Pre SQL", "Post SQL", "Update Override", "Pre-session SQL", "Post-session SQL" })
        {
            var v = inst.Attrs.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(v)) continue;
            foreach (Match m in SqlObjRx.Matches(v))
            {
                string t = m.Groups[2].Value.Trim('"');
                g.Instances.TryAdd("SQL:" + t, new Instance { Name = "SQL:" + t, Type = "SQLTABLE", TransType = "SQL table (" + key + ")", ObjectName = t, Connection = inst.Connection });
                AddEdge("SQL:" + t, "*", inst.Name, "*", FlowKind.Indirect, Clip(v), key);
            }
        }
    }

    static string StripGroup(string port, string group) => port.EndsWith(group, StringComparison.OrdinalIgnoreCase) ? port[..^group.Length] : Regex.Replace(port, @"\d+$", "");

    /// <summary>SQ SQL override: T-SQL parse edilebiliyorsa select listesi → çıkış portlarına pozisyonel; FROM/JOIN tabloları → SQL:tablo düğümleri.</summary>
    void SqlOverride(Instance inst, string sql, List<Port> outputs, string key)
    {
        g.SqlOverrides++;
        var script = TryParseTsql(sql);
        var tables = new List<string>();
        if (script != null)
        {
            g.SqlOverridesParsed++;
            var v = new SqlVisitor(); script.Accept(v);
            tables = v.Tables;
            var connectedOut = outputs.Where(o => g.Edges.Any(e => e.FromInst.Equals(inst.Name, StringComparison.OrdinalIgnoreCase) && e.FromPort.Equals(o.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (connectedOut.Count == 0) connectedOut = outputs;
            for (int i = 0; i < Math.Min(v.SelectItems.Count, connectedOut.Count); i++)
                foreach (var (tbl, col, kind) in v.SelectItems[i])
                    AddEdge("SQL:" + (tbl ?? "?"), col, inst.Name, connectedOut[i].Name, kind, "", key + " (pozisyonel)");
            foreach (var (tbl, col) in v.WhereCols) foreach (var o in connectedOut) AddEdge("SQL:" + (tbl ?? "?"), col, inst.Name, o.Name, FlowKind.Indirect, "", key + " WHERE/JOIN");
        }
        else
        {
            foreach (Match m in SqlObjRx.Matches(sql)) tables.Add(m.Groups[2].Value.Trim('"'));
            g.Unresolved.Add(("SqlOverrideNotParsed", Clip(sql, 100), inst.Name + " (regex ile yalnız tablo adları)"));
            foreach (var o in outputs) foreach (var t in tables.Distinct(StringComparer.OrdinalIgnoreCase)) AddEdge("SQL:" + t, "*", inst.Name, o.Name, FlowKind.Positional, "", key + " (regex; kolon eşlemesi yok)");
        }
        foreach (var t in tables.Distinct(StringComparer.OrdinalIgnoreCase))
            g.Instances.TryAdd("SQL:" + t, new Instance { Name = "SQL:" + t, Type = "SQLTABLE", TransType = "SQL override table", ObjectName = t, Connection = inst.Connection, ViaInstance = inst.AssocSources.FirstOrDefault() ?? "" });
    }

    static TSqlFragment? TryParseTsql(string sql)
    {
        // Informatica parametreleri ($$X, $X) ve Oracle sözdizimi: kaba temizlik sonrası T-SQL parser
        string cleaned = Regex.Replace(sql, @"\$\$?\w+", "0");
        cleaned = Regex.Replace(cleaned, @"\(\+\)", "");
        cleaned = Regex.Replace(cleaned, @"\bNVL\s*\(", "ISNULL(", RegexOptions.IgnoreCase);
        var p = new TSql170Parser(true);
        var f = p.Parse(new StringReader(cleaned), out var errs);
        return errs.Count == 0 ? f : null;
    }

    sealed class SqlVisitor : TSqlFragmentVisitor
    {
        public List<string> Tables = new();
        public List<List<(string? tbl, string col, FlowKind kind)>> SelectItems = new();
        public List<(string? tbl, string col)> WhereCols = new();
        readonly Dictionary<string, string> alias = new(StringComparer.OrdinalIgnoreCase);
        bool selectDone;
        public override void Visit(NamedTableReference n)
        {
            string name = string.Join(".", n.SchemaObject.Identifiers.Select(i => i.Value));
            if (!Tables.Contains(name, StringComparer.OrdinalIgnoreCase)) Tables.Add(name);
            if (n.Alias != null) alias[n.Alias.Value] = name;
            alias.TryAdd(n.SchemaObject.BaseIdentifier.Value, name);
        }
        public override void Visit(QuerySpecification q)
        {
            if (selectDone) return; selectDone = true;
            if (q.FromClause != null) foreach (var t in q.FromClause.TableReferences) t.Accept(this);
            foreach (var se in q.SelectElements)
            {
                var item = new List<(string?, string, FlowKind)>();
                if (se is SelectScalarExpression sse)
                {
                    var cols = new ColVisitor(); sse.Expression.Accept(cols);
                    bool bare = sse.Expression is ColumnReferenceExpression;
                    foreach (var (qual, col) in cols.Cols) item.Add((Resolve(qual), col, bare ? FlowKind.Direct : cols.Agg ? FlowKind.Aggregate : FlowKind.Expression));
                }
                else if (se is SelectStarExpression) item.Add((null, "*", FlowKind.Positional));
                SelectItems.Add(item);
            }
            if (q.WhereClause != null) { var cv = new ColVisitor(); q.WhereClause.Accept(cv); foreach (var (qual, col) in cv.Cols) WhereCols.Add((Resolve(qual), col)); }
            if (q.FromClause != null) foreach (var t in q.FromClause.TableReferences) { var cv = new ColVisitor(); t.Accept(cv); foreach (var (qual, col) in cv.Cols) WhereCols.Add((Resolve(qual), col)); }
        }
        string? Resolve(string? q) => q == null ? (Tables.Count == 1 ? Tables[0] : null) : alias.TryGetValue(q, out var t) ? t : q;
    }
    sealed class ColVisitor : TSqlFragmentVisitor
    {
        public List<(string? q, string c)> Cols = new(); public bool Agg;
        public override void Visit(ColumnReferenceExpression c) { if (c.MultiPartIdentifier == null) return; var ids = c.MultiPartIdentifier.Identifiers; Cols.Add((ids.Count > 1 ? ids[^2].Value : null, ids[^1].Value)); }
        public override void Visit(FunctionCall f) { if (Regex.IsMatch(f.FunctionName.Value, "^(SUM|COUNT|AVG|MIN|MAX|COUNT_BIG|STRING_AGG)$", RegexOptions.IgnoreCase)) Agg = true; }
    }

    /// <summary>İfade metnindeki, bu transformation'ın portlarına karşılık gelen adlar.</summary>
    IEnumerable<string> Refs(string expr, Instance inst)
    {
        if (string.IsNullOrWhiteSpace(expr)) yield break;
        string noStr = Regex.Replace(expr, @"'([^']|'')*'", "''");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in IdentRx.Matches(noStr))
        {
            string id = m.Groups[1].Value;
            if (Keywords.Contains(id) || !seen.Add(id)) continue;
            if (inst.PortByName(id) != null) yield return id;
        }
    }
    static string Clip(string s, int max = 300) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length <= max ? s : s[..max] + "…"; }
}

// ---------------------------------------------------------------- session'lar
static class SessionReader
{
    public static List<SessionInfo> Read(Folder f)
    {
        var list = new List<SessionInfo>();
        foreach (var wf in f.Workflows)
        {
            string wfName = (string?)wf.Attribute("NAME") ?? "";
            foreach (var s in wf.Descendants("SESSION")) list.Add(Parse(f, wfName, s, false));
            foreach (var ti in wf.Descendants("TASKINSTANCE").Where(t => string.Equals((string?)t.Attribute("TYPE"), "Session", StringComparison.OrdinalIgnoreCase) && string.Equals((string?)t.Attribute("REUSABLE"), "YES", StringComparison.OrdinalIgnoreCase)))
            {
                string tn = (string?)ti.Attribute("TASKNAME") ?? (string?)ti.Attribute("NAME") ?? "";
                if (f.Sessions.TryGetValue(tn, out var rs)) list.Add(Parse(f, wfName, rs, true));
            }
        }
        foreach (var rs in f.Sessions.Values)
        {
            string rn = (string?)rs.Attribute("NAME") ?? "";
            if (!list.Any(x => x.Reusable && x.Name.Equals(rn, StringComparison.OrdinalIgnoreCase))) list.Add(Parse(f, "", rs, true));
        }
        return list;
    }

    static SessionInfo Parse(Folder f, string wf, XElement s, bool reusable)
    {
        var si = new SessionInfo { Folder = f.Name, Workflow = wf, Name = (string?)s.Attribute("NAME") ?? "", Mapping = (string?)s.Attribute("MAPPINGNAME") ?? "", Reusable = reusable };
        foreach (var ext in s.Elements("SESSIONEXTENSION"))
        {
            string inst = (string?)ext.Attribute("SINSTANCENAME") ?? ""; string type = (string?)ext.Attribute("TYPE") ?? "";
            var cr = ext.Element("CONNECTIONREFERENCE");
            if (cr != null) si.Connections[inst] = ((string?)cr.Attribute("CONNECTIONNAME") ?? (string?)cr.Attribute("VARIABLE") ?? "", (string?)cr.Attribute("CONNECTIONTYPE") ?? (string?)cr.Attribute("CNXREFNAME") ?? "", type);
            if (!si.InstanceAttrs.TryGetValue(inst, out var d)) si.InstanceAttrs[inst] = d = new(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ext.Elements("ATTRIBUTE")) d[(string?)a.Attribute("NAME") ?? ""] = (string?)a.Attribute("VALUE") ?? "";
        }
        foreach (var sti in s.Elements("SESSTRANSFORMATIONINST"))
        {
            string inst = (string?)sti.Attribute("SINSTANCENAME") ?? "";
            if (!si.InstanceAttrs.TryGetValue(inst, out var d)) si.InstanceAttrs[inst] = d = new(StringComparer.OrdinalIgnoreCase);
            foreach (var a in sti.Elements("ATTRIBUTE")) d[(string?)a.Attribute("NAME") ?? ""] = (string?)a.Attribute("VALUE") ?? "";
        }
        return si;
    }
}

// ---------------------------------------------------------------- koşu
sealed class InfaRun
{
    readonly InfaConfig cfg; readonly string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public List<ColumnLineageRow> ColumnRows = new(); public List<PortLineageRow> PortRows = new(); public List<ObjectLineageRow> ObjectRows = new();
    public List<MappingRow> MappingRows = new(); public List<SessionRow> SessionRows = new(); public List<UnresolvedRow> UnresolvedRows = new(); public List<SummaryRow> SummaryRows = new();
    public InfaRun(InfaConfig cfg) { this.cfg = cfg; }

    public void Execute()
    {
        var sw = Stopwatch.StartNew();
        InfaLog.Info($"RunId {runId}");
        var folders = RepoLoader.Load(cfg);
        foreach (var f in folders)
        {
            var sessions = SessionReader.Read(f);
            foreach (var s in sessions)
                foreach (var (inst, (conn, ctype, rw)) in s.Connections)
                {
                    var at = s.InstanceAttrs.GetValueOrDefault(inst) ?? new();
                    SessionRows.Add(new SessionRow
                    {
                        RunId = runId, Folder = f.Name, Workflow = s.Workflow, Session = s.Name, Mapping = s.Mapping, Reusable = s.Reusable, Instance = inst, ReaderWriter = rw, Connection = conn, ConnectionType = ctype,
                        OwnerOverride = at.GetValueOrDefault("Owner Name") ?? at.GetValueOrDefault("Table Name Prefix") ?? "", TableOverride = at.GetValueOrDefault("Target Table Name") ?? at.GetValueOrDefault("Source Table Name") ?? "",
                        SqlOverride = at.GetValueOrDefault("Sql Query") ?? at.GetValueOrDefault("Lookup Sql Override") ?? "", FileName = at.GetValueOrDefault("Output file name") ?? at.GetValueOrDefault("Source filename") ?? ""
                    });
                }
            int ok = 0, err = 0, instTotal = 0, connTotal = 0, edgeTotal = 0, sqlO = 0, sqlP = 0, tgtCols = 0, tgtWith = 0;
            foreach (var m in f.Mappings.Values)
            {
                var g = new MappingAnalyzer(folders, f).Analyze(m);
                var mSessions = sessions.Where(s => s.Mapping.Equals(g.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                int sessionCount = mSessions.Count;
                if (!cfg.PerSession || mSessions.Count == 0) mSessions = new List<SessionInfo> { new SessionInfo { Folder = f.Name, Mapping = g.Name } };
                foreach (var e in g.Edges)
                    PortRows.Add(new PortLineageRow { RunId = runId, Folder = f.Name, Mapping = g.Name, FromInstance = e.FromInst, FromInstanceType = TypeOf(g, e.FromInst), FromPort = e.FromPort, ToInstance = e.ToInst, ToInstanceType = TypeOf(g, e.ToInst), ToPort = e.ToPort, FlowKind = e.Kind.ToString(), Expression = e.Expr, Note = e.Note });
                foreach (var (kind, name, note) in g.Unresolved) UnresolvedRows.Add(new UnresolvedRow { RunId = runId, Folder = f.Name, Mapping = g.Name, Instance = note, Kind = kind, Name = name });
                var (tc, tw) = EmitPhysical(f, g, mSessions);
                tgtCols += tc; tgtWith += tw;
                MappingRows.Add(new MappingRow
                {
                    RunId = runId, Folder = f.Name, Mapping = g.Name, Status = g.Errors == "" ? (g.Unresolved.Count == 0 ? "Ok" : "OkWithUnresolved") : "Error",
                    Instances = g.Instances.Count, Sources = g.Instances.Values.Count(i => i.Type == "SOURCE"), Targets = g.Instances.Values.Count(i => i.Type == "TARGET"), Lookups = g.Instances.Values.Count(i => i.IsLookup), Mapplets = g.Mapplets,
                    Connectors = g.Connectors, PortEdges = g.Edges.Count, SqlOverrides = g.SqlOverrides, SqlOverridesParsed = g.SqlOverridesParsed, UnresolvedCount = g.Unresolved.Count, Sessions = sessionCount, Errors = g.Errors
                });
                if (g.Errors == "") ok++; else err++;
                instTotal += g.Instances.Count; connTotal += g.Connectors; edgeTotal += g.Edges.Count; sqlO += g.SqlOverrides; sqlP += g.SqlOverridesParsed;
            }
            SummaryRows.Add(new SummaryRow
            {
                RunId = runId, Folder = f.Name, Mappings = f.Mappings.Count, MappingsOk = ok, MappingsWithErrors = err, SuccessRate = f.Mappings.Count == 0 ? 1 : Math.Round(ok / (double)f.Mappings.Count, 4),
                Workflows = f.Workflows.Count, Sessions = sessions.Count, Instances = instTotal, Connectors = connTotal, PortEdges = edgeTotal, SqlOverrides = sqlO, SqlOverridesParsed = sqlP, SqlOverrideParseRate = sqlO == 0 ? 1 : Math.Round(sqlP / (double)sqlO, 4),
                ColumnLineageRows = ColumnRows.Count(r => r.Folder == f.Name), ObjectLineageRows = ObjectRows.Count(r => r.Folder == f.Name), UnresolvedNames = UnresolvedRows.Count(r => r.Folder == f.Name),
                TargetColumnsTotal = tgtCols, TargetColumnsWithLineage = tgtWith, TargetCoverage = tgtCols == 0 ? 1 : Math.Round(tgtWith / (double)tgtCols, 4)
            });
            InfaLog.Info($"  {f.Name}: {f.Mappings.Count} mapping ({ok} ok / {err} hata), {sessions.Count} session, SQL override {sqlP}/{sqlO} parse, hedef kolon kapsama {tgtWith}/{tgtCols}");
        }
        InfaOutput.Write(cfg, runId, this);
        InfaLog.Info($"Bitti: {sw.Elapsed.TotalSeconds:F1} s — {ColumnRows.Count} kolon satırı, {ObjectRows.Count} nesne satırı, {PortRows.Count} port kenarı");
    }

    static string TypeOf(MappingGraph g, string inst) => g.Instances.TryGetValue(inst, out var i) ? (i.Type == "TRANSFORMATION" ? i.TransType : i.Type) : inst.StartsWith("SQL:") ? "SQLTABLE" : inst.StartsWith("LKP:") ? "LOOKUPTABLE" : inst.StartsWith("SP:") ? "PROCEDURE" : "?";

    /// <summary>Session override'larıyla fiziksel kimlik: (connection, dbtype, owner, name, type).</summary>
    static (string conn, string dbtype, string owner, string name, string type) Physical(Instance i, SessionInfo s)
    {
        var at = s.InstanceAttrs.GetValueOrDefault(i.Name) ?? new();
        string conn = s.Connections.TryGetValue(i.Name, out var c) ? c.conn : i.Connection;
        string dbtype = s.Connections.TryGetValue(i.Name, out var c2) && c2.type != "" ? c2.type : i.DbType;
        string owner = at.GetValueOrDefault("Owner Name") ?? at.GetValueOrDefault("Table Name Prefix") ?? i.Owner;
        string name = at.GetValueOrDefault("Target Table Name") ?? at.GetValueOrDefault("Source Table Name") ?? i.ObjectName;
        string type = i.Type switch { "SOURCE" => "Source", "TARGET" => "Target", "LOOKUPTABLE" => "LookupTable", "SQLTABLE" => "SqlOverrideTable", "PROCEDURE" => "Procedure", _ => i.Type };
        if (dbtype.Contains("Flat", StringComparison.OrdinalIgnoreCase) || dbtype.Contains("File", StringComparison.OrdinalIgnoreCase)) { type = "File"; name = at.GetValueOrDefault("Output file name") ?? at.GetValueOrDefault("Source filename") ?? name; }
        if (i.Type is "LOOKUPTABLE" or "SQLTABLE" or "PROCEDURE")
        {
            if (i.Type == "SQLTABLE" && i.ViaInstance != "" && s.Connections.TryGetValue(i.ViaInstance, out var vc)) { conn = vc.conn; if (vc.type != "") dbtype = vc.type; }
            if (i.Type == "LOOKUPTABLE")
            {
                var lkpAt = s.InstanceAttrs.GetValueOrDefault(i.Name["LKP:".Length..]);
                conn = lkpAt?.GetValueOrDefault("Connection Information") ?? conn;
                if (conn.StartsWith("$Source", StringComparison.OrdinalIgnoreCase) || conn.StartsWith("$Target", StringComparison.OrdinalIgnoreCase)) conn = ResolveDollar(conn, s);
                name = lkpAt?.GetValueOrDefault("Lookup table name") ?? name;
            }
            int dot = name.LastIndexOf('.');
            if (dot > 0 && owner == "") { owner = name[..dot]; name = name[(dot + 1)..]; }
        }
        return (conn, dbtype, owner, name, type);
    }
    static string ResolveDollar(string v, SessionInfo s)
    {
        bool src = v.StartsWith("$Source", StringComparison.OrdinalIgnoreCase);
        var cand = s.Connections.Where(kv => kv.Value.rw.Equals(src ? "READER" : "WRITER", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value.conn).Distinct().ToList();
        return cand.Count == 1 ? cand[0] : v;
    }

    static bool IsEndpoint(Instance? i) => i == null || i.Type is "SOURCE" or "LOOKUPTABLE" or "SQLTABLE" or "PROCEDURE" or "TARGET";

    (int tgtCols, int tgtWith) EmitPhysical(Folder f, MappingGraph g, List<SessionInfo> sessions)
    {
        int tgtCols = 0, tgtWith = 0;
        foreach (var s in sessions)
        {
            bool first = ReferenceEquals(s, sessions[0]);
            foreach (var i in g.Instances.Values)
            {
                if (!IsEndpoint(i) || i.Type == "MAPPLET") continue;
                var (conn, dbt, owner, name, type) = Physical(i, s);
                string action = i.Type switch
                {
                    "SOURCE" => "Reads",
                    "TARGET" => (s.InstanceAttrs.GetValueOrDefault(i.Name)?.GetValueOrDefault("Truncate target table option") ?? "NO").Equals("YES", StringComparison.OrdinalIgnoreCase) ? "TruncateAndWrites" : "Writes",
                    "LOOKUPTABLE" => "Lookup", "SQLTABLE" => "ReadsViaSql", "PROCEDURE" => "Calls", _ => "?"
                };
                if (i.Type == "TARGET" && !g.Edges.Any(e => e.ToInst.Equals(i.Name, StringComparison.OrdinalIgnoreCase))) action = "TargetNotConnected";
                ObjectRows.Add(new ObjectLineageRow { RunId = runId, Folder = f.Name, Workflow = s.Workflow, Session = s.Name, Mapping = g.Name, Instance = i.Name, InstanceType = type, Action = action, Connection = conn, DbType = dbt, ObjectSchema = owner, ObjectName = name, ObjectType = type, Confidence = i.Def == null && i.Type is "SOURCE" or "TARGET" ? "Low" : conn == "" ? "Medium" : "High", Note = i.Type == "SQLTABLE" ? "SQL override" : "" });
            }
            foreach (var t in g.Instances.Values.Where(x => x.Type == "TARGET"))
            {
                var (tconn, tdbt, towner, tname, ttype) = Physical(t, s);
                foreach (var tp in t.Ports)
                {
                    if (first) tgtCols++;
                    var seen = new HashSet<string>(); int found = 0; int budget = 20000;
                    Walk(g, t.Name, tp.Name, 0, tp.Name, FlowKind.Direct, new HashSet<string>(), "", (srcInst, srcPort, hops, path, kind, expr) =>
                    {
                        if (budget-- <= 0) return;
                        var si = g.Instances.GetValueOrDefault(srcInst);
                        var (sconn, sdbt, sowner, sname, stype) = si != null ? Physical(si, s) : ("", "", "", srcInst, "?");
                        bool constant = si != null && !IsEndpoint(si);
                        if (constant) { sname = srcInst; stype = "Constant/Unconnected"; }
                        string key = $"{srcInst}|{srcPort}|{tp.Name}|{kind}";
                        if (!seen.Add(key)) return;
                        found++;
                        ColumnRows.Add(new ColumnLineageRow
                        {
                            RunId = runId, Folder = f.Name, Workflow = s.Workflow, Session = s.Name, Mapping = g.Name,
                            SourceConnection = sconn, SourceDbType = sdbt, SourceSchema = sowner, SourceObject = sname, SourceObjectType = stype, SourceColumn = srcPort,
                            TargetConnection = tconn, TargetDbType = tdbt, TargetSchema = towner, TargetObject = tname, TargetObjectType = ttype, TargetColumn = tp.Name,
                            FlowKind = kind.ToString(), Hops = hops, Path = path, Expression = expr, Confidence = constant ? "Medium" : kind == FlowKind.Positional ? "Medium" : si == null ? "Low" : "High", Note = constant ? "kaynağa bağlı olmayan port (sabit/ifade)" : si == null ? "kaynak instance çözülmedi" : ""
                        });
                    });
                    if (found > 0 && first) tgtWith++;
                }
            }
        }
        return (tgtCols, tgtWith);
    }

    void Walk(MappingGraph g, string inst, string port, int hops, string path, FlowKind kind, HashSet<string> visited, string expr, Action<string, string, int, string, FlowKind, string> emit)
    {
        string key = MappingGraph.Key(inst, port);
        if (hops > cfg.MaxCollapseDepth || !visited.Add(key)) return;
        var edges = g.Incoming.GetValueOrDefault(key) ?? g.Incoming.GetValueOrDefault(MappingGraph.Key(inst, "*"));
        if (edges == null || edges.Count == 0)
        {
            if (hops > 0) emit(inst, port, hops, path, kind, expr);
            visited.Remove(key); return;
        }
        foreach (var e in edges)
        {
            var k2 = (kind == FlowKind.Indirect || e.Kind == FlowKind.Indirect) ? FlowKind.Indirect : Stronger(kind, e.Kind);
            string p2 = e.FromInst + "." + e.FromPort + " → " + path;
            string ex2 = e.Expr != "" ? e.Expr : expr;
            var si = g.Instances.GetValueOrDefault(e.FromInst);
            bool endpoint = (si != null && si.Type is "SOURCE" or "LOOKUPTABLE" or "SQLTABLE" or "PROCEDURE") || e.FromInst.StartsWith("SQL:") || e.FromInst.StartsWith("LKP:") || e.FromInst.StartsWith("SP:");
            if (endpoint)
            {
                string k = MappingGraph.Key(e.FromInst, e.FromPort);
                if (visited.Add(k)) { emit(e.FromInst, e.FromPort, hops + 1, p2, k2, ex2); visited.Remove(k); }
            }
            else Walk(g, e.FromInst, e.FromPort, hops + 1, p2, k2, visited, ex2, emit);
        }
        visited.Remove(key);
    }
    static FlowKind Stronger(FlowKind a, FlowKind b)
    {
        static int R(FlowKind k) => k switch { FlowKind.Direct => 0, FlowKind.Positional => 1, FlowKind.Expression => 2, FlowKind.Aggregate => 3, _ => -1 };
        return R(a) >= R(b) ? a : b;
    }
}

// ---------------------------------------------------------------- çıktı
static class InfaOutput
{
    static readonly HashSet<string> LongCols = new(StringComparer.OrdinalIgnoreCase) { "Expression", "Note", "Path", "Errors", "SqlOverride", "Name" };
    static readonly (string, Type)[] Tables = { ("Summary", typeof(SummaryRow)), ("ColumnLineage", typeof(ColumnLineageRow)), ("ObjectLineage", typeof(ObjectLineageRow)), ("PortLineage", typeof(PortLineageRow)), ("Mappings", typeof(MappingRow)), ("Sessions", typeof(SessionRow)), ("Unresolved", typeof(UnresolvedRow)) };

    public static void Write(InfaConfig cfg, string runId, InfaRun r)
    {
        Directory.CreateDirectory(cfg.OutDir);
        long total = r.ColumnRows.Count + r.PortRows.Count + r.ObjectRows.Count;
        if (cfg.Xlsx && total > 3_000_000) { InfaLog.Warn($"Excel atlandı ({total:N0} satır); CSV/SqlBulkCopy kullanın."); cfg.Xlsx = false; cfg.Csv = true; }
        if (cfg.Xlsx)
        {
            var path = Path.Combine(cfg.OutDir, $"infa-lineage-{runId}.xlsx");
            using var wb = new XLWorkbook();
            Sheet(wb, "Summary", r.SummaryRows, cfg); Sheet(wb, "ColumnLineage", r.ColumnRows, cfg); Sheet(wb, "ObjectLineage", r.ObjectRows, cfg); Sheet(wb, "PortLineage", r.PortRows, cfg);
            Sheet(wb, "Mappings", r.MappingRows, cfg); Sheet(wb, "Sessions", r.SessionRows, cfg); Sheet(wb, "Unresolved", r.UnresolvedRows, cfg);
            wb.SaveAs(path); InfaLog.Info("Excel: " + path);
        }
        if (cfg.Csv)
        {
            var dir = Path.Combine(cfg.OutDir, "infa-csv-" + runId); Directory.CreateDirectory(dir);
            Csv(Path.Combine(dir, "Summary.csv"), r.SummaryRows, cfg.CsvSeparator); Csv(Path.Combine(dir, "ColumnLineage.csv"), r.ColumnRows, cfg.CsvSeparator); Csv(Path.Combine(dir, "ObjectLineage.csv"), r.ObjectRows, cfg.CsvSeparator);
            Csv(Path.Combine(dir, "PortLineage.csv"), r.PortRows, cfg.CsvSeparator); Csv(Path.Combine(dir, "Mappings.csv"), r.MappingRows, cfg.CsvSeparator); Csv(Path.Combine(dir, "Sessions.csv"), r.SessionRows, cfg.CsvSeparator); Csv(Path.Combine(dir, "Unresolved.csv"), r.UnresolvedRows, cfg.CsvSeparator);
            InfaLog.Info("CSV: " + dir);
        }
        if (cfg.Ddl) { var p = Path.Combine(cfg.OutDir, "infa_lineage_tables.sql"); File.WriteAllText(p, Ddl(cfg.SqlSchema), new UTF8Encoding(true)); InfaLog.Info("DDL: " + p); }
        if (!string.IsNullOrWhiteSpace(cfg.SqlBulkTarget))
        {
            try
            {
                using var cn = new SqlConnection(cfg.SqlBulkTarget); cn.Open();
                foreach (var b in Regex.Split(Ddl(cfg.SqlSchema), @"^\s*GO\s*$", RegexOptions.Multiline)) { var t = b.Trim(); if (t.Length == 0 || t.StartsWith("--")) continue; using var cmd = new SqlCommand(t, cn); cmd.ExecuteNonQuery(); }
                Load(cn, cfg.SqlSchema, "Summary", r.SummaryRows); Load(cn, cfg.SqlSchema, "ColumnLineage", r.ColumnRows); Load(cn, cfg.SqlSchema, "ObjectLineage", r.ObjectRows); Load(cn, cfg.SqlSchema, "PortLineage", r.PortRows);
                Load(cn, cfg.SqlSchema, "Mappings", r.MappingRows); Load(cn, cfg.SqlSchema, "Sessions", r.SessionRows); Load(cn, cfg.SqlSchema, "Unresolved", r.UnresolvedRows);
                InfaLog.Info($"SqlBulkCopy: {cfg.SqlSchema}.* yüklendi");
            }
            catch (Exception ex) { InfaLog.Error("SqlBulkCopy: " + ex.Message); }
        }
    }
    static void Sheet<T>(XLWorkbook wb, string name, List<T> rows, InfaConfig cfg)
    {
        int max = Math.Max(1000, cfg.MaxRowsPerSheet); int part = 0;
        for (int start = 0; start == 0 || start < rows.Count; start += max)
        {
            part++;
            var ws = wb.Worksheets.Add(part == 1 ? name : $"{name}_{part}");
            var slice = rows.Skip(start).Take(max).ToList(); var props = typeof(T).GetProperties();
            for (int c = 0; c < props.Length; c++) ws.Cell(1, c + 1).Value = props[c].Name;
            ws.Row(1).Style.Font.Bold = true;
            if (slice.Count > 0) { if (slice.Count <= 100_000) ws.Cell(1, 1).InsertTable(slice, name + (part > 1 ? part.ToString() : ""), true); else ws.Cell(2, 1).InsertData(slice); }
            ws.SheetView.FreezeRows(1);
            if (slice.Count <= 20_000) ws.Columns().AdjustToContents(1, Math.Min(slice.Count + 1, 200), 8, 60);
        }
    }
    static void Csv<T>(string path, List<T> rows, string sep)
    {
        var props = typeof(T).GetProperties(); using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(sep, props.Select(p => p.Name)));
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Clear();
            for (int i = 0; i < props.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = props[i].GetValue(r); string s = v switch { null => "", double d => d.ToString(CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
                if (s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(s);
            }
            w.WriteLine(sb.ToString());
        }
    }
    static string SqlType(PropertyInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (t == typeof(int)) return "int";
        if (t == typeof(double)) return "float";
        if (t == typeof(bool)) return "bit";
        if (t == typeof(DateTime)) return "datetime2(3)";
        return LongCols.Contains(p.Name) ? "nvarchar(max)" : "nvarchar(512)";
    }
    public static string Ddl(string schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');").AppendLine("GO");
        foreach (var (table, type) in Tables)
        {
            sb.AppendLine($"IF OBJECT_ID(N'[{schema}].[{table}]') IS NULL").AppendLine($"CREATE TABLE [{schema}].[{table}] (").AppendLine("    [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            var props = type.GetProperties();
            for (int i = 0; i < props.Length; i++) sb.AppendLine($"    [{props[i].Name}] {SqlType(props[i])} NULL{(i < props.Length - 1 ? "," : "")}");
            sb.AppendLine(");").AppendLine("GO");
        }
        return sb.ToString();
    }
    static void Load<T>(SqlConnection cn, string schema, string table, List<T> rows)
    {
        var props = typeof(T).GetProperties(); var dt = new DataTable();
        foreach (var p in props) dt.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
        foreach (var r in rows) { var vals = new object[props.Length]; for (int i = 0; i < props.Length; i++) vals[i] = props[i].GetValue(r) ?? DBNull.Value; dt.Rows.Add(vals); }
        using var bc = new SqlBulkCopy(cn) { DestinationTableName = $"[{schema}].[{table}]", BatchSize = 10_000, BulkCopyTimeout = 0 };
        foreach (var p in props) bc.ColumnMappings.Add(p.Name, p.Name);
        bc.WriteToServer(dt);
    }
}

}
#endregion
