// ============================================================================
#region 8. Çıktı: CSV (akışlı yazma/okuma), parça yazıcı, Excel, DDL, SqlBulkCopy
// ============================================================================

/// <summary>Satır sınıfları için hızlı (önceden derlenmiş) property erişimi; CSV/Excel/bulk hepsi bunu kullanır.</summary>
static class RowType<T> where T : new()
{
    public static readonly PropertyInfo[] Props = typeof(T).GetProperties();
    public static readonly string[] Names = Props.Select(p => p.Name).ToArray();
    public static readonly Func<T, object?>[] Getters = Props.Select(p => (Func<T, object?>)(r => p.GetValue(r))).ToArray();
    public static readonly Action<T, string>[] Setters = Props.Select(p =>
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        return (Action<T, string>)((r, s) =>
        {
            if (!p.CanWrite) return;
            if (s.Length == 0 && t != typeof(string)) return;
            object v = t == typeof(int) ? int.Parse(s, CultureInfo.InvariantCulture)
                     : t == typeof(long) ? long.Parse(s, CultureInfo.InvariantCulture)
                     : t == typeof(double) ? double.Parse(s, CultureInfo.InvariantCulture)
                     : t == typeof(bool) ? (s == "True" || s == "true" || s == "1")
                     : t == typeof(DateTime) ? DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                     : s;
            p.SetValue(r, v);
        });
    }).ToArray();
}

static class Csv
{
    public static string Cell(object? v, string sep)
    {
        string s = v switch { null => "", DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"), double d => d.ToString(CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };
        if (s.Contains(sep) || s.Contains('"') || s.Contains('\n') || s.Contains('\r')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
    public static string Header<T>(string sep) where T : new() => string.Join(sep, RowType<T>.Names);
    public static string Line<T>(T row, string sep, StringBuilder sb) where T : new()
    {
        sb.Clear();
        var g = RowType<T>.Getters;
        for (int i = 0; i < g.Length; i++) { if (i > 0) sb.Append(sep); sb.Append(Cell(g[i](row), sep)); }
        return sb.ToString();
    }

    public static StreamWriter OpenWriter(string path, bool append = false)
    {
        bool gz = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        Stream fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        if (gz) fs = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        return new StreamWriter(fs, append ? new UTF8Encoding(false) : new UTF8Encoding(true), 1 << 16);
    }
    public static StreamReader OpenReader(string path)
    {
        Stream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) fs = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
        return new StreamReader(fs, Encoding.UTF8, true, 1 << 16);
    }

    public static void Write<T>(string path, IEnumerable<T> rows, string sep) where T : new()
    {
        using var w = OpenWriter(path);
        w.WriteLine(Header<T>(sep));
        var sb = new StringBuilder();
        foreach (var r in rows) w.WriteLine(Line(r, sep, sb));
    }

    /// <summary>RFC4180 okuyucu: alanları (tırnak, gömülü satır sonu dahil) döner. İlk kayıt başlıktır.</summary>
    public static IEnumerable<string[]> ReadRecords(string path, string sep)
    {
        using var r = OpenReader(path);
        char sc = sep[0];
        var fields = new List<string>(32); var sb = new StringBuilder();
        int ch; bool inQ = false, any = false;
        while ((ch = r.Read()) >= 0)
        {
            char c = (char)ch;
            if (inQ)
            {
                if (c == '"') { if (r.Peek() == '"') { r.Read(); sb.Append('"'); } else inQ = false; }
                else sb.Append(c);
                continue;
            }
            if (c == '"') { inQ = true; any = true; }
            else if (c == sc) { fields.Add(sb.ToString()); sb.Clear(); any = true; }
            else if (c == '\r') { }
            else if (c == '\n') { fields.Add(sb.ToString()); sb.Clear(); yield return fields.ToArray(); fields.Clear(); any = false; }
            else { sb.Append(c); any = true; }
        }
        if (any || sb.Length > 0) { fields.Add(sb.ToString()); yield return fields.ToArray(); }
    }

    /// <summary>Başlığa göre eşleyerek satır nesneleri üretir (kolon sırası/eksik kolon toleranslı).</summary>
    public static IEnumerable<T> Read<T>(string path, string sep) where T : new()
    {
        if (!File.Exists(path)) yield break;
        int[]? map = null;
        foreach (var rec in ReadRecords(path, sep))
        {
            if (map == null)
            {
                var idx = RowType<T>.Names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
                map = rec.Select(h => idx.TryGetValue(h.TrimStart('﻿'), out var i) ? i : -1).ToArray();
                continue;
            }
            var t = new T();
            for (int i = 0; i < rec.Length && i < map.Length; i++) if (map[i] >= 0) RowType<T>.Setters[map[i]](t, rec[i]);
            yield return t;
        }
    }

    /// <summary>Ham satır kopyalama: kaynak dosyanın başlığını atlar, satırları hedefe yazar. Dönüş: kopyalanan satır.</summary>
    public static long AppendRaw(string src, StreamWriter dst, Func<string, bool>? keep = null)
    {
        long n = 0;
        using var r = OpenReader(src);
        string? line = r.ReadLine();   // header
        if (line == null) return 0;
        // gömülü satır sonu içeren alanlar: tırnak dengesini izleyerek fiziksel satırları mantıksal kayda birleştir
        var sb = new StringBuilder(); bool open = false;
        while ((line = r.ReadLine()) != null)
        {
            if (open) sb.Append('\n');
            sb.Append(line);
            open ^= (CountQuotes(line) & 1) == 1;
            if (open) continue;
            var rec = sb.ToString(); sb.Clear();
            if (keep == null || keep(rec)) { dst.WriteLine(rec); n++; }
        }
        return n;
    }
    static int CountQuotes(string s) { int c = 0; foreach (var ch in s) if (ch == '"') c++; return c; }
}

/// <summary>Görev parçası yazıcısı: tablo başına akışlı CSV (isteğe bağlı gzip), thread-güvenli.</summary>
sealed class PartWriter : IDisposable
{
    readonly string dir; readonly string sep; public readonly bool Compressed;
    readonly Dictionary<string, (StreamWriter w, object gate, long count)> files = new();
    readonly object gate = new();
    public PartWriter(string dir, string sep, bool compress) { this.dir = dir; this.sep = sep; Compressed = compress; Directory.CreateDirectory(dir); }

    public static string FileName(string table, bool compressed) => table + (compressed ? ".csv.gz" : ".csv");

    public void Add<T>(string table, T row) where T : new()
    {
        (StreamWriter w, object g, long _) e;
        lock (gate)
        {
            if (!files.TryGetValue(table, out e))
            {
                var w = Csv.OpenWriter(Path.Combine(dir, FileName(table, Compressed)));
                w.WriteLine(Csv.Header<T>(sep));
                e = (w, new object(), 0);
                files[table] = e;
            }
        }
        var sb = _sb ??= new StringBuilder();
        string line = Csv.Line(row, sep, sb);
        lock (e.g) { e.w.WriteLine(line); }
        lock (gate) { var cur = files[table]; files[table] = (cur.w, cur.gate, cur.count + 1); }
    }
    [ThreadStatic] static StringBuilder? _sb;

    public Dictionary<string, long> Counts() { lock (gate) return files.ToDictionary(k => k.Key, v => v.Value.count); }
    public void Dispose() { lock (gate) { foreach (var f in files.Values) f.w.Dispose(); files.Clear(); } }
}

static class Output
{
    static readonly HashSet<string> LongCols = new(StringComparer.OrdinalIgnoreCase) { "Expression", "Note", "Template", "Holes", "Errors", "Path", "ParseError", "Name" };

    public static readonly (string table, Type type)[] Tables =
    {
        ("Summary", typeof(SummaryRow)), ("ColumnLineage", typeof(ColumnLineageRow)), ("ObjectLineage", typeof(ObjectLineageRow)), ("Collapsed", typeof(CollapsedRow)),
        ("Modules", typeof(ModuleRow)), ("DynamicSql", typeof(DynamicSqlRow)), ("Unresolved", typeof(UnresolvedRow)),
        ("TopIssues", typeof(IssueSummaryRow)), ("CatalogDeps", typeof(CatalogDepRow)), ("Statements", typeof(StatementRow)), ("Query", typeof(QueryRow)),
        ("UnresolvedByKind", typeof(UnresolvedKindRow)),
    };

    /// <summary>Excel: satır sayısı sınırın altındaysa tüm sayfalar (CSV'den geri okunur), değilse yalnız özet sayfalar.</summary>
    public static void WriteExcel(LineageConfig cfg, string runId, string csvDir, LineageData small)
    {
        var dir = cfg.Output.Directory; Directory.CreateDirectory(dir);
        string sep = cfg.Output.CsvSeparator;
        long totalRows = 0;
        foreach (var t in new[] { "ColumnLineage", "ObjectLineage", "Collapsed", "Modules", "DynamicSql", "Unresolved" }) totalRows += CountLines(Path.Combine(csvDir, t + ".csv"));
        bool big = totalRows > 3_000_000;
        if (big) Log.Warn($"Büyük çıktı: toplam {totalRows:N0} satır (> 3.000.000). Lineage sayfaları yalnız CSV'de; Excel'e özet sayfalar (Summary, Modules, DynamicSql, Unresolved, TopIssues, UnresolvedByKind) konur. Tam yükleme için --sql-target kullanın.");
        var path = Path.Combine(dir, big ? $"lineage-ozet-{runId}.xlsx" : $"lineage-{runId}.xlsx");
        var sw = Stopwatch.StartNew();
        using var wb = new XLWorkbook();
        AddSheet(wb, "Summary", small.SummaryRows, cfg);
        if (!big)
        {
            AddSheet(wb, "ColumnLineage", Csv.Read<ColumnLineageRow>(Path.Combine(csvDir, "ColumnLineage.csv"), sep).ToList(), cfg);
            AddSheet(wb, "ObjectLineage", Csv.Read<ObjectLineageRow>(Path.Combine(csvDir, "ObjectLineage.csv"), sep).ToList(), cfg);
            AddSheet(wb, "Collapsed", Csv.Read<CollapsedRow>(Path.Combine(csvDir, "Collapsed.csv"), sep).ToList(), cfg);
        }
        AddSheet(wb, "Modules", Csv.Read<ModuleRow>(Path.Combine(csvDir, "Modules.csv"), sep).ToList(), cfg);
        AddSheet(wb, "DynamicSql", Csv.Read<DynamicSqlRow>(Path.Combine(csvDir, "DynamicSql.csv"), sep).ToList(), cfg);
        AddSheet(wb, "Unresolved", Csv.Read<UnresolvedRow>(Path.Combine(csvDir, "Unresolved.csv"), sep).Take(big ? cfg.Output.MaxRowsPerSheet : int.MaxValue).ToList(), cfg);
        AddSheet(wb, "TopIssues", small.IssueSummaryRows, cfg);
        if (small.QueryRows.Count > 0) AddSheet(wb, "Query", small.QueryRows, cfg);
        if (!big) AddSheet(wb, "CatalogDeps", Csv.Read<CatalogDepRow>(Path.Combine(csvDir, "CatalogDeps.csv"), sep).ToList(), cfg);
        if (!big && File.Exists(Path.Combine(csvDir, "Statements.csv"))) AddSheet(wb, "Statements", Csv.Read<StatementRow>(Path.Combine(csvDir, "Statements.csv"), sep).ToList(), cfg);
        AddSheet(wb, "UnresolvedByKind", small.UnresolvedKindRows, cfg);
        wb.SaveAs(path);
        Log.Info($"Excel: {path} ({sw.ElapsedMilliseconds} ms)" + (big ? " — yalnız özet sayfalar" : ""));
    }

    static long CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        long n = 0; using var r = Csv.OpenReader(path); while (r.ReadLine() != null) n++;
        return Math.Max(0, n - 1);
    }

    static void AddSheet<T>(XLWorkbook wb, string name, List<T> rows, LineageConfig cfg)
    {
        int max = Math.Max(1000, cfg.Output.MaxRowsPerSheet);
        int part = 0;
        for (int start = 0; start == 0 || start < rows.Count; start += max)
        {
            part++;
            var ws = wb.Worksheets.Add(part == 1 ? name : $"{name}_{part}");
            var slice = rows.Skip(start).Take(max).ToList();
            var props = typeof(T).GetProperties();
            for (int c = 0; c < props.Length; c++) ws.Cell(1, c + 1).Value = props[c].Name;
            ws.Row(1).Style.Font.Bold = true;
            if (slice.Count > 0)
            {
                if (slice.Count <= 100_000) ws.Cell(1, 1).InsertTable(slice, name + (part > 1 ? part.ToString() : ""), true);
                else ws.Cell(2, 1).InsertData(slice);
            }
            ws.SheetView.FreezeRows(1);
            if (slice.Count <= 20_000) ws.Columns().AdjustToContents(1, Math.Min(slice.Count + 1, 200), 8, 60);
        }
    }

    static string SqlType(PropertyInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (t == typeof(int)) return "int";
        if (t == typeof(long)) return "bigint";
        if (t == typeof(double)) return "float";
        if (t == typeof(bool)) return "bit";
        if (t == typeof(DateTime)) return "datetime2(3)";
        return LongCols.Contains(p.Name) ? "nvarchar(max)" : "nvarchar(512)";
    }

    public static void WriteDdl(LineageConfig cfg)
    {
        Directory.CreateDirectory(cfg.Output.Directory);
        var path = Path.Combine(cfg.Output.Directory, "lineage_tables.sql");
        File.WriteAllText(path, Ddl(cfg.Output.SqlSchema), new UTF8Encoding(true));
        Log.Info($"DDL: {path}");
    }

    public static string Ddl(string schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');");
        sb.AppendLine("GO");
        foreach (var (table, type) in Tables)
        {
            sb.AppendLine($"IF OBJECT_ID(N'[{schema}].[{table}]') IS NULL");
            sb.AppendLine($"CREATE TABLE [{schema}].[{table}] (");
            sb.AppendLine("    [Id] bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            var props = type.GetProperties();
            for (int i = 0; i < props.Length; i++)
                sb.AppendLine($"    [{props[i].Name}] {SqlType(props[i])} NULL{(i < props.Length - 1 ? "," : "")}");
            sb.AppendLine(");");
            sb.AppendLine("GO");
            // şema evrimi: sonradan eklenen kolonlar
            foreach (var p in props) sb.AppendLine($"IF COL_LENGTH(N'[{schema}].[{table}]', N'{p.Name}') IS NULL ALTER TABLE [{schema}].[{table}] ADD [{p.Name}] {SqlType(p)} NULL;");
            sb.AppendLine("GO");
        }
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Collapsed_Target') CREATE INDEX IX_Collapsed_Target ON [{schema}].[Collapsed](RunId, TargetDatabase, TargetSchema, TargetObject, TargetColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Collapsed_Source') CREATE INDEX IX_Collapsed_Source ON [{schema}].[Collapsed](RunId, SourceDatabase, SourceSchema, SourceObject, SourceColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ColumnLineage_Target') CREATE INDEX IX_ColumnLineage_Target ON [{schema}].[ColumnLineage](RunId, TargetDatabase, TargetSchema, TargetObject, TargetColumn);");
        sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ObjectLineage_Object') CREATE INDEX IX_ObjectLineage_Object ON [{schema}].[ObjectLineage](RunId, ObjectDatabase, ObjectSchema, ObjectName);");
        sb.AppendLine("GO");
        sb.Append(Views(schema));
        return sb.ToString();
    }

    /// <summary>Sorgulanabilir katman: son koşu view'leri ve özyinelemeli upstream/downstream fonksiyonları.</summary>
    public static string Views(string s)
    {
        string latest = $"(SELECT TOP 1 RunId FROM [{s}].[Summary] ORDER BY Id DESC)";
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_LatestRun] AS SELECT TOP 1 RunId FROM [{s}].[Summary] ORDER BY Id DESC;").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ColumnEdges] AS SELECT c.* FROM [{s}].[Collapsed] c WHERE c.RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ColumnEdgesDetail] AS SELECT c.* FROM [{s}].[ColumnLineage] c WHERE c.RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_ObjectEdges] AS SELECT DISTINCT RunId, Server, [Database], ModuleSchema, ModuleName, ModuleType, Action, ObjectServer, ObjectDatabase, ObjectSchema, ObjectName, ObjectType, Provenance FROM [{s}].[ObjectLineage] WHERE RunId = {latest};").AppendLine("GO");
        sb.AppendLine($"CREATE OR ALTER VIEW [{s}].[vw_Nodes] AS SELECT DISTINCT SourceServer AS Server, SourceDatabase AS [Database], SourceSchema AS [Schema], SourceObject AS [Object], SourceObjectType AS ObjectType, SourceColumn AS [Column] FROM [{s}].[Collapsed] WHERE RunId = {latest} UNION SELECT DISTINCT TargetServer, TargetDatabase, TargetSchema, TargetObject, 'Table', TargetColumn FROM [{s}].[Collapsed] WHERE RunId = {latest};").AppendLine("GO");
        foreach (var (fn, fromSide, toSide) in new[] { ("fn_Upstream", "Target", "Source"), ("fn_Downstream", "Source", "Target") })
        {
            sb.AppendLine($@"CREATE OR ALTER FUNCTION [{s}].[{fn}](@Database nvarchar(256), @Schema nvarchar(256), @Object nvarchar(256), @Column nvarchar(256), @MaxHops int)
RETURNS TABLE AS RETURN
WITH r AS (
  SELECT c.{toSide}Server AS Server, c.{toSide}Database AS [Database], c.{toSide}Schema AS [Schema], c.{toSide}Object AS [Object], c.{toSide}Column AS [Column],
         c.{fromSide}Column AS StartColumn, 1 AS Hops, c.FlowKind, c.Confidence,
         CAST(c.SourceSchema + '.' + c.SourceObject + '.' + c.SourceColumn + ' -> ' + c.TargetSchema + '.' + c.TargetObject + '.' + c.TargetColumn AS nvarchar(max)) AS Path,
         CAST('|' + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|' AS nvarchar(max)) AS Visited,
         CAST(c.ModuleSchema + '.' + c.ModuleName AS nvarchar(max)) AS Modules
  FROM [{s}].[Collapsed] c
  WHERE c.RunId = {latest} AND c.{fromSide}Database = @Database AND c.{fromSide}Schema = @Schema AND c.{fromSide}Object = @Object AND (@Column IS NULL OR c.{fromSide}Column = @Column)
  UNION ALL
  SELECT c.{toSide}Server, c.{toSide}Database, c.{toSide}Schema, c.{toSide}Object, c.{toSide}Column, r.StartColumn, r.Hops + 1, c.FlowKind, c.Confidence,
         {(fromSide == "Target" ? "c.SourceSchema + '.' + c.SourceObject + '.' + c.SourceColumn + ' -> ' + r.Path" : "r.Path + ' -> ' + c.TargetSchema + '.' + c.TargetObject + '.' + c.TargetColumn")},
         r.Visited + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|',
         {(fromSide == "Target" ? "c.ModuleSchema + '.' + c.ModuleName + ' > ' + r.Modules" : "r.Modules + ' > ' + c.ModuleSchema + '.' + c.ModuleName")}
  FROM r JOIN [{s}].[Collapsed] c ON c.RunId = {latest} AND c.{fromSide}Database = r.[Database] AND c.{fromSide}Schema = r.[Schema] AND c.{fromSide}Object = r.[Object] AND c.{fromSide}Column = r.[Column]
  WHERE r.Hops < @MaxHops AND r.Visited NOT LIKE '%|' + c.{toSide}Database + '.' + c.{toSide}Schema + '.' + c.{toSide}Object + '.' + c.{toSide}Column + '|%'
)
SELECT Server, [Database], [Schema], [Object], [Column], StartColumn, Hops, FlowKind, Confidence, Path, Modules FROM r;").AppendLine("GO");
        }
        sb.AppendLine($"-- Örnek: SELECT * FROM [{s}].[fn_Upstream](N'dw_production', N'dbo', N'MUSTERI_OZET', NULL, 10) ORDER BY StartColumn, Hops;");
        sb.AppendLine($"-- Örnek: SELECT * FROM [{s}].[fn_Downstream](N'dw_production', N'dbo', N'MUSTERI', N'AD', 10);");
        return sb.ToString();
    }

    /// <summary>Son CSV klasöründen SqlBulkCopy ile yükler (akışlı; 20.000 satırlık partiler).</summary>
    public static void BulkLoad(LineageConfig cfg, string csvDir)
    {
        var sw = Stopwatch.StartNew();
        using var cn = new SqlConnection(cfg.Output.SqlBulkTarget);
        cn.Open();
        foreach (var batch in Regex.Split(Ddl(cfg.Output.SqlSchema), @"^\s*GO\s*$", RegexOptions.Multiline))
        {
            var text = batch.Trim();
            if (text.Length == 0 || text.Split('\n').All(l => l.TrimStart().StartsWith("--") || l.Trim().Length == 0)) continue;
            using var cmd = new SqlCommand(text, cn) { CommandTimeout = 300 };
            cmd.ExecuteNonQuery();
        }
        string sep = cfg.Output.CsvSeparator;
        long total = 0;
        foreach (var (table, type) in Tables)
        {
            var path = Path.Combine(csvDir, table + ".csv");
            if (!File.Exists(path)) continue;
            var m = typeof(Output).GetMethod(nameof(LoadTable), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
            total += (long)m.Invoke(null, new object[] { cn, cfg.Output.SqlSchema, table, path, sep })!;
        }
        Log.Info($"SqlBulkCopy: {cfg.Output.SqlSchema}.* yüklendi, {total:N0} satır ({sw.ElapsedMilliseconds} ms); sorgu: SELECT * FROM [{cfg.Output.SqlSchema}].[fn_Upstream](db, schema, tablo, kolon|NULL, 10)");
    }

    static long LoadTable<T>(SqlConnection cn, string schema, string table, string path, string sep) where T : new()
    {
        var props = RowType<T>.Props; var getters = RowType<T>.Getters;
        var dt = new DataTable();
        foreach (var p in props) dt.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
        using var bc = new SqlBulkCopy(cn) { DestinationTableName = $"[{schema}].[{table}]", BatchSize = 10_000, BulkCopyTimeout = 0 };
        foreach (var p in props) bc.ColumnMappings.Add(p.Name, p.Name);
        long n = 0;
        foreach (var r in Csv.Read<T>(path, sep))
        {
            var vals = new object[props.Length];
            for (int i = 0; i < props.Length; i++) vals[i] = getters[i](r) ?? DBNull.Value;
            dt.Rows.Add(vals); n++;
            if (dt.Rows.Count >= 20_000) { bc.WriteToServer(dt); dt.Clear(); }
        }
        if (dt.Rows.Count > 0) bc.WriteToServer(dt);
        return n;
    }
}

#endregion
