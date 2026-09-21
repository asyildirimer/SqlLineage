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
            var dbs = (cat.FindServer(curServer)?.DbNames ?? Enumerable.Empty<string>()).Where(d => rx.IsMatch(d)).Take(12).ToList();
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
