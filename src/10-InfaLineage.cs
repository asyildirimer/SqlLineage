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
            int mi = 0; var hb = Stopwatch.StartNew();
            foreach (var m in f.Mappings.Values)
            {
                mi++;
                string mname = (string?)m.Attribute("NAME") ?? "";
                if (hb.Elapsed.TotalSeconds >= 60) { InfaLog.Info($"  [{f.Name}] {mi}/{f.Mappings.Count} mapping — şimdi: {mname}"); hb.Restart(); }
                var msw = Stopwatch.StartNew();
                var g = new MappingAnalyzer(folders, f).Analyze(m);
                long analyzeMs = msw.ElapsedMilliseconds;
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
                if (msw.ElapsedMilliseconds > 30_000) InfaLog.Warn($"  [{f.Name}] {g.Name}: {msw.ElapsedMilliseconds / 1000} s (analiz {analyzeMs / 1000} s, fiziksel {(msw.ElapsedMilliseconds - analyzeMs) / 1000} s; {g.Instances.Count} instance, {g.Edges.Count} kenar, {sessionCount} session)");
                _memo.Remove(g);
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

    /// <summary>Hedef porttan kaynağa geriye yürür. Düğüm başına sonuç kümesi bir kez hesaplanıp önbelleklenir (memo): elmas biçimli
    /// mapping'lerde yol sayısı üstel büyür, önbellek olmadan saatler sürer. Hops = en kısa, Path = ilk bulunan yol.</summary>
    void Walk(MappingGraph g, string inst, string port, int hops, string path, FlowKind kind, HashSet<string> visited, string expr, Action<string, string, int, string, FlowKind, string> emit)
    {
        foreach (var r in Sources(g, inst, port, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            if (r.hops == 0) continue;   // hedef portun kendisi (girişi yok) kaynak değildir
            var k = (kind == FlowKind.Indirect || r.kind == FlowKind.Indirect) ? FlowKind.Indirect : Stronger(kind, r.kind);
            emit(r.inst, r.port, hops + r.hops, r.path + " → " + path, k, r.expr != "" ? r.expr : expr);
        }
    }

    sealed record SrcResult(string inst, string port, int hops, string path, FlowKind kind, string expr);
    readonly Dictionary<MappingGraph, Dictionary<string, List<SrcResult>>> _memo = new();
    const int MaxResultsPerNode = 5000;

    /// <summary>(inst, port) düğümünü besleyen uç kaynaklar; path = "src.port → … → inst.port" (bu düğüm dahil).</summary>
    List<SrcResult> Sources(MappingGraph g, string inst, string port, HashSet<string> onStack)
    {
        string key = MappingGraph.Key(inst, port);
        if (!_memo.TryGetValue(g, out var memo)) _memo[g] = memo = new(StringComparer.OrdinalIgnoreCase);
        if (memo.TryGetValue(key, out var cached)) return cached;
        var result = new List<SrcResult>();
        if (!onStack.Add(key)) return result;   // döngü: bu kenar üzerinden yol yok
        var edges = g.Incoming.GetValueOrDefault(key) ?? g.Incoming.GetValueOrDefault(MappingGraph.Key(inst, "*"));
        if (edges == null || edges.Count == 0)
            result.Add(new SrcResult(inst, port, 0, inst + "." + port, FlowKind.Direct, ""));   // girişi olmayan port: sabit/bağlantısız kaynak
        else
        {
            var best = new Dictionary<string, SrcResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in edges)
            {
                var si = g.Instances.GetValueOrDefault(e.FromInst);
                bool endpoint = (si != null && si.Type is "SOURCE" or "LOOKUPTABLE" or "SQLTABLE" or "PROCEDURE") || e.FromInst.StartsWith("SQL:") || e.FromInst.StartsWith("LKP:") || e.FromInst.StartsWith("SP:");
                string here = inst + "." + port;
                if (endpoint)
                {
                    Add(best, new SrcResult(e.FromInst, e.FromPort, 1, e.FromInst + "." + e.FromPort + " → " + here, e.Kind, e.Expr));
                    continue;
                }
                string fk = MappingGraph.Key(e.FromInst, e.FromPort);
                if (onStack.Contains(fk)) continue;   // döngü (örn. v = v + 1 değişken portu): bu kenar üzerinden yol yok
                foreach (var r in Sources(g, e.FromInst, e.FromPort, onStack))
                {
                    var k = (r.kind == FlowKind.Indirect || e.Kind == FlowKind.Indirect) ? FlowKind.Indirect : Stronger(r.kind, e.Kind);
                    Add(best, new SrcResult(r.inst, r.port, r.hops + 1, r.path + " → " + here, k, r.expr != "" ? r.expr : e.Expr));
                    if (best.Count >= MaxResultsPerNode) break;
                }
                if (best.Count >= MaxResultsPerNode) break;
            }
            result.AddRange(best.Values);
        }
        onStack.Remove(key);
        memo[key] = result;
        return result;

        static void Add(Dictionary<string, SrcResult> best, SrcResult r)
        {
            string k = r.inst + "|" + r.port + "|" + r.kind;
            if (!best.TryGetValue(k, out var cur) || r.hops < cur.hops) best[k] = r;
        }
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
