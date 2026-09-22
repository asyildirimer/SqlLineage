// ============================================================================
#region 7. Görev motoru: tek DB için ön geçiş / analiz (satırlar diske akar, tanımlar bırakılır)
// ============================================================================

sealed class ModuleStats
{
    public string Server = "", Database = ""; public string Status = ""; public AnalysisResult? Result;
}

/// <summary>Bellek içi tam sonuç (merge'de Excel/sorgu/graf için; CSV'den geri okunur).</summary>
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
    public List<UnresolvedKindRow> UnresolvedKindRows = new();
}

/// <summary>Ön geçiş çıktısı (JSON): çağıran literal argümanları + kod tarafından yaratılan kalıcı tablolar. Analiz görevleri hepsini yükler.</summary>
sealed class PrepassResult
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>ObjRef.Key (çağrılan modül) → parametre adı → literal değerler.</summary>
    public Dictionary<string, Dictionary<string, List<string>>> CallerArgs { get; set; } = new();
    public List<OverlayTable> Overlays { get; set; } = new();
    public int Scanned { get; set; }
}
sealed class OverlayTable
{
    public string Server { get; set; } = ""; public string Database { get; set; } = ""; public string Schema { get; set; } = ""; public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = new();
}

/// <summary>Görev sonu üst verisi (meta.json): merge'in özet, trigger yayılımı ve RS kolon adları için ihtiyacı olan her şey.</summary>
sealed class TaskMeta
{
    public string RunId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Server { get; set; } = "";       // config adı
    public string ServerName { get; set; } = "";   // SERVERPROPERTY('ServerName')
    public string Database { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public string Node { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public long ElapsedMs { get; set; }
    public SummaryRow Summary { get; set; } = new();
    /// <summary>Prosedür sonuç kümesi (RS1) kolon adları; merge'de pozisyonel RS1:#i → ad eşlemesi için.</summary>
    public List<ResultSetInfo> ResultSetNames { get; set; } = new();
    public List<TriggerInfo> Triggers { get; set; } = new();
    public Dictionary<string, long> RowCounts { get; set; } = new();
    public bool Compressed { get; set; }
}
sealed class ResultSetInfo
{
    public string Server { get; set; } = ""; public string Database { get; set; } = ""; public string Schema { get; set; } = ""; public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = new();
}
sealed class TriggerInfo
{
    public string ParentServer { get; set; } = ""; public string ParentDatabase { get; set; } = ""; public string ParentSchema { get; set; } = ""; public string ParentName { get; set; } = "";
    public string Server { get; set; } = ""; public string Database { get; set; } = ""; public string Schema { get; set; } = ""; public string Name { get; set; } = "";
    public string Events { get; set; } = "";
}

/// <summary>Tek (sunucu, DB) görevi: ön geçiş ya da analiz. Katalog tembel; tanımlar akışla gelir, analiz sonrası bırakılır; satırlar PartWriter ile diske akar.</summary>
sealed class DbTaskRunner
{
    readonly LineageConfig cfg; readonly string runId;
    readonly Catalog cat; readonly ServerCatalog srv; readonly DbCatalog db;
    readonly object gate = new();
    readonly List<ModuleStats> stats = new();
    readonly Dictionary<string, ResultSetInfo> resultSetNames = new();
    readonly HashSet<string> seenObjKeys = new();   // katalog çapraz kontrolü için: modül→nesne
    Incremental? incremental;
    long colRows, objRows; int reusedModules;

    public DbTaskRunner(LineageConfig cfg, string runId, Catalog cat, ServerCatalog srv, DbCatalog db)
    { this.cfg = cfg; this.runId = runId; this.cat = cat; this.srv = srv; this.db = db; }

    // ---------------- modül listesi (bu DB + bu DB'de koşan job adımları)
    public List<ObjInfo> CollectModules(IEnumerable<JobStepInfo>? jobSteps)
    {
        var list = new List<ObjInfo>();
        int excluded = 0;
        foreach (var m in db.Modules)
        {
            if (cfg.ExcludeModuleNameContains.Any(x => m.Ref.Name.Contains(x, StringComparison.OrdinalIgnoreCase))) { excluded++; continue; }
            list.Add(m);
        }
        if (jobSteps != null)
            foreach (var js in jobSteps.Where(j => NameComparer.Eq(j.Subsystem, "TSQL")))
                list.Add(new ObjInfo { TypeCode = "JOB", Ref = new ObjRef(srv.Name, db.Name, "job", $"{js.JobName}#{js.StepId}", ObjType.JobStep), Definition = js.Command, DefaultSchema = "dbo", Db = db });
        if (excluded > 0) Log.Debug($"{excluded} modül ad filtresiyle (ExcludeModuleNameContains) dışlandı");
        return list;
    }

    // ---------------- ön geçiş: EXEC proc literal argümanları + kod yaratan kalıcı tablolar
    public PrepassResult RunPrepass(List<ObjInfo> modules)
    {
        var sw = Stopwatch.StartNew();
        var res = new PrepassResult { Server = srv.ConfigName, Database = db.Name };
        var callerArgs = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>>();
        var overlays = new ConcurrentDictionary<string, OverlayTable>(NameComparer.Instance);
        int found = 0, created = 0, scanned = 0;
        var preList = modules.Where(m => m.TypeCode is "P" or "TR" or "JOB" or "FILE" && (m.HasModuleRow || m.DefinitionLoaded) && !m.DefinitionMissing).ToList();
        Log.Debug($"[{srv.ConfigName}.{db.Name}] ön geçiş: {preList.Count} prosedür/trigger/job");
        RunWorkers(preList, m =>
        {
            int n = Interlocked.Increment(ref scanned);
            try
            {
                var def = m.Definition;
                if (def == null) return;
                var mdb = m.Db ?? db;
                var pr = ParserLadder.Parse(def, m.QuotedIdentifier, srv.Major, mdb.Compat);
                if (pr.Fragment == null) return;
                var v = new CallerArgVisitor();
                pr.Fragment.Accept(v);
                if (v.Calls.Count == 0 && v.Created.Count == 0) return;
                var resolver = new ModuleAnalyzer(cat, cfg, runId, srv, mdb, m) { SuppressEmit = true };
                foreach (var (son, cols) in v.Created)
                {
                    string dbName = son.DatabaseIdentifier?.Value.NullIfEmpty() ?? mdb.Name;
                    string srvName = son.ServerIdentifier?.Value.NullIfEmpty() == null ? srv.Name : cat.ResolveLinked(srv.Name, son.ServerIdentifier!.Value).serverName;
                    if (!cat.HasDb(srvName, dbName)) continue;
                    var tdb = cat.FindDb(srvName, dbName);
                    if (tdb == null) continue;
                    string schema = son.SchemaIdentifier?.Value.NullIfEmpty() ?? (m.TypeCode is "JOB" or "FILE" ? m.DefaultSchema : m.Ref.Schema);
                    string name = son.BaseIdentifier.Value;
                    if (tdb.Find(schema, name) != null) continue;
                    var ot = new OverlayTable { Server = tdb.Server, Database = tdb.Name, Schema = schema, Name = name, Columns = cols };
                    if (overlays.TryAdd($"{tdb.Server}|{tdb.Name}|{schema}.{name}", ot))
                    {
                        Interlocked.Increment(ref created);
                        // bu görev içinde de görünür olsun
                        tdb.Overlay.TryAdd(schema + "." + name, new ObjInfo { TypeCode = "U", Ref = new ObjRef(tdb.Server, tdb.Name, schema, name, ObjType.Table), Columns = cols.Select((c, i) => new ColInfo { Name = c, Ordinal = i + 1 }).ToList(), Db = tdb });
                    }
                }
                foreach (var (son, args) in v.Calls)
                {
                    var (r, info, _) = resolver.ResolveObject(son);
                    if (info == null) continue;
                    var byParam = callerArgs.GetOrAdd(r.With(info.Ref.Type).Key, _ => new(NameComparer.Instance));
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
            catch (Exception ex) { Log.Debug($"pre-pass {m.Ref}: {ex.Message}"); }
            finally { m.ReleaseDefinition(); }
        }, stream: true, label: srv.ConfigName + "." + db.Name + " ön geçiş");
        foreach (var kv in callerArgs) res.CallerArgs[kv.Key] = kv.Value.ToDictionary(p => p.Key, p => p.Value.Keys.ToList());
        res.Overlays = overlays.Values.ToList();
        res.Scanned = scanned;
        Log.Info($"[{srv.ConfigName}.{db.Name}] ön geçiş: {preList.Count} modül, {found} çağıran literal argümanı, {created} kod yaratımı tablo, {sw.Elapsed.TotalSeconds:F0} s");
        return res;
    }

    /// <summary>Ön geçiş çıktılarını sürece yükler: CallerArgs statik haritasına ve kataloğun bekleyen overlay'lerine.</summary>
    public static void LoadPrepass(Catalog cat, IEnumerable<PrepassResult> results)
    {
        int args = 0, ov = 0;
        foreach (var pr in results)
        {
            foreach (var (key, byParam) in pr.CallerArgs)
            {
                var d = StringFlow.CallerArgs.GetOrAdd(key, _ => new(NameComparer.Instance));
                foreach (var (p, lits) in byParam) { var set = d.GetOrAdd(p, _ => new()); foreach (var l in lits) if (set.TryAdd(l, 0)) args++; }
            }
            foreach (var o in pr.Overlays)
            {
                var oi = new ObjInfo { TypeCode = "U", Ref = new ObjRef(o.Server, o.Database, o.Schema, o.Name, ObjType.Table), Columns = o.Columns.Select((c, i) => new ColInfo { Name = c, Ordinal = i + 1 }).ToList() };
                cat.PendingOverlays.GetOrAdd(o.Server + "|" + o.Database, _ => new()).Add(oi);
                ov++;
                var srv = cat.FindServer(o.Server);
                if (srv != null && srv.Dbs.TryGetValue(o.Database, out var loaded) && loaded.Find(o.Schema, o.Name) == null) { oi.Db = loaded; loaded.Overlay.TryAdd(o.Schema + "." + o.Name, oi); }
            }
        }
        Log.Debug($"Ön geçiş yüklendi: {args} literal argüman, {ov} overlay tablo");
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

    // ---------------- işçi havuzu: üretici (tanım akışı) + büyük stack'li tüketici thread'ler, sınırlı kuyruk
    void RunWorkers(List<ObjInfo> modules, Action<ObjInfo> work, bool stream, string label)
    {
        int par = Math.Max(1, cfg.Parallelism);
        var queue = new BlockingCollection<ObjInfo>(boundedCapacity: Math.Max(128, par * 16));
        int done = 0, total = modules.Count;
        var progress = Stopwatch.StartNew();
        var workers = new List<Thread>();
        for (int w = 0; w < par; w++)
        {
            var t = new Thread(() =>
            {
                try
                {
                    foreach (var m in queue.GetConsumingEnumerable())
                    {
                        try { work(m); }
                        catch (Exception ex) { Interlocked.Increment(ref workerErrors); Log.Debug($"{m.Ref}: {ex.GetType().Name}: {ex.Message}"); }
                        int d = Interlocked.Increment(ref done);
                        if (d < total && progress.Elapsed.TotalSeconds >= 60) { lock (progress) { if (progress.Elapsed.TotalSeconds >= 60) { Log.Info($"  [{label}] {d}/{total} modül"); progress.Restart(); } } }
                    }
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Log.Error($"işçi thread hatası: {ex.GetType().Name}: {ex.Message}"); }
            }, 64 * 1024 * 1024) { IsBackground = true, Name = "lineage-" + w };
            workers.Add(t); t.Start();
        }
        Exception? producerError = null;
        try
        {
            foreach (var m in stream ? db.StreamModules(modules) : modules) queue.Add(m);
        }
        catch (Exception ex) { producerError = ex; }
        finally { queue.CompleteAdding(); }
        foreach (var t in workers) t.Join();     // kuyruk işçiler bitmeden kapatılmaz
        queue.Dispose();
        if (producerError != null) throw new InvalidOperationException("modül tanımları çekilirken hata: " + producerError.Message, producerError);
    }
    int workerErrors;

    // ---------------- analiz
    public TaskMeta RunAnalyze(List<ObjInfo> modules, PartWriter parts, string engineSignature)
    {
        var sw = Stopwatch.StartNew();
        if (cfg.Incremental)
        {
            if (string.IsNullOrWhiteSpace(cfg.Output.SqlBulkTarget)) Log.Warn("--incremental için --sql-target gerekli; tam koşu yapılıyor");
            else
            {
                incremental = Incremental.Load(cfg, new Dictionary<string, string>(NameComparer.Instance) { [srv.Name + "|" + db.Name] = engineSignature }, srv.Name, db.Name);
                if (incremental == null) Log.Warn("Önceki koşu bulunamadı; tam koşu");
            }
        }
        Log.Debug($"[{srv.ConfigName}.{db.Name}] analiz: {modules.Count} modül");
        RunWorkers(modules, m => AnalyzeOne(m, parts), stream: true, label: srv.ConfigName + "." + db.Name);
        int missed = CatalogCrossCheck(parts);
        var meta = new TaskMeta
        {
            RunId = runId, Server = srv.ConfigName, ServerName = srv.Name, Database = db.Name, EngineVersion = App.EngineVersion,
            ElapsedMs = sw.ElapsedMilliseconds, ResultSetNames = resultSetNames.Values.ToList(), Compressed = parts.Compressed,
        };
        foreach (var t in db.Modules.Where(m => m.TypeCode == "TR" && !m.IsDisabled && m.ParentObjectId != null))
            if (db.ById.TryGetValue(t.ParentObjectId!.Value, out var parent))
                meta.Triggers.Add(new TriggerInfo { ParentServer = parent.Ref.Server, ParentDatabase = parent.Ref.Database, ParentSchema = parent.Ref.Schema, ParentName = parent.Ref.Name, Server = t.Ref.Server, Database = t.Ref.Database, Schema = t.Ref.Schema, Name = t.Ref.Name, Events = t.TriggerEvents });
        meta.Summary = BuildSummary(engineSignature, sw, missed);
        meta.RowCounts = parts.Counts();
        return meta;
    }

    void AnalyzeOne(ObjInfo mod, PartWriter parts)
    {
        var sw = Stopwatch.StartNew();
        string? def = mod.Definition;
        var row = new ModuleRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, ModuleType = mod.ModuleTypeName, ModifyDate = mod.ModifyDate, DefinitionLength = def?.Length ?? 0, DefinitionHash = Incremental.Hash(def) };
        var st = new ModuleStats { Server = mod.Ref.Server, Database = mod.Ref.Database };
        AnalysisResult? res = null;
        try
        {
            if (incremental != null && incremental.TryReuse(mod, row, out var reused))
            {
                row.Reused = true; row.ParseStatus = reused.ParseStatus; row.ParserUsed = reused.ParserUsed; row.Errors = reused.Errors; row.Holes = reused.Holes;
                row.Statements = reused.Statements; row.DynamicSites = reused.DynamicSites; row.ColumnEdges = reused.ColumnEdges; row.ObjectEdges = reused.ObjectEdges; row.UnresolvedNames = reused.UnresolvedNames;
                st.Status = row.ParseStatus; res = incremental.ResultFor(mod.Ref, runId);
                Interlocked.Increment(ref reusedModules);
                Flush(mod, row, st, res, parts);
                return;
            }
            if (mod.LoadError != null)
            {
                row.ParseStatus = "Error"; row.Errors = mod.LoadError;
                res = CatalogFallback(mod, row.ParseStatus);
            }
            else if (def != null && def.Length > cfg.MaxDefinitionChars)
            {
                row.ParseStatus = "DefinitionSizeLimit"; row.Errors = $"tanım {def.Length:N0} karakter > MaxDefinitionChars {cfg.MaxDefinitionChars:N0}";
                res = CatalogFallback(mod, row.ParseStatus);
            }
            else if (def == null)
            {
                row.ParseStatus = mod.IsClr ? "NoDefinition(CLR)" : "NoDefinition(Encrypted)";
                res = CatalogFallback(mod, row.ParseStatus);
            }
            else
            {
                var mdb = mod.Db ?? db;
                var pr = ParserLadder.Parse(def, mod.QuotedIdentifier, srv.Major, mdb.Compat);
                row.ParseStatus = pr.Status; row.ParserUsed = "TSql" + pr.ParserUsed; row.Errors = pr.ErrorText; row.Holes = string.Join(" | ", pr.Holes);
                if (pr.Fragment == null) res = CatalogFallback(mod, "Quarantined");
                else
                {
                    var an = new ModuleAnalyzer(cat, cfg, runId, srv, mdb, mod);
                    try { an.Analyze(pr); }
                    catch (TimeoutException tex) { row.ParseStatus = "Timeout"; row.Errors = Join(row.Errors, tex.Message); }
                    res = an.Result;
                }
            }
        }
        catch (Exception ex)
        {
            row.ParseStatus = "Error"; row.Errors = Join(row.Errors, ex.GetType().Name + ": " + ex.Message);
            Log.Debug($"{mod.Ref}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // prosedür/trigger/job metni bir daha gerekmez; view/fonksiyon metni başka modüllerin kolon türetimi için tembel kalır
            if (mod.TypeCode is "P" or "PC" or "TR" or "JOB") mod.ReleaseDefinition();
        }
        row.ElapsedMs = (int)sw.ElapsedMilliseconds;
        st.Status = row.ParseStatus;
        if (res != null) { row.Statements = res.Statements; row.DynamicSites = res.DynamicSites; row.ColumnEdges = res.ColumnRows.Count; row.ObjectEdges = res.ObjectRows.Count; row.UnresolvedNames = res.UnresolvedRows.Count; }
        Flush(mod, row, st, res, parts);
    }

    /// <summary>Modül sonucunu diske akıtır; bellekte yalnız sayaçlar ve küçük anahtar kümeleri kalır.</summary>
    void Flush(ObjInfo mod, ModuleRow row, ModuleStats st, AnalysisResult? res, PartWriter parts)
    {
        if (res != null)
        {
            st.Result = new AnalysisResult { Statements = res.Statements, ObjectRefs = res.ObjectRefs, ObjectRefsResolved = res.ObjectRefsResolved, DynamicSites = res.DynamicSites, DynamicResolved = res.DynamicResolved };
            bool Ex(string n) => cfg.ExcludeObjectNameContains.Count > 0 && cfg.ExcludeObjectNameContains.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
            long c = 0, o = 0;
            foreach (var r in res.ColumnRows) { if (Ex(r.SourceObject) || Ex(r.TargetObject)) continue; parts.Add("ColumnLineage", r); c++; }
            var seen = new List<string>(res.ObjectRows.Count);
            foreach (var r in res.ObjectRows)
            {
                if (Ex(r.ObjectName)) continue;
                parts.Add("ObjectLineage", r); o++;
                seen.Add(FqnKey(r.Server, r.Database, r.ModuleSchema, r.ModuleName) + "→" + NameComparer.Norm(r.ObjectName).ToUpperInvariant());
            }
            foreach (var r in res.DynamicRows) parts.Add("DynamicSql", r);
            foreach (var r in res.UnresolvedRows) parts.Add("Unresolved", r);
            foreach (var r in res.StatementRows) parts.Add("Statements", r);
            lock (gate)
            {
                colRows += c; objRows += o;
                foreach (var k in seen) seenObjKeys.Add(k);
                if (res.ResultSetNames.Count > 0) resultSetNames[FqnKey(mod.Ref)] = new ResultSetInfo { Server = mod.Ref.Server, Database = mod.Ref.Database, Schema = mod.Ref.Schema, Name = mod.Ref.Name, Columns = res.ResultSetNames[0] };
            }
        }
        parts.Add("Modules", row);
        lock (gate) stats.Add(st);
    }

    static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "; " + b;
    public static string FqnKey(ObjRef r) => NameComparer.Norm($"{r.Server}.{r.Database}.{r.Schema}.{r.Name}").ToUpperInvariant();
    public static string FqnKey(string server, string db, string schema, string name) => NameComparer.Norm($"{server}.{db}.{schema}.{name}").ToUpperInvariant();

    /// <summary>Şifreli/CLR/karantina modüller: sys.sql_expression_dependencies'ten nesne düzeyi satırlar.</summary>
    AnalysisResult CatalogFallback(ObjInfo mod, string reason)
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

    // ---------------- katalog çapraz kontrolü: SQL Server'ın gördüğü ama bizim görmediğimiz referanslar
    int CatalogCrossCheck(PartWriter parts)
    {
        int missed = 0;
        foreach (var d in db.Deps)
        {
            if (!db.ById.TryGetValue(d.ReferencingId, out var mod) || !mod.IsModule) continue;
            string key = FqnKey(mod.Ref) + "→" + NameComparer.Norm(d.Name).ToUpperInvariant();
            bool found = seenObjKeys.Contains(key);
            parts.Add("CatalogDeps", new CatalogDepRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, ReferencedServer = d.Server ?? mod.Ref.Server, ReferencedDatabase = d.Database ?? db.Name, ReferencedSchema = d.Schema ?? "", ReferencedName = d.Name, FoundInAnalysis = found });
            if (mod.DefinitionMissing || !mod.HasModuleRow || d.Server != null) continue;
            if (found) continue;
            missed++;
            parts.Add("Unresolved", new UnresolvedRow { RunId = runId, Server = mod.Ref.Server, Database = mod.Ref.Database, ModuleSchema = mod.Ref.Schema, ModuleName = mod.Ref.Name, Kind = "CatalogDepMissed", Severity = "warning", Name = $"{d.Database ?? db.Name}.{d.Schema ?? "?"}.{d.Name}", Note = "sys.sql_expression_dependencies'te var, analizde yok" });
        }
        Log.Debug($"Katalog çapraz kontrolü: {missed} kaçırılmış referans (Unresolved sayfasında CatalogDepMissed)");
        return missed;
    }

    // ---------------- özet (bu DB; ObjectLineageRows/CollapsedRows merge'de tamamlanır)
    SummaryRow BuildSummary(string engineSignature, Stopwatch sw, int missed)
    {
        int elapsedMs = (int)sw.ElapsedMilliseconds;
        var st = stats.Where(s => s.Result != null).Select(s => s.Result!).ToList();
        var statuses = stats.Select(s => s.Status).ToList();
        var row = new SummaryRow
        {
            RunId = runId, Server = srv.Name, Database = db.Name, Modules = stats.Count,
            Parsed = statuses.Count(x => x == "Parsed"), ParsedWithHoles = statuses.Count(x => x == "ParsedWithHoles"),
            Quarantined = statuses.Count(x => x is "Quarantined" or "Timeout" or "Error"), NoDefinition = statuses.Count(x => x.StartsWith("NoDefinition")),
            ObjectRefs = st.Sum(s => s.ObjectRefs), ObjectRefsResolved = st.Sum(s => s.ObjectRefsResolved),
            DynamicSites = st.Sum(s => s.DynamicSites), DynamicResolved = st.Sum(s => s.DynamicResolved),
            ColumnLineageRows = (int)Math.Min(int.MaxValue, colRows), ObjectLineageRows = (int)Math.Min(int.MaxValue, objRows),
            CatalogDepsMissed = missed, ReusedModules = reusedModules, EngineSignature = engineSignature, ElapsedMs = elapsedMs,
        };
        int withDef = row.Modules - row.NoDefinition;
        row.ParseRate = withDef == 0 ? 1 : Math.Round((row.Parsed + row.ParsedWithHoles) / (double)withDef, 4);
        row.BindRate = row.ObjectRefs == 0 ? 1 : Math.Round(row.ObjectRefsResolved / (double)row.ObjectRefs, 4);
        row.DynamicResolutionRate = row.DynamicSites == 0 ? 1 : Math.Round(row.DynamicResolved / (double)row.DynamicSites, 4);
        int errors = statuses.Count(x => x is "Error" or "Timeout");
        Log.Info($"[{srv.ConfigName}.{db.Name}] analiz: modül {row.Modules}, parse {row.ParseRate:P1}, bind {row.BindRate:P1}, dinamik {row.DynamicSites} site / {row.DynamicResolutionRate:P0}, kolon {row.ColumnLineageRows:N0}, nesne {row.ObjectLineageRows:N0}, katalog kaçağı {missed}{(errors > 0 ? $", HATA {errors} modül (Modules.csv Errors)" : "")}{(row.ReusedModules > 0 ? $", {row.ReusedModules} önceki koşudan" : "")}, {sw.Elapsed.TotalSeconds:F0} s (tanım çekme {db.FetchMs / 1000.0:F0} s / {db.FetchBatches} parti{(db.FetchRetries > 0 ? $", {db.FetchRetries} yeniden deneme" : "")}{(db.SingleFetches > 0 ? $", {db.SingleFetches} tekil çekim" : "")})");
        return row;
    }
}

#endregion
