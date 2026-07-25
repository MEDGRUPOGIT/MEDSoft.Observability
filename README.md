# MEDSoft.Observability

Pacote **OTel in-process compartilhado** de todo app .NET do MEDSoft —
[ADR-041](https://github.com/MEDGRUPOGIT/gitops-platform/blob/main/docs/adr/ADR-041-observabilidade-in-process-app-medsoft.md)
do `gitops-plataform`. Um ponto único: **traces + métricas + logs** via OTLP push
pro `alloy-receiver`; `service.name` = workload k8s; **sem sampler** (o tail do
coletor decide); `ILogger`→OTLP correlacionado (`trace_id`).

## Uso

```csharp
// Program.cs (API) — recursos (DB/cache/AWS/MQ) ja vem no nucleo desde a v0.2.0
builder.Services.AddMEDSoftObservability(isWebApp: true);

// Worker / Integration (headless)
services.AddMEDSoftObservability(isWebApp: false);

// So o que e do DOMINIO do app precisa de extraTracing/extraMetrics
builder.Services.AddMEDSoftObservability(
    isWebApp: true,
    extraTracing: t => t.AddSource("MinhaApp.Dominio"),
    extraMetrics: m => m.AddMeter("MinhaApp.Dominio"),
    enableGenAI: false);
```

## Núcleo (todo app, zero config)

HttpClient · `MEDGRUPO.Messaging` + `MEDGRUPO.ServiceConnect` (MEDGRUPO.SDK) ·
runtime metrics (`dotnet_*`) · AspNetCore se `isWebApp` · `ILogger`→OTLP.

**Desde a v0.2.0, também os recursos** — sem nenhuma linha a mais:

| Recurso | Como | Observação |
|---|---|---|
| PostgreSQL | `AddSource("Npgsql")` | sem dependência nova (o `AddNpgsql()` do `Npgsql.OpenTelemetry` é exatamente isso) |
| SQL Server | `AddSqlClientInstrumentation()` | texto do comando **não** é capturado (evita PII) |
| EF Core | `AddEntityFrameworkCoreInstrumentation()` | span lógico, complementa o do provider |
| MongoDB | `AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources")` | ⚠️ **exige wiring no app** — ver abaixo |
| Redis | `AddRedisInstrumentation()` | ⚠️ exige `IConnectionMultiplexer` no DI |
| AWS SDK (S3/SNS/SQS) | `AddAWSInstrumentation()` | o zero-code **não** cobre AWS |
| RabbitMQ.Client 7 | `AddSource("RabbitMQ.Client.Publisher"/"Subscriber")` | no-op em apps na 6.x |

### ⚠️ Os dois que exigem ação no app

**MongoDB** — registrar o source é metade do trabalho. Sem o `ClusterConfigurator`
no `MongoClientSettings`, nenhum span é produzido:

```csharp
// + PackageReference MongoDB.Driver.Core.Extensions.DiagnosticSources 3.0.0
settings.ClusterConfigurator = cb =>
    cb.Subscribe(new DiagnosticsActivityEventSubscriber());
```

**Redis** — `AddRedisInstrumentation()` se engancha via `IConnectionMultiplexer`
do DI. Se o app constrói o multiplexer na mão e não registra, vira **no-op
silencioso**.

## App-específico (via `extraTracing` / `extraMetrics`)

Sobrou só o que é do **domínio** do app:

| Precisa | Linha |
|---|---|
| Spans custom | `.AddSource("MinhaApp.Dominio")` |
| Meters custom | `.AddMeter("MinhaApp.Dominio")` |

> **Histórico (v0.1.0 → v0.2.0).** Os recursos eram opt-in aqui. Auditoria de
> 2026-07-25 mediu o resultado: **nenhuma das 10 workloads passava
> `extraTracing`** — todas deixaram `// DB via extraTracing = follow-up` no
> código — e a produção tinha **zero span** de Mongo, Postgres, SQL Server,
> Redis e S3. Opt-in que ninguém exerce é cobertura zero, então o default virou
> "instrumentado".
