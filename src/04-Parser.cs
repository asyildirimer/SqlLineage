#region 4. Parser merdiveni
// ============================================================================

sealed class ParseResult
{
    public TSqlFragment? Fragment;
    public int ParserUsed;
    public IList<ParseError> Errors = new List<ParseError>();
    public List<string> Holes = new();
    public string Status = "Parsed";      // Parsed | ParsedWithHoles | Quarantined | NoDefinition
    public string Text = "";
    public string ErrorText => string.Join(" | ", Errors.Take(5).Select(e => $"L{e.Line}:{e.Column} {e.Message}"));
}

static class ParserLadder
{
    static readonly ConcurrentDictionary<(int, bool), ConcurrentBag<TSqlParser>> _pool = new();
    static readonly Regex StmtStart = new(@"^\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|SET|DECLARE|IF|ELSE|WHILE|BEGIN|END|EXEC|EXECUTE|WITH|CREATE|ALTER|DROP|TRUNCATE|PRINT|RETURN|FETCH|OPEN|CLOSE|DEALLOCATE|GO|RAISERROR|THROW|COMMIT|ROLLBACK|GOTO|WAITFOR|BULK|USE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int FromServerMajor(int m) => m switch { <= 8 => 80, 9 => 90, 10 => 100, 11 => 110, 12 => 120, 13 => 130, 14 => 140, 15 => 150, 16 => 160, _ => 170 };
    public static int FromCompat(int c) => c <= 80 ? 80 : c >= 170 ? 170 : (c / 10) * 10;

    public static TSqlParser Create(int v, bool q) => v switch
    {
        80 => new TSql80Parser(q), 90 => new TSql90Parser(q), 100 => new TSql100Parser(q), 110 => new TSql110Parser(q),
        120 => new TSql120Parser(q), 130 => new TSql130Parser(q), 140 => new TSql140Parser(q), 150 => new TSql150Parser(q),
        160 => new TSql160Parser(q), _ => new TSql170Parser(q)
    };
    public static TSqlParser Rent(int v, bool q)
    {
        var bag = _pool.GetOrAdd((v, q), _ => new ConcurrentBag<TSqlParser>());
        return bag.TryTake(out var p) ? p : Create(v, q);
    }
    public static void Return(int v, bool q, TSqlParser p) => _pool.GetOrAdd((v, q), _ => new ConcurrentBag<TSqlParser>()).Add(p);

    public static TSqlFragment? ParseOnce(int v, bool q, string text, out IList<ParseError> errors)
    {
        var p = Rent(v, q);
        try { return p.Parse(new StringReader(text), out errors); }
        finally { Return(v, q, p); }
    }

    static readonly Regex LegacyRaiserror = new(@"\bRAISERROR\s+(\d+)\s+('(?:[^']|'')*'|@\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PhysLoc = new(@"%%(physloc|lockres)%%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Eski/bozuk sözdizimini parse edilebilir eşdeğerine çevirir (lineage anlamı değişmez).</summary>
    public static string Normalize(string text)
    {
        if (text.IndexOf("RAISERROR", StringComparison.OrdinalIgnoreCase) >= 0) text = LegacyRaiserror.Replace(text, "RAISERROR($2, 16, 1)");
        if (text.Contains("%%")) text = PhysLoc.Replace(text, "__$1__");
        if ((text.Contains("&gt;") || text.Contains("&lt;") || text.Contains("&amp;") || text.Contains("&quot;")) && !text.Contains('<') && !text.Contains('>'))
            text = System.Net.WebUtility.HtmlDecode(text);   // HTML-kaçışlı job adımı metni
        return text;
    }

    public static ParseResult Parse(string text, bool quoted, int serverMajor, int compat)
    {
        text = Normalize(text);
        var res = new ParseResult { Text = text };
        int pServer = FromServerMajor(serverMajor), pCompat = FromCompat(compat);
        var ladder = new List<int> { compat <= 80 ? 80 : pServer, 170, pCompat, 80 }.Distinct().ToList();
        TSqlFragment? best = null; IList<ParseError> bestErr = new List<ParseError>(); int bestV = ladder[0]; int bestCount = int.MaxValue;
        foreach (var v in ladder)
        {
            var f = ParseOnce(v, quoted, text, out var errs);
            if (errs.Count == 0) { res.Fragment = f; res.ParserUsed = v; return res; }
            if (errs.Count < bestCount) { bestCount = errs.Count; best = f; bestErr = errs; bestV = v; }
        }
        // Delik açma: hatalı ifade aralığını boşlukla değiştir (offset'ler sabit kalır), en fazla 5 kez.
        string cur = text;
        var holes = new List<string>();
        var errList = bestErr;
        for (int round = 0; round < 5 && errList.Count > 0; round++)
        {
            var e = errList[0];
            var (start, end) = HoleSpan(cur, e.Offset);
            if (end <= start) break;
            holes.Add($"L{e.Line}: {Snippet(cur.Substring(start, end - start))} ← {e.Message}");
            var sb = new StringBuilder(cur);
            for (int i = start; i < end; i++) if (sb[i] != '\n' && sb[i] != '\r') sb[i] = ' ';
            cur = sb.ToString();
            var f = ParseOnce(bestV, quoted, cur, out errList);
            if (errList.Count == 0)
            {
                res.Fragment = f; res.ParserUsed = bestV; res.Holes = holes; res.Status = "ParsedWithHoles"; res.Text = cur; res.Errors = bestErr;
                return res;
            }
        }
        res.Fragment = null; res.ParserUsed = bestV; res.Errors = bestErr; res.Holes = holes; res.Status = "Quarantined";
        return res;
    }

    static (int, int) HoleSpan(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length) offset = Math.Max(0, text.Length - 1);
        int start = text.LastIndexOf('\n', offset) + 1;
        int pos = text.IndexOf('\n', offset);
        int lines = 0;
        int end = text.Length;
        while (pos >= 0 && pos < text.Length && lines < 40)
        {
            int next = text.IndexOf('\n', pos + 1);
            string lineText = text.Substring(pos + 1, (next < 0 ? text.Length : next) - pos - 1);
            if (StmtStart.IsMatch(lineText)) { end = pos + 1; break; }
            pos = next; lines++;
        }
        if (pos < 0) end = text.Length;
        return (start, end);
    }
    public static string Snippet(string s, int max = 160)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

#endregion
