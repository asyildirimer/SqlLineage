// ============================================================================
#region 1. Giriş / Config
// ============================================================================

public static class App
{
    public const string EngineVersion = "2.0.0";
    public static async Task<int> RunAsync(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // API'den çağrılırken de (Program.cs atlanırsa)
        if (args.Length > 0 && args[0].Equals("infa", StringComparison.OrdinalIgnoreCase)) return await InfaLineage.App.RunAsync(args[1..]);
        try
        {
            var parsed = Cli.Parse(args);
            if (parsed == null) return 1;
            var (cmd, cfg, o) = parsed.Value;
            string work = o.WorkDir ?? cfg.WorkDirectory;
            string node = o.Node ?? cfg.Node;
            switch (cmd)
            {
                case "plan":
                    await LineagePipeline.PlanAsync(cfg, work, o.Force);
                    Console.WriteLine(LineagePipeline.Status(work, false));
                    return 0;
                case "run":
                    if (o.TaskId != null) { var t = await LineagePipeline.RunTaskAsync(cfg, work, o.TaskId, node, !o.NoPlanUpdate); return t.Status == "done" ? 0 : 3; }
                    if (o.Next || o.Loop) { await LineagePipeline.RunPendingAsync(cfg, work, node, o.Loop); return 0; }
                    Console.WriteLine("run: --task <id> ya da --next / --loop verin (SqlLineage status ile görevleri görün)."); return 1;
                case "next":
                    {
                        var t = LineagePipeline.ClaimNext(work, node);
                        Console.WriteLine(t == null ? "{}" : JsonSerializer.Serialize(t, Cli.JsonOpts));
                        return 0;
                    }
                case "merge":
                    await LineagePipeline.MergeAsync(cfg, work); return 0;
                case "status":
                    Console.WriteLine(LineagePipeline.Status(work, o.Json)); return 0;
                default: // run-all (eski tek komutlu kullanım)
                    await LineagePipeline.RunAllAsync(cfg, work, o.Resume); return 0;
            }
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

public sealed class LineageConfig
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
    /// <summary>Çalışma klasörü: plan.json, prepass/, parts/. Dağıtık koşuda tüm düğümlerin gördüğü paylaşılan klasör olmalı.</summary>
    public string WorkDirectory { get; set; } = "work";
    /// <summary>Parça CSV'lerini gzip'le (paylaşılan diske/ağa yazarken küçük dosya).</summary>
    public bool CompressParts { get; set; } = false;
    /// <summary>Merge'de ColumnLineage/ObjectLineage satırlarını tekilleştir (yeniden koşulan görevlerin mükerrerine karşı emniyet).</summary>
    public bool MergeDedup { get; set; } = true;
    /// <summary>Bu düğümün adı (plan.json'da görev sahibi olarak yazılır). Varsayılan: makine adı.</summary>
    public string Node { get; set; } = Environment.MachineName;
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
    /// <summary>Bu şemalardaki modüller (proc/view/fonksiyon/trigger) analiz edilmez; tablolar katalogda kalır (referanslar çözülür).</summary>
    public List<string> ExcludeSchemas { get; set; } = new();
    /// <summary>Bu DB bağlamında koşan Agent job adımları analiz edilmez (örn. ["msdb","tempdb"]). excludeDatabases da job adımlarına uygulanır.</summary>
    public List<string> ExcludeJobDatabases { get; set; } = new();
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

    public static LineageConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<LineageConfig>(File.ReadAllText(path), Cli.JsonOpts) ?? new LineageConfig();
        for (int i = 0; i < cfg.Connections.Count; i++)
            if (string.IsNullOrWhiteSpace(cfg.Connections[i].Name)) cfg.Connections[i].Name = "SRV" + (i + 1);
        return cfg;
    }
}

public sealed class ConnectionConfig
{
    public string Name { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    /// <summary>Boşsa global "databases" geçerli.</summary>
    public List<string> Databases { get; set; } = new();
    public List<string> ExcludeDatabases { get; set; } = new();
    /// <summary>Bu şemalardaki modüller (proc/view/fonksiyon/trigger) analiz edilmez; tablolar katalogda kalır (referanslar çözülür).</summary>
    public List<string> ExcludeSchemas { get; set; } = new();
    /// <summary>Bu DB bağlamında koşan Agent job adımları analiz edilmez (örn. ["msdb","tempdb"]). excludeDatabases da job adımlarına uygulanır.</summary>
    public List<string> ExcludeJobDatabases { get; set; } = new();
    /// <summary>Bu sunucuda tanımlı linked server adı → taranan sunucunun adı (sys.servers.data_source eşleşmiyorsa).</summary>
    public Dictionary<string, string> ServerAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class QueryConfig
{
    /// <summary>"db.schema.object" ya da "db.schema.object.column" (sunucu isteğe bağlı: "server.db.schema.object").</summary>
    public string Node { get; set; } = "";
    public string Direction { get; set; } = "up";   // up | down | both
    public int MaxHops { get; set; } = 10;
    public int MaxRows { get; set; } = 500_000;
}
public sealed class GraphConfig
{
    public string Node { get; set; } = "";
    public int MaxHops { get; set; } = 3;
    public string Format { get; set; } = "dot";     // dot | graphml | json
    public string Level { get; set; } = "object";   // object | column
}

public sealed class OutputConfig
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

sealed class CliOptions
{
    public string? WorkDir, TaskId, Node;
    public bool Next, Loop, Force, Resume, NoPlanUpdate, Json;
}

static class Cli
{
    static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase) { "plan", "run", "run-all", "merge", "status", "next" };

    const string Usage = """
        SqlLineage — MSSQL lineage çıkarıcı (DB DB görev akışı)

          SqlLineage run-all  [seçenekler]        plan + tüm görevler sırayla + merge (tek makine; alt komut verilmezse bu)
          SqlLineage plan     [--force]           sunuculara bağlan, DB listesini plan.json'a yaz (work/plan.json)
          SqlLineage run      --task <id>         plan.json'daki tek görevi çalıştır (id: prepass:SRV:DB | analyze:SRV:DB)
          SqlLineage run      --next [--loop]     sıradaki görevi kilitle-al-çalıştır (--loop: görev kalmayana kadar)
          SqlLineage next                          sıradaki görevi "running" yapıp JSON olarak yazdırır (kendi kuyruğunuz için)
          SqlLineage merge                         parçaları birleştir → CSV/Excel/DDL/SQL (prosedürler arası + indirgeme burada)
          SqlLineage status   [--json]             plan durumu
          SqlLineage infa ...                      Informatica PowerCenter XML export modu (infa --help)

          --config <lineage.json>      yapılandırma dosyası (varsayılan: ./lineage.json varsa)
          --init                       örnek lineage.json yazar ve çıkar
          --work <dir>                 çalışma klasörü: plan.json, prepass/, parts/ (varsayılan work; dağıtıkta paylaşılan klasör)
          --node <ad>                  bu düğümün adı (varsayılan makine adı)
          --resume                     run-all: mevcut planın bekleyen görevlerini bitirip merge et
          --no-plan-update             run --task: plan.json'a durum yazma (durumu kendi servisiniz tutuyorsa)
          --compress-parts             parça CSV'lerini gzip'le
          --no-dedup                   merge'de satır tekilleştirmeyi kapat
          --server "<name>=<connstr>"  bağlantı ekler (tekrarlanabilir; name isteğe bağlı)
          --db <name>                  yalnız bu DB'ler (tekrarlanabilir)
          --files <path>               .sql dosyası/klasörü (bağlantısız mod; tekrarlanabilir)
          --out <dir>                  çıktı klasörü (varsayılan out)
          --sql-target "<connstr>"     merge: sonuçları bu DB'de lineage.* tablolarına bulk yükler
          --no-xlsx / --no-csv         ilgili çıktıyı kapatır
          --parallel <n>               paralel modül analizi
          --config-tables              dinamik SQL delikleri için kaynak tablodan DISTINCT değer okur (canlı, salt okunur)
          --exclude-db <ad>            bu DB'yi tarama (tekrarlanabilir)
          --incremental                --sql-target ile: değişmeyen modülleri önceki koşudan geri yükle
          --statements                 her ifadenin metnini Statements tablosuna yaz
          --query "<db.schema.obj[.col]>" [--direction up|down|both] [--max-hops N]   merge: çok sekmeli kolon soyağacı
          --graph "<db.schema.obj[.col]>" [--graph-hops N] [--graph-format dot|graphml|json] [--graph-level object|column]
          --verbose
        """;

    public static (string cmd, LineageConfig cfg, CliOptions opts)? Parse(string[] args)
    {
        string cmd = "run-all";
        int start = 0;
        if (args.Length > 0 && Commands.Contains(args[0])) { cmd = args[0].ToLowerInvariant(); start = 1; }
        string? cfgPath = null;
        var overrides = new List<Action<LineageConfig>>();
        var o = new CliOptions();
        for (int i = start; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} için değer eksik");
            switch (a)
            {
                case "--config": cfgPath = Next(); break;
                case "--init": WriteSample(); return null;
                case "--work": o.WorkDir = Next(); break;
                case "--task": o.TaskId = Next(); break;
                case "--node": o.Node = Next(); break;
                case "--next": o.Next = true; break;
                case "--loop": o.Loop = true; break;
                case "--force": o.Force = true; break;
                case "--resume": o.Resume = true; break;
                case "--no-plan-update": o.NoPlanUpdate = true; break;
                case "--json": o.Json = true; break;
                case "--compress-parts": overrides.Add(c => c.CompressParts = true); break;
                case "--no-dedup": overrides.Add(c => c.MergeDedup = false); break;
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
            cfg = LineageConfig.Load(cfgPath);
            Log.Info($"Config: {Path.GetFullPath(cfgPath)}");
        }
        else cfg = new LineageConfig();
        foreach (var f in overrides) f(cfg);
        Log.Verbose = cfg.Verbose;
        bool needsConn = cmd is "plan" or "run" or "run-all" or "next";
        if (needsConn && cfg.Connections.Count == 0 && cfg.SqlFiles.Count == 0 && cmd != "next")
        {
            Console.WriteLine(Usage);
            Console.WriteLine("\nNe bağlantı ne de --files verildi. `--init` ile örnek lineage.json üretin.");
            return null;
        }
        for (int i = 0; i < cfg.Connections.Count; i++)
            if (string.IsNullOrWhiteSpace(cfg.Connections[i].Name)) cfg.Connections[i].Name = "SRV" + (i + 1);
        return (cmd, cfg, o);
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
        Console.WriteLine("lineage.json yazıldı. Bağlantı dizesini düzenleyip: SqlLineage run-all --config lineage.json");
        Console.WriteLine("Not: Microsoft.Data.SqlClient varsayılan olarak Encrypt=True; sertifika yoksa TrustServerCertificate=True ekleyin.");
    }
}

#endregion
