using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngineeringAI.Core.Llm;

public sealed class LlamaModelProvider : IDisposable
{
    private readonly LlamaOptions _options;
    private readonly ILogger<LlamaModelProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();

    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private bool _disposed;

    public LlamaModelProvider(IOptions<LlamaOptions> options, ILogger<LlamaModelProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(
        string prompt,
        int? maxTokens = null,
        float? temperature = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        await foreach (var token in StreamAsync(prompt, maxTokens, temperature, cancellationToken)
                           .ConfigureAwait(false))
        {
            sb.Append(token);
        }

        return sb.ToString().Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        int? maxTokens = null,
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();

            var executor = new StatelessExecutor(_weights!, _modelParams!);

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens ?? _options.MaxTokens,
                AntiPrompts = _options.AntiPrompts.ToList(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature ?? _options.Temperature
                }
            };

            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return token;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_weights is not null)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_weights is not null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.ModelPath) || !File.Exists(_options.ModelPath))
            {
                throw new FileNotFoundException(
                    $"GGUF model not found at '{_options.ModelPath}'. Configure '{LlamaOptions.SectionName}:ModelPath'.");
            }

            _logger.LogInformation("Loading GGUF model from {Path}", _options.ModelPath);

            _modelParams = new ModelParams(_options.ModelPath)
            {
                ContextSize = _options.ContextSize,
                GpuLayerCount = _options.GpuLayerCount
            };

            _weights = LLamaWeights.LoadFromFile(_modelParams);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _weights?.Dispose();
        _gate.Dispose();
    }
}