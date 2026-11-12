using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zentwik.UI.Views;

namespace Zentwik.Smoke
{
    internal static class Program
    {
        private static readonly string RepoRoot = FindRepoRoot();
        private static readonly string OutputDir = Path.Combine(RepoRoot, "build", "Zentwik.UiSmoke", "out");
        private static readonly string LogPath = Path.Combine(OutputDir, "smoke.log");

        [STAThread]
        private static int Main()
        {
            Directory.CreateDirectory(OutputDir);
            File.WriteAllText(LogPath, "");

            int failures = 0;

            try
            {
                Application app = CreateApplication();

                failures += CheckResources(app);
                failures += CheckKeyCollisions(app);
                failures += InstantiateViews();
                failures += RenderWindow(app);
                failures += RenderTuningView(app);
                failures += DumpTuningLayout();
                failures += VerifyInteractions();
                failures += AuditStaticResourceKeys(app);
            }
            catch (Exception ex)
            {
                Write("[FATAL] " + ex);
                failures++;
            }

            Write();
            Write(failures == 0 ? "[SUMMARY] failures=0" : $"[SUMMARY] failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// 从程序集所在目录向上找 Zentwik.slnx，定位仓库根。
        /// 不写死绝对路径 —— 换台机器 / 换个目录克隆都能直接跑。
        /// </summary>
        private static string FindRepoRoot()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);

            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Zentwik.slnx")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new InvalidOperationException("找不到 Zentwik.slnx，无法定位仓库根目录。");
        }

        /// <summary>
        /// 建立 Application 并挂载主题字典。
        ///
        /// 顺序须与 AppThemeService.Initialize 一致：主题 → Accent → Controls → 强调色画刷 → 图标几何。
        /// 相对 URI 按入口程序集解析，本探针入口不是 Zentwik.UI，只能用绝对 pack URI。
        /// </summary>
        private static Application CreateApplication()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            foreach (string relative in new[]
                     {
                         "Themes/Tokens.Dark.xaml",
                         "Themes/Accent.xaml",
                         "Themes/Controls.xaml"
                     })
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Zentwik.UI;component/" + relative, UriKind.Absolute)
                });
            }

            // 强调色画刷由 AppThemeService 在代码里填充，XAML 中没有定义。
            Zentwik.UI.Services.AppThemeService theme = Zentwik.UI.Services.AppThemeService.Instance;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["AccentBrush"] = theme.AccentBrush,
                ["AccentForegroundBrush"] = theme.AccentForegroundBrush,
                ["AccentSubtleBrush"] = theme.AccentSubtleBrush,
                ["BrandAccentBrush"] = theme.BrandAccentBrush,
                ["BrandAccentMidBrush"] = theme.BrandAccentMidBrush,
                ["BrandAccentLightBrush"] = theme.BrandAccentLightBrush
            });

            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Zentwik.UI;component/Themes/BrandLogo.xaml", UriKind.Absolute)
            });

            Write("[0] Application 就绪：主题 / Accent / Controls / 强调色 / 图标 共 5 份字典");
            return app;
        }

        /// <summary>
        /// 在整个资源树上查键。
        ///
        /// 【坑】不能只查 <c>MergedDictionaries[0]</c>：
        /// App.xaml 合并了三份字典（Colors.Dark / Accent / Styles），
        /// 样式全在第三份里。ResourceDictionary 的索引器**会**连带搜索合并字典，
        /// 所以直接查 app.Resources 才是对的。
        /// </summary>
        private static object? Lookup(Application app, string key) => app.Resources[key];

        /// <summary>资源字典必须非空，且颜色 / 样式键都必须能解析。</summary>
        private static int CheckResources(Application app)
        {
            int bad = 0;

            Write($"[1] 资源字典：合并 {app.Resources.MergedDictionaries.Count} 份");

            // 不变量：明暗两份令牌字典的键集合必须完全一致 —— 主题切换是整份字典按键覆盖，
            // 少一个键就会在切主题后留下上一个主题的画刷。
            ResourceDictionary dark = LoadDictionary("Themes/Tokens.Dark.xaml");
            ResourceDictionary light = LoadDictionary("Themes/Tokens.Light.xaml");

            HashSet<string> darkKeys = Keys(dark);
            HashSet<string> lightKeys = Keys(light);

            foreach (string key in darkKeys.Except(lightKeys).OrderBy(k => k, StringComparer.Ordinal))
            {
                Write($"    ! Tokens.Light 缺少键：{key}");
                bad++;
            }

            foreach (string key in lightKeys.Except(darkKeys).OrderBy(k => k, StringComparer.Ordinal))
            {
                Write($"    ! Tokens.Dark 缺少键：{key}");
                bad++;
            }

            foreach (string key in darkKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (Lookup(app, key) is null)
                {
                    Write($"    ! 令牌不可解析：{key}");
                    bad++;
                }
            }

            Write(bad == 0
                ? $"    {darkKeys.Count} 个令牌键明暗一致且可解析 ✓"
                : $"    {bad} 处令牌问题");
            return bad;
        }

        private static ResourceDictionary LoadDictionary(string relative)
        {
            return new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Zentwik.UI;component/" + relative, UriKind.Absolute)
            };
        }

        private static HashSet<string> Keys(ResourceDictionary dictionary)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (object key in dictionary.Keys)
            {
                if (key is string text)
                    keys.Add(text);
            }
            return keys;
        }

        /// <summary>
        /// 跨字典键名冲突检测。
        ///
        /// 【为什么要查】WPF 的资源字典按 key 索引、**不区分类型**。
        /// 同一个 key 在两份合并字典里各定义一次时，后合并的会静默覆盖先合并的：
        /// 若两者类型不同（Brush vs Style），用到的地方会在运行期抛
        /// 「不是属性 X 的有效值」—— 编译期与静态检查都发现不了。
        /// 本项目真实踩过一次：颜色字典的 TextMeta（画刷）被样式字典的
        /// TextMeta（TextBlock 样式）覆盖，ComboBox 的箭头 Stroke 直接崩。
        ///
        /// 同类型覆盖只是「谁生效」的问题，类型不同才是 bug —— 这里只报后者为失败。
        /// </summary>
        private static int CheckKeyCollisions(Application app)
        {
            int bad = 0;
            var owners = new Dictionary<string, (int Dictionary, string Type)>(StringComparer.Ordinal);

            for (int i = 0; i < app.Resources.MergedDictionaries.Count; i++)
            {
                ResourceDictionary dictionary = app.Resources.MergedDictionaries[i];

                foreach (object key in dictionary.Keys)
                {
                    if (key is not string name)
                        continue;

                    string type = dictionary[name]?.GetType().Name ?? "null";

                    if (owners.TryGetValue(name, out var previous))
                    {
                        if (previous.Type != type)
                        {
                            Write($"    ! 键名冲突：'{name}' 在字典#{previous.Dictionary} 是 {previous.Type}，在字典#{i} 是 {type}");
                            bad++;
                        }
                    }
                    else
                    {
                        owners[name] = (i, type);
                    }
                }
            }

            Write(bad == 0
                ? $"[1b] 跨字典键名冲突：{owners.Count} 个键，0 处类型冲突 ✓"
                : $"[1b] 跨字典键名冲突：{bad} 处类型冲突");

            return bad;
        }

        /// <summary>
        /// 交互验证：侧栏折叠、切页选中态。
        /// 这两处是本轮**新写**的逻辑（折叠改列宽、切页改 ToggleButton 选中态），
        /// 光看静态截图证明不了，必须真的点一下看结果。
        /// </summary>
        private static int VerifyInteractions()
        {
            int bad = 0;
            Write("[2c] 交互验证");

            var window = new Zentwik.UI.MainWindow { Width = 1280, Height = 820 };
            window.Measure(new Size(1280, 820));
            window.Arrange(new Rect(0, 0, 1280, 820));
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
            window.UpdateLayout();

            FrameworkElement? root = window.Content as FrameworkElement;
            if (root is null)
            {
                Write("    ! 主窗口没有内容根元素");
                return 1;
            }

            root.Measure(new Size(1280, 820));
            root.Arrange(new Rect(0, 0, 1280, 820));
            root.UpdateLayout();

            ContentControl? host = Descendants(root).OfType<ContentControl>().FirstOrDefault(c => c.Name == "ContentHost");
            RadioButton? navProcess = Descendants(root).OfType<RadioButton>().FirstOrDefault(b => b.Name == "NavProcess");
            RadioButton? navTuning = Descendants(root).OfType<RadioButton>().FirstOrDefault(b => b.Name == "NavTuning");

            if (host is null || navProcess is null || navTuning is null)
            {
                Write("    ! 找不到 ContentHost / NavProcess / NavTuning");
                return 1;
            }

            object? before = host.Content;

            navProcess.IsChecked = true;
            window.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
            window.UpdateLayout();

            object? after = host.Content;
            Write($"    导航切换：{before?.GetType().Name ?? "(null)"} → {after?.GetType().Name ?? "(null)"}");

            if (before is null || ReferenceEquals(before, after))
            {
                Write("    ! 导航切换未生效");
                bad++;
            }

            if (navTuning.IsChecked == true)
            {
                Write("    ! 切页后「调校」仍处于选中态");
                bad++;
            }

            Save(root, 1280, 820, Path.Combine(OutputDir, "mainwindow_process.png"));
            return bad;
        }

        /// <summary>
        /// 布局内省：把调校页头部几个文本块的**实际坐标**打出来。
        /// 截图只能看出"看起来不对"，坐标才能说明是被裁了、还是重叠了、还是被谁压住了。
        /// </summary>
        private static int DumpTuningLayout()
        {
            Write("[2b] 调校页头部布局内省");

            var view = new TuningView { Width = 1000, Height = 1450 };
            view.Measure(new Size(1000, 1450));
            view.Arrange(new Rect(0, 0, 1000, 1450));
            view.UpdateLayout();
            view.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
            view.UpdateLayout();

            var scroll = Descendants(view).OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null)
            {
                Write($"    ScrollViewer actual={scroll.ActualWidth:F0}x{scroll.ActualHeight:F0} offset=({scroll.VerticalOffset:F1})");
            }

            foreach (TextBlock block in Descendants(view).OfType<TextBlock>().Take(6))
            {
                Point origin = block.TranslatePoint(new Point(0, 0), view);
                Write($"    TextBlock \"{Truncate(block.Text, 18)}\" " +
                      $"actual={block.ActualWidth:F1}x{block.ActualHeight:F1} " +
                      $"pos=({origin.X:F1},{origin.Y:F1}) " +
                      $"font={block.FontSize} weight={block.FontWeight} " +
                      $"family=\"{block.FontFamily.Source}\"");
            }

            // 页头 Grid 的高度决定了里面的 StackPanel 是否会被裁
            foreach (Grid grid in Descendants(view).OfType<Grid>().Take(2))
            {
                Write($"    Grid actual={grid.ActualWidth:F1}x{grid.ActualHeight:F1} " +
                      $"children={grid.Children.Count} " +
                      $"row0={grid.RowDefinitions.Count} col0={grid.ColumnDefinitions.Count}");
            }

            return 0;
        }

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "(空)";

            string flat = text.Replace("\r", " ").Replace("\n", " ");
            return flat.Length <= max ? flat : flat[..max] + "…";
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;

                foreach (DependencyObject descendant in Descendants(child))
                    yield return descendant;
            }
        }

        /// <summary>把每个页面都 new 一遍：任何 StaticResource 缺失都会在这里抛。</summary>
        private static int InstantiateViews()
        {
            int bad = 0;

            void Try(string name, Func<FrameworkElement> factory)
            {
                try
                {
                    _ = factory();
                    Write($"    ✓ {name}");
                }
                catch (Exception ex)
                {
                    Write($"    ! {name} 构造失败：{ex.GetType().Name}: {ex.Message}");
                    bad++;
                }
            }

            Write("[2] 页面实例化（StaticResource 运行期解析）");

            // MainWindow 不在此列：其构造函数调 AppThemeService.Initialize()，内部用相对 URI
            // 加载 Themes/*.xaml，按入口程序集解析；本探针入口不是 Zentwik.UI，必然失败。
            // 它的资源键仍由 [3] 的静态审计覆盖。
            Try("TuningView", () => new TuningView());
            Try("ProcessView", () => new ProcessView());
            Try("PowerView", () => new PowerView());
            Try("ScenariosView", () => new ScenariosView());
            Try("SettingsView", () => new SettingsView());
            Try("AdvancedView", () => new AdvancedView());
            Try("BehaviorView", () => new BehaviorView());
            Try("LogsView", () => new LogsView());

            return bad;
        }

        /// <summary>
        /// 离屏渲染主窗口。
        ///
        /// 【坑】未 Show 的 Window 没有 HwndSource，直接 Render(window) 只会得到一张全黑图。
        /// 窗口的内容根元素才是真正有视觉树的东西，渲染它。
        /// </summary>
        private static int RenderWindow(Application app)
        {
            try
            {
                var window = new Zentwik.UI.MainWindow
                {
                    Width = 1280,
                    Height = 820
                };

                if (window.Content is not FrameworkElement root)
                {
                    Write("    ! 主窗口没有内容根元素");
                    return 1;
                }

                // 【为什么要铺底色】离屏渲染出的 PNG 是带 alpha 的。
                // 窗口根 Grid 没有自己的 Background，透明区域的白色文字会被写成
                // 预乘 alpha，存成 PNG 再看就是"字被啃掉一块"的假象 —— 那是渲染伪影，不是界面问题。
                // 真实运行时窗口有 Background="{DynamicResource WindowBgBrush}" 兜着，
                // 这里补上同一层底色，模拟真实观感。
                if (root is Panel panel)
                    panel.Background = (Brush)app.Resources["WindowBgBrush"];

                window.Measure(new Size(1280, 820));
                window.Arrange(new Rect(0, 0, 1280, 820));
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
                window.UpdateLayout();

                root.Measure(new Size(1280, 820));
                root.Arrange(new Rect(0, 0, 1280, 820));
                root.UpdateLayout();

                Save(root, 1280, 820, Path.Combine(OutputDir, "mainwindow.png"));
                Crop(Path.Combine(OutputDir, "mainwindow.png"), 900, 100, 380, 300, 3, Path.Combine(OutputDir, "crop_refresh.png"));
                Crop(Path.Combine(OutputDir, "mainwindow.png"), 0, 0, 300, 60, 4, Path.Combine(OutputDir, "crop_titlebar.png"));
                Write("    ✓ mainwindow.png");
                return 0;
            }
            catch (Exception ex)
            {
                Write("    ! 渲染主窗口失败：" + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        /// <summary>离屏渲染调校页（内容区单独看，细节更清楚）。</summary>
        private static int RenderTuningView(Application app)
        {
            try
            {
                var view = new TuningView
                {
                    Width = 1000,
                    Height = 1450,
                    // 铺底色：消除透明底导致的预乘 alpha 伪影（详见 RenderWindow 里的说明）
                    Background = (Brush)app.Resources["WindowBgBrush"]
                };

                view.Measure(new Size(1000, 1450));
                view.Arrange(new Rect(0, 0, 1000, 1450));
                view.UpdateLayout();
                view.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
                view.UpdateLayout();

                Save(view, 1000, 1450, Path.Combine(OutputDir, "tuningview.png"));
                Write("    ✓ tuningview.png");

                // 放大裁剪：标题区、底部状态行、刷新按钮
                Crop(Path.Combine(OutputDir, "tuningview.png"), 0, 0, 420, 70, 3, Path.Combine(OutputDir, "crop_title.png"));
                Crop(Path.Combine(OutputDir, "tuningview.png"), 0, 825, 420, 40, 3, Path.Combine(OutputDir, "crop_bottom.png"));
                Crop(Path.Combine(OutputDir, "tuningview.png"), 630, 140, 90, 50, 6, Path.Combine(OutputDir, "crop_refresh_tuning.png"));
                Write("    ✓ 裁剪图 3 张");

                // 对照渲染：关掉 ClearType 再渲一张。
                // 若默认那张的字形是"重影"，而这张干净，说明是离屏 ClearType 的渲染伪影，
                // 而不是界面本身的问题。
                string[] renderingModes = Enum.GetNames(typeof(System.Windows.Media.TextRenderingMode));
                Write("    TextRenderingMode 成员: " + string.Join(",", renderingModes));

                // 用 Enum.Parse 而不是直接写枚举成员：成员名各框架版本有出入，
                // 写死会在编译期就炸，而这里只是想拿到一个"非 ClearType"的对照。
                string? grayscale = renderingModes.FirstOrDefault(n => n.Contains("Gray", StringComparison.OrdinalIgnoreCase))
                                   ?? renderingModes.FirstOrDefault(n => n.Contains("Ideal", StringComparison.OrdinalIgnoreCase));

                if (grayscale is not null)
                {
                    var plain = new TuningView
                    {
                        Width = 1000,
                        Height = 1450,
                        Background = (Brush)app.Resources["WindowBgBrush"]
                    };
                    TextOptions.SetTextRenderingMode(
                        plain,
                        (System.Windows.Media.TextRenderingMode)Enum.Parse(typeof(System.Windows.Media.TextRenderingMode), grayscale));

                    plain.Measure(new Size(1000, 1450));
                    plain.Arrange(new Rect(0, 0, 1000, 1450));
                    plain.UpdateLayout();
                    plain.Dispatcher.Invoke(DispatcherPriority.Loaded, new Action(() => { }));
                    plain.UpdateLayout();
                    Save(plain, 1000, 1450, Path.Combine(OutputDir, "tuningview_grayscale.png"));
                    Crop(Path.Combine(OutputDir, "tuningview_grayscale.png"), 0, 0, 420, 70, 3, Path.Combine(OutputDir, "crop_title_gray.png"));
                    Write($"    ✓ tuningview_grayscale.png（模式 {grayscale}）");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Write("    ! 渲染调校页失败：" + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 静态审计：把仓库里所有 .xaml 里的 StaticResource 键抽出来，
        /// 逐个到资源字典里查。仓库自带的 xamlcheck.py 只查"模板内跨作用域"，
        /// 查不出"这个键压根没人定义"这一类。
        /// </summary>
        private static int AuditStaticResourceKeys(Application app)
        {
            string repo = @"D:\Projects\Zentwik";
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            int checkedKeys = 0;

            foreach (string file in Directory.EnumerateFiles(repo, "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains(@"\Legacy\")
                    || file.Contains(@".ai_backup_"))
                    continue;

                // 先剥掉 XML 注释：注释里出现的 Style="{StaticResource xxx}" 是说明文字，不是真引用。
                string raw = File.ReadAllText(file);
                string text = Regex.Replace(raw, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

                // 本文件自己定义的键（页面局部资源，如转换器）也算"有定义"。
                var localKeys = new HashSet<string>(
                    Regex.Matches(raw, @"x:Key\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""")
                        .Select(m => m.Groups[1].Value),
                    StringComparer.Ordinal);

                foreach (Match match in Regex.Matches(text, @"StaticResource\s+([A-Za-z_][A-Za-z0-9_]*)"))
                {
                    string key = match.Groups[1].Value;
                    checkedKeys++;

                    if (localKeys.Contains(key))
                        continue;

                    if (Lookup(app, key) is null)
                        missing.Add($"{key}   ← {Path.GetRelativePath(repo, file)}");
                }
            }

            Write($"[3] StaticResource 静态审计：检查 {checkedKeys} 处引用");

            foreach (string item in missing)
                Write("    ! 未定义：" + item);

            Write(missing.Count == 0
                ? "    全部 StaticResource 键都有定义 ✓"
                : $"    {missing.Count} 个键未定义");

            return missing.Count;
        }

        private static void Save(FrameworkElement element, int width, int height, string path)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>
        /// 裁剪 + 放大，便于肉眼看细节。用纯 WPF（BitmapImage / CroppedBitmap / TransformedBitmap），
        /// 不引入任何外部图像库。
        /// </summary>
        private static void Crop(string sourcePath, int x, int y, int w, int h, int scale, string destPath)
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(sourcePath);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();

            var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
            var scaled = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));

            using FileStream stream = File.Create(destPath);
            encoder.Save(stream);
        }

        /// <summary>同时写控制台与日志文件 —— 探针可能在任意一步崩掉，日志能保住已完成的进度。</summary>
        private static void Write(string line = "")
        {
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // 日志写不进去也不能影响主流程
            }
        }
    }
}
