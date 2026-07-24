# MEDSoft.Observability

Pacote **OTel in-process compartilhado** de todo app .NET do MEDSoft —
[ADR-041](https://github.com/MEDGRUPOGIT/gitops-platform/blob/main/docs/adr/ADR-041-observabilidade-in-process-app-medsoft.md)
do `gitops-plataform`. Um ponto único: **traces + métricas + logs** via OTLP push
pro `alloy-receiver`; `service.name` = workload k8s; **sem sampler** (o tail do
coletor decide); `ILogger`→OTLP correlacionado (`trace_id`).

## Uso

```csharp
// Program.cs (API)
builder.Services.AddMEDSoftObservability(
    isWebApp: true,
    extraTracing: t => t
        .AddEntityFrameworkCoreInstrumentation()
        .AddNpgsql()
        .AddRedisInstrumentation(),
    extraMetrics: m => m.AddMeter("MyApp.Domain"),
    enableGenAI: false);

// Worker / Integration (headless)
services.AddMEDSoftObservability(isWebApp: false);
```

## Núcleo (todo app, zero config)

HttpClient · `MEDGRUPO.Messaging` + `MEDGRUPO.ServiceConnect` (MEDGRUPO.SDK) ·
runtime metrics (`dotnet_*`) · AspNetCore se `isWebApp` · `ILogger`→OTLP.

## App-específico (via `extraTracing` / `extraMetrics`)

Adicione o **pacote** de instrumentação + a **linha**:

| Precisa | Pacote | Linha |
|---|---|---|
| PostgreSQL | `Npgsql.OpenTelemetry` | `.AddNpgsql()` |
| EF Core | `OpenTelemetry.Instrumentation.EntityFrameworkCore` | `.AddEntityFrameworkCoreInstrumentation()` |
| Redis | `OpenTelemetry.Instrumentation.StackExchangeRedis` | `.AddRedisInstrumentation()` |
| Mongo (<3.7) | `MongoDB.Driver.Core.Extensions.DiagnosticSources` | `.AddSource(DiagnosticsActivityEventSubscriber.ActivitySourceName)` + `ClusterConfigurator` |
| AWS SDK | `OpenTelemetry.Instrumentation.AWS` **1.14.2** | `.AddAWSInstrumentation()` |
| RabbitMQ.Client 7 | (nativo) | `.AddSource("RabbitMQ.Client.Publisher").AddSource("RabbitMQ.Client.Subscriber")` |
| Spans custom | (nativo) | `.AddSource("MinhaApp.Dominio")` |

## GenAI (`enableGenAI: true`)

Registra `Microsoft.SemanticKernel*` (sources + meters → `gen_ai.*` /
`semantic_kernel_connectors_openai_tokens_*`). O app precisa **ligar o switch
experimental do SK** (`Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics`,
variante **não-sensível** — tokens/modelo/latência, sem prompt/response PII). Só
vale em imagem **rebuildada** após o switch. Dashboard/rule no gitops
(`gen-ai.json` / `service:genai_tokens_*`).

## Contrato (nos VALUES do app — obrigatório)

```yaml
podAnnotations:
  instrumentation.opentelemetry.io/inject-dotnet: "false"   # auto-inject + SDK = crash exit 139
  medgrupo.io/logs: "otlp"                                  # após validar OTLP
```
E o chart `medgrupo-prod-app ≥ 0.5.12` (injeta `OTEL_SERVICE_NAME`).

## Consumir (GitHub Packages)

`nuget.config` do app aponta pra `https://nuget.pkg.github.com/MEDGRUPOGIT/index.json`
(PAT com `read:packages`, env `GH_PACKAGES_TOKEN`). Ver
`docs/github-packages-nuget` no gitops-plataform.

---
Referências: `docs/instrumentacao-apps-lgtm.md`, ADR-024/031/040/041 do
`gitops-plataform`. Base: `ObservabilityConfiguration` do MEDPlanner/VideoStream.
