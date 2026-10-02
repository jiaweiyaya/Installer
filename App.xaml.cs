using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Installer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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
    }
}