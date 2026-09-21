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
            else curDb = cat.FindServer(srvName)?.DbNames.FirstOrDefault() ?? curDb;
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
