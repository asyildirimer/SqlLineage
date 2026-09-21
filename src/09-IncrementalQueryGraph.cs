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

    public static Incremental? Load(LineageConfig cfg, Dictionary<string, string> signatures, string? server = null, string? database = null)
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
            string dbFilter = server != null ? " AND Server = @srv AND [Database] = @db" : "";
            void P(SqlCommand c) { c.Parameters.AddWithValue("@r", inc.PrevRunId); if (server != null) { c.Parameters.AddWithValue("@srv", server); c.Parameters.AddWithValue("@db", database ?? ""); } }
            using (var cmd = new SqlCommand($"SELECT Server, [Database], EngineSignature FROM [{s}].[Summary] WHERE RunId = @r{dbFilter}", cn))
            {
                P(cmd);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string k = r.GetString(0) + "|" + r.GetString(1);
                    if (signatures.TryGetValue(k, out var cur) && cur == (r.IsDBNull(2) ? "" : r.GetString(2))) eligible.Add(k);
                    else Log.Info($"  artımlı: {k} imzası değişti → tam analiz");
                }
            }
            if (eligible.Count == 0) return null;
            foreach (var m in Read<ModuleRow>(cn, $"SELECT * FROM [{s}].[Modules] WHERE RunId = @r{dbFilter}", P))
                if (eligible.Contains(m.Server + "|" + m.Database)) inc.prev[Key(m.Server, m.Database, m.ModuleSchema, m.ModuleName)] = m;
            AnalysisResult Res(string server, string db, string schema, string name)
            {
                string k = Key(server, db, schema, name);
                return inc.rows.TryGetValue(k, out var a) ? a : inc.rows[k] = new AnalysisResult();
            }
            foreach (var x in Read<ColumnLineageRow>(cn, $"SELECT * FROM [{s}].[ColumnLineage] WHERE RunId = @r AND Provenance NOT IN ('Interprocedural','Trigger'){dbFilter}", P)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).ColumnRows.Add(x);
            foreach (var x in Read<ObjectLineageRow>(cn, $"SELECT * FROM [{s}].[ObjectLineage] WHERE RunId = @r AND Provenance NOT IN ('Interprocedural','Trigger'){dbFilter}", P)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).ObjectRows.Add(x);
            foreach (var x in Read<DynamicSqlRow>(cn, $"SELECT * FROM [{s}].[DynamicSql] WHERE RunId = @r{dbFilter}", P)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).DynamicRows.Add(x);
            foreach (var x in Read<UnresolvedRow>(cn, $"SELECT * FROM [{s}].[Unresolved] WHERE RunId = @r AND Kind <> 'CatalogDepMissed'{dbFilter}", P)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).UnresolvedRows.Add(x);
            if (cfg.IncludeStatementText)
                foreach (var x in Read<StatementRow>(cn, $"SELECT * FROM [{s}].[Statements] WHERE RunId = @r{dbFilter}", P)) if (eligible.Contains(x.Server + "|" + x.Database)) Res(x.Server, x.Database, x.ModuleSchema, x.ModuleName).StatementRows.Add(x);
            Log.Info($"Artımlı: önceki koşu {inc.PrevRunId}, {inc.prev.Count} modül aday, {eligible.Count} DB imzası eşleşti, {sw.ElapsedMilliseconds} ms");
            return inc;
        }
        catch (Exception ex) { Log.Warn("Artımlı yükleme başarısız, tam koşu: " + ex.Message); return null; }
    }

    static IEnumerable<T> Read<T>(SqlConnection cn, string sql, Action<SqlCommand> bind) where T : new()
    {
        var props = typeof(T).GetProperties().ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        bind(cmd);
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
        if (p.Length == 4) return cat.HasDb(p[0], p[1]) ? (p[0], p[1], p[2], p[3], null) : (null, p[0], p[1], p[2], p[3]);
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
