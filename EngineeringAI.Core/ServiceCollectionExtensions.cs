using EngineeringAI.Core.Abstractions;
using EngineeringAI.Core.Agent;
using EngineeringAI.Core.Llm;
using EngineeringAI.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace EngineeringAI.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEngineeringAI(
        this IServiceCollection services,
        Action<LlamaOptions> configure)
    {
        services.AddOptions<LlamaOptions>().Configure(configure);

        services.TryAddSingleton<LlamaModelProvider>();
        services.TryAddSingleton<IChatCompletionService, LlamaChatCompletionService>();
        services.AddHostedService<LlamaWarmupService>();

        return services;
    }

    public static IServiceCollection AddEngineeringDomain<TState>(
        this IServiceCollection services,
        Func<IServiceProvider, AgentDomainDefinition<TState>> domainFactory,
        TimeSpan? draftTtl = null)
        where TState : class, IDraftState, new()
    {
        services.TryAddSingleton(new DesignFlowStore<TState>(draftTtl));
        services.TryAddSingleton(domainFactory);
        services.TryAddSingleton(sp => sp.GetRequiredService<Func<IServiceProvider, AgentDomainDefinition<TState>>>()(sp));
        services.TryAddSingleton<AgentOrchestrator<TState>>();

        return services;
    }
}