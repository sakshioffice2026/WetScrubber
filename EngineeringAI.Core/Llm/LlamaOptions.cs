namespace EngineeringAI.Core.Llm;

public sealed class LlamaOptions
{
    public const string SectionName = "EngineeringAI:Llama";

    public string ModelPath { get; set; } = string.Empty;

    public uint ContextSize { get; set; } = 4096;

    public int GpuLayerCount { get; set; } = 0;

    public int MaxTokens { get; set; } = 768;

    public float Temperature { get; set; } = 0.1f;

    public IList<string> AntiPrompts { get; set; } = new List<string>
    {
        "<|im_end|>",
        "<|im_start|>user"
    };
}