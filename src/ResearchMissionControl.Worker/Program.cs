using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResearchMissionControl.Application;
using ResearchMissionControl.Infrastructure;
using ResearchMissionControl.Worker;

// The .NET Worker Service: polls the cloud API for claimed jobs and runs the governed
// research orchestrator (local Ollama + approved sources) for each one.
//
// Config via environment variables (defaults suit local dev):
//   RMC_API_BASE       cloud API base URL           (http://localhost:8787)
//   RMC_WORKER_ID      worker identity              (worker:alpha)
//   RMC_WORKER_SECRET  worker bearer credential     (dev-worker-secret)
//   RMC_MODEL          approved Ollama model        (qwen3.5:9b)
//   RMC_OLLAMA_BASE    Ollama base URL (loopback)   (http://localhost:11434)
//   RMC_WORKER_DB_DIR  per-job SQLite directory     (<temp>/rmc-worker)
//   RMC_POLL_SECONDS   idle poll interval           (15)

static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;

var apiBase = Env("RMC_API_BASE", "http://localhost:8787");
var workerId = Env("RMC_WORKER_ID", "worker:alpha");
var secret = Env("RMC_WORKER_SECRET", "dev-worker-secret");
var model = Env("RMC_MODEL", "qwen3.5:9b");
var ollamaBase = Env("RMC_OLLAMA_BASE", "http://localhost:11434");
var dbDir = Env("RMC_WORKER_DB_DIR", Path.Combine(Path.GetTempPath(), "rmc-worker"));
Directory.CreateDirectory(dbDir);
var pollSeconds = int.TryParse(Env("RMC_POLL_SECONDS", "15"), out var seconds) ? seconds : 15;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ICloudApiClient>(_ => new CloudApiClient(apiBase, workerId, secret));
builder.Services.AddSingleton<IResearchRunner>(_ => new ResearchRunner(dbDir, model, ollamaBase));
builder.Services.AddSingleton(sp => new WorkerLoop(
    sp.GetRequiredService<ICloudApiClient>(),
    sp.GetRequiredService<IResearchRunner>(),
    log: message => Console.WriteLine($"[worker] {message}")));
builder.Services.AddHostedService(sp =>
    new ResearchWorkerService(sp.GetRequiredService<WorkerLoop>(), TimeSpan.FromSeconds(pollSeconds)));

Console.WriteLine($"[worker] {workerId} polling {apiBase} every {pollSeconds}s; model {model} via {ollamaBase}");

using var host = builder.Build();
await host.RunAsync();
