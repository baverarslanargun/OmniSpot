using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.Models;

public class LLMSettings
{
    [JsonPropertyName("use_gpu")]
    public bool UseGpu { get; set; } = false;
    
    [JsonPropertyName("gpu_layers")]
    public int GpuLayers { get; set; } = 32;

    [JsonPropertyName("auto_gpu_layers")]
    public bool AutoGpuLayers { get; set; } = true;
    
    [JsonPropertyName("context_size")]
    public int ContextSize { get; set; } = 512;
    
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 80;
    
    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;
    
    [JsonPropertyName("batch_size")]
    public int BatchSize { get; set; } = 128;
    
    [JsonPropertyName("threads")]
    public int Threads { get; set; } = -1;
}
