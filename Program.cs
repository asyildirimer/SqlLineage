// SqlLineage — giriş noktası. Kendi API/servisinizden çağırmak için LineagePipeline (src/11-Pipeline.cs) kullanın:
//   await LineagePipeline.PlanAsync(cfg, workDir);                 // DB listesi → plan.json
//   await LineagePipeline.RunTaskAsync(cfg, workDir, taskId, node); // tek görev (prepass / analyze / jobs)
//   await LineagePipeline.MergeAsync(cfg, workDir);                // parçaları birleştir → CSV/Excel/SQL
// Komut satırı: SqlLineage plan|run|run-all|merge|status|infa ...  (SqlLineage --help)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
return await App.RunAsync(args);
