// ============================================================================
#region 3. Katalog (tembel: sunucu listesi önce, DB içeriği ilk erişimde, modül tanımları akış halinde)
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
    public bool DefinitionMissing;         // WITH ENCRYPTION / CLR
    public bool HasModuleRow;              // sys.sql_modules / numbered_procedures satırı var (tanım tembel çekilebilir)
    public bool QuotedIdentifier = true;
    public bool AnsiNulls = true;
    public List<ParamInfo> Params = new();
    public int? ParentObjectId;            // trigger
    public bool IsDisabled, IsInsteadOf;
    public string TriggerEvents = "";
    public string DefaultSchema = "dbo";   // sahibin default şeması (şemasız EXEC çözümü için)
    public int? ProcedureNumber;
    public int NumberedParentId;           // numaralı prosedür: ana prosedürün object_id'si
    public bool IsMsShipped;
    public DbCatalog? Db;                  // sahibi (tembel tanım çekimi için)
    public string? LoadError;              // tanım sunucudan çekilemedi (görev sürer, modül Error olur)

    string? _definition; bool _defFetched;
    /// <summary>Modül metni. Sunucu kataloğunda tembeldir: ilk okumada tek nesne için çekilir; analiz sonrası <see cref="ReleaseDefinition"/> ile bırakılır.</summary>
    public string? Definition
    {
        get
        {
            if (!_defFetched && HasModuleRow && !DefinitionMissing && Db?.DefinitionFetcher != null)
            {
                _definition = Db.DefinitionFetcher(this);
                _defFetched = true;
            }
            return _definition;
        }
        set { _definition = value; _defFetched = true; if (value != null) HasModuleRow = true; }
    }
    public bool DefinitionLoaded => _defFetched;
    /// <summary>Tanım metnini bellekten bırakır (yalnız tembel çekilebilenlerde; gerekirse yeniden çekilir).</summary>
    public void ReleaseDefinition() { if (Db?.DefinitionFetcher != null) { _definition = null; _defFetched = false; } }
    /// <summary>Akış yükleyicisi: metni koyar (bırakıldıktan sonra tembel çekimle yeniden gelebilir).</summary>
    internal void SetStreamedDefinition(string? def) { _definition = def; _defFetched = true; }

    public string ModuleTypeName => TypeCode switch
    {
        "P" => "Procedure", "PC" => "ClrProcedure", "V" => "View", "FN" => "ScalarFunction", "IF" => "InlineTvf",
        "TF" => "MsTvf", "FS" => "ClrScalarFunction", "FT" => "ClrTvf", "TR" => "Trigger", "JOB" => "JobStep", "FILE" => "Script",
        _ => TypeCode
    };
    public bool IsModule => TypeCode is "P" or "V" or "FN" or "IF" or "TF" or "TR" or "JOB" or "FILE" or "PC" or "FS" or "FT";
    public bool IsClr => TypeCode is "PC" or "FS" or "FT";
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
    public bool DepsLoaded;
    public ConcurrentDictionary<string, ObjInfo> Overlay = new(NameComparer.Instance); // SELECT INTO / CREATE TABLE ile tarama sırasında yaratılanlar
    public bool IsTurkish => Collation.StartsWith("Turkish", StringComparison.OrdinalIgnoreCase);
    /// <summary>Tek nesnenin tanımını sunucudan çeker (tembel). Dosya modunda null.</summary>
    public Func<ObjInfo, string?>? DefinitionFetcher;
    /// <summary>Modül tanımlarını akış halinde verir (sys.sql_modules tek sorgu). Dosya modunda modülleri olduğu gibi döner.</summary>
    public Func<IEnumerable<ObjInfo>, IEnumerable<ObjInfo>>? DefinitionStreamer;
    /// <summary>Tanım çekme istatistikleri (görev özeti için).</summary>
    public long FetchMs, FetchBatches, FetchRetries, SingleFetches;

    public ObjInfo? Find(string schema, string name)
    {
        if (Objects.TryGetValue(schema + "." + name, out var o)) return o;
        if (Overlay.TryGetValue(schema + "." + name, out o)) return o;
        return null;
    }
    public IEnumerable<ObjInfo> FindByName(string name) => Objects.Values.Where(o => NameComparer.Eq(o.Ref.Name, name));
    public void Add(ObjInfo o)
    {
        o.Db = this;
        Objects[o.Ref.Schema + "." + o.Ref.Name] = o;
        if (o.ObjectId != 0) ById[o.ObjectId] = o;
    }
    /// <summary>Verilen modülleri tanımları doldurulmuş olarak (akışla) sırayla verir; tüketici işi bitince ReleaseDefinition çağırır.</summary>
    public IEnumerable<ObjInfo> StreamModules(IEnumerable<ObjInfo> modules) => DefinitionStreamer != null ? DefinitionStreamer(modules) : modules;
}

sealed class LinkedServerInfo { public string Name = ""; public string DataSource = ""; public string Catalog = ""; public string Provider = ""; }

sealed class JobStepInfo
{
    public string JobName = ""; public int StepId; public string StepName = ""; public string Subsystem = ""; public string Command = ""; public string DatabaseName = ""; public bool Enabled;
}

sealed class KnownDb { public string Name = ""; public int Compat = 150; public string Collation = ""; public int DatabaseId; }

sealed class ServerCatalog
{
    public string Name = "";            // SERVERPROPERTY('ServerName')
    public string ConfigName = "";
    public string MachineName = "";
    public string ConnectionString = "";
    public int Major = 15;
    public string Version = "";
    /// <summary>Yüklenmiş DB'ler. Tembel yüklemede <see cref="GetOrLoad"/> doldurur.</summary>
    public ConcurrentDictionary<string, DbCatalog> Dbs = new(NameComparer.Instance);
    /// <summary>Filtreden geçen tüm DB'ler (içeriği yüklenmemiş olabilir).</summary>
    public Dictionary<string, KnownDb> KnownDbs = new(NameComparer.Instance);
    public Dictionary<string, LinkedServerInfo> Linked = new(NameComparer.Instance);
    public List<JobStepInfo> JobSteps = new();
    readonly ConcurrentDictionary<string, Lazy<DbCatalog?>> _lazy = new(NameComparer.Instance);

    public IEnumerable<string> DbNames => KnownDbs.Count > 0 ? KnownDbs.Keys : Dbs.Keys;
    public bool Knows(string db) => KnownDbs.ContainsKey(db) || Dbs.ContainsKey(db);

    public DbCatalog? GetOrLoad(string name, Func<ServerCatalog, KnownDb, DbCatalog?>? loader)
    {
        if (Dbs.TryGetValue(name, out var d)) return d;
        if (loader == null || !KnownDbs.TryGetValue(name, out var k)) return null;
        var lazy = _lazy.GetOrAdd(k.Name, _ => new Lazy<DbCatalog?>(() => loader(this, k), LazyThreadSafetyMode.ExecutionAndPublication));
        var db = lazy.Value;
        if (db != null) Dbs[db.Name] = db;
        return db;
    }
}

sealed class Catalog
{
    public List<ServerCatalog> Servers = new();
    public List<Regex> ArchivePatterns = new();
    /// <summary>Tembel DB yükleyicisi (sunucu kataloğu). Dosya modunda null.</summary>
    public Func<ServerCatalog, KnownDb, DbCatalog?>? DbLoader;
    /// <summary>Ön geçişten gelen kod-yaratımı tablolar: "server|db" → nesneler. DB yüklenince overlay'e uygulanır.</summary>
    public readonly ConcurrentDictionary<string, List<ObjInfo>> PendingOverlays = new(NameComparer.Instance);

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

    /// <summary>DB kataloğu; yüklü değilse (ve biliniyorsa) yükler. Arşiv adları kanonik DB'ye düşer.</summary>
    public DbCatalog? FindDb(string server, string db)
    {
        var srv = FindServer(server); if (srv == null) return null;
        var d = srv.GetOrLoad(db, DbLoader);
        if (d != null) return d;
        var canon = Canonical(db);
        return !NameComparer.Eq(canon, db) ? srv.GetOrLoad(canon, DbLoader) : null;
    }
    /// <summary>Yüklemeden: bu DB taranan bir DB mi?</summary>
    public bool HasDb(string server, string db)
    {
        var srv = FindServer(server); if (srv == null) return false;
        if (srv.Knows(db)) return true;
        var canon = Canonical(db);
        return !NameComparer.Eq(canon, db) && srv.Knows(canon);
    }

    public void ApplyPendingOverlays(DbCatalog db)
    {
        if (!PendingOverlays.TryGetValue(db.Server + "|" + db.Name, out var list)) return;
        foreach (var o in list)
        {
            if (db.Find(o.Ref.Schema, o.Ref.Name) != null) continue;
            o.Db = db;
            db.Overlay.TryAdd(o.Ref.Schema + "." + o.Ref.Name, o);
        }
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
    public static void ResetCache() => _cache.Clear();
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
    static readonly HashSet<string> SystemDbs = new(NameComparer.Instance) { "master", "msdb", "tempdb", "model", "distribution" };
    /// <summary>Job adımı analiz edilecek mi: TSQL alt sistemi, DB'si excludeDatabases / excludeJobDatabases'te değil.</summary>
    public static bool JobStepWanted(LineageConfig cfg, ServerCatalog s, JobStepInfo js)
    {
        if (!NameComparer.Eq(js.Subsystem, "TSQL")) return false;
        var cc = cfg.Connections.FirstOrDefault(c => NameComparer.Eq(c.Name, s.ConfigName));
        if (cfg.ExcludeJobDatabases.Any(x => NameComparer.Eq(x, js.DatabaseName))) return false;
        if (cfg.ExcludeDatabases.Any(x => NameComparer.Eq(x, js.DatabaseName))) return false;
        if (cc != null && cc.ExcludeDatabases.Any(x => NameComparer.Eq(x, js.DatabaseName))) return false;
        return true;
    }
    public static bool IsSystemDb(string name) => SystemDbs.Contains(name);

    /// <summary>Sunucuları bağlar: sürüm, linked server'lar, job adımları, filtreden geçen DB listesi. DB içeriği yüklenmez (tembel).</summary>
    public static async Task<Catalog> LoadServersAsync(LineageConfig cfg)
    {
        var cat = new Catalog();
        foreach (var p in cfg.ArchiveDatabasePatterns) { try { cat.ArchivePatterns.Add(new Regex(p, RegexOptions.IgnoreCase)); } catch (Exception ex) { Log.Warn($"ArchiveDatabasePatterns geçersiz regex '{p}': {ex.Message}"); } }
        cat.DbLoader = (s, k) =>
        {
            try { var db = LoadDb(cfg, s, k, withDeps: false); cat.ApplyPendingOverlays(db); return db; }
            catch (Exception ex) { Log.Error($"[{s.ConfigName}] {k.Name} yüklenemedi: {ex.Message}"); return null; }
        };
        var tasks = cfg.Connections.Select(c => LoadServerAsync(cfg, cat, c)).ToList();
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

    /// <summary>Hedef DB'yi tam yükler (bağımlılıklar dahil) ve overlay'leri uygular.</summary>
    public static DbCatalog? EnsureTargetDb(LineageConfig cfg, Catalog cat, ServerCatalog s, string dbName)
    {
        if (s.Dbs.TryGetValue(dbName, out var loaded))
        {
            if (!loaded.DepsLoaded && !string.IsNullOrEmpty(s.ConnectionString)) LoadDeps(s, loaded);
            cat.ApplyPendingOverlays(loaded);
            return loaded;
        }
        if (!s.KnownDbs.TryGetValue(dbName, out var k)) return null;
        var db = LoadDb(cfg, s, k, withDeps: true);
        cat.ApplyPendingOverlays(db);
        s.Dbs[db.Name] = db;
        return db;
    }

    static async Task<ServerCatalog?> LoadServerAsync(LineageConfig cfg, Catalog cat, ConnectionConfig cc)
    {
        var sw = Stopwatch.StartNew();
        var s = new ServerCatalog { ConfigName = cc.Name, ConnectionString = cc.ConnectionString };
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
            Log.Debug($"[{cc.Name}] bağlandı: {s.Name} (v{s.Version})");
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
                    Log.Debug($"[{cc.Name}] {s.JobSteps.Count} job adımı");
                }
                catch (Exception ex) { Log.Warn($"[{cc.Name}] msdb job adımları okunamadı: {ex.Message}"); }
            }

            // databases (yalnız liste)
            using (var cmd = new SqlCommand("SELECT name, compatibility_level, ISNULL(collation_name,''), database_id FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name", cn))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    var name = r.GetString(0); int id = r.GetInt32(3);
                    var dbList = cc.Databases.Count > 0 ? cc.Databases : cfg.Databases;
                    bool wanted = dbList.Contains("*") ? id > 4 && !NameComparer.Eq(name, "distribution") : dbList.Any(d => NameComparer.Eq(d, name));
                    if (wanted && (cc.ExcludeDatabases.Any(x => NameComparer.Eq(x, name)) || cfg.ExcludeDatabases.Any(x => NameComparer.Eq(x, name)))) { Log.Debug($"[{cc.Name}] {name} hariç tutuldu (excludeDatabases)"); wanted = false; }
                    if (wanted && cfg.SkipArchiveDatabases && cat.IsArchive(name)) { Log.Debug($"[{cc.Name}] {name} arşiv DB (→ {cat.Canonical(name)}), taranmıyor"); wanted = false; }
                    if (wanted) s.KnownDbs[name] = new KnownDb { Name = name, Compat = Convert.ToInt32(r.GetValue(1)), Collation = r.GetString(2), DatabaseId = id };
                }
            Log.Debug($"[{cc.Name}] {s.KnownDbs.Count} DB listelendi ({sw.ElapsedMilliseconds} ms); içerik ilk erişimde yüklenir");
            return s;
        }
        catch (Exception ex)
        {
            Log.Error($"[{cc.Name}] bağlantı/katalog hatası: {ex.Message}" + (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? "  → bağlantı dizesine TrustServerCertificate=True ekleyin" : ""));
            return null;
        }
    }

    /// <summary>Plan aşaması için DB boyutu: modül sayısı ve toplam tanım baytı.</summary>
    public static (int modules, long definitionBytes) MeasureDb(ServerCatalog s, string dbName)
    {
        if (string.IsNullOrEmpty(s.ConnectionString)) return (s.Dbs.TryGetValue(dbName, out var d) ? d.Modules.Count : 0, 0);
        try
        {
            using var cn = new SqlConnection(s.ConnectionString); cn.Open();
            using var cmd = new SqlCommand($"SELECT COUNT(*), ISNULL(SUM(CAST(DATALENGTH(definition) AS bigint)),0) FROM {Q(dbName)}.sys.sql_modules", cn) { CommandTimeout = 120 };
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt64(1));
        }
        catch (Exception ex) { Log.Debug($"MeasureDb {s.ConfigName}.{dbName}: {ex.Message}"); return (0, 0); }
    }

    static string Q(string dbName) => "[" + dbName.Replace("]", "]]") + "]";

    /// <summary>DB kataloğu: nesneler, kolonlar, TVP'ler, synonym'ler, parametreler, trigger'lar; modül tanımları TEMBEL (metin çekilmez).</summary>
    public static DbCatalog LoadDb(LineageConfig cfg, ServerCatalog s, KnownDb k, bool withDeps)
    {
        var sw = Stopwatch.StartNew();
        string dbName = k.Name;
        var db = new DbCatalog { Server = s.Name, Name = dbName, Compat = k.Compat, Collation = k.Collation };
        using var cn = new SqlConnection(s.ConnectionString);
        cn.Open();
        cn.ChangeDatabase(dbName);
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
        using (var r = cmd.ExecuteReader())
            while (r.Read())
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
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!db.ById.TryGetValue(r.GetInt32(0), out var o)) continue;
                o.Columns.Add(new ColInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3), IsIdentity = r.GetBoolean(4), IsComputed = r.GetBoolean(5), IsRowVersion = r.GetByte(6) == 189 });
            }

        // table types (TVP)
        using (var cmd = new SqlCommand(@"SELECT tt.type_table_object_id, s.name, tt.name FROM sys.table_types tt JOIN sys.schemas s ON s.schema_id = tt.schema_id", cn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var o = new ObjInfo { ObjectId = r.GetInt32(0), TypeCode = "TT", Ref = new ObjRef(s.Name, dbName, r.GetString(1), r.GetString(2), ObjType.TableType), Db = db };
                db.TableTypes[o.Ref.Schema + "." + o.Ref.Name] = o;
                db.ById[o.ObjectId] = o;
            }
        using (var cmd = new SqlCommand(@"SELECT c.object_id, c.column_id, c.name, ISNULL(t.name,'') FROM sys.columns c JOIN sys.table_types tt ON tt.type_table_object_id = c.object_id LEFT JOIN sys.types t ON t.user_type_id = c.user_type_id ORDER BY c.object_id, c.column_id", cn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o) && o.TypeCode == "TT")
                    o.Columns.Add(new ColInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3) });

        // synonyms
        using (var cmd = new SqlCommand("SELECT object_id, base_object_name FROM sys.synonyms", cn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o)) o.SynonymBase = r.GetString(1);

        // modules: yalnız üst veri (metin tembel/akışla çekilir)
        using (var cmd = new SqlCommand("SELECT m.object_id, CASE WHEN m.definition IS NULL THEN 1 ELSE 0 END, m.uses_quoted_identifier, m.uses_ansi_nulls FROM sys.sql_modules m", cn) { CommandTimeout = 0 })
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!db.ById.TryGetValue(r.GetInt32(0), out var o)) continue;
                o.HasModuleRow = true; o.DefinitionMissing = r.GetInt32(1) == 1; o.QuotedIdentifier = r.GetBoolean(2); o.AnsiNulls = r.GetBoolean(3);
            }
        // numbered procedures
        try
        {
            using var cmd = new SqlCommand("SELECT object_id, procedure_number, CASE WHEN definition IS NULL THEN 1 ELSE 0 END FROM sys.numbered_procedures", cn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0); int num = Convert.ToInt32(r.GetValue(1)); bool missing = r.GetInt32(2) == 1;
                if (!db.ById.TryGetValue(id, out var parent)) continue;
                var o = new ObjInfo { ObjectId = 0, NumberedParentId = id, TypeCode = "P", Ref = new ObjRef(s.Name, dbName, parent.Ref.Schema, parent.Ref.Name + ";" + num, ObjType.Procedure), HasModuleRow = true, DefinitionMissing = missing, ProcedureNumber = num, DefaultSchema = parent.DefaultSchema, ModifyDate = parent.ModifyDate };
                db.Add(o);
            }
        }
        catch (Exception ex) { Log.Debug($"numbered_procedures: {ex.Message}"); }

        // parameters
        using (var cmd = new SqlCommand("SELECT p.object_id, p.parameter_id, p.name, ISNULL(t.name,''), p.is_output, ISNULL(t.is_table_type,0) FROM sys.parameters p LEFT JOIN sys.types t ON t.user_type_id = p.user_type_id WHERE p.parameter_id > 0 ORDER BY p.object_id, p.parameter_id", cn) { CommandTimeout = 0 })
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o))
                    o.Params.Add(new ParamInfo { Ordinal = r.GetInt32(1), Name = r.GetString(2), TypeName = r.GetString(3), IsOutput = r.GetBoolean(4), IsTableType = r.GetBoolean(5) });

        // triggers
        if (cfg.IncludeTriggers)
        {
            using var cmd = new SqlCommand(@"SELECT t.object_id, t.parent_id, t.is_disabled, t.is_instead_of_trigger,
                                             STUFF((SELECT ','+te.type_desc FROM sys.trigger_events te WHERE te.object_id = t.object_id FOR XML PATH('')),1,1,'')
                                             FROM sys.triggers t WHERE t.parent_class = 1", cn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (db.ById.TryGetValue(r.GetInt32(0), out var o))
                { o.ParentObjectId = r.GetInt32(1); o.IsDisabled = r.GetBoolean(2); o.IsInsteadOf = r.GetBoolean(3); o.TriggerEvents = r.IsDBNull(4) ? "" : r.GetString(4); }
        }
        else
            foreach (var t in db.Objects.Values.Where(o => o.TypeCode == "TR").ToList()) { db.Objects.Remove(t.Ref.Schema + "." + t.Ref.Name); }

        db.Modules = db.Objects.Values.Where(o => o.IsModule && (o.HasModuleRow || o.IsClr)).ToList();
        string cs = s.ConnectionString;
        db.DefinitionFetcher = o => FetchDefinition(cs, dbName, o);
        db.DefinitionStreamer = mods => StreamDefinitions(cs, dbName, db, mods);
        if (withDeps) LoadDeps(s, db, cn);
        Log.Debug($"  {s.Name}.{dbName}: {db.Objects.Count} nesne, {db.Modules.Count} modül, compat {k.Compat}{(withDeps ? ", bağımlılıklar" : "")} ({sw.ElapsedMilliseconds} ms)");
        return db;
    }

    static void LoadDeps(ServerCatalog s, DbCatalog db, SqlConnection? open = null)
    {
        try
        {
            using var own = open == null ? new SqlConnection(s.ConnectionString) : null;
            var cn = open ?? own!;
            if (open == null) { cn.Open(); cn.ChangeDatabase(db.Name); }
            using var cmd = new SqlCommand("SELECT referencing_id, referenced_server_name, referenced_database_name, referenced_schema_name, referenced_entity_name FROM sys.sql_expression_dependencies WHERE referenced_minor_id = 0", cn) { CommandTimeout = 0 };
            using var r = cmd.ExecuteReader();
            while (r.Read())
                db.Deps.Add(new DepInfo { ReferencingId = r.GetInt32(0), Server = r.IsDBNull(1) ? null : r.GetString(1), Database = r.IsDBNull(2) ? null : r.GetString(2), Schema = r.IsDBNull(3) ? null : r.GetString(3), Name = r.GetString(4) });
            db.DepsLoaded = true;
        }
        catch (Exception ex) { Log.Debug($"sql_expression_dependencies: {ex.Message}"); }
    }

    /// <summary>Tek nesnenin tanımı (tembel; view/TVF kolon türetimi gibi seyrek ihtiyaçlar için).</summary>
    static string? FetchDefinition(string cs, string dbName, ObjInfo o)
    {
        if (o.Db != null) Interlocked.Increment(ref o.Db.SingleFetches);
        try
        {
            using var cn = new SqlConnection(cs); cn.Open();
            using var cmd = o.ProcedureNumber is int
                ? new SqlCommand($"SELECT definition FROM {Q(dbName)}.sys.numbered_procedures WHERE object_id = @id AND procedure_number = @n", cn)
                : new SqlCommand($"SELECT definition FROM {Q(dbName)}.sys.sql_modules WHERE object_id = @id", cn);
            cmd.Parameters.AddWithValue("@id", o.ProcedureNumber is int ? o.NumberedParentId : o.ObjectId);
            if (o.ProcedureNumber is int n2) cmd.Parameters.AddWithValue("@n", n2);
            cmd.CommandTimeout = 120;
            var v = cmd.ExecuteScalar();
            return v is string s ? s : null;
        }
        catch (Exception ex) { Log.Debug($"tanım çekilemedi {o.Ref}: {ex.Message}"); return null; }
    }

    /// <summary>Modül tanımlarını kısa partiler halinde çeker: tek bağlantı sürekli kullanılır (boşta kalıp kesilmez), kopunca yeniden açılır;
    /// ilk yeniden deneme beklemesiz (havuzdaki ölü bağlantı), sonrakiler artan bekleme. Parti kesin başarısız olursa yalnız o modüller LoadError olur.</summary>
    static IEnumerable<ObjInfo> StreamDefinitions(string cs, string dbName, DbCatalog db, IEnumerable<ObjInfo> modules)
    {
        var wanted = modules.ToList();
        var pending = wanted.Where(m => m.HasModuleRow && !m.DefinitionMissing && !m.DefinitionLoaded && m.Db == db).ToList();
        var pendingSet = new HashSet<ObjInfo>(pending);
        foreach (var m in wanted) if (!pendingSet.Contains(m)) yield return m;
        const int batch = 64;
        SqlConnection? cn = null;
        try
        {
            for (int i = 0; i < pending.Count; i += batch)
            {
                var slice = pending.Skip(i).Take(batch).ToList();
                Exception? last = null;
                var sw = Stopwatch.StartNew();
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    try
                    {
                        if (cn == null || cn.State != ConnectionState.Open) { cn?.Dispose(); cn = new SqlConnection(cs); cn.Open(); }
                        FetchBatch(cn, dbName, slice); last = null; break;
                    }
                    catch (Exception ex) when (ex is SqlException or IOException or InvalidOperationException)
                    {
                        last = ex; Interlocked.Increment(ref db.FetchRetries);
                        Log.Debug($"tanım partisi {dbName} #{i / batch} deneme {attempt + 1}: {ex.Message}");
                        try { cn?.Dispose(); } catch { } cn = null;
                        if (attempt > 0) Thread.Sleep(1000 * attempt);
                    }
                }
                Interlocked.Add(ref db.FetchMs, sw.ElapsedMilliseconds); Interlocked.Increment(ref db.FetchBatches);
                if (last != null)
                {
                    Log.Warn($"[{dbName}] {slice.Count} modülün tanımı 6 denemede çekilemedi: {last.Message}");
                    foreach (var m in slice) { m.LoadError = "tanım çekilemedi: " + last.Message; m.SetStreamedDefinition(null); }
                }
                foreach (var m in slice) yield return m;
            }
        }
        finally { cn?.Dispose(); }
    }

    static void FetchBatch(SqlConnection cn, string dbName, List<ObjInfo> slice)
    {
        var normal = slice.Where(m => m.ProcedureNumber == null).ToList();
        if (normal.Count > 0)
        {
            var byId = normal.ToDictionary(m => m.ObjectId);
            using var cmd = new SqlCommand($"SELECT object_id, definition FROM {Q(dbName)}.sys.sql_modules WHERE object_id IN ({string.Join(",", byId.Keys)})", cn) { CommandTimeout = 300 };
            using var r = cmd.ExecuteReader(CommandBehavior.SequentialAccess);
            while (r.Read())
                if (byId.TryGetValue(r.GetInt32(0), out var o)) o.SetStreamedDefinition(r.IsDBNull(1) ? null : r.GetString(1));
        }
        foreach (var m in slice.Where(m => m.ProcedureNumber != null))
        {
            using var cmd = new SqlCommand($"SELECT definition FROM {Q(dbName)}.sys.numbered_procedures WHERE object_id = @id AND procedure_number = @n", cn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@id", m.NumberedParentId); cmd.Parameters.AddWithValue("@n", m.ProcedureNumber!.Value);
            m.SetStreamedDefinition(cmd.ExecuteScalar() as string);
        }
        foreach (var m in slice) if (!m.DefinitionLoaded) { m.SetStreamedDefinition(null); m.DefinitionMissing = true; }
    }
}

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
        Log.Debug($"[FILES] {files.Count} dosya, {db.Modules.Count} modül, {synthetic} sentetik tablo");
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
