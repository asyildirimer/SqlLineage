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
    /// <summary>Görevler arası statik önbellekleri sıfırlar (aynı süreçte birden çok DB görevi).</summary>
    public static void ResetCaches() { _derivedCols.Clear(); StringFlow.CallerArgs.Clear(); ConfigTableReader.ResetCache(); }
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
                else curDb = cat.FindServer(serverName)?.DbNames.FirstOrDefault() ?? curDb;
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
