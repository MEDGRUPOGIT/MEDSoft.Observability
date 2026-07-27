# MEDSoft.Observability

**OTel in-process compartilhado** de todo app .NET do MEDSoft —
[ADR-041](https://github.com/MEDGRUPOGIT/gitops-platform/blob/main/docs/adr/ADR-041-observabilidade-in-process-app-medsoft.md)
do `gitops-plataform`. Um ponto único: **traces + métricas + logs** via OTLP push
pro `alloy-receiver`; `service.name` = workload k8s (+ `service.version` desde a
v0.3.0); **sem sampler** (o tail do coletor decide); `ILogger`→OTLP correlacionado
(`trace_id`).

Desde a **v0.4.0** são **DOIS pacotes em lockstep** (mesma versão, sempre —
[issue #4](https://github.com/MEDGRUPOGIT/MEDSoft.Observability/issues/4)):

| Pacote | Quem usa | Imagem base |
|---|---|---|
| `MEDSoft.Observability` (núcleo) | **todo** app — framework-neutro | worker roda em **`runtime`** |
| `MEDSoft.Observability.AspNetCore` (satélite) | **só apps web** (server spans + `http.server.*` + filtro de health) | exige **`aspnet`** (FrameworkReference) |

> Era o núcleo que carregava `OpenTelemetry.Instrumentation.AspNetCore` (e com
> ela o shared framework ASP.NET) — forçando **todo worker** à base `aspnet`.
> O CI tem guard que impede o núcleo de voltar a citar aspnetcore.

## Uso

```csharp
// ── App web (API) ── os DOIS pacotes, nessa ordem:
builder.Services.AddMEDSoftObservability();            // núcleo: pipeline + recursos
builder.Services.AddMEDSoftAspNetCoreObservability();  // satélite: AspNetCore (health filtrado por default)

// ── Worker / Integration (headless) ── SÓ o núcleo; Dockerfile em base `runtime`:
services.AddMEDSoftObservability();

// Só o que é do DOMÍNIO do app precisa de extraTracing/extraMetrics (núcleo):
builder.Services.AddMEDSoftObservability(
    extraTracing: t => t.AddSource("MinhaApp.Dominio"),
    extraMetrics: m => m.AddMeter("MinhaApp.Dominio"),
    enableGenAI: false);

// App web com filtro próprio de trace: additionalFilter COMBINA com o de
// health (options.Filter é atribuição — setar direto apagaria o do pacote):
builder.Services.AddMEDSoftAspNetCoreObservability(
    additionalFilter: ctx => !ctx.WebSockets.IsWebSocketRequest);
```

### Migração v0.3.0 → v0.4.0 (breaking)

| Era (v0.3.0) | Fica (v0.4.0) |
|---|---|
| `AddMEDSoftObservability(isWebApp: true)` | `AddMEDSoftObservability()` + **add pacote satélite** + `AddMEDSoftAspNetCoreObservability()` |
| `AddMEDSoftObservability(isWebApp: false)` | `AddMEDSoftObservability()` (só recompilar) — e o Dockerfile do worker **volta pra base `runtime`** |
| `filterHealthChecks:` no núcleo | parâmetro do satélite |
| `Configure<AspNetCoreTraceInstrumentationOptions>` p/ somar filtro | `additionalFilter:` do satélite (o pacote combina os predicados) |

A remoção do `isWebApp` é **barulhenta de propósito**: chamada antiga não
compila — nada de telemetria sumindo em silêncio por um bool esquecido. E o
satélite **exige** o núcleo registrado antes (senão lança no boot; satélite
sozinho seria span descartado em silêncio).

## Pré-requisitos (leia antes de adotar)

- **Imagem base**: worker com só o núcleo → **`runtime`**. App web (satélite) →
  `aspnet` (o satélite carrega `FrameworkReference` — em `runtime` o app quebra
  no boot, por design: falha alta, não silêncio).
- **AWSSDK v4**: `OpenTelemetry.Instrumentation.AWS` (núcleo) exige AWSSDK.Core
  4.x transitivo. App ainda em AWSSDK **v3** precisa migrar antes (major com
  breaking changes) — senão o restore força o upgrade sem ninguém pedir.
- **MongoDB.Driver < 3.7**: o caminho `DiagnosticSources` vale para os drivers
  atuais da frota (3.5.x). Driver ≥ 3.7 tem caminho próprio de instrumentação —
  reavaliar quando algum app subir.
- **RabbitMQ.Client ≥ 7** para spans nativos de MQ (a 6.x não tem
  ActivitySource — só os spans da `MEDGRUPO.Messaging`).

## Núcleo (todo app, zero config)

HttpClient · `MEDGRUPO.Messaging` + `MEDGRUPO.ServiceConnect` (MEDGRUPO.SDK) ·
runtime metrics (`dotnet_*`) · `ILogger`→OTLP.

**Desde a v0.2.0, também os recursos** — sem nenhuma linha a mais:

| Recurso | Como | Observação |
|---|---|---|
| PostgreSQL | `AddSource("Npgsql")` | sem dependência nova; Npgsql ≤9 e 10 emitem **dialetos diferentes** de métrica — as rules da plataforma toleram os dois (gitops#776) |
| SQL Server | `AddSqlClientInstrumentation()` | texto do comando **não** é capturado (evita PII) |
| EF Core | `AddEntityFrameworkCoreInstrumentation()` | span lógico, complementa o do provider |
| MongoDB | `AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources")` | ⚠️ **exige wiring no app** — ver abaixo |
| Redis | `AddRedisInstrumentation()` | ⚠️ exige `IConnectionMultiplexer` no DI |
| AWS SDK (S3/SNS/SQS) | `AddAWSInstrumentation()` | o zero-code **não** cobre AWS; exige AWSSDK v4 |
| RabbitMQ.Client 7 | `AddSource("RabbitMQ.Client.Publisher"/"Subscriber")` | no-op em apps na 6.x |

### ⚠️ Os três que falham em silêncio

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

**RabbitMQ < 7** — os sources `RabbitMQ.Client.*` não existem na 6.x; registrar
é no-op sem erro (caso real: legacyintegrationhub em 6.8.0).

## Satélite (só apps web)

`AddMEDSoftAspNetCoreObservability(filterHealthChecks = true, additionalFilter = null)`:

- **Server spans** com `RecordException = true` + **métricas** `http.server.*` —
  no MESMO pipeline do núcleo (resource/exporter únicos).
- **`filterHealthChecks`** (default on): probes (`/health*`, `/healthz`,
  `/ready`, `/live`, `/alive`) não viram span — filtrados NA ORIGEM (poupa
  wire/CPU; o drop_ci do coletor seria tarde). ⚠️ Só **traces**: a métrica
  `http.server.request.duration` continua contando probes (a instrumentação não
  tem filtro de métrica; igual v0.3.0).
- **`additionalFilter`**: predicado extra do app (true = coleta), combinado por
  AND com o de health. Use SEMPRE isto em vez de
  `Configure<AspNetCoreTraceInstrumentationOptions>` — `Filter` é atribuição e
  setar direto **apaga** o filtro de health do pacote.
- Chame **uma** vez, **depois** do núcleo.

## Configuração por ambiente (envs — o app não configura nada em código)

| Env | Quem injeta | Efeito |
|---|---|---|
| `OTEL_SERVICE_NAME` | chart `medgrupo-prod-app ≥ 0.5.12` | `service.name` = workload k8s |
| `OTEL_SERVICE_VERSION` | chart `≥ 0.5.15` (tag da imagem) | `service.version` ("o deploy piorou?") |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | chart/values | destino OTLP (alloy-receiver) |
| `OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT` | opcional | destino por sinal (respeitado desde a v0.3.0) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | opcional | `grpc` (default no net8+) ou `http/protobuf` (respeitado desde a v0.3.0) |
| `OTEL_TRACES_SAMPLER=always_off` | emergência | kill-switch de traces sem redeploy |

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

## Consumir (feeds)

- **Feed S3 interno** (ADR-044, sem credencial, rede interna/VPN) ou **GitHub
  Packages** (`https://nuget.pkg.github.com/MEDGRUPOGIT/index.json`, PAT
  `read:packages` na env `GH_PACKAGES_TOKEN`) — conforme o nuget.config do app.
- O `packageSourceMapping` com `<package pattern="MEDSoft.*"/>` **já cobre o
  satélite** — adicionar o pacote novo não pede mudança de nuget.config.
- Deploy workflow precisa de `NUGET_TOKEN: ${{ secrets.NUGET_READ_TOKEN }}`
  quando o caminho de restore usa GitHub Packages.

## Histórico

> **v0.1.0 → v0.2.0.** Os recursos eram opt-in. Auditoria de 2026-07-25 mediu o
> resultado: **nenhuma das 10 workloads passava `extraTracing`** — e a produção
> tinha **zero span** de Mongo, Postgres, SQL Server, Redis e S3. Opt-in que
> ninguém exerce é cobertura zero, então o default virou "instrumentado".
>
> **v0.2.0 → v0.3.0** (auditoria OTel 2026-07-26, validada ao vivo no LGTM):
> linha OpenTelemetry **1.17.0** (3 CVEs do exporter 1.14.0 + grafo misto — o
> runtime real já rodava 1.15/1.16 por cima do pin); **resource único** com
> `service.version`; **envs OTLP respeitadas** (protocolo e endpoints por
> sinal); **filtro default de health checks**; guard de publish (tag deve casar
> com `<Version>`).
>
> **v0.3.0 → v0.4.0** (issue #4, BREAKING): **split** — núcleo framework-neutro
> (workers voltam pra base **`runtime`**; a FrameworkReference ASP.NET saiu) +
> satélite `MEDSoft.Observability.AspNetCore` (`AddMEDSoftAspNetCoreObservability`
> com `filterHealthChecks` e `additionalFilter`). `isWebApp` aposentado
> (barulhento de propósito). Lockstep de versão via `Directory.Build.props` +
> guards novos no CI (anti-divergência de Version; núcleo nunca cita aspnetcore).

---
Referências: `docs/instrumentacao-apps-lgtm.md`, ADR-024/031/040/041/**043** do
`gitops-plataform`. Base: `ObservabilityConfiguration` do MEDPlanner/VideoStream.
