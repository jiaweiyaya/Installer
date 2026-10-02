using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace Installer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 注册全局未捕获异常监听
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogCrashException(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogCrashException(args.Exception, "DispatcherUnhandledException");
                args.Handled = true;
                Shutdown(-1);
            };

            // 2. 检查是否为卸载流程
            string exeName = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
            bool isUninstallMode = string.Equals(exeName, "uninstall.exe", StringComparison.OrdinalIgnoreCase)
                                   || (e.Args.Length > 0 && e.Args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

            if (isUninstallMode)
            {
                PerformUninstall();
                Shutdown();
                return;
            }

            // 3. 正常安装模式下的管理员权限检查
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "install.exe",
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(processInfo);
                }
                catch
                {
                }

                Shutdown();
                return;
            }
        }

        private static void PerformUninstall()
        {
            var config = InstallerConfig.Load();

            var dialogResult = MessageBox.Show(
                $"确定要完全卸载 {config.AppName} 及其全部组件吗？",
                $"{config.AppName} 卸载向导",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (dialogResult != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                // 1. 关闭正在运行的主程序
                string procName = Path.GetFileNameWithoutExtension(config.MainExecutable);
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                // 2. 删除桌面快捷方式
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopLnk = Path.Combine(desktop, $"{config.AppName}.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);

                // 3. 删除开始菜单快捷方式与文件夹
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", config.AppName);
                if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);

                // 4. 清除 Windows 注册表卸载记录
                string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{config.AppName}";
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(keyPath, false);

                // 5. 调用外部静默进程延时删除安装目录本体
                string currentDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 1 /nobreak >nul & rmdir /s /q \"{currentDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                MessageBox.Show($"{config.AppName} 已成功从你的电脑中完全卸载！", "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"卸载失败：{ex.Message}", "卸载异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void LogCrashException(Exception? ex, string source)
        {
            if (ex == null) return;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string logPath = Path.Combine(desktop, "Installer_Crash.log");

            string logContent = $@"=========================================
Installer 崩溃时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
异常捕获源: {source}
异常类型: {ex.GetType().FullName}
异常信息: {ex.Message}
=========================================
调用堆栈:
{ex.StackTrace}
=========================================
内部异常:
{ex.InnerException?.ToString() ?? "无内部异常"}
";

            try
            {
                File.AppendAllText(logPath, logContent);
                MessageBox.Show($"安装程序遇到严重错误崩溃：\n\n{ex.Message}\n\n详细崩溃日志已保存至桌面：\n{logPath}", "安装程序异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
            }
        }
    }
}