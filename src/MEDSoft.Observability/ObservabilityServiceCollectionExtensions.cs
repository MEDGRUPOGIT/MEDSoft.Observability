using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MEDSoft.Observability;

/// <summary>
/// Configuração OTel in-process compartilhada de TODO app .NET do MEDSoft
/// (ADR-041 do gitops-plataform). Um ponto único: traces + métricas + logs via
/// OTLP push pro alloy-receiver; <c>service.name</c> = workload k8s; <b>sem
/// sampler</b> (o tail_sampling do coletor é o único decisor de storage);
/// <c>ILogger</c>→OTLP com o mesmo resource (correlação Log↔Trace).
///
/// Núcleo (todo app, sem config): HttpClient, sources/meters
/// <c>MEDGRUPO.Messaging</c>/<c>MEDGRUPO.ServiceConnect</c> (da MEDGRUPO.SDK),
/// runtime metrics (<c>dotnet_*</c>), AspNetCore (se <paramref name="isWebApp"/>),
/// logging — e, desde a v0.2.0, os RECURSOS: PostgreSQL (Npgsql), SQL Server,
/// EF Core, MongoDB, Redis, AWS SDK e RabbitMQ.Client 7.
///
/// <para><b>Por que os recursos vieram pro núcleo (v0.2.0).</b> Na v0.1.0 eram
/// opt-in via <paramref name="extraTracing"/>. Auditoria de 2026-07-25:
/// <b>NENHUMA das 10 workloads passava <c>extraTracing</c></b> — todas chamavam só
/// <c>AddMEDSoftObservability(isWebApp:…)</c>, deixando um comentário
/// "// DB via extraTracing = follow-up". Resultado medido: ZERO span de Mongo,
/// Postgres, SQL Server, Redis ou S3 em produção. Opt-in que ninguém exerce é
/// cobertura zero — o default passa a ser "instrumentado".</para>
///
/// <para>Segue app-específico via <paramref name="extraTracing"/>/<paramref name="extraMetrics"/>:
/// <c>ActivitySource</c>/<c>Meter</c> custom do domínio.</para>
///
/// Contrato (ADR-024/031) — nos VALUES do app, não no código:
/// <list type="bullet">
///   <item><c>instrumentation.opentelemetry.io/inject-dotnet: "false"</c> —
///     auto-inject (CLR profiler) + SDK in-process juntos crasham (exit 139).</item>
///   <item><c>medgrupo.io/logs: "otlp"</c> — dedup do stdout após validar OTLP.</item>
///   <item>chart <c>medgrupo-prod-app &gt;= 0.5.12</c> injeta <c>OTEL_SERVICE_NAME</c>.</item>
/// </list>
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registra o pipeline OTel (traces/métricas/logs) do MEDSoft.
    /// </summary>
    /// <param name="isWebApp">Liga a instrumentação ASP.NET Core (server span + http.server metrics).</param>
    /// <param name="extraTracing">Instrumentações de trace específicas do app (DB/AWS/MQ/sources custom).</param>
    /// <param name="extraMetrics">Meters específicos do app.</param>
    /// <param name="enableGenAI">Registra sources/meters <c>Microsoft.SemanticKernel*</c> (gen_ai.* — o app liga o switch experimental do SK, não-sensível; ADR-041).</param>
    public static IServiceCollection AddMEDSoftObservability(
        this IServiceCollection services,
        bool isWebApp = false,
        Action<TracerProviderBuilder>? extraTracing = null,
        Action<MeterProviderBuilder>? extraMetrics = null,
        bool enableGenAI = false)
    {
        // Endpoint OTLP: respeita OTEL_EXPORTER_OTLP_ENDPOINT (ConfigMap do K8s ->
        // alloy-receiver -> LGTM). Fallback = default do SDK (localhost:4317, dev).
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        // OTEL_SERVICE_NAME (chart >= 0.5.12 = nome do workload k8s) tem
        // precedência; fallback pro nome do processo (dev local). Nunca cravar o
        // nome no código (ADR-024, "um sinal, um dono").
        var resolvedServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
            ?? AppDomain.CurrentDomain.FriendlyName;

        void ConfigureOtlp(OtlpExporterOptions options)
        {
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            {
                options.Endpoint = new Uri(otlpEndpoint);
            }

            options.Protocol = OtlpExportProtocol.Grpc;
        }

        services.AddOpenTelemetry()
            .ConfigureResource(res => res
                .AddService(resolvedServiceName))
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
                    .AddAWSInstrumentation();

                if (isWebApp)
                {
                    // RecordException: grava exception events (status=Error) nos
                    // spans de request que escapem pro pipeline ASP.NET.
                    tracer.AddAspNetCoreInstrumentation(options => options.RecordException = true);
                }

                if (enableGenAI)
                {
                    // Semantic Kernel: chat/embeddings + gen_ai.* (o app precisa
                    // ligar o switch experimental do SK). Wildcard cobre os
                    // connectors (Microsoft.SemanticKernel.Connectors.*).
                    tracer.AddSource("Microsoft.SemanticKernel*");
                }

                // Instrumentações específicas do app (EFCore/Npgsql/Redis/Mongo/
                // AWS/RabbitMQ.Client 7/sources custom).
                extraTracing?.Invoke(tracer);

                tracer.AddOtlpExporter(ConfigureOtlp);
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
                    // Alimentam `db_client_operation_duration_*`, que as recording
                    // rules `db_client:*` da plataforma consomem (RED de banco por
                    // db_system_name/service_name/server_address).
                    // AddMeter por NOME é seguro: meter inexistente é no-op, não erro.
                    .AddMeter("Npgsql")
                    .AddMeter("OpenTelemetry.Instrumentation.SqlClient");

                if (isWebApp)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (enableGenAI)
                {
                    // semantic_kernel_connectors_openai_tokens_* (gen_ai.client.token.usage).
                    metrics.AddMeter("Microsoft.SemanticKernel*");
                }

                extraMetrics?.Invoke(metrics);

                // OTLP push (ADR-013): NÃO usar PrometheusExporter — sem endpoint
                // de scrape mapeado, as métricas não saem do processo.
                metrics.AddOtlpExporter(ConfigureOtlp);
            })
            // Logs do ILogger via OTLP — correlação Log↔Trace no LGTM. A
            // plataforma liga a ponte; os devs escrevem os statements de log.
            .WithLogging(
                logging => logging
                    // Resource do log explícito: o ConfigureResource unificado NÃO
                    // aplica no provider de log do host (ficaria no ApplicationName,
                    // divergindo do trace). Mesmo service.name (ADR-024).
                    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(resolvedServiceName))
                    .AddOtlpExporter(ConfigureOtlp),
                options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                });

        return services;
    }
}
