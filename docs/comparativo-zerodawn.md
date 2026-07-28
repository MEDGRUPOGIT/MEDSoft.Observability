# MEDSoft.Observability × ZeroDawn — proposta de convergência de observabilidade

> **Para quem é este documento:** mantenedores do template
> [`medgrupo-sdk-zerodawn`](https://github.com/MEDGRUPOGIT/medgrupo-sdk-zerodawn) e do
> [`medgrupo-sdk`](https://github.com/MEDGRUPOGIT/medgrupo-sdk), e times que criam APIs a
> partir do template.
>
> **TL;DR:** o ZeroDawn padronizou a arquitetura das APIs — e isso funcionou. A proposta
> aqui é fazer o mesmo com a telemetria: a `OpenTelemetryConfiguration` do template vira
> uma delegação de 2 linhas para este pacote, **sem mudar o shape do template** (mesma
> classe, mesmo call-site, mesmo fluxo de dev local com Jaeger). O que muda é o que se
> ganha: telemetria **em produção**, logs correlacionados, instrumentação de recursos
> (PostgreSQL, Redis, SQL Server, AWS, RabbitMQ) e integração com a MEDGRUPO.SDK — tudo
> mantido num lugar só pela plataforma, em vez de copiado em cada repo.

---

## 1. O que o ZeroDawn acertou (e este pacote reaproveita)

O template resolveu um problema real: cada API nascia com uma arquitetura diferente. Hoje
as APIs novas nascem com o mesmo layout (`Configurations/`, CQRS, multi-target
net8/net10), e isso é exatamente o tipo de padronização que uma plataforma precisa.

Três decisões do template que este pacote **preserva de propósito**:

1. **O padrão `Configurations/`** — a proposta de adoção não remove a
   `OpenTelemetryConfiguration` nem o call-site no `Program.cs`; só o miolo da classe
   passa a delegar ao pacote. O diff num app real fica pequeno e o template continua
   reconhecível.
2. **O fluxo de dev local** (`infra/observability.yml` com Jaeger all-in-one) — o
   exporter OTLP do SDK, **sem nenhuma env configurada, aponta por default para
   `localhost:4317`**. Ou seja: o Jaeger do docker-compose do template continua
   recebendo traces com zero configuração. Esse fluxo não se perde.
3. **A instrumentação que já existia** — `AspNetCore`, `HttpClient`, `EFCore` e
   `Runtime` do template estão todas no pacote, nas mesmas famílias de instrumentação.

A configuração OTel do template foi um ponto de partida razoável para o alvo que existia
em abril/2026 (traces locais no Jaeger do dev). O que mudou desde então foi a plataforma
por baixo dela: hoje existe backend central (Tempo/Mimir/Loki), coletor gerenciado
(`alloy-receiver`), contrato de variáveis de ambiente injetadas pelo chart, tail sampling
e redação de segredos — e a configuração embutida no template não enxerga nada disso.

## 2. Comparativo lado a lado

Referências: `OpenTelemetryConfiguration.cs` do template (main, 28/04/2026) × este pacote
na v0.4.0 (núcleo `MEDSoft.Observability` + satélite `MEDSoft.Observability.AspNetCore`).

| Dimensão | ZeroDawn hoje | MEDSoft.Observability v0.4.0 | Referência |
|---|---|---|---|
| **Produção** | desligado (`if (!environment.IsProduction())`) | ligado em todos os ambientes; kill-switch sem redeploy via `OTEL_TRACES_SAMPLER=always_off` | ADR-041 |
| **Endpoint** | hardcoded `http://localhost:4317/` no código | 100% por env (`OTEL_EXPORTER_OTLP_ENDPOINT` e variantes por sinal); default do SDK em dev local continua `localhost:4317` | ADR-013 |
| **Sinais** | traces + métricas | traces + métricas + **logs** (`ILogger`→OTLP), com **resource único** nos 3 sinais (correlação log↔trace por `trace_id`) | ADR-031 |
| **Métricas saem do processo?** | não — `AddPrometheusExporter()` sem o middleware de scrape (o `/metrics` nunca é mapeado no `Program.cs`) | sim — OTLP push para o coletor | ADR-013 |
| **`service.name`** | `AddService(applicationName)` com o `Application.Name` do appsettings (string livre, ex. com espaços/parênteses) | `OTEL_SERVICE_NAME` injetada pelo chart (= nome do workload k8s), fallback pro nome do processo em dev | ADR-024 |
| **`service.version`** | — | `OTEL_SERVICE_VERSION` injetada pelo chart (= tag da imagem) | ADR-043 |
| **Instrumentação de recursos** | EFCore | EFCore + **Npgsql + SQL Server + Redis + AWS SDK + MongoDB (source) + RabbitMQ.Client 7** | — |
| **Integração MEDGRUPO.SDK** | — | sources/meters `MEDGRUPO.Messaging` e `MEDGRUPO.ServiceConnect` registrados — sem isso o traceparent que o Messaging propaga **morre na fila** (o `StartActivity` do consumer retorna null e o worker abre um trace novo) | — |
| **Worker (sem ASP.NET)** | template não cobre | núcleo é framework-neutro: worker roda em imagem base `runtime`, com traces+métricas+logs | issue #4 |
| **Ruído de health probe** | spans de `/health*` entram | filtrados no satélite (`filterHealthChecks`, default true), com `additionalFilter` combinável por AND | — |
| **Exceções no trace** | — | `RecordException = true` nos spans de request | — |
| **Console exporter** | ligado (traces e métricas no stdout) | não existe — em pod, stdout vira custo de log | — |
| **Linha OpenTelemetry** | 1.14.0 (+ `EntityFrameworkCore 1.15.0-beta.1` + `SharpAbp...Prometheus 3.5.5`) | 1.17.0 (3 CVEs conhecidas do exporter 1.14 corrigidas a partir da 1.15.3) | — |
| **Pacotes no csproj do app** | 8 diretos | 2 (`MEDSoft.Observability` + `.AspNetCore`; worker usa só 1) | — |
| **Manutenção** | copiada em cada repo gerado (drift natural) | 1 bump de versão por app; evolução centralizada | — |

Onde está provado: a v0.4.0 roda hoje em **10 repositórios / 23 workloads** do
`eks-medsoft-prod-0326` (APIs e workers), com validação ao vivo no backend — filtro de
health com prova positiva (requests chegam na métrica, zero span), workers em base
`runtime` exportando, e correlação de logs ativa.

## 3. Por que "os dois juntos" não funciona (o risco de coexistência)

Este é o ponto mais importante para quem tem a classe do template no repo: **as duas
configurações não convivem**. `AddOpenTelemetry()` devolve sempre o mesmo builder — as
configurações fazem **merge**, não substituição. Já vivemos esse caso em produção
(autorizacao-api, pré-v0.2.0):

- dois exporters de trace registrados, um deles apontando pro `localhost:4317` hardcoded
  → **fila de export enchendo e falhando em loop** dentro do pod;
- `AddService(applicationName)` do template **atropela** o `OTEL_SERVICE_NAME` que o
  chart injeta → o serviço aparece no backend com nome fora do padrão (quebra dashboards,
  recording rules e o vínculo com o workload);
- `AddPrometheusExporter()` sem scrape = métricas presas no processo, enquanto o caminho
  suportado pela plataforma é OTLP push (ADR-013).

E um detalhe sutil do guard `!IsProduction()`: a homol da frota roda
`ASPNETCORE_ENVIRONMENT=Staging`. Ou seja, **na homol o guard liga o OTel** — exportando
para um `localhost:4317` que não existe no pod. O guard não protege a homol; só cega a
produção, que é exatamente onde a telemetria paga a conta (o caso recente: um worker novo
consumindo RabbitMQ em produção sem nenhum sinal próprio — se a fila encalhar, ninguém
vê).

Por isso a proposta de adoção **substitui** o corpo da classe em vez de conviver com ele.

## 4. O que a plataforma já faz "por fora" (e que interage com o código do app)

Transparência sobre o ambiente onde esse código roda — nada disso é configurável pelo
app, e a configuração embutida do template hoje briga com alguns desses pontos:

| Mecanismo | O que faz | Interação com o código do app |
|---|---|---|
| Chart `medgrupo-prod-app` ≥ 0.5.13 | injeta `OTEL_SERVICE_NAME` (= workload) | `AddService(nome)` em código sobrepõe a env — por isso o pacote lê a env em vez de aceitar nome por parâmetro |
| Chart ≥ 0.5.15 | injeta `OTEL_SERVICE_VERSION` (= tag da imagem) | o pacote popula `service.version` só se a env existir (sem fallback de assembly, que mentiria `1.0.0.0`) |
| `alloy-receiver` (coletor) | tail sampling governado + redação de segredos + drop de telemetria de health/CI | o app **não precisa** (e não deve) configurar sampler; decisão de armazenamento é central |
| Operador OTel (auto-inject) | instrumentação zero-code para apps sem SDK | app com SDK in-process **não pode** ter a annotation `inject-dotnet` (os dois juntos = crash, exit 139); o contrato nos values é `inject-dotnet: "false"` |
| Beyla (eBPF, onde habilitado) | RED metrics para processos sem SDK | Beyla se **auto-exclui** de processo que exporta OTLP — adotar SDK muda a fonte das métricas; o pacote repõe com `http.server.*` semconv estável |

É também por isso que "cada app com sua config own-rolled" escala mal: cada uma dessas
regras precisaria ser conhecida e mantida em cada repo. No pacote, elas moram num lugar
só.

## 5. O que muda (e o que não muda) para o dev

**Não muda:**

- `docker compose -f infra/observability.yml up` + rodar a API = traces no Jaeger local,
  **sem configurar nada** (default do SDK = `localhost:4317`).
- O jeito de escrever spans/métricas de domínio: `ActivitySource`/`Meter` com o nome da
  aplicação continuam funcionando — entram via `extraTracing`/`extraMetrics`.
- O shape do template: `Configurations/OpenTelemetryConfiguration.cs` continua existindo;
  o `Program.cs` continua chamando `.ConfigureOpenTelemetry(...)`.

**Muda:**

- Saem 8 `PackageReference` OpenTelemetry.* do csproj; entram 2 (`MEDSoft.Observability`
  e, na API, `MEDSoft.Observability.AspNetCore`).
- Sai o `AddConsoleExporter()` (spans no stdout) e o `AddPrometheusExporter()` (que não
  expunha o `/metrics` — o middleware nunca foi mapeado). Quem quiser ver métrica local
  pode subir um otel-collector no compose (o `infra/prometheus.yml` do template já prevê
  um `otel-collector:8888`).
- Em cluster, a telemetria passa a existir **em produção**, com logs correlacionados.

## 6. E o lugar do pacote no `medgrupo-sdk`?

Avaliamos três caminhos. O detalhe importante: **a integração técnica já existe e já
funciona** — o pacote registra os sources/meters `MEDGRUPO.Messaging` e
`MEDGRUPO.ServiceConnect` por nome, sem dependência de compilação em nenhuma direção, e o
traceparent que o Messaging propaga (produtor → header → consumer) só vira trace contínuo
quando esses sources estão registrados. A pergunta real não é "como integrar", é "onde
cada coisa deve morar".

| Caminho | O que exige | Custo/risco |
|---|---|---|
| **(a) Mover os 2 projetos pro sln do sdk** (renomear `MEDGRUPO.SDK.Observability*`) | migrar o versionamento lockstep (`Directory.Build.props`) num repo onde cada csproj tem `<Version>` próprio; trocar o gate de publish (lá: push na main; aqui: tag `v*` com guard tag×Version); levar o CI de PR junto (o guard que impede o núcleo de referenciar AspNetCore — sem ele, worker em base `runtime` volta a quebrar no boot) | rename = PR de churn em **toda a frota já instrumentada** (PackageReference + usings); pacotes atuais órfãos nos feeds; irreversível |
| **(b) Manter o repo próprio; o sdk documenta e protege o contrato** | 1 teste no sdk afirmando os literais `"MEDGRUPO.Messaging"`/`"MEDGRUPO.ServiceConnect"` (hoje um rename dessas consts não quebra nenhum build — os spans só somem, em silêncio, na frota inteira); seção no README raiz; a seção "Observabilidade" do README do Messaging passa a apontar pro pacote em vez de ensinar o wiring manual | ≈ zero; opcional alinhar `OpenTelemetry.Api` 1.14.0 → 1.17.0 nos dois projetos do sdk |
| **(c) O template ZeroDawn adota o pacote** | delegar o corpo da `OpenTelemetryConfiguration` do template ao pacote (mesma mudança do PR de exemplo abaixo) + `nuget.config` do template no feed interno | pequeno; é o **multiplicador** — toda API nova nasce instrumentada de ponta a ponta |

**Recomendação: (b) + (c).** O caminho (a) fica em aberto como decisão de governança —
se fizer sentido o pacote viver sob o guarda-chuva do sdk no futuro, o passo honesto é
levar junto o `Directory.Build.props`, o CI de PR e um `CODEOWNERS`; mecanicamente é
tudo factível, só não compra nenhuma capacidade que (b) não dê, e custa o rename na
frota.

## 7. Proposta concreta (em passos pequenos)

1. **Prova num app real:** PR (draft) no `MEDSoft.Service.AutorizacaoConteudos`
   delegando a `OpenTelemetryConfiguration` existente ao pacote — API e Worker, ponta a
   ponta, com diff pequeno e call-site preservado. Serve de referência de "como fica" e
   pode ser descartado/ajustado à vontade — é proposta, não fato consumado.
   Abertos: [AutorizacaoConteudos#32](https://github.com/MEDGRUPOGIT/MEDSoft.Service.AutorizacaoConteudos/pull/32)
   (main) e [#33](https://github.com/MEDGRUPOGIT/MEDSoft.Service.AutorizacaoConteudos/pull/33)
   (réplica homol); chart com `OTEL_SERVICE_VERSION` em
   [gitops-catalog#165](https://github.com/MEDGRUPOGIT/gitops-catalog/pull/165).
2. **Template:** mesma delegação na `OpenTelemetryConfiguration` do ZeroDawn (o template
   ganha telemetria de produção sem perder o fluxo local de dev).
3. **medgrupo-sdk:** teste de contrato dos nomes de source/meter + ajuste das seções de
   README (caminho (b) acima).
4. **Roadmap conjunto do pacote:** o que o time do template precisar (novos recursos,
   filtros, opções) entra por issue/PR aqui — o repo é pequeno de propósito (2 arquivos
   de código) justamente para ser fácil de revisar e evoluir.

Qualquer ponto deste documento está aberto a ajuste — a intenção é somar o alcance do
template com a operação da plataforma, não substituir um pelo outro.

---

### Apêndice A — contrato de ambiente (o que o app recebe em cluster)

| Variável | Origem | Papel |
|---|---|---|
| `OTEL_SERVICE_NAME` | chart ≥ 0.5.13 (= nome do workload) | identidade do serviço nos 3 sinais |
| `OTEL_SERVICE_VERSION` | chart ≥ 0.5.15 (= tag da imagem) | `service.version` no resource |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | values do app (`alloy-receiver` do cluster) | destino OTLP; sem ela (dev local), default `localhost:4317` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | values do app (`grpc`) | protocolo; default net8+ já é gRPC |
| `OTEL_TRACES_SAMPLER=always_off` | operação (emergência) | kill-switch de traces sem redeploy |

Annotations nos values (contrato com o operador/coleta de logs):
`instrumentation.opentelemetry.io/inject-dotnet: "false"` e `medgrupo.io/logs: "otlp"`.

### Apêndice B — ADRs citados (repo `gitops-platform`)

- **ADR-013** — métricas por OTLP push (não Prometheus pull por scrape de app).
- **ADR-024** — "um sinal, um dono": `service.name` = workload, definido pela
  plataforma, não pelo código.
- **ADR-031** — correlação e alinhamento de versões OTel na frota.
- **ADR-041** — SDK in-process como padrão de instrumentação dos apps .NET.
- **ADR-043** — contrato de completude da telemetria (inclui `service.version`).
- **ADR-044** — feed NuGet interno S3/Sleet (consumo sem credencial na rede interna).
