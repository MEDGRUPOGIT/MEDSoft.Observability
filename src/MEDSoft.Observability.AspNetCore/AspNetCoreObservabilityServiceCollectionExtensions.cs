using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MEDSoft.Observability;

/// <summary>
/// Instrumentação ASP.NET Core do MEDSoft — satélite do núcleo
/// <c>MEDSoft.Observability</c> (issue #4 / v0.4.0). É pacote SEPARADO porque
/// <c>OpenTelemetry.Instrumentation.AspNetCore</c> arrasta o shared framework
/// (<c>FrameworkReference Microsoft.AspNetCore.App</c>) — no núcleo, isso
/// forçava TODO worker à imagem base <c>aspnet</c>. Worker referencia só o
/// núcleo e roda em base <c>runtime</c>; app web referencia os dois pacotes e
/// chama os dois métodos (núcleo primeiro).
///
/// Mesmo namespace do núcleo de propósito: um único
/// <c>using MEDSoft.Observability;</c> traz os dois métodos (padrão do próprio
/// OTel, que pendura extensões em <c>OpenTelemetry.Trace</c> seja qual for o
/// pacote de origem).
/// </summary>
public static class AspNetCoreObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registra server spans + métricas <c>http.server.*</c> do ASP.NET Core
    /// no MESMO pipeline OTel do núcleo. Chame UMA vez (duplicar a chamada
    /// duplica a instrumentação), DEPOIS de
    /// <see cref="ObservabilityServiceCollectionExtensions.AddMEDSoftObservability"/>.
    /// </summary>
    /// <param name="filterHealthChecks">Descarta spans de probe (<c>/health*</c>,
    /// <c>/healthz</c>, <c>/ready</c>, <c>/live</c>, <c>/alive</c>) na origem —
    /// sem sampler no app, cada probe do kubelet viraria span exportado (o
    /// drop_ci do coletor descarta lá, mas o custo de wire/CPU do app fica).
    /// SÓ vale para TRACES: a métrica <c>http.server.request.duration</c>
    /// continua contando probes (a instrumentação não tem filtro de métrica;
    /// mesmo comportamento da v0.3.0). Desligue por app se probes precisarem
    /// de trace.</param>
    /// <param name="additionalFilter">Predicado EXTRA do app (true = coleta o
    /// span), combinado por AND com o de health. Existe porque
    /// <c>options.Filter</c> é ATRIBUIÇÃO (a última vence): setar direto via
    /// <c>Configure&lt;AspNetCoreTraceInstrumentationOptions&gt;</c> APAGARIA o
    /// filtro de health do pacote — caso real: o websockethub repetia o
    /// predicado de health à mão só para somar o de WebSocket. Deve ser barato
    /// e NÃO lançar (exceção dentro do Filter descarta o span).</param>
    public static IServiceCollection AddMEDSoftAspNetCoreObservability(
        this IServiceCollection services,
        bool filterHealthChecks = true,
        Func<HttpContext, bool>? additionalFilter = null)
    {
        // Fail-fast: satélite SEM o núcleo compila e sobe — mas os spans
        // AspNetCore nasceriam num pipeline sem resource e sem exporter e
        // morreriam EM SILÊNCIO (a classe de falha que este repo mais evita;
        // é o mesmo espírito do guard tag×Version do publish). O marcador é
        // registrado pelo AddMEDSoftObservability() do núcleo.
        if (!services.Any(d => d.ServiceType == typeof(MEDSoftObservabilityMarker)))
        {
            throw new InvalidOperationException(
                "Chame AddMEDSoftObservability() (nucleo) ANTES de AddMEDSoftAspNetCoreObservability(). " +
                "Sem o nucleo nao ha resource nem exporter: os spans ASP.NET Core seriam descartados em silencio.");
        }

        // AddOpenTelemetry() repetido é o mecanismo OFICIAL de extensão
        // cross-cutting do SDK: os callbacks de WithTracing/WithMetrics
        // deferem pros MESMOS TracerProvider/MeterProvider singletons que o
        // núcleo registrou (TryAddSingleton; o hosted service é
        // TryAddEnumerable e não inicia 2x). O resource do núcleo alcança os
        // spans daqui. REGRA DURA: aqui NUNCA ConfigureResource /
        // AddOtlpExporter / WithLogging — duplicar o exporter duplicaria TODA
        // a telemetria do pipeline (caso real: autorizacao-api pré-v0.2.0).
        services.AddOpenTelemetry()
            .WithTracing(tracer => tracer.AddAspNetCoreInstrumentation(options =>
            {
                // RecordException: grava exception events (status=Error) nos
                // spans de request que escapem pro pipeline ASP.NET.
                options.RecordException = true;

                // Filter é ATRIBUIÇÃO — o pacote COMBINA os predicados aqui
                // (health && app) em vez de deixar o app sobrescrever.
                // Semântica: TRUE = coleta o span. Health primeiro
                // (short-circuit barato no caminho quente do kubelet).
                options.Filter = (filterHealthChecks, additionalFilter) switch
                {
                    (true, null) => static ctx => !IsHealthProbe(ctx),
                    (true, not null) => ctx => !IsHealthProbe(ctx) && additionalFilter(ctx),
                    (false, not null) => additionalFilter,
                    (false, null) => null, // sem filtro: não pagar um lambda passa-tudo
                };
            }))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation());

        return services;
    }

    // Predicado MOVIDO do núcleo v0.3.0, byte a byte (paridade de
    // comportamento é critério de aceite da issue #4). /health* cobre o
    // readiness /health/live/ da frota por prefixo.
    private static bool IsHealthProbe(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/live", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/alive", StringComparison.OrdinalIgnoreCase);
    }
}
