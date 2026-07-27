using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MEDSoft.Observability;

/// <summary>
/// Marcador interno: prova que o pipeline do núcleo (resource + exporters) foi
/// registrado. O satélite <c>MEDSoft.Observability.AspNetCore</c> checa isto
/// para falhar ALTO no boot em vez de exportar nada em silêncio (spans num
/// pipeline sem exporter são gravados e descartados sem erro nenhum). Não use.
/// </summary>
public sealed class MEDSoftObservabilityMarker { }

/// <summary>
/// Configuração OTel in-process compartilhada de TODO app .NET do MEDSoft
/// (ADR-041 do gitops-plataform). Um ponto único: traces + métricas + logs via
/// OTLP push pro alloy-receiver; <c>service.name</c> = workload k8s; <b>sem
/// sampler</b> (o tail_sampling do coletor é o único decisor de storage);
/// <c>ILogger</c>→OTLP com o mesmo resource (correlação Log↔Trace).
///
/// Núcleo (todo app, sem config): HttpClient, sources/meters
/// <c>MEDGRUPO.Messaging</c>/<c>MEDGRUPO.ServiceConnect</c> (da MEDGRUPO.SDK),
/// runtime metrics (<c>dotnet_*</c>), logging — e, desde a v0.2.0, os RECURSOS:
/// PostgreSQL (Npgsql), SQL Server, EF Core, MongoDB, Redis, AWS SDK e
/// RabbitMQ.Client 7.
///
/// <para><b>v0.4.0 (issue #4) — núcleo FRAMEWORK-NEUTRO.</b> A instrumentação
/// ASP.NET Core saiu para o pacote satélite
/// <c>MEDSoft.Observability.AspNetCore</c> (e com ela o
/// <c>FrameworkReference Microsoft.AspNetCore.App</c> que forçava TODO worker
/// à imagem base <c>aspnet</c>). Worker referencia SÓ este núcleo e roda em
/// base <c>runtime</c>; app web referencia os dois e chama
/// <c>AddMEDSoftAspNetCoreObservability()</c> depois deste método — os dois
/// alimentam o MESMO pipeline OTel. O parâmetro <c>isWebApp</c> se aposentou:
/// remoção deliberadamente BARULHENTA (chamada antiga não compila), nada de
/// telemetria sumindo em silêncio por um bool esquecido.</para>
///
/// <para><b>Por que os recursos vieram pro núcleo (v0.2.0).</b> Na v0.1.0 eram
/// opt-in via <paramref name="extraTracing"/>. Auditoria de 2026-07-25:
/// <b>NENHUMA das 10 workloads passava <c>extraTracing</c></b> — todas chamavam só
/// <c>AddMEDSoftObservability(isWebApp:…)</c>, deixando um comentário
/// "// DB via extraTracing = follow-up". Resultado medido: ZERO span de Mongo,
/// Postgres, SQL Server, Redis ou S3 em produção. Opt-in que ninguém exerce é
/// cobertura zero — o default passa a ser "instrumentado".</para>
///
/// <para><b>v0.3.0 (auditoria OTel 2026-07-26).</b> Linha OpenTelemetry 1.17.0
/// (fecha as 3 CVEs do exporter 1.14.0 e o grafo misto — o runtime real já
/// resolvia 1.15/1.16 por cima do pin); resource ÚNICO para os três sinais,
/// agora com <c>service.version</c> (env <c>OTEL_SERVICE_VERSION</c>); as envs
/// OTLP padrão passam a ser respeitadas (<c>OTEL_EXPORTER_OTLP_PROTOCOL</c> e
/// endpoints por sinal — antes eram neutralizadas por override no código).</para>
///
/// <para>Segue app-específico via <paramref name="extraTracing"/>/<paramref name="extraMetrics"/>:
/// <c>ActivitySource</c>/<c>Meter</c> custom do domínio.</para>
///
/// Contrato (ADR-024/031) — nos VALUES do app, não no código:
/// <list type="bullet">
///   <item><c>instrumentation.opentelemetry.io/inject-dotnet: "false"</c> —
///     auto-inject (CLR profiler) + SDK in-process juntos crasham (exit 139).</item>
///   <item><c>medgrupo.io/logs: "otlp"</c> — dedup do stdout após validar OTLP.</item>
///   <item>chart <c>medgrupo-prod-app &gt;= 0.5.12</c> injeta <c>OTEL_SERVICE_NAME</c>;
///     <c>&gt;= 0.5.15</c> injeta também <c>OTEL_SERVICE_VERSION</c> = tag da
///     imagem (habilita "o deploy piorou?" por atribuição direta — ADR-043).</item>
/// </list>
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registra o pipeline OTel (traces/métricas/logs) do MEDSoft. App web:
    /// chame também <c>AddMEDSoftAspNetCoreObservability()</c> do pacote
    /// <c>MEDSoft.Observability.AspNetCore</c> (DEPOIS deste — ele valida a
    /// ordem e falha alto se o núcleo não estiver registrado).
    /// </summary>
    /// <param name="extraTracing">Instrumentações de trace específicas do app (sources custom do domínio).</param>
    /// <param name="extraMetrics">Meters específicos do app.</param>
    /// <param name="enableGenAI">Registra sources/meters <c>Microsoft.SemanticKernel*</c> (gen_ai.* — o app liga o switch experimental do SK, não-sensível; ADR-041).</param>
    public static IServiceCollection AddMEDSoftObservability(
        this IServiceCollection services,
        Action<TracerProviderBuilder>? extraTracing = null,
        Action<MeterProviderBuilder>? extraMetrics = null,
        bool enableGenAI = false)
    {
        // Marcador lido pelo satélite (fail-fast de ordem/ausência do núcleo).
        services.TryAddSingleton<MEDSoftObservabilityMarker>();

        // OTEL_SERVICE_NAME (chart >= 0.5.12 = nome do workload k8s) tem
        // precedência; fallback pro nome do processo (dev local). Nunca cravar o
        // nome no código (ADR-024, "um sinal, um dono").
        var resolvedServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? AppDomain.CurrentDomain.FriendlyName;

        // service.version (ADR-043, item 3): canal = OTEL_SERVICE_VERSION
        // (chart injeta a tag da imagem). SEM fallback de assembly de
        // propósito — o default 1.0.0.0 mentiria e atropelaria quem popular
        // service.version via OTEL_RESOURCE_ATTRIBUTES (o detector de env do
        // CreateDefault lê essa var; AddService por cima dela VENCE).
        var resolvedServiceVersion = Environment.GetEnvironmentVariable("OTEL_SERVICE_VERSION");

        // Resource ÚNICO para traces, métricas E logs — atributo novo entra num
        // lugar só e os três sinais saem idênticos (correlação por resource,
        // ADR-031). Era o ponto frágil da auditoria: o resource dos logs era
        // construído à parte e divergiria em silêncio ao primeiro atributo novo.
        void ConfigureSharedResource(ResourceBuilder res) => res.AddService(
            resolvedServiceName,
            serviceVersion: string.IsNullOrWhiteSpace(resolvedServiceVersion) ? null : resolvedServiceVersion);

        // v0.3.0: SEM delegate de exporter. O OtlpExporterOptions do SDK já lê
        // sozinho OTEL_EXPORTER_OTLP_ENDPOINT, as variantes por sinal
        // (OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT) e
        // OTEL_EXPORTER_OTLP_PROTOCOL — e em net8+ o default de protocolo JÁ é
        // gRPC. O override antigo (Endpoint + Protocol=Grpc em código) era
        // redundante no caminho feliz e NEUTRALIZAVA as envs por sinal e a
        // migração de protocolo (achado A6 da auditoria 2026-07-26).

        services.AddOpenTelemetry()
            .ConfigureResource(ConfigureSharedResource)
            .WithTracing(tracer =>
            {
                tracer
                    .AddHttpClientInstrumentation()
                    // ActivitySources da MEDGRUPO.SDK — mensageria (RabbitMQ) e
                    // ServiceConnect (HTTP entre serviços). NUNCA MEDGRUPO.SDK.*
                    // (os sources reais são MEDGRUPO.Messaging/ServiceConnect;
                    // nome errado = trace some em silêncio).
                    .AddSource("MEDGRUPO.Messaging")
                    .AddSource("MEDGRUPO.ServiceConnect")

                    // ---- RECURSOS por SOURCE (v0.2.0) — zero dependência nova ----

                    // PostgreSQL. O Npgsql (6+) já expõe ActivitySource próprio;
                    // `AddNpgsql()` do pacote Npgsql.OpenTelemetry é literalmente
                    // `AddSource("Npgsql")`. Usar o source direto evita arrastar
                    // uma dependência de Npgsql numa versão fixa — o pacote é
                    // consumido por apps em Npgsql 9 E 10, e forçar 10 seria
                    // conflito REAL de versão (não é bloat, é build quebrado).
                    // NOTA: 9 e 10 emitem DIALETOS diferentes de métrica/atributo
                    // (db_client_commands_* × db_client_operation_*; db.system ×
                    // db.system.name) — as rules da plataforma toleram os dois
                    // (gitops#776).
                    .AddSource("Npgsql")

                    // RabbitMQ.Client 7 — sources NATIVOS (não há pacote de
                    // instrumentação). Em apps ainda na 6.x isto é no-op: a 6.x
                    // não tem ActivitySource, então o span só existe via
                    // MEDGRUPO.Messaging (ou após bump para 7.x).
                    .AddSource("RabbitMQ.Client.Publisher")
                    .AddSource("RabbitMQ.Client.Subscriber")

                    // MongoDB. ATENÇÃO: registrar o source é METADE do trabalho —
                    // o app PRECISA fazer o wiring do ClusterConfigurator no
                    // MongoClientSettings (pacote MongoDB.Driver.Core.Extensions.
                    // DiagnosticSources), senão nenhum span é produzido e isto
                    // aqui fica silenciosamente inerte. Referência de wiring:
                    // MEDPlanner .../Data/Factories/MongoClientFactory.cs
                    .AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources")

                    // ---- RECURSOS por INSTRUMENTAÇÃO (v0.2.0) ----

                    // SQL Server (Microsoft.Data.SqlClient + Dapper). Cobre
                    // conteudos e trilhas, que estavam 100% cegos no DB.
                    // SetDbStatementForText fica FALSE (default): capturar o texto
                    // do comando levaria PII/segredo pro trace.
                    .AddSqlClientInstrumentation()

                    // EF Core — span lógico da query (complementa o do provider).
                    .AddEntityFrameworkCoreInstrumentation()

                    // Redis. GOTCHA: exige um IConnectionMultiplexer registrado no
                    // DI para se enganchar; sem isso vira no-op SILENCIOSO. Se o
                    // app constrói o multiplexer na mão, registrar no DI ou usar
                    // o overload que recebe a instância via extraTracing.
                    .AddRedisInstrumentation()

                    // AWS SDK (S3, SNS, SQS, DynamoDB…). Estava no núcleo do
                    // MEDSoft.VideoStream.Observability e foi PERDIDO na
                    // generalização — autenticacao publica em SNS e apostilas usa
                    // S3, ambos invisíveis. O zero-code (CLR profiler) TAMBÉM não
                    // cobre AWS SDK, então sem isto não há span por nenhuma via.
                    // PRÉ-REQUISITO: Instrumentation.AWS >= 1.12 exige AWSSDK v4
                    // transitivo — app ainda em AWSSDK v3 precisa migrar antes de
                    // adotar o pacote (major com breaking changes).
                    .AddAWSInstrumentation();

                if (enableGenAI)
                {
                    // Semantic Kernel: chat/embeddings + gen_ai.* (o app precisa
                    // ligar o switch experimental do SK). Wildcard cobre os
                    // connectors (Microsoft.SemanticKernel.Connectors.*).
                    tracer.AddSource("Microsoft.SemanticKernel*");
                }

                // Instrumentações específicas do app (sources custom do domínio).
                extraTracing?.Invoke(tracer);

                tracer.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddHttpClientInstrumentation()
                    // >= 1.14 emite dotnet_* (semconv novo, sem prefixo process_runtime_).
                    .AddRuntimeInstrumentation()
                    .AddMeter("MEDGRUPO.Messaging")
                    .AddMeter("MEDGRUPO.ServiceConnect")

                    // ---- Métricas de RECURSO (v0.2.0) ----
                    // Alimentam db_client_(operation|commands)_duration_* — os
                    // DOIS dialetos do Npgsql (10 e <=9) são consumidos pelas
                    // recording rules `db_client:*` da plataforma (gitops#776).
                    // AddMeter por NOME é seguro: meter inexistente é no-op, não erro.
                    .AddMeter("Npgsql")
                    .AddMeter("OpenTelemetry.Instrumentation.SqlClient");

                if (enableGenAI)
                {
                    // semantic_kernel_connectors_openai_tokens_* (gen_ai.client.token.usage).
                    metrics.AddMeter("Microsoft.SemanticKernel*");
                }

                extraMetrics?.Invoke(metrics);

                // OTLP push (ADR-013): NÃO usar PrometheusExporter — sem endpoint
                // de scrape mapeado, as métricas não saem do processo.
                metrics.AddOtlpExporter();
            })
            // Logs do ILogger via OTLP — correlação Log↔Trace no LGTM. A
            // plataforma liga a ponte; os devs escrevem os statements de log.
            .WithLogging(
                logging =>
                {
                    // Mesmo resource dos traces/métricas, construído pela MESMA
                    // action (o ConfigureResource do builder unificado não
                    // alcança o provider de log do host; construir à parte era o
                    // jeito de quebrar a correlação em silêncio).
                    var logResource = ResourceBuilder.CreateDefault();
                    ConfigureSharedResource(logResource);
                    logging
                        .SetResourceBuilder(logResource)
                        .AddOtlpExporter();
                },
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                });

        return services;
    }
}
