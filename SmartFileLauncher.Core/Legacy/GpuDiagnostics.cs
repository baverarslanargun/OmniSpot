using System;
using System.Diagnostics;
using System.IO;

namespace SmartFileLauncher.Core.Services;

public static class GpuDiagnostics
{
    public static (bool success, string message) VerifyCudaDlls()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var cudaFolder = Path.Combine(baseDir, "runtimes", "win-x64", "native", "cuda12");
        
        if (!Directory.Exists(cudaFolder))
            return (false, $"CUDA folder not found: {cudaFolder}");
        
        var requiredDlls = new[] { "ggml-cuda.dll", "ggml.dll", "llama.dll" };
        var missing = requiredDlls.Where(dll => !File.Exists(Path.Combine(baseDir, cudaFolder, dll))).ToList();
        
        if (missing.Any())
            return (false, $"Missing CUDA DLLs: {string.Join(", ", missing)}");
        
        return (true, $"All CUDA DLLs present in {cudaFolder}");
    }

    public static (bool success, string raw, int memoryTotalMB) QueryNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p!.StandardOutput.ReadToEnd().Trim();
            if (int.TryParse(output, out var mem))
                return (true, output, mem);
            return (false, output, 0);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }

    public static int RecommendLayers(int vramMB, long modelSizeMB)
    {
        if (vramMB <= 0) return 0;
        double perLayer = modelSizeMB / 32.0;
        var usable = Math.Max(vramMB - 512, perLayer);
        int layers = (int)Math.Floor(usable / perLayer);
        if (layers < 1) layers = 1;
        if (layers > 32) layers = 32;
        return layers;
    }

    public static (bool success, int usedMB, string message) QueryGpuMemoryUsage()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p!.StandardOutput.ReadToEnd().Trim();
            if (int.TryParse(output, out var mem))
                return (true, mem, $"{mem} MB");
            return (false, 0, $"Parse failed: {output}");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
