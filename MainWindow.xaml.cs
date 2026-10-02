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
        private ScrollViewer? _docScrollViewer;
        private bool _hasReadAgreement = false;
        private bool _isMarkdownValid = false;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Windows 10 1809 及 Windows 11 原生暗黑模式标题栏属性
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            InitializeComponent();
            _config = InstallerConfig.Load();

            // 监听并执行 Markdig 内部所有的超链接跳转指令，直接调用系统浏览器
            CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, OnMarkdigHyperlinkExecuted));

            InitDisplay();
            LoadMarkdownContent();

            // 协议阅读完成前，首屏下一步按钮保持置灰禁用
            BtnNext.IsEnabled = false;

            // 窗体渲染完成后挂载内部滚动检测器
            this.Loaded += (s, e) => HookScrollViewer();
        }

        private void OnMarkdigHyperlinkExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (e.Parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = uri.AbsoluteUri,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
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
            _isMarkdownValid = false;

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
                            _isMarkdownValid = false;
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
                            _isMarkdownValid = true; // 哈希一致，放行通过
                        }
                    }
                    else
                    {
                        mdText = System.Text.Encoding.UTF8.GetString(fileBytes);
                        _isMarkdownValid = true; // 未设校验哈希，正常放行
                    }
                }
                catch (Exception ex)
                {
                    _isMarkdownValid = false;
                    mdText = $@"# 读取说明文档失败

无法读取指定的文件 `{_config.MarkdownFile}`。

**错误详情：**
`{ex.Message}`";
                }
            }
            else
            {
                // 找不到文档时明确报错并锁定
                _isMarkdownValid = false;
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
            }
            catch (Exception ex)
            {
                _isMarkdownValid = false;
                var errDoc = new System.Windows.Documents.FlowDocument();
                errDoc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run($"文档渲染异常：{ex.Message}"))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60))
                });
                DocViewer.Document = errDoc;
            }

            // 无论任何原因导致文档不可用，都将下一步按钮强制锁死置灰
            if (!_isMarkdownValid)
            {
                BtnNext.IsEnabled = false;
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

        private void HookScrollViewer()
        {
            _docScrollViewer = FindVisualChild<ScrollViewer>(DocViewer);
            if (_docScrollViewer != null)
            {
                // 开启物理像素级平滑流动，消灭顿挫卡顿感
                _docScrollViewer.CanContentScroll = false;
                _docScrollViewer.ScrollChanged += (s, e) => CheckIfScrolledToBottom();
                CheckIfScrolledToBottom();
            }
        }

        private void CheckIfScrolledToBottom()
        {
            // 铁门神把关：只有文档本身完全合法有效，才有资格进入阅读滚动解锁流程
            if (!_isMarkdownValid)
            {
                if (_currentStep == 1)
                {
                    BtnNext.IsEnabled = false;
                    TxtAgreementTip.Visibility = Visibility.Visible;
                }
                return;
            }

            if (_hasReadAgreement || _docScrollViewer == null) return;

            // 容差 8 像素；若内容较短无需滚动 (ScrollableHeight <= 0)，直接解锁
            if (_docScrollViewer.ScrollableHeight <= 0 ||
                _docScrollViewer.VerticalOffset >= _docScrollViewer.ScrollableHeight - 8)
            {
                _hasReadAgreement = true;
                if (_currentStep == 1)
                {
                    BtnNext.IsEnabled = true;
                    TxtAgreementTip.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
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
                bool hasEnoughSpace = drive.AvailableFreeSpace >= _requiredBytes;

                if (hasEnoughSpace)
                {
                    TxtSpaceInfo.Text = $"需要 {Math.Max(1, reqMb)} MB，可用 {freeMb} MB";
                    TxtSpaceInfo.Foreground = new SolidColorBrush(Color.FromRgb(138, 143, 153));
                    if (_currentStep == 3)
                    {
                        BtnNext.IsEnabled = true;
                    }
                }
                else
                {
                    TxtSpaceInfo.Text = $"需要 {Math.Max(1, reqMb)} MB，可用 {freeMb} MB（磁盘剩余空间不足！）";
                    TxtSpaceInfo.Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                    if (_currentStep == 3)
                    {
                        BtnNext.IsEnabled = false;
                    }
                }
            }
            catch
            {
                TxtSpaceInfo.Text = "路径格式无效";
                TxtSpaceInfo.Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                if (_currentStep == 3)
                {
                    BtnNext.IsEnabled = false;
                }
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

                TxtAgreementTip.Visibility = Visibility.Collapsed;
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
                // 进入第 3 步选路径页面，立即检查当前目标路径的真实剩余空间
                UpdateDiskSpace();
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
                BtnNext.Content = "同意并继续";
                BtnNext.IsEnabled = _isMarkdownValid && _hasReadAgreement;
                TxtAgreementTip.Visibility = (_isMarkdownValid && _hasReadAgreement) ? Visibility.Collapsed : Visibility.Visible;
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
            var (sourceBin, archName) = InstallerConfig.GetTargetPayloadDirectory();

            // 核心修复：在进入后台线程前，必须在 UI 线程提前读取复选框状态，防止跨线程崩溃
            bool createDesktop = ChkDesktop.IsChecked == true;
            bool createStartMenu = ChkStartMenu.IsChecked == true;

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string logPath = Path.Combine(desktop, "Installer_Crash.log");
            var logLines = new List<string>
            {
                $"[INFO {DateTime.Now:HH:mm:ss}] 开始执行安装流程",
                $"[INFO] 目标安装目录: {targetDir}",
                $"[INFO] 源载荷目录 ({archName}): {sourceBin}"
            };

            try
            {
                await Task.Run(() =>
                {
                    // 1. 验证载荷目录
                    if (string.IsNullOrEmpty(sourceBin) || !Directory.Exists(sourceBin))
                    {
                        throw new DirectoryNotFoundException($"未能找到有效的程序源文件目录: {sourceBin}");
                    }

                    // 2. 复制所有安装文件
                    logLines.Add($"[INFO] 正在创建目标目录: {targetDir}");
                    Directory.CreateDirectory(targetDir);

                    logLines.Add("[INFO] 正在复制文件清单...");
                    CopyDirectoryWithLogging(sourceBin, targetDir, logLines);

                    // 3. 创建桌面与开始菜单快捷方式
                    string mainExe = Path.Combine(targetDir, _config.MainExecutable);
                    if (!File.Exists(mainExe))
                    {
                        logLines.Add($"[WARN] 主执行文件暂未找到: {mainExe}");
                    }

                    if (createDesktop)
                    {
                        logLines.Add("[INFO] 正在创建桌面快捷方式...");
                        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        CreateShortcut(Path.Combine(desktopPath, $"{_config.AppName}.lnk"), mainExe, targetDir);
                    }

                    if (createStartMenu)
                    {
                        logLines.Add("[INFO] 正在创建开始菜单快捷方式...");
                        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", _config.AppName);
                        Directory.CreateDirectory(startMenu);
                        CreateShortcut(Path.Combine(startMenu, $"{_config.AppName}.lnk"), mainExe, targetDir);
                    }

                    // 4. 部署独立的卸载器实体 uninstall.exe
                    logLines.Add("[INFO] 正在生成卸载程序组件...");
                    string currentInstaller = Environment.ProcessPath ?? string.Empty;
                    if (File.Exists(currentInstaller))
                    {
                        File.Copy(currentInstaller, Path.Combine(targetDir, "uninstall.exe"), true);
                    }

                    // 5. 写入 Windows 注册表卸载信息
                    logLines.Add("[INFO] 正在写入系统注册表卸载项...");
                    RegisterUninstall(targetDir);
                    logLines.Add("[INFO] 全部安装步骤执行完毕。");
                });

                // 安装顺利完成
                _currentStep = 5;
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Value = 100;
                TxtInstallStatus.Text = "安装完成！";
                TxtCurrentAction.Text = $"{_config.AppName} 已成功安装到你的电脑。";
                BtnNext.Content = "启动并完成";
                BtnNext.IsEnabled = true;
            }
            catch (Exception ex)
            {
                // 捕获异常，记录追踪日志并优雅呈现在界面上，绝不闪退
                logLines.Add($"[FATAL ERROR] 安装过程崩溃: {ex.GetType().FullName}");
                logLines.Add($"[FATAL ERROR] 错误消息: {ex.Message}");
                logLines.Add($"[FATAL ERROR] 调用堆栈:\n{ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    logLines.Add($"[FATAL ERROR] 内部异常: {ex.InnerException.Message}");
                }

                try
                {
                    File.WriteAllLines(logPath, logLines);
                }
                catch
                {
                }

                _currentStep = 5;
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Value = 0;
                TxtInstallStatus.Text = "安装失败！";
                TxtInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                TxtCurrentAction.Text = $"原因: {ex.Message}\n详细追踪日志已输出至桌面: Installer_Crash.log";
                BtnNext.Content = "关闭退出";
                BtnNext.IsEnabled = true;
            }
        }

        private static void CopyDirectoryWithLogging(string source, string target, List<string> logs)
        {
            // 使用 Path.GetRelativePath 进行安全路径映射，彻底规避 string.Replace 的替换缺陷
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                string relDir = Path.GetRelativePath(source, dir);
                string destDir = Path.Combine(target, relDir);
                Directory.CreateDirectory(destDir);
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relFile = Path.GetRelativePath(source, file);
                string destFile = Path.Combine(target, relFile);
                logs.Add($"[COPY] {relFile}");
                File.Copy(file, destFile, true);
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
                    string uninstallerPath = Path.Combine(targetDir, "uninstall.exe");
                    string icoPath = Path.Combine(targetDir, "app.ico");
                    string iconTarget = File.Exists(icoPath) ? icoPath : Path.Combine(targetDir, _config.MainExecutable);

                    key.SetValue("DisplayName", _config.AppName);
                    key.SetValue("DisplayVersion", _config.AppVersion);
                    key.SetValue("Publisher", _config.Publisher);
                    key.SetValue("InstallLocation", targetDir);
                    key.SetValue("DisplayIcon", iconTarget);
                    // 绑定 uninstall.exe 作为原生卸载指令
                    key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
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