using System;
using System.IO;
using System.Text.Json;

namespace Installer
{
    public class InstallerConfig
    {
        public string AppName { get; set; } = "FontAutoLoader";
        public string AppVersion { get; set; } = "1.0.0";
        public string Publisher { get; set; } = "Jiaweiya";
        public string MainExecutable { get; set; } = "FontAutoLoader.exe";
        public string DefaultFolderName { get; set; } = "FontAutoLoader";
        public string MarkdownFile { get; set; } = "README.md";
        public string MarkdownSha256 { get; set; } = string.Empty;
        public string RepositoryUrl { get; set; } = string.Empty;
        public string RepositoryButtonText { get; set; } = "访问项目仓库";
        public string LicenseName { get; set; } = string.Empty;
        public string LicenseBadgeColor { get; set; } = "#107C41";
        public bool CreateDesktopShortcut { get; set; } = true;
        public bool CreateStartMenuShortcut { get; set; } = true;

        public static InstallerConfig Load()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer_config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonSerializer.Deserialize<InstallerConfig>(json);
                    if (config != null) return config;
                }
                catch
                {
                }
            }
            return new InstallerConfig();
        }

        public static (string Path, string ArchName) GetTargetPayloadDirectory()
        {
            string baseBin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

            string targetFolder = arch switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                _ => "x64"
            };

            string specificPath = Path.Combine(baseBin, targetFolder);
            if (Directory.Exists(specificPath))
            {
                return (specificPath, targetFolder);
            }

            // ARM64 兼容降级回退：Win11 ARM 支持运行 x64 模拟程序
            if (arch == System.Runtime.InteropServices.Architecture.Arm64)
            {
                string x64Fallback = Path.Combine(baseBin, "x64");
                if (Directory.Exists(x64Fallback))
                {
                    return (x64Fallback, "x64 (仿真模式)");
                }
            }

            // 平铺 bin 根目录兜底
            return (Directory.Exists(baseBin) ? baseBin : string.Empty, "默认架构");
        }

        public long CalculateRequiredBytes()
        {
            var (binPath, _) = GetTargetPayloadDirectory();
            if (string.IsNullOrEmpty(binPath) || !Directory.Exists(binPath))
            {
                return 0;
            }

            long total = 0;
            var dir = new DirectoryInfo(binPath);
            foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                total += f.Length;
            }
            return total;
        }
    }
}