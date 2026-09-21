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
