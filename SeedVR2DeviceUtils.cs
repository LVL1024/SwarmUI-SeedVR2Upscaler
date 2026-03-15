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

    /// <summary>Cached ROCm GPU count. Null means not yet queried; 0 means unavailable or none detected.</summary>
    private static int? _cachedRocmGpuCount = null;

    /// <summary>
    /// Attempts to count AMD GPUs via rocm-smi. Returns 0 if rocm-smi is unavailable or fails.
    /// On ROCm systems, AMD GPUs are exposed to PyTorch as cuda:X devices, mirroring CUDA indexing.
    /// Result is cached since GPU count does not change while the server is running.
    /// </summary>
    private static int CountRocmGpus()
    {
        if (_cachedRocmGpuCount.HasValue)
        {
            return _cachedRocmGpuCount.Value;
        }
        try
        {
            ProcessStartInfo psi = new("rocm-smi", "--showid --csv")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi);
            if (p is null)
            {
                _cachedRocmGpuCount = 0;
                return 0;
            }
            // Read stdout asynchronously with a 5s timeout to prevent hanging if rocm-smi stalls.
            // ReadToEnd() alone can block indefinitely, so we race it against the timeout.
            Task<string> readTask = p.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(5000))
            {
                p.Kill();
                _cachedRocmGpuCount = 0;
                return 0;
            }
            p.WaitForExit(1000);
            if (!p.HasExited || p.ExitCode != 0)
            {
                _cachedRocmGpuCount = 0;
                return 0;
            }
            string output = readTask.Result;
            int count = 0;
            bool firstLine = true;
            foreach (string line in output.Split('\n'))
            {
                if (firstLine) { firstLine = false; continue; } // skip header
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count++;
                }
            }
            _cachedRocmGpuCount = count;
            return count;
        }
        catch
        {
            _cachedRocmGpuCount = 0;
            return 0;
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


