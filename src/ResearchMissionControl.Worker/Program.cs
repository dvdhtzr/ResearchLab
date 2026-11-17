using Microsoft.Extensions.Hosting;

// Minimal host. The poll loop, lease heartbeat, reconciliation, and research
// execution are wired up in M2/M3 as hosted services. For now this exists so the
// solution builds and the host can start; it does no work yet.
var builder = Host.CreateApplicationBuilder(args);
using var host = builder.Build();
await host.RunAsync();
