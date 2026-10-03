namespace EngineeringAI.Core.Llm;

public sealed class LlamaOptions
{
    public const string SectionName = "EngineeringAI:Llama";

    public string ModelPath { get; set; } = string.Empty;

    public uint ContextSize { get; set; } = 2048;

    public int GpuLayerCount { get; set; } = 0;

    public int MaxTokens { get; set; } = 256;

    public float Temperature { get; set; } = 0.0f;

    public uint BatchSize { get; set; } = 512;

    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount);

    public bool PreloadOnStartup { get; set; } = true;

    public IList<string> AntiPrompts { get; set; } = new List<string>
    {
        "<|im_end|>",
        "<|im_start|>user"
    };
}