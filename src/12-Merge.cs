// ============================================================================
#region 12. Merge: parçaları birleştirme, prosedürler arası yayılım, kalıcı→kalıcı indirgeme (sıkıştırılmış bellek), özetler, Excel/SQL
// ============================================================================

/// <summary>String havuzu: aynı ad tek kopya, kenarlar int kimlik taşır.</summary>
sealed class Pool
{
    readonly Dictionary<string, int> map = new(StringComparer.Ordinal);
    readonly List<string> list = new();
    public int Id(string s) { if (map.TryGetValue(s, out var i)) return i; i = list.Count; list.Add(s); map[s] = i; return i; }
    public string this[int i] => list[i];
    public int Count => list.Count;
}

/// <summary>Ad-normalize (ı/İ → i, büyük harf) 64-bit FNV-1a anahtarı; string anahtar yerine long tutulur.</summary>
static class NK
{
    const ulong Basis = 14695981039346656037, Prime = 1099511628211;
    public static ulong Start() => Basis;
    public static ulong Add(ulong h, string s)
    {
        foreach (var raw in s)
        {
            char c = raw == 'ı' || raw == 'İ' ? 'i' : raw;
            c = char.ToUpperInvariant(c);
            h = (h ^ c) * Prime;
        }
        return (h ^ 0x1F) * Prime;   // ayraç
    }
    public static long Key(string a, string b, string c, string d) => (long)Add(Add(Add(Add(Start(), a), b), c), d);
    public static long Key(string a, string b, string c, string d, string e) => (long)Add(Add(Add(Add(Add(Start(), a), b), c), d), e);
    public static long Key(long prefix, string e) => (long)Add((ulong)prefix, e);
    public static long Key(long a, long b) => (long)(((ulong)a ^ 0x9E3779B97F4A7C15UL) * Prime ^ (ulong)b);
}

sealed class Merger
{
    readonly LineageConfig cfg; readonly string workDir; readonly PlanFile plan; readonly string runId; readonly string sep;
    readonly string csvDir;
    readonly List<(PlanTask task, TaskMeta meta, string dir)> parts = new();
    readonly Pool pool = new();

    // nesne düzeyi (Interprocedural) yapıları
    struct ObjEdge { public int Action, OServer, ODb, OSchema, OName, OType, Conf, StmtType; public int StmtNo, Line; public long ObjKey; }
    struct ModInfo { public int Server, Db, Schema, Name, Type; }
    readonly Dictionary<long, List<ObjEdge>> own = new(), calls = new();
    readonly Dictionary<long, ModInfo> modInfo = new();
    readonly Dictionary<long, List<(long key, int schemaDot, int server, int db)>> callees = new(), callers = new();
    readonly Dictionary<long, List<(long trgKey, string events, int schema, int name)>> triggers = new();
    readonly Dictionary<long, List<string>> resultSetNames = new();
    // kolon düzeyi (Collapse)
    sealed class CEdge { public int SServer, SDb, SSchema, SObj, SType, SCol; public FlowKind Kind; public int Conf; public long ModKey; }
    readonly Dictionary<long, List<CEdge>> incoming = new();
    // özet sayaçları: "server|db" → eklenen satırlar
    readonly Dictionary<string, (long obj, long col)> addedRows = new(NameComparer.Instance);

    public Merger(LineageConfig cfg, string workDir, PlanFile plan)
    {
        this.cfg = cfg; this.workDir = workDir; this.plan = plan; runId = plan.RunId; sep = cfg.Output.CsvSeparator;
        csvDir = Path.Combine(cfg.Output.Directory, "csv-" + runId);
    }

    public void Run()
    {
        var sw = Stopwatch.StartNew();
        LoadMetas();
        Directory.CreateDirectory(csvDir);
        Concat();
        BuildObjectIndex();
        Interprocedural();
        Collapse();
        var small = new LineageData();
        small.SummaryRows = BuildSummary();
        (small.IssueSummaryRows, small.UnresolvedKindRows) = BuildIssueSummaries();
        Csv.Write(Path.Combine(csvDir, "Summary.csv"), small.SummaryRows, sep);
        Csv.Write(Path.Combine(csvDir, "TopIssues.csv"), small.IssueSummaryRows, sep);
        Csv.Write(Path.Combine(csvDir, "UnresolvedByKind.csv"), small.UnresolvedKindRows, sep);
        var cat = StubCatalog();
        if ((cfg.Query != null && cfg.Query.Node != "") || (cfg.Graph != null && cfg.Graph.Node != ""))
        {
            var data = new LineageData { CollapsedRows = Csv.Read<CollapsedRow>(Path.Combine(csvDir, "Collapsed.csv"), sep).ToList() };
            if (cfg.Query != null && cfg.Query.Node != "") { small.QueryRows = LineageQuery.Run(cfg, runId, data, cat); Csv.Write(Path.Combine(csvDir, "Query.csv"), small.QueryRows, sep); }
            if (cfg.Graph != null && cfg.Graph.Node != "") GraphExport.Write(cfg, runId, data, cat);
        }
        Log.Info($"CSV: {csvDir}");
        if (cfg.Output.Xlsx) { try { Output.WriteExcel(cfg, runId, csvDir, small); } catch (Exception ex) { Log.Error("Excel yazılamadı: " + ex.Message); } }
        if (cfg.Output.SqlTableDdl) Output.WriteDdl(cfg);
        if (!string.IsNullOrWhiteSpace(cfg.Output.SqlBulkTarget)) { try { Output.BulkLoad(cfg, csvDir); } catch (Exception ex) { Log.Error("SqlBulkCopy hatası: " + ex.Message); } }
        if (!cfg.Output.Csv) Log.Info("Not: --no-csv verildi ama CSV klasörü merge'in çalışma ürünüdür; silinmedi.");
        Log.Info($"Merge bitti: {parts.Count} parça, {sw.Elapsed.TotalSeconds:F0} s");
    }

    // ---------------------------------------------------------------- parçalar
    void LoadMetas()
    {
        foreach (var t in plan.Tasks.Where(t => t.Kind == "analyze"))
        {
            string dir = Path.Combine(PlanStore.PartsDir(workDir), PlanStore.PartDirName(t));
            string metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) { Log.Warn($"Parça yok/yarım: {t.Id} ({t.Status}) — atlanıyor"); continue; }
            TaskMeta? meta;
            try { meta = JsonSerializer.Deserialize<TaskMeta>(File.ReadAllText(metaPath), Cli.JsonOpts); } catch (Exception ex) { Log.Warn($"meta.json okunamadı {t.Id}: {ex.Message}"); continue; }
            if (meta == null) continue;
            if (meta.RunId != runId) { Log.Warn($"Parça başka koşudan ({meta.RunId} ≠ {runId}): {t.Id} — atlanıyor"); continue; }
            parts.Add((t, meta, dir));
            foreach (var rs in meta.ResultSetNames) resultSetNames[NK.Key(rs.Server, rs.Database, rs.Schema, rs.Name)] = rs.Columns;
            foreach (var tr in meta.Triggers)
            {
                long pk = NK.Key(tr.ParentServer, tr.ParentDatabase, tr.ParentSchema, tr.ParentName);
                (triggers.TryGetValue(pk, out var l) ? l : triggers[pk] = new()).Add((NK.Key(tr.Server, tr.Database, tr.Schema, tr.Name), tr.Events, pool.Id(tr.Schema), pool.Id(tr.Name)));
            }
        }
        Log.Info($"Merge: {parts.Count}/{plan.Tasks.Count(t => t.Kind == "analyze")} parça, RunId {runId}");
    }

    string PartFile(TaskMeta meta, string dir, string table) => Path.Combine(dir, PartWriter.FileName(table, meta.Compressed));

    /// <summary>Parça CSV'lerini tablo başına tek dosyada birleştirir (akışlı; isteğe bağlı satır tekilleştirme).</summary>
    void Concat()
    {
        var sw = Stopwatch.StartNew();
        foreach (var (table, type) in Output.Tables)
        {
            if (table is "Summary" or "Collapsed" or "TopIssues" or "Query" or "UnresolvedByKind") continue;
            var srcs = parts.Select(p => (p.meta, path: PartFile(p.meta, p.dir, table))).Where(x => File.Exists(x.path)).ToList();
            if (srcs.Count == 0 && table == "Statements") continue;
            string header = (string)typeof(Csv).GetMethod(nameof(Csv.Header))!.MakeGenericMethod(type).Invoke(null, new object[] { sep })!;
            bool dedup = cfg.MergeDedup && table is "ColumnLineage" or "ObjectLineage";
            var seen = dedup ? new HashSet<long>() : null;
            long n = 0, dropped = 0;
            using (var w = Csv.OpenWriter(Path.Combine(csvDir, table + ".csv")))
            {
                w.WriteLine(header);
                foreach (var (meta, path) in srcs)
                    n += Csv.AppendRaw(path, w, seen == null ? null : rec => { if (seen.Add((long)NK.Add(NK.Start(), rec))) return true; dropped++; return false; });
            }
            Log.Info($"  {table}: {n:N0} satır{(dropped > 0 ? $" ({dropped:N0} mükerrer düşüldü)" : "")}");
        }
        Log.Info($"Birleştirme: {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- nesne düzeyi indeks (ObjectLineage.csv'den, sıkıştırılmış)
    static bool IsEffect(string a) => a is "Reads" or "Insert" or "Update" or "Delete" or "Merge" or "Truncate" or "Create" or "Drop" or "Writes" or "References";
    static bool Persistent(string t) => t is "Table" or "View" or "External" or "Unresolved" or "System" or "File" or "Sequence" or "Synonym";

    void BuildObjectIndex()
    {
        var sw = Stopwatch.StartNew();
        var path = Path.Combine(csvDir, "ObjectLineage.csv");
        if (!File.Exists(path)) return;
        long n = 0;
        foreach (var r in Csv.Read<ObjectLineageRow>(path, sep))
        {
            n++;
            if (r.Provenance is "Interprocedural" or "Trigger") continue;
            long k = NK.Key(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            if (!modInfo.ContainsKey(k)) modInfo[k] = new ModInfo { Server = pool.Id(r.Server), Db = pool.Id(r.Database), Schema = pool.Id(r.ModuleSchema), Name = pool.Id(r.ModuleName), Type = pool.Id(r.ModuleType) };
            bool isCall = r.Action == "Calls" && r.ObjectType is "Procedure" or "ClrModule" or "Unresolved" or "External";
            bool isEff = IsEffect(r.Action) && Persistent(r.ObjectType);
            if (!isCall && !isEff)
            {
                // paylaşılan temp geçişi için çağrı grafı (Calls, statik) — fonksiyon çağrıları da dahil
                if (r.Action == "Calls" && r.Provenance is "Static" or "DynamicStatic") AddCallGraph(k, r);
                continue;
            }
            long ok = NK.Key(r.ObjectServer, r.ObjectDatabase, r.ObjectSchema, r.ObjectName);
            var e = new ObjEdge { Action = pool.Id(r.Action), OServer = pool.Id(r.ObjectServer), ODb = pool.Id(r.ObjectDatabase), OSchema = pool.Id(r.ObjectSchema), OName = pool.Id(r.ObjectName), OType = pool.Id(r.ObjectType), Conf = pool.Id(r.Confidence), StmtType = pool.Id(r.StatementType), StmtNo = r.StatementNo, Line = r.Line, ObjKey = ok };
            if (isCall)
            {
                (calls.TryGetValue(k, out var l) ? l : calls[k] = new()).Add(e);
                if (r.Provenance is "Static" or "DynamicStatic") AddCallGraph(k, r);
            }
            else (own.TryGetValue(k, out var l2) ? l2 : own[k] = new()).Add(e);
        }
        Log.Info($"Nesne indeksi: {n:N0} satır, {modInfo.Count:N0} modül, {own.Count:N0} etkili, {calls.Count:N0} çağıran, havuz {pool.Count:N0} ad, {sw.ElapsedMilliseconds} ms");
    }
    void AddCallGraph(long caller, ObjectLineageRow r)
    {
        long callee = NK.Key(r.ObjectServer, r.ObjectDatabase, r.ObjectSchema, r.ObjectName);
        (callees.TryGetValue(caller, out var l1) ? l1 : callees[caller] = new()).Add((callee, pool.Id(r.ObjectSchema + "." + r.ObjectName), pool.Id(r.ObjectServer), pool.Id(r.ObjectDatabase)));
        (callers.TryGetValue(callee, out var l2) ? l2 : callers[callee] = new()).Add((caller, pool.Id(r.ModuleSchema + "." + r.ModuleName), pool.Id(r.Server), pool.Id(r.Database)));
    }

    // ---------------------------------------------------------------- prosedürler arası (nesne düzeyi) + trigger yayılımı → ObjectLineage.csv'ye eklenir
    void Interprocedural()
    {
        var sw = Stopwatch.StartNew();
        var path = Path.Combine(csvDir, "ObjectLineage.csv");
        if (!File.Exists(path)) return;
        long added = 0;
        using var w = Csv.OpenWriter(path, append: true);
        var sb = new StringBuilder();
        foreach (var start in own.Keys.Union(calls.Keys).ToList())
        {
            var visited = new HashSet<long> { start };
            var q = new Queue<(long key, int depth, string path, ObjEdge? via)>();
            void Enqueue(long key, int depth, string p, ObjEdge? via) { if (visited.Add(key)) q.Enqueue((key, depth, p, via)); }
            void Expand(long key, int depth, string p, ObjEdge? via)
            {
                if (depth >= cfg.MaxCallDepth) return;
                if (calls.TryGetValue(key, out var cl))
                    foreach (var c in cl) Enqueue(c.ObjKey, depth + 1, p + " > " + pool[c.OSchema] + "." + pool[c.OName], via ?? c);
                if (own.TryGetValue(key, out var ol))
                    foreach (var e in ol)
                    {
                        string act = pool[e.Action];
                        if (act is not ("Insert" or "Update" or "Delete" or "Merge")) continue;
                        if (!triggers.TryGetValue(e.ObjKey, out var tl)) continue;
                        foreach (var (tk, ev, tsch, tname) in tl)
                            if (ev.Contains(act == "Merge" ? "INSERT" : act.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase) || ev == "")
                                Enqueue(tk, depth + 1, p + " > [trg]" + pool[tsch] + "." + pool[tname], via ?? e);
                    }
            }
            var m = modInfo[start];
            string server = pool[m.Server], database = pool[m.Db], mschema = pool[m.Schema], mname = pool[m.Name], mtype = pool[m.Type];
            Expand(start, 0, mschema + "." + mname, null);
            var emitted = new HashSet<long>();
            long addedHere = 0;
            while (q.Count > 0)
            {
                var (key, depth, p, via) = q.Dequeue();
                if (own.TryGetValue(key, out var effects))
                    foreach (var e in effects)
                    {
                        if (!emitted.Add(NK.Key(e.ObjKey, e.Action))) continue;
                        var row = new ObjectLineageRow
                        {
                            RunId = runId, Server = server, Database = database, ModuleSchema = mschema, ModuleName = mname, ModuleType = mtype,
                            StatementNo = via?.StmtNo ?? 0, StatementType = via == null ? "" : pool[via.Value.StmtType], Line = via?.Line ?? 0, Action = pool[e.Action],
                            ObjectServer = pool[e.OServer], ObjectDatabase = pool[e.ODb], ObjectSchema = pool[e.OSchema], ObjectName = pool[e.OName], ObjectType = pool[e.OType],
                            Provenance = p.Contains("[trg]") ? "Trigger" : "Interprocedural", Confidence = pool[e.Conf] == "Exact" ? "High" : pool[e.Conf], Note = p
                        };
                        w.WriteLine(Csv.Line(row, sep, sb));
                        added++; addedHere++;
                    }
                Expand(key, depth, p, via);
            }
            if (addedHere > 0) { var k = server + "|" + database; var cur = addedRows.GetValueOrDefault(k); addedRows[k] = (cur.obj + addedHere, cur.col); }
        }
        Log.Info($"Prosedürler arası yayılım: {added:N0} satır, {sw.ElapsedMilliseconds} ms");
    }

    // ---------------------------------------------------------------- kalıcıdan kalıcıya indirgeme → Collapsed.csv
    static bool Transient(string t) => t is "TempTable" or "TableVariable" or "Tvp" or "View" or "ScalarFunction" or "InlineTvf" or "MsTvf" or "Procedure";
    static bool Local(string t) => t is "TempTable" or "TableVariable" or "Tvp";
    static long NodeKey(string server, string db, string schema, string obj, string col) => NK.Key(server, db, schema, obj, col);
    static string Worst(string a, string b) { static int R(string c) => c switch { "Exact" => 0, "High" => 1, "Medium" => 2, _ => 3 }; return R(a) >= R(b) ? a : b; }

    void Collapse()
    {
        var sw = Stopwatch.StartNew();
        var src = Path.Combine(csvDir, "ColumnLineage.csv");
        var outPath = Path.Combine(csvDir, "Collapsed.csv");
        if (!File.Exists(src)) { Csv.Write(outPath, Array.Empty<CollapsedRow>(), sep); return; }
        // Geçiş A: yalnız geçici hedefli kenarlar indekslenir (Walk sadece bunları arar)
        long indexed = 0;
        foreach (var r in Csv.Read<ColumnLineageRow>(src, sep))
        {
            if (r.FlowKind == "Indirect" || !Transient(r.TargetObjectType)) continue;
            long modKey = NK.Key(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            long tk = NodeKey(r.TargetServer, r.TargetDatabase, r.TargetSchema, r.TargetObject, r.TargetColumn);
            if (Local(r.TargetObjectType)) tk = NK.Key(modKey, tk);
            (incoming.TryGetValue(tk, out var l) ? l : incoming[tk] = new()).Add(new CEdge { SServer = pool.Id(r.SourceServer), SDb = pool.Id(r.SourceDatabase), SSchema = pool.Id(r.SourceSchema), SObj = pool.Id(r.SourceObject), SType = pool.Id(r.SourceObjectType), SCol = pool.Id(r.SourceColumn), Kind = Enum.Parse<FlowKind>(r.FlowKind), Conf = pool.Id(r.Confidence), ModKey = modKey });
            indexed++;
        }
        Log.Info($"İndirgeme indeksi: {indexed:N0} geçici-hedefli kenar, {incoming.Count:N0} düğüm, havuz {pool.Count:N0} ad, {sw.ElapsedMilliseconds} ms");
        // Geçiş B: kalıcı hedefli kenarlardan geriye yürü
        long outRows = 0;
        var seen = new HashSet<long>();
        using var w = Csv.OpenWriter(outPath);
        w.WriteLine(Csv.Header<CollapsedRow>(sep));
        var sb = new StringBuilder();
        foreach (var r in Csv.Read<ColumnLineageRow>(src, sep))
        {
            if (r.FlowKind == "Indirect") continue;
            if (!(r.TargetObjectType is "Table" or "External" or "Unresolved" or "System")) continue;
            long modKey = NK.Key(r.Server, r.Database, r.ModuleSchema, r.ModuleName);
            long tgtKey = NodeKey(r.TargetServer, r.TargetDatabase, r.TargetSchema, r.TargetObject, r.TargetColumn);
            var startEdge = new CEdge { SServer = pool.Id(r.SourceServer), SDb = pool.Id(r.SourceDatabase), SSchema = pool.Id(r.SourceSchema), SObj = pool.Id(r.SourceObject), SType = pool.Id(r.SourceObjectType), SCol = pool.Id(r.SourceColumn), Kind = Enum.Parse<FlowKind>(r.FlowKind), Conf = pool.Id(r.Confidence), ModKey = modKey };
            int budget = 5000;
            string dbKey = r.Server + "|" + r.Database;
            Walk(startEdge, modKey, 1, r.SourceSchema + "." + r.SourceObject + "." + r.SourceColumn, startEdge.Kind, Worst("Exact", r.Confidence), new HashSet<long>(), (s, hops, path, kind, conf) =>
            {
                if (budget-- <= 0) return;
                long key = NK.Key(NK.Key(modKey, NodeKey(pool[s.SServer], pool[s.SDb], pool[s.SSchema], pool[s.SObj], pool[s.SCol])), NK.Key(tgtKey, (long)kind));
                if (!seen.Add(key)) return;
                var row = new CollapsedRow
                {
                    RunId = runId, Server = r.Server, Database = r.Database, ModuleSchema = r.ModuleSchema, ModuleName = r.ModuleName, ModuleType = r.ModuleType,
                    SourceServer = pool[s.SServer], SourceDatabase = pool[s.SDb], SourceSchema = pool[s.SSchema], SourceObject = pool[s.SObj], SourceObjectType = pool[s.SType], SourceColumn = pool[s.SCol],
                    TargetServer = r.TargetServer, TargetDatabase = r.TargetDatabase, TargetSchema = r.TargetSchema, TargetObject = r.TargetObject, TargetColumn = r.TargetColumn,
                    FlowKind = kind.ToString(), Hops = hops, Path = path + " → " + r.TargetSchema + "." + r.TargetObject + "." + r.TargetColumn, Confidence = conf
                };
                w.WriteLine(Csv.Line(row, sep, sb));
                outRows++;
                var cur = addedRows.GetValueOrDefault(dbKey); addedRows[dbKey] = (cur.obj, cur.col + 1);
            });
        }
        incoming.Clear();
        Log.Info($"İndirgeme: {outRows:N0} kalıcı→kalıcı satır, {sw.ElapsedMilliseconds} ms");
    }

    void Walk(CEdge s, long modKey, int hops, string path, FlowKind kind, string conf, HashSet<long> visited, Action<CEdge, int, string, FlowKind, string> emit)
    {
        string stype = pool[s.SType];
        if (!Transient(stype) || hops > cfg.MaxCollapseDepth) { emit(s, hops, path, kind, conf); return; }
        string sserver = pool[s.SServer], sdb = pool[s.SDb], sschema = pool[s.SSchema], sobj = pool[s.SObj], scol = pool[s.SCol];
        long nk = NodeKey(sserver, sdb, sschema, sobj, scol);
        bool local = Local(stype);
        long lookup = local ? NK.Key(modKey, nk) : nk;
        // proc sonuç kümesi pozisyonel: RS1:#i → RS1:<ad>
        if (stype == "Procedure" && scol.StartsWith("RS1:#") && int.TryParse(scol[5..], out int ord) && resultSetNames.TryGetValue(NK.Key(sserver, sdb, sschema, sobj), out var names) && ord - 1 < names.Count)
            lookup = NodeKey(sserver, sdb, sschema, sobj, "RS1:" + names[ord - 1]);
        if (!visited.Add(lookup)) return;
        var edges = incoming.TryGetValue(lookup, out var l) ? l : null;
        if (edges == null && scol != "*")
        {
            long star = local ? NK.Key(modKey, NodeKey(sserver, sdb, sschema, sobj, "*")) : NodeKey(sserver, sdb, sschema, sobj, "*");
            incoming.TryGetValue(star, out edges);
        }
        long edgesMod = modKey;
        if ((edges == null || edges.Count == 0) && stype == "TempTable" && !sobj.StartsWith("##"))
        {
            // bu modülde doldurulmamış temp: çağrılan (ya da çağıran) modülün aynı adlı temp'ine bak
            foreach (var (nk2, sd, sv, sdb2) in (callees.TryGetValue(modKey, out var cl) ? cl : new()).Concat(callers.TryGetValue(modKey, out var cr) ? cr : new()))
            {
                // modül-yerel düğümlerde şema alanı = sahibi modül ("schema.name")
                string sch = pool[sd];
                long other = NK.Key(nk2, NodeKey(pool[sv], pool[sdb2], sch, sobj, scol));
                if (!incoming.TryGetValue(other, out edges)) incoming.TryGetValue(NK.Key(nk2, NodeKey(pool[sv], pool[sdb2], sch, sobj, "*")), out edges);
                if (edges != null && edges.Count > 0) { edgesMod = nk2; break; }
            }
        }
        if (edges == null || edges.Count == 0) { emit(s, hops, path, kind, conf); visited.Remove(lookup); return; }
        foreach (var e in edges)
            Walk(e, edgesMod, hops + 1, pool[e.SSchema] + "." + pool[e.SObj] + "." + pool[e.SCol] + " → " + path, Deps.Stronger(kind, e.Kind), Worst(conf, pool[e.Conf]), visited, emit);
        visited.Remove(lookup);
    }

    // ---------------------------------------------------------------- özetler
    List<SummaryRow> BuildSummary()
    {
        var rows = new List<SummaryRow>();
        foreach (var (task, meta, _) in parts)
        {
            var s = meta.Summary; s.RunId = runId;
            var add = addedRows.GetValueOrDefault(s.Server + "|" + s.Database);
            s.ObjectLineageRows = (int)Math.Min(int.MaxValue, s.ObjectLineageRows + add.obj);
            s.CollapsedRows = (int)Math.Min(int.MaxValue, add.col);
            s.ElapsedMs = (int)Math.Min(int.MaxValue, meta.ElapsedMs);
            rows.Add(s);
        }
        // "(jobs)" görevi: planlanmamış DB'lerde (master vb.) koşan job adımlarının eklenen satırları o sunucunun (jobs) satırına
        foreach (var jobsRow in rows.Where(r => r.Database == PlanTask.JobsDb))
        {
            var covered = new HashSet<string>(rows.Where(r => NameComparer.Eq(r.Server, jobsRow.Server)).Select(r => r.Server + "|" + r.Database), NameComparer.Instance);
            foreach (var (k, add) in addedRows)
                if (k.StartsWith(jobsRow.Server + "|", StringComparison.OrdinalIgnoreCase) && !covered.Contains(k))
                { jobsRow.ObjectLineageRows += (int)add.obj; jobsRow.CollapsedRows += (int)add.col; }
        }
        return rows.OrderBy(r => r.Server, NameComparer.Instance).ThenBy(r => r.Database, NameComparer.Instance).ToList();
    }

    (List<IssueSummaryRow>, List<UnresolvedKindRow>) BuildIssueSummaries()
    {
        var status = new Dictionary<long, string>();
        var modPath = Path.Combine(csvDir, "Modules.csv");
        if (File.Exists(modPath)) foreach (var m in Csv.Read<ModuleRow>(modPath, sep)) status[NK.Key(m.Server, m.Database, m.ModuleSchema, m.ModuleName)] = m.ParseStatus;
        var byMod = new Dictionary<long, (string server, string db, string schema, string name, int issues, int errors, int warnings, Dictionary<string, int> kinds)>();
        var byKind = new Dictionary<string, (string server, string db, string kind, int count, HashSet<string> names, string example)>(NameComparer.Instance);
        var unrPath = Path.Combine(csvDir, "Unresolved.csv");
        if (File.Exists(unrPath))
            foreach (var u in Csv.Read<UnresolvedRow>(unrPath, sep))
            {
                long k = NK.Key(u.Server, u.Database, u.ModuleSchema, u.ModuleName);
                if (!byMod.TryGetValue(k, out var e)) e = (u.Server, u.Database, u.ModuleSchema, u.ModuleName, 0, 0, 0, new Dictionary<string, int>());
                e.issues++; if (u.Severity == "error") e.errors++; else if (u.Severity == "warning") e.warnings++;
                e.kinds[u.Kind] = e.kinds.GetValueOrDefault(u.Kind) + 1;
                byMod[k] = e;
                string kk = u.Server + "|" + u.Database + "|" + u.Kind;
                if (!byKind.TryGetValue(kk, out var g)) g = (u.Server, u.Database, u.Kind, 0, new HashSet<string>(NameComparer.Instance), u.Name);
                g.count++; g.names.Add(u.Name);
                byKind[kk] = g;
            }
        var top = byMod.Select(kv => new IssueSummaryRow
        {
            RunId = runId, Server = kv.Value.server, Database = kv.Value.db, ModuleSchema = kv.Value.schema, ModuleName = kv.Value.name,
            ParseStatus = status.GetValueOrDefault(kv.Key) ?? "", Issues = kv.Value.issues, Errors = kv.Value.errors, Warnings = kv.Value.warnings,
            TopKinds = string.Join("; ", kv.Value.kinds.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key}={x.Value}"))
        }).OrderByDescending(x => x.Errors).ThenByDescending(x => x.Issues).Take(5000).ToList();
        var kinds = byKind.Values.Select(g => new UnresolvedKindRow { RunId = runId, Server = g.server, Database = g.db, Kind = g.kind, Count = g.count, DistinctNames = g.names.Count, Example = g.example }).OrderByDescending(x => x.Count).ToList();
        return (top, kinds);
    }

    Catalog StubCatalog()
    {
        var cat = new Catalog();
        foreach (var s in plan.Servers)
        {
            var sc = new ServerCatalog { Name = s.ServerName, ConfigName = s.Name, Version = s.Version };
            foreach (var d in s.Databases) sc.KnownDbs[d] = new KnownDb { Name = d };
            cat.Add(sc);
        }
        return cat;
    }
}

#endregion
