// ============================================================================
#region 11. Boru hattı: plan.json (DB görev listesi) → görev (tek DB) → merge. Kendi servisinizden LineagePipeline'ı çağırın.
// ============================================================================

/// <summary>Çalışma klasöründeki plan.json: koşu kimliği, sunucular ve DB başına görevler (durumlarıyla).</summary>
public sealed class PlanFile
{
    public string RunId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string EngineVersion { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public List<PlanServer> Servers { get; set; } = new();
    public List<PlanTask> Tasks { get; set; } = new();
    public string? MergedAt { get; set; }
    public string? OutputDirectory { get; set; }
}
public sealed class PlanServer
{
    public string Name { get; set; } = "";         // lineage.json'daki bağlantı adı (görevler bununla eşleşir)
    public string ServerName { get; set; } = "";   // SERVERPROPERTY('ServerName')
    public string Version { get; set; } = "";
    public List<string> Databases { get; set; } = new();
    public int JobSteps { get; set; }
}
public sealed class PlanTask
{
    public string Id { get; set; } = "";           // "<kind>:<server>:<database>"
    public string Kind { get; set; } = "";         // prepass | analyze
    public int Phase { get; set; }                 // 1 = prepass, 2 = analyze (tüm 1'ler bitince)
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";     // "(jobs)": planlanan DB'lerin dışındaki job adımları
    public int Modules { get; set; }
    public long DefinitionBytes { get; set; }
    public string Status { get; set; } = "pending"; // pending | running | done | failed
    public string? Node { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public long? ElapsedMs { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public string? Part { get; set; }              // parça klasörü (work'e göreli)
    public const string JobsDb = "(jobs)";
    public static string MakeId(string kind, string server, string db) => $"{kind}:{server}:{db}";
}

/// <summary>plan.json okuma/yazma; paylaşılan klasörde çalışan düğümler için kilit dosyalı atomik güncelleme.</summary>
static class PlanStore
{
    public static string PlanPath(string workDir) => Path.Combine(workDir, "plan.json");
    public static string PartsDir(string workDir) => Path.Combine(workDir, "parts");
    public static string PrepassDir(string workDir) => Path.Combine(workDir, "prepass");
    static string LockPath(string workDir) => Path.Combine(workDir, "plan.lock");

    public static PlanFile Load(string workDir)
    {
        var p = PlanPath(workDir);
        if (!File.Exists(p)) throw new FileNotFoundException("plan.json yok; önce `plan` çalıştırın: " + p);
        return JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(p), Cli.JsonOpts) ?? throw new InvalidDataException("plan.json okunamadı");
    }
    public static void Save(PlanFile plan, string workDir)
    {
        Directory.CreateDirectory(workDir);
        var path = PlanPath(workDir);
        var json = JsonSerializer.Serialize(plan, Cli.JsonOpts);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        // Windows: hedef dosya o an başka bir işlemce (Defender, indeksleyici, okuyan düğüm) açıksa taşıma
        // "Access denied" / paylaşım ihlali verir; geçicidir → yeniden dene, en sonda doğrudan yaz.
        Exception? last = null;
        for (int i = 0; i < 30; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; Thread.Sleep(100 + i * 50); }
        }
        try { File.WriteAllText(path, json, new UTF8Encoding(false)); try { File.Delete(tmp); } catch { } Log.Warn("plan.json taşınamadı, doğrudan yazıldı: " + last?.Message); }
        catch (Exception ex) { throw new IOException($"plan.json yazılamadı ({path}): {ex.Message}; geçici kopya: {tmp}", ex); }
    }
    /// <summary>Kilit altında oku-değiştir-yaz. Kilit 60 s içinde alınamazsa (eski kilit) kırılır.</summary>
    public static T Update<T>(string workDir, Func<PlanFile, T> change)
    {
        Directory.CreateDirectory(workDir);
        var lockPath = LockPath(workDir);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var fs = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                try
                {
                    var plan = Load(workDir);
                    var r = change(plan);
                    Save(plan, workDir);
                    return r;
                }
                finally { fs.Dispose(); try { File.Delete(lockPath); } catch { } }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(lockPath) && sw.Elapsed.TotalSeconds > 30) throw;
                if (sw.Elapsed.TotalSeconds > 60 && (DateTime.UtcNow - File.GetCreationTimeUtc(lockPath)).TotalSeconds > 60) { Log.Warn("plan.lock eski; kırılıyor"); try { File.Delete(lockPath); } catch { } }
                Thread.Sleep(Random.Shared.Next(50, 250));
            }
        }
    }
    public static string PartDirName(PlanTask t) => Sanitize(t.Server) + "__" + Sanitize(t.Database);
    public static string Sanitize(string s) { var sb = new StringBuilder(); foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'); return sb.ToString(); }
}

/// <summary>Programatik API: Plan → RunTask (düğümlerde, paralel) → Merge. Komut satırı da bunu çağırır.</summary>
public static class LineagePipeline
{
    // ---------------------------------------------------------------- PLAN
    /// <summary>Sunuculara bağlanır, DB listesini çıkarır, plan.json yazar. Mevcut plan varsa force=false ile hata verir.</summary>
    public static async Task<PlanFile> PlanAsync(LineageConfig cfg, string workDir, bool force = false)
    {
        var planPath = PlanStore.PlanPath(workDir);
        if (File.Exists(planPath) && !force) throw new InvalidOperationException($"Mevcut plan var: {planPath}. Devam etmek için `run --next`/`merge`, yeni koşu için `plan --force` ya da başka --work klasörü.");
        var sw = Stopwatch.StartNew();
        var cat = await CatalogLoader.LoadServersAsync(cfg);
        if (cat.Servers.Count == 0) throw new InvalidOperationException("Hiçbir sunucuya bağlanılamadı / dosya yok.");
        var plan = new PlanFile { RunId = NewRunId(), CreatedAt = DateTime.Now, EngineVersion = App.EngineVersion, CreatedBy = Environment.MachineName, OutputDirectory = cfg.Output.Directory };
        foreach (var s in cat.Servers)
        {
            var ps = new PlanServer { Name = s.ConfigName, ServerName = s.Name, Version = s.Version, Databases = s.DbNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), JobSteps = s.JobSteps.Count };
            plan.Servers.Add(ps);
            foreach (var dbName in ps.Databases)
            {
                var (mods, bytes) = CatalogLoader.MeasureDb(s, dbName);
                foreach (var kind in new[] { "prepass", "analyze" })
                    plan.Tasks.Add(new PlanTask { Id = PlanTask.MakeId(kind, s.ConfigName, dbName), Kind = kind, Phase = kind == "prepass" ? 1 : 2, Server = s.ConfigName, Database = dbName, Modules = mods, DefinitionBytes = bytes });
            }
            int orphanJobs = s.JobSteps.Count(j => NameComparer.Eq(j.Subsystem, "TSQL") && !s.Knows(j.DatabaseName));
            if (orphanJobs > 0)
                foreach (var kind in new[] { "prepass", "analyze" })
                    plan.Tasks.Add(new PlanTask { Id = PlanTask.MakeId(kind, s.ConfigName, PlanTask.JobsDb), Kind = kind, Phase = kind == "prepass" ? 1 : 2, Server = s.ConfigName, Database = PlanTask.JobsDb, Modules = orphanJobs });
        }
        // büyükten küçüğe: uzun görevler önce dağıtılsın
        plan.Tasks = plan.Tasks.OrderBy(t => t.Phase).ThenByDescending(t => t.DefinitionBytes).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(PlanStore.PartsDir(workDir)); Directory.CreateDirectory(PlanStore.PrepassDir(workDir));
        PlanStore.Save(plan, workDir);
        Log.Info($"Plan: {planPath} — RunId {plan.RunId}, {plan.Servers.Count} sunucu, {plan.Tasks.Count(t => t.Kind == "analyze")} DB görevi (+ön geçiş), toplam {plan.Tasks.Sum(t => t.Kind == "analyze" ? t.Modules : 0):N0} modül, {sw.ElapsedMilliseconds} ms");
        return plan;
    }

    public static string NewRunId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    // ---------------------------------------------------------------- GÖREV
    /// <summary>Sıradaki çalıştırılabilir görevi kilit altında "running" yapar ve döner; yoksa null. Faz 2 görevleri tüm faz 1 görevleri bitince açılır.</summary>
    public static PlanTask? ClaimNext(string workDir, string node)
    {
        return PlanStore.Update(workDir, plan =>
        {
            var t = NextRunnable(plan);
            if (t == null) return null;
            t.Status = "running"; t.Node = node; t.StartedAt = DateTime.Now; t.Attempts++; t.Error = null;
            return t;
        });
    }
    public static PlanTask? NextRunnable(PlanFile plan)
    {
        bool phase1Done = plan.Tasks.Where(t => t.Phase == 1).All(t => t.Status is "done" or "failed");
        return plan.Tasks.FirstOrDefault(t => t.Status == "pending" && (t.Phase == 1 || phase1Done));
    }

    /// <summary>Tek görevi çalıştırır (plan.json'daki id ile). updatePlan=false: durum kendi kuyruğunuzda tutuluyorsa.</summary>
    public static async Task<PlanTask> RunTaskAsync(LineageConfig cfg, string workDir, string taskId, string node = "", bool updatePlan = true)
    {
        var plan = PlanStore.Load(workDir);
        var task = plan.Tasks.FirstOrDefault(t => t.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Görev yok: " + taskId);
        if (updatePlan) PlanStore.Update(workDir, p => { var t = p.Tasks.First(x => x.Id == task.Id); t.Status = "running"; t.Node = node; t.StartedAt = DateTime.Now; t.Attempts++; t.Error = null; return 0; });
        return await ExecuteAsync(cfg, workDir, plan, task, node, updatePlan);
    }

    /// <summary>Kilitle sıradaki görevi al ve çalıştır; loop=true ise görev kalmayana kadar sürer. Dönüş: çalıştırılan görev sayısı.</summary>
    public static async Task<int> RunPendingAsync(LineageConfig cfg, string workDir, string node, bool loop = true)
    {
        int n = 0;
        while (true)
        {
            var task = ClaimNext(workDir, node);
            if (task == null) break;
            var plan = PlanStore.Load(workDir);
            await ExecuteAsync(cfg, workDir, plan, task, node, updatePlan: true);
            n++;
            if (!loop) break;
        }
        var left = PlanStore.Load(workDir).Tasks.Count(t => t.Status == "pending");
        Log.Info($"{n} görev çalıştırıldı; bekleyen {left}");
        return n;
    }

    static async Task<PlanTask> ExecuteAsync(LineageConfig cfg, string workDir, PlanFile plan, PlanTask task, string node, bool updatePlan)
    {
        var sw = Stopwatch.StartNew();
        string runId = plan.RunId;
        Log.Info($"Görev {task.Id} başladı (düğüm {node})");
        try
        {
            ModuleAnalyzer.ResetCaches();
            var cat = await CatalogLoader.LoadServersAsync(cfg);
            var srv = cat.Servers.FirstOrDefault(s => NameComparer.Eq(s.ConfigName, task.Server)) ?? throw new InvalidOperationException($"Sunucu bağlanamadı / config'de yok: {task.Server}");
            bool jobsTask = task.Database == PlanTask.JobsDb;
            DbCatalog db;
            List<ObjInfo> modules;
            if (jobsTask)
            {
                db = new DbCatalog { Server = srv.Name, Name = PlanTask.JobsDb, Compat = 150, DepsLoaded = true };
                var stubs = new Dictionary<string, DbCatalog>(NameComparer.Instance);
                modules = new List<ObjInfo>();
                foreach (var js in srv.JobSteps.Where(j => NameComparer.Eq(j.Subsystem, "TSQL") && !srv.Knows(j.DatabaseName)))
                {
                    if (!stubs.TryGetValue(js.DatabaseName, out var sdb)) stubs[js.DatabaseName] = sdb = new DbCatalog { Server = srv.Name, Name = js.DatabaseName, Compat = 150, DepsLoaded = true };
                    modules.Add(new ObjInfo { TypeCode = "JOB", Ref = new ObjRef(srv.Name, js.DatabaseName, "job", $"{js.JobName}#{js.StepId}", ObjType.JobStep), Definition = js.Command, DefaultSchema = "dbo", Db = sdb });
                }
            }
            else
            {
                db = CatalogLoader.EnsureTargetDb(cfg, cat, srv, task.Database) ?? throw new InvalidOperationException($"DB yüklenemedi: {task.Server}.{task.Database}");
                modules = new DbTaskRunner(cfg, runId, cat, srv, db).CollectModules(srv.JobSteps.Where(j => NameComparer.Eq(j.DatabaseName, db.Name)));
            }
            var runner = new DbTaskRunner(cfg, runId, cat, srv, db);
            string part = PlanStore.PartDirName(task);
            if (task.Kind == "prepass")
            {
                var res = runner.RunPrepass(modules);
                var path = Path.Combine(PlanStore.PrepassDir(workDir), part + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(res, Cli.JsonOpts), new UTF8Encoding(false));
                task.Part = Path.GetRelativePath(workDir, path);
            }
            else
            {
                // tüm ön geçiş çıktıları (çağıran literal argümanları + overlay tablolar)
                var pre = new List<PrepassResult>();
                foreach (var f in Directory.EnumerateFiles(PlanStore.PrepassDir(workDir), "*.json"))
                    try { var pr = JsonSerializer.Deserialize<PrepassResult>(File.ReadAllText(f), Cli.JsonOpts); if (pr != null) pre.Add(pr); } catch (Exception ex) { Log.Warn($"ön geçiş dosyası okunamadı {f}: {ex.Message}"); }
                int expected = plan.Tasks.Count(t => t.Kind == "prepass");
                if (pre.Count < expected) Log.Warn($"Ön geçiş eksik: {pre.Count}/{expected} dosya (dinamik SQL delikleri eksik kalabilir)");
                DbTaskRunner.LoadPrepass(cat, pre);
                cat.ApplyPendingOverlays(db);

                string partDir = Path.Combine(PlanStore.PartsDir(workDir), part);
                var metaPath = Path.Combine(partDir, "meta.json");
                if (File.Exists(metaPath)) File.Delete(metaPath);   // yarım/eski parça işaretini kaldır
                string signature = jobsTask ? App.EngineVersion : Incremental.Signature(cfg, db);
                TaskMeta meta;
                using (var parts = new PartWriter(partDir, cfg.Output.CsvSeparator, cfg.CompressParts))
                    meta = runner.RunAnalyze(modules, parts, signature);
                meta.TaskId = task.Id; meta.Kind = task.Kind; meta.Node = node; meta.StartedAt = task.StartedAt ?? DateTime.Now.AddMilliseconds(-sw.ElapsedMilliseconds); meta.FinishedAt = DateTime.Now;
                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, Cli.JsonOpts), new UTF8Encoding(false));
                task.Part = Path.GetRelativePath(workDir, partDir);
            }
            task.Status = "done"; task.Error = null;
        }
        catch (Exception ex)
        {
            task.Status = "failed"; task.Error = ex.GetType().Name + ": " + ex.Message;
            Log.Error($"Görev {task.Id} başarısız: {ex.GetType().Name}: {ex.Message}" + (ex.InnerException != null ? $" ← {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : ""));
            Log.Debug(ex.ToString());
        }
        task.FinishedAt = DateTime.Now; task.ElapsedMs = sw.ElapsedMilliseconds; task.Node = node;
        if (updatePlan)
        {
            try
            {
                PlanStore.Update(workDir, p =>
                {
                    var t = p.Tasks.First(x => x.Id == task.Id);
                    t.Status = task.Status; t.Error = task.Error; t.FinishedAt = task.FinishedAt; t.ElapsedMs = task.ElapsedMs; t.Part = task.Part; t.Node = node;
                    return 0;
                });
            }
            catch (Exception ex) { Log.Error($"plan.json güncellenemedi (görev {task.Id} {task.Status}; parça diskte): {ex.Message}"); }
        }
        Log.Info($"Görev {task.Id}: {task.Status.ToUpperInvariant()}, {sw.Elapsed.TotalSeconds:F0} s");
        // katalog/önbellekler bir sonraki görev için serbest
        ModuleAnalyzer.ResetCaches();
        GC.Collect(); GC.WaitForPendingFinalizers();
        return task;
    }

    // ---------------------------------------------------------------- MERGE
    /// <summary>Tüm parçaları birleştirir: CSV/Excel/DDL/SQL yükleme + prosedürler arası yayılım + kalıcı→kalıcı indirgeme + özetler.</summary>
    public static Task MergeAsync(LineageConfig cfg, string workDir)
    {
        var plan = PlanStore.Load(workDir);
        new Merger(cfg, workDir, plan).Run();
        PlanStore.Update(workDir, p => { p.MergedAt = DateTime.Now.ToString("s"); return 0; });
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- HEPSİ (tek makine)
    /// <summary>Plan (yeni) → tüm görevler sırayla (DB DB) → merge. resume=true: mevcut planın bekleyen görevlerini bitirip merge eder.</summary>
    public static async Task RunAllAsync(LineageConfig cfg, string workDir, bool resume = false)
    {
        var sw = Stopwatch.StartNew();
        if (!resume || !File.Exists(PlanStore.PlanPath(workDir))) await PlanAsync(cfg, workDir, force: true);
        await RunPendingAsync(cfg, workDir, Environment.MachineName, loop: true);
        var plan = PlanStore.Load(workDir);
        var failed = plan.Tasks.Where(t => t.Status == "failed").ToList();
        if (failed.Count > 0) Log.Warn($"{failed.Count} görev başarısız: {string.Join(", ", failed.Take(10).Select(t => t.Id))}{(failed.Count > 10 ? " …" : "")} — merge eldeki parçalarla yapılıyor");
        await MergeAsync(cfg, workDir);
        Log.Info($"Bitti: {sw.Elapsed.TotalSeconds:F0} s");
    }

    public static string Status(string workDir, bool json)
    {
        var plan = PlanStore.Load(workDir);
        if (json) return JsonSerializer.Serialize(plan, Cli.JsonOpts);
        var sb = new StringBuilder();
        sb.AppendLine($"RunId {plan.RunId}  oluşturuldu {plan.CreatedAt:yyyy-MM-dd HH:mm}  motor {plan.EngineVersion}  merge: {plan.MergedAt ?? "-"}");
        foreach (var g in plan.Tasks.GroupBy(t => t.Phase).OrderBy(g => g.Key))
            sb.AppendLine($"  faz {g.Key} ({g.First().Kind}): {g.Count(t => t.Status == "done")} bitti, {g.Count(t => t.Status == "running")} çalışıyor, {g.Count(t => t.Status == "failed")} başarısız, {g.Count(t => t.Status == "pending")} bekliyor");
        foreach (var t in plan.Tasks.Where(t => t.Status is "running" or "failed"))
            sb.AppendLine($"  {t.Status,-8} {t.Id}  {t.Node}  {(t.Error != null ? "— " + t.Error : "")}");
        return sb.ToString();
    }
}

#endregion
