
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EngineeringAI.Core.Llm;

public sealed class LlamaWarmupService : BackgroundService
{
    private readonly LlamaModelProvider _provider;
    private readonly ILogger<LlamaWarmupService> _logger;

    public LlamaWarmupService(LlamaModelProvider provider, ILogger<LlamaWarmupService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_provider.Options.PreloadOnStartup)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                _provider.Preload();

                await _provider.GenerateAsync(
                        "<|im_start|>user\n{}<|im_end|>\n<|im_start|>assistant\n",
                        maxTokens: 2,
                        stopWhenJsonComplete: false,
                        cancellationToken: stoppingToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("Llama model preloaded and warmed up.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Llama model preload failed; it will load on first request.");
            }
        }, stoppingToken);
    }
}