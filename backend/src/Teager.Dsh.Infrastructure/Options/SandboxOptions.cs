namespace Teager.Dsh.Infrastructure.Options;

public class SandboxOptions
{
    public string? PreviewBaseUrl { get; set; }
    public string[] NetworkAllowedHosts { get; set; } = [];
    // Explicit administrator opt-in for VPN DNS mappings in 198.18.0.0/15.
    public string[] ModelFakeIpHosts { get; set; } = [];
    public bool Enabled { get; set; }
    public string Image { get; set; } = "teager-dsh-sandbox:usage-only";
    public string DockerExecutable { get; set; } = "docker";
    // When API itself runs in Docker, translate DataDir into its daemon-host path.
    public string? HostDataDir { get; set; }
    public int MemoryMb { get; set; } = 512;
    public double Cpus { get; set; } = 1;
    public int MaxProcesses { get; set; } = 128;
    public int MaxExecutions { get; set; } = 4;
    public int MaxFiles { get; set; } = 50000;
    public int MaxStorageMb { get; set; } = 5120;
    public int MaxMinutes { get; set; } = 60;
    // Legacy setting retained for configuration compatibility; use model.params.taskBudget.
    public long ModelBudgetCredits { get; set; }
}
