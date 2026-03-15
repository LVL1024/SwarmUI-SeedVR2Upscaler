using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FreneticUtilities.FreneticExtensions;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace SeedVR2Upscaler;

/// <summary>Helper utilities for SeedVR2 device option discovery and selection.</summary>
public static class SeedVR2DeviceUtils
{
    /// <summary>Builds a list of possible offload devices.</summary>
    public static List<string> GetSeedVR2OffloadDeviceValues(Session session)
    {
        // Mirror SeedVR2 python behavior
        List<string> local = BuildLocalSeedVR2DeviceList().Select(v => $"{v}///{v}").ToList();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> merged = [];
        foreach (string v in local)
        {
            string raw = v.Before("///").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            if (seen.Add(raw))
            {
                merged.Add(v);
            }
        }
        return merged;
    }

    /// <summary>
    /// Thread-safe cached probe for AMD GPU count via rocm-smi.
    /// Lazy ensures only one rocm-smi process is ever spawned regardless of concurrent callers.
    /// </summary>
    private static readonly Lazy<int> _rocmGpuCount = new(ProbeRocmGpuCount);

    /// <summary>
    /// Attempts to count AMD GPUs via rocm-smi. Returns 0 if rocm-smi is unavailable or fails.
    /// On ROCm systems, AMD GPUs are exposed to PyTorch as cuda:X devices, mirroring CUDA indexing.
    /// </summary>
    private static int CountRocmGpus() => _rocmGpuCount.Value;

    /// <summary>One-time rocm-smi probe backing <see cref="_rocmGpuCount"/>.</summary>
    private static int ProbeRocmGpuCount()
    {
        const int BudgetMs = 5000;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        Process p = null;
        Task<string> stdoutTask = null;
        Task stderrTask = null;
        bool succeeded = false;
        try
        {
            ProcessStartInfo psi = new("rocm-smi", "--showid --csv")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p = Process.Start(psi);
            if (p is null)
            {
                return 0;
            }
            // Drain stdout and stderr concurrently to prevent pipe buffer deadlocks.
            // Each wait uses the remaining fraction of the overall 5s budget.
            stdoutTask = p.StandardOutput.ReadToEndAsync();
            stderrTask = p.StandardError.ReadToEndAsync();
            int ioRemaining = BudgetMs - (int)sw.ElapsedMilliseconds;
            if (ioRemaining <= 0 || !Task.WhenAll(stdoutTask, stderrTask).Wait(ioRemaining))
            {
                return 0; // finally kills and observes tasks
            }
            int exitRemaining = BudgetMs - (int)sw.ElapsedMilliseconds;
            if (exitRemaining <= 0 || !p.WaitForExit(exitRemaining))
            {
                return 0; // finally kills
            }
            if (p.ExitCode != 0)
            {
                return 0;
            }
            int count = 0;
            bool firstLine = true;
            foreach (string line in stdoutTask.Result.Split('\n'))
            {
                if (firstLine) { firstLine = false; continue; } // skip header
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count++;
                }
            }
            succeeded = true;
            return count;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (!succeeded && p is not null)
            {
                // Ensure the process is terminated on every non-success path, including exceptions.
                // Disposing Process does not kill the child — explicit kill is required.
                KillProcess(p);
                // Observe in-flight read tasks so their exceptions don't go unobserved.
                try { Task.WhenAll(stdoutTask ?? Task.CompletedTask, stderrTask ?? Task.CompletedTask).Wait(1000); } catch { }
            }
            p?.Dispose();
        }
    }

    /// <summary>
    /// Kills a process safely, ignoring errors if it has already exited.
    /// Uses a bounded wait after kill to avoid blocking indefinitely on processes
    /// stuck in uninterruptible sleep.
    /// </summary>
    private static void KillProcess(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000); // bounded — process may linger in uninterruptible sleep
            }
        }
        catch
        {
            // ignore — process may have exited between HasExited check and Kill
        }
    }

    /// <summary>
    /// Local replica of SeedVR2's python get_device_list() behavior (memory_manager.get_device_list).
    /// Detects NVIDIA GPUs (via nvidia-smi), AMD GPUs (via rocm-smi), and Apple MPS.
    /// </summary>
    public static List<string> BuildLocalSeedVR2DeviceList()
    {
        List<string> devs = [];
        bool hasCuda = false;
        bool hasMps = false;

        // CUDA: enumerate NVIDIA GPUs
        try
        {
            NvidiaUtil.NvidiaInfo[] gpus = NvidiaUtil.QueryNvidia();
            if (gpus is not null && gpus.Length > 0)
            {
                hasCuda = true;
                for (int i = 0; i < gpus.Length; i++)
                {
                    devs.Add($"cuda:{i}");
                }
            }
        }
        catch
        {
            // ignore
        }

        // ROCm: enumerate AMD GPUs (exposed as cuda:X in PyTorch)
        // Only check if no NVIDIA GPUs were found (mixed setups are extremely rare)
        if (!hasCuda)
        {
            int rocmCount = CountRocmGpus();
            if (rocmCount > 0)
            {
                hasCuda = true;
                for (int i = 0; i < rocmCount; i++)
                {
                    devs.Add($"cuda:{i}");
                }
            }
        }

        // MPS: best-effort detection (python checks torch.backends.mps.is_available()).
        // SwarmUI doesn't have torch, so we approximate: macOS => potentially mps.
        // This only affects the optional "cpu exclusion" behavior when MPS-only.
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                hasMps = true;
                devs.Add("mps");
            }
        }
        catch
        {
            // ignore
        }

        List<string> result = [];
        result.Add("none");
        // Mirror python logic: include cpu only if (has_cuda OR not has_mps)
        if (hasCuda || !hasMps)
        {
            result.Add("cpu");
        }
        result.AddRange(devs);
        return result.Count > 0 ? result : [];
    }

    /// <summary>
    /// Resolves an offload device string to send to SeedVR2 ComfyUI nodes.
    /// The param is toggleable: if the user did not enable it, a reasonable device is selected automatically.
    /// </summary>
    public static string ResolveSeedVR2OffloadDevice(WorkflowGenerator g, T2IRegisteredParam<string> offloadParam, bool requireNotNone, string requireNotNoneReason)
    {
        // Local device options are derived from SwarmUI-side detection and should match SeedVR2 python's get_device_list()
        // as closely as possible (without importing torch)
        List<string> localList = BuildLocalSeedVR2DeviceList();
        HashSet<string> localAllowed = new(localList, StringComparer.OrdinalIgnoreCase);

        string resolved;
        if (g.UserInput.TryGet(offloadParam, out string chosen))
        {
            resolved = chosen.Before("///").Trim();
        }
        else
        {
            // Param not enabled (or not present): choose a sane device automatically.
            // If a non-"none" value is required, prefer cpu when available, otherwise first non-none.
            if (requireNotNone)
            {
                if (localAllowed.Contains("cpu"))
                {
                    resolved = "cpu";
                }
                else
                {
                    resolved = localList.FirstOrDefault(v => !v.Equals("none", StringComparison.OrdinalIgnoreCase)) ?? "none";
                }
            }
            else
            {
                resolved = "none";
            }
        }

        if (localAllowed.Count > 0 && !localAllowed.Contains(resolved))
        {
            string valid = localAllowed.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).JoinString(", ");
            throw new SwarmUserErrorException($"SeedVR2: Invalid VAE offload device '{resolved}'. Valid values (locally detected) are: {valid}.");
        }

        if (requireNotNone && resolved.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new SwarmUserErrorException($"SeedVR2: {requireNotNoneReason} requires 'SeedVR2 VAE Offload Device' to be set (for example 'cpu' or 'cuda:0').");
        }

        return resolved;
    }

    /// <summary>Back-compat wrapper for old call sites.</summary>
    public static string ResolveSeedVR2VAEOffloadDevice(WorkflowGenerator g, T2IRegisteredParam<string> offloadParam, bool tiledVAE, bool cacheModel)
    {
        // VAE caching requires offload_device != none.
        return ResolveSeedVR2OffloadDevice(g, offloadParam, requireNotNone: cacheModel, requireNotNoneReason: "'SeedVR2 Cache Model'");
    }

    /// <summary>
    /// Returns the primary compute device for SeedVR2 model loading.
    /// Prefers CUDA GPUs (NVIDIA or AMD ROCm, both exposed as cuda:X), then MPS on macOS, then falls back to CPU.
    /// </summary>
    public static string GetPrimaryComputeDevice()
    {
        List<string> devices = BuildLocalSeedVR2DeviceList();
        // Prefer cuda:X (first available GPU), then mps, then cpu
        string cuda = devices.FirstOrDefault(d => d.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase));
        if (cuda is not null)
        {
            return cuda;
        }
        if (devices.Contains("mps"))
        {
            return "mps";
        }
        return "cpu";
    }
}


