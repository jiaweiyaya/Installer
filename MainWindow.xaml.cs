using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Markdig;
using Markdig.Wpf;

namespace Installer
{
    public partial class MainWindow : Window
    {
        private readonly InstallerConfig _config;
        private int _currentStep = 1;
        private long _requiredBytes = 0;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Windows 10 1809 及 Windows 11 原生暗黑模式标题栏属性
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            InitializeComponent();
            _config = InstallerConfig.Load();
            InitDisplay();
            LoadMarkdownContent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // 激活 Windows 原生文件资源管理器同款沉浸式深色标题栏
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }

        private void InitDisplay()
        {
            this.Title = $"{_config.AppName} 安装程序";
            TxtIntroAppName.Text = _config.AppName;
            TxtIntroVersion.Text = $"版本 {_config.AppVersion}";

            // 动态配置协议徽章
            if (!string.IsNullOrWhiteSpace(_config.LicenseName))
            {
                BadgeLicenseBorder.Visibility = Visibility.Visible;
                TxtLicenseName.Text = _config.LicenseName;
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(_config.LicenseBadgeColor);
                    BadgeLicenseBorder.Background = new SolidColorBrush(color);
                }
                catch
                {
                    BadgeLicenseBorder.Background = new SolidColorBrush(Color.FromRgb(16, 124, 65));
                }
            }
            else
            {
                BadgeLicenseBorder.Visibility = Visibility.Collapsed;
            }

            // 动态配置仓库主页按钮
            if (!string.IsNullOrWhiteSpace(_config.RepositoryUrl))
            {
                BtnRepo.Visibility = Visibility.Visible;
                BtnRepo.Content = string.IsNullOrWhiteSpace(_config.RepositoryButtonText) ? "访问项目仓库" : _config.RepositoryButtonText;
            }
            else
            {
                BtnRepo.Visibility = Visibility.Collapsed;
            }

            // 默认安装位置
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            TxtInstallPath.Text = Path.Combine(programFiles, _config.DefaultFolderName);

            ChkDesktop.IsChecked = _config.CreateDesktopShortcut;
            ChkStartMenu.IsChecked = _config.CreateStartMenuShortcut;

            _requiredBytes = _config.CalculateRequiredBytes();
            UpdateDiskSpace();
        }

        private void LoadMarkdownContent()
        {
            string mdPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.MarkdownFile);
            string mdText;
            bool hashValid = true;

            if (File.Exists(mdPath))
            {
                try
                {
                    byte[] fileBytes = File.ReadAllBytes(mdPath);

                    // 如果配置了 SHA-256，执行严格的防篡改完整性校验
                    if (!string.IsNullOrWhiteSpace(_config.MarkdownSha256))
                    {
                        byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(fileBytes);
                        string computedHash = Convert.ToHexString(hashBytes);

                        if (!computedHash.Equals(_config.MarkdownSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            hashValid = false;
                            mdText = $@"# 说明文档安全校验失败

安装程序检测到说明文档文件 `{_config.MarkdownFile}` 的 SHA-256 校验和不匹配，内容可能已被非法篡改或文件损坏！

### 完整性比对信息
- **预期安全哈希 (Expected)**：
  `{_config.MarkdownSha256}`
- **实际文件哈希 (Calculated)**：
  `{computedHash}`

出于安装安全考虑，已自动终止文档加载并锁定安装向导。请重新获取官方原版安装包。";
                        }
                        else
                        {
                            mdText = System.Text.Encoding.UTF8.GetString(fileBytes);
                        }
                    }
                    else
                    {
                        mdText = System.Text.Encoding.UTF8.GetString(fileBytes);
                    }
                }
                catch (Exception ex)
                {
                    mdText = $@"# 读取说明文档失败

无法读取指定的文件 `{_config.MarkdownFile}`。

**错误详情：**
`{ex.Message}`";
                }
            }
            else
            {
                // 找不到文档时明确报错
                mdText = $@"# 无法加载说明文档

安装程序未能找到指定的文档文件：`{_config.MarkdownFile}`

### 故障排查建议
- **安装包不完整**：请检查安装包是否已完全解压，缺少 `{_config.MarkdownFile}` 文件；
- **配置路径不匹配**：请检查 `installer_config.json` 中的 `MarkdownFile` 字段；
- **当前程序搜索路径**：
  `{AppDomain.CurrentDomain.BaseDirectory}`

请将项目说明文档放置于程序运行同级目录下后重新启动。";
            }

            try
            {
                var pipeline = new Markdig.MarkdownPipelineBuilder().UseSupportedExtensions().Build();
                var doc = Markdig.Wpf.Markdown.ToFlowDocument(mdText, pipeline);
                doc.Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230));

                DocViewer.Document = doc;
                DocViewer.AddHandler(System.Windows.Documents.Hyperlink.RequestNavigateEvent,
                    new System.Windows.Navigation.RequestNavigateEventHandler(OnHyperlinkNavigate));

                // 校验失败时将下一步按钮置灰锁定
                if (!hashValid)
                {
                    BtnNext.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                var errDoc = new System.Windows.Documents.FlowDocument();
                errDoc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run($"文档渲染异常：{ex.Message}"))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60))
                });
                DocViewer.Document = errDoc;
            }
        }

        private void OnHyperlinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
            e.Handled = true;
        }

        private void BtnRepo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _config.RepositoryUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void UpdateDiskSpace()
        {
            try
            {
                string path = TxtInstallPath.Text.Trim();
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
                var drive = new DriveInfo(root);

                long reqMb = _requiredBytes / (1024 * 1024);
                long freeMb = drive.AvailableFreeSpace / (1024 * 1024);

                TxtSpaceInfo.Text = $"需要 {Math.Max(1, reqMb)} MB，可用 {freeMb} MB";
            }
            catch
            {
                TxtSpaceInfo.Text = "路径格式无效";
            }
        }

        private void TxtInstallPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateDiskSpace();
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择安装文件夹",
                InitialDirectory = TxtInstallPath.Text
            };

            if (dlg.ShowDialog() == true)
            {
                TxtInstallPath.Text = Path.Combine(dlg.FolderName, _config.DefaultFolderName);
            }
        }

        private void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                // 第 1 步 (介绍) ➔ 第 2 步 (环境检查)
                _currentStep = 2;
                SwitchPageWithAnimation(PageStepIntro, PageStepCheck, isForward: true);

                BtnCancel.Visibility = Visibility.Collapsed;
                BtnBack.Visibility = Visibility.Visible;
                BtnRecheck.Visibility = Visibility.Visible;
                BtnNext.Content = "下一步";

                RunEnvironmentChecks();
            }
            else if (_currentStep == 2)
            {
                // 第 2 步 (环境检查) ➔ 第 3 步 (安装配置)
                _currentStep = 3;
                SwitchPageWithAnimation(PageStepCheck, PageStepConfig, isForward: true);

                BtnRecheck.Visibility = Visibility.Collapsed;
                BtnNext.Content = "开始安装";
                BtnNext.IsEnabled = true;
            }
            else if (_currentStep == 3)
            {
                // 第 3 步 (安装配置) ➔ 第 4 步 (执行释放)
                _currentStep = 4;
                SwitchPageWithAnimation(PageStepConfig, PageStepProgress, isForward: true);

                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.IsEnabled = false;
                BtnNext.Content = "正在安装...";

                StartInstallTask();
            }
            else if (_currentStep == 5)
            {
                // 完成退出并拉起主程序
                string exePath = Path.Combine(TxtInstallPath.Text.Trim(), _config.MainExecutable);
                if (File.Exists(exePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });
                }
                Application.Current.Shutdown();
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 2)
            {
                // 第 2 步 ➔ 第 1 步 (介绍)
                _currentStep = 1;
                SwitchPageWithAnimation(PageStepCheck, PageStepIntro, isForward: false);

                BtnBack.Visibility = Visibility.Collapsed;
                BtnRecheck.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Visible;
                BtnNext.Content = "下一步";
                BtnNext.IsEnabled = true;
            }
            else if (_currentStep == 3)
            {
                // 第 3 步 ➔ 第 2 步 (环境检查)
                _currentStep = 2;
                SwitchPageWithAnimation(PageStepConfig, PageStepCheck, isForward: false);

                BtnRecheck.Visibility = Visibility.Visible;
                BtnNext.Content = "下一步";
                RunEnvironmentChecks();
            }
        }

        private void BtnRecheck_Click(object sender, RoutedEventArgs e)
        {
            RunEnvironmentChecks();
        }

        /// <summary>
        /// 模仿 Windows 11 Fluent 页面切换：左右微平移 30px + 透明度交叉淡入淡出动效
        /// </summary>
        private void SwitchPageWithAnimation(FrameworkElement fromPage, FrameworkElement toPage, bool isForward)
        {
            double enterFrom = isForward ? 30 : -30;
            double exitTo = isForward ? -30 : 30;

            // 1. 准备新页面进场状态
            toPage.Visibility = Visibility.Visible;
            toPage.Opacity = 0;
            var toTransform = new TranslateTransform(enterFrom, 0);
            toPage.RenderTransform = toTransform;

            // 2. 准备旧页面退场状态
            var fromTransform = new TranslateTransform(0, 0);
            fromPage.RenderTransform = fromTransform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(220);

            // 3. 执行旧页面淡出微移
            var fadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
            var slideOut = new DoubleAnimation(0, exitTo, duration) { EasingFunction = ease };
            fadeOut.Completed += (s, e) =>
            {
                fromPage.Visibility = Visibility.Collapsed;
                fromPage.Opacity = 1;
                fromTransform.X = 0;
            };
            fromPage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            fromTransform.BeginAnimation(TranslateTransform.XProperty, slideOut);

            // 4. 执行新页面淡入微移
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var slideIn = new DoubleAnimation(enterFrom, 0, duration) { EasingFunction = ease };
            toPage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            toTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
        }

        private void RunEnvironmentChecks()
        {
            CheckListPanel.Children.Clear();
            var checks = new List<(string Name, bool Passed)>();

            // 1. 管理员运行权限
            using (var id = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(id);
                checks.Add(("管理员安装权限", principal.IsInRole(WindowsBuiltInRole.Administrator)));
            }

            // 2. 操作系统版本 (Win10 / Win11)
            checks.Add(("操作系统兼容性 (Windows 10/11)", Environment.OSVersion.Version.Major >= 10));

            // 3. 架构与载荷匹配检测
            var (sourceBin, archName) = InstallerConfig.GetTargetPayloadDirectory();
            bool binExists = !string.IsNullOrEmpty(sourceBin) && Directory.Exists(sourceBin);
            checks.Add(($"系统架构匹配与文件就绪 ({archName})", binExists));

            // 4. 磁盘剩余空间
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(TxtInstallPath.Text)) ?? "C:\\";
                var drive = new DriveInfo(root);
                checks.Add(("磁盘剩余可用空间", drive.AvailableFreeSpace >= _requiredBytes));
            }
            catch
            {
                checks.Add(("磁盘剩余可用空间", false));
            }

            TxtCheckSummary.Text = $"共 {checks.Count} 项";

            bool allPassed = true;
            foreach (var (name, passed) in checks)
            {
                if (!passed) allPassed = false;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                var icon = new TextBlock
                {
                    Text = passed ? "✓" : "✕",
                    Foreground = passed ? new SolidColorBrush(Color.FromRgb(46, 204, 113)) : new SolidColorBrush(Color.FromRgb(231, 76, 60)),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                var text = new TextBlock
                {
                    Text = $"{name}: {(passed ? "通过" : "未通过")}",
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                    FontSize = 13
                };
                sp.Children.Add(icon);
                sp.Children.Add(text);
                CheckListPanel.Children.Add(sp);
            }

            BtnNext.IsEnabled = allPassed;
        }

        private async void StartInstallTask()
        {
            string targetDir = TxtInstallPath.Text.Trim();
            var (sourceBin, _) = InstallerConfig.GetTargetPayloadDirectory();

            await Task.Run(() =>
            {
                // 1. 复制所有文件
                Directory.CreateDirectory(targetDir);
                if (Directory.Exists(sourceBin))
                {
                    CopyDirectory(sourceBin, targetDir);
                }

                // 2. 创建快捷方式
                string mainExe = Path.Combine(targetDir, _config.MainExecutable);
                if (ChkDesktop.IsChecked == true)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(Path.Combine(desktop, $"{_config.AppName}.lnk"), mainExe, targetDir);
                }

                if (ChkStartMenu.IsChecked == true)
                {
                    string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", _config.AppName);
                    Directory.CreateDirectory(startMenu);
                    CreateShortcut(Path.Combine(startMenu, $"{_config.AppName}.lnk"), mainExe, targetDir);
                }

                // 3. 写入 Windows 注册表卸载信息
                RegisterUninstall(targetDir);
            });

            // 安装完成，进入完成就绪态
            _currentStep = 5;
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            TxtInstallStatus.Text = "安装完成！";
            TxtCurrentAction.Text = $"{_config.AppName} 已成功安装到你的电脑。";
            BtnNext.Content = "启动并完成";
            BtnNext.IsEnabled = true;
        }

        private static void CopyDirectory(string source, string target)
        {
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, target));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, target), true);
            }
        }

        private static void CreateShortcut(string lnkPath, string targetExe, string workDir)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic sc = shell.CreateShortcut(lnkPath);
                sc.TargetPath = targetExe;
                sc.WorkingDirectory = workDir;
                sc.Save();
            }
            catch
            {
            }
        }

        private void RegisterUninstall(string targetDir)
        {
            try
            {
                string keyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{_config.AppName}";
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);
                if (key != null)
                {
                    key.SetValue("DisplayName", _config.AppName);
                    key.SetValue("DisplayVersion", _config.AppVersion);
                    key.SetValue("Publisher", _config.Publisher);
                    key.SetValue("InstallLocation", targetDir);
                    key.SetValue("DisplayIcon", Path.Combine(targetDir, _config.MainExecutable));
                    key.SetValue("UninstallString", $"cmd /c rmdir /s /q \"{targetDir}\"");
                }
            }
            catch
            {
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}