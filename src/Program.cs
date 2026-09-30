// ZCodeWallpaper - 外部壁纸注入器
// 通过 CDP(Chrome DevTools Protocol) 向 ZCode 渲染层注入毛玻璃壁纸覆盖层。
// 目标框架: .NET Framework 4.8 (Windows 自带)，语法: C# 5。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZCodeWallpaper
{
    // 极简本地化: 中文系统显示中文, 其他语言显示英文
    public static class L
    {
        public static readonly bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        public static string T(string zh, string en) { return Zh ? zh : en; }
    }

    public class Config
    {
        public string ImagePath = "";
        public double Opacity = 0.25;
        public double Blur = 12;
        public double Brightness = 1.0;
        public double Scale = 1.08;
        public string Fit = "cover";
        public string Position = "center";
        public int Port = 19788;
        public bool Launch = true;      // false = 纯附加模式(不自启 ZCode)
        public string ZcodePath = "";   // 留空则自动探测

        public static string DefaultText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# ============ ZCode 壁纸配置 ============");
            sb.AppendLine("# 修改并保存本文件, 壁纸会立即刷新, 无需重启。");
            sb.AppendLine("");
            sb.AppendLine("# 图片路径 (必填, 清空则关闭壁纸)");
            sb.AppendLine("path=");
            sb.AppendLine("");
            sb.AppendLine("# 不透明度 0~1, 越小壁纸越淡 (建议 0.2~0.35)");
            sb.AppendLine("opacity=0.25");
            sb.AppendLine("");
            sb.AppendLine("# 模糊度, 单位像素, 0 表示不模糊 (建议 8~16)");
            sb.AppendLine("blur=12");
            sb.AppendLine("");
            sb.AppendLine("# 亮度 0.5~1.5, 1 表示不变");
            sb.AppendLine("brightness=1");
            sb.AppendLine("");
            sb.AppendLine("# 缩放, 略大于 1 可避免模糊后边缘露白");
            sb.AppendLine("scale=1.08");
            sb.AppendLine("");
            sb.AppendLine("# 填充方式: cover=铺满窗口(可能裁剪) contain=完整显示(可能留边)");
            sb.AppendLine("fit=cover");
            sb.AppendLine("");
            sb.AppendLine("# ZCode 安装路径 (留空自动探测)");
            sb.AppendLine("zcodePath=");
            sb.AppendLine("");
            sb.AppendLine("# 调试端口 (避免与其他程序冲突即可)");
            sb.AppendLine("port=19788");
            return sb.ToString();
        }
    }

    public static class ConfigLoader
    {
        public static Config Load(string file)
        {
            Config c = new Config();
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string key = line.Substring(0, i).Trim().ToLowerInvariant();
                string val = line.Substring(i + 1).Trim();
                try
                {
                    switch (key)
                    {
                        case "path": c.ImagePath = val; break;
                        case "opacity": c.Opacity = Clamp(double.Parse(val), 0.02, 1.0); break;
                        case "blur": c.Blur = Clamp(double.Parse(val), 0, 100); break;
                        case "brightness": c.Brightness = Clamp(double.Parse(val), 0.1, 3.0); break;
                        case "scale": c.Scale = Clamp(double.Parse(val), 1.0, 2.0); break;
                        case "fit": c.Fit = (val == "contain") ? "contain" : "cover"; break;
                        case "position": case "pos":
                            c.Position = (val == "top" || val == "bottom") ? val : "center"; break;
                        case "zcodepath": c.ZcodePath = val; break;
                        case "port": c.Port = (int)Clamp(int.Parse(val), 1024, 65535); break;
                        case "launch": c.Launch = val != "0" && val.ToLowerInvariant() != "false"; break;
                    }
                }
                catch { }
            }
            return c;
        }

        static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    // ---- 一个已连接的 CDP 页面目标 ----
    public class Target : IDisposable
    {
        public string Id;
        public string Url;
        System.Net.WebSockets.ClientWebSocket ws;
        Dictionary<int, TaskCompletionSource<string>> waiters = new Dictionary<int, TaskCompletionSource<string>>();
        object gate = new object();
        int nextId = 0;
        CancellationTokenSource cts = new CancellationTokenSource();

        public static async Task<Target> Connect(string wsUrl)
        {
            Target t = new Target();
            t.ws = new System.Net.WebSockets.ClientWebSocket();
            await t.ws.ConnectAsync(new Uri(wsUrl), t.cts.Token);
            Task.Run(() => t.ReceiveLoop());
            return t;
        }

        async void ReceiveLoop()
        {
            byte[] buf = new byte[64 * 1024];
            try
            {
                while (!cts.IsCancellationRequested && ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    StringBuilder sb = new StringBuilder();
                    bool endOfMessage = false;
                    while (!endOfMessage)
                    {
                        var seg = new ArraySegment<byte>(buf);
                        var res = await ws.ReceiveAsync(seg, cts.Token);
                        if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        {
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                        endOfMessage = res.EndOfMessage;
                    }
                    string text = sb.ToString();
                    Match m = Regex.Match(text, "\"id\"\\s*:\\s*(\\d+)");
                    if (m.Success)
                    {
                        int id = int.Parse(m.Groups[1].Value);
                        TaskCompletionSource<string> tcs = null;
                        lock (gate)
                        {
                            if (waiters.TryGetValue(id, out tcs))
                            {
                                waiters.Remove(id);
                            }
                        }
                        if (tcs != null) tcs.TrySetResult(text);
                    }
                }
            }
            catch { }
        }

        public async Task<string> Send(string method, string jsonParams, int timeoutMs)
        {
            int id;
            TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
            lock (gate)
            {
                id = ++nextId;
                waiters[id] = tcs;
            }
            string msg = "{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + jsonParams + "}";
            byte[] bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);
            Task done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done == tcs.Task) return tcs.Task.Result;
            lock (gate) { waiters.Remove(id); }
            return "{\"error\":\"timeout\"}";
        }

        public async Task Inject(string source)
        {
            string jsonParams = "{\"source\":" + JsonQuote(source) + "}";
            await Send("Page.enable", "{}", 5000);
            await Send("Page.addScriptToEvaluateOnNewDocument", jsonParams, 10000);
            await Send("Runtime.evaluate", "{\"expression\":" + JsonQuote(source) + "}", 30000);
        }

        public static string JsonQuote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        public bool IsOpen()
        {
            return ws != null && ws.State == System.Net.WebSockets.WebSocketState.Open;
        }

        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            try { ws.Dispose(); } catch { }
        }
    }

    public class App : ApplicationContext
    {
        NotifyIcon tray;
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath;
        public Config cfg;
        Dictionary<string, Target> targets = new Dictionary<string, Target>();
        object gate = new object();
        System.Threading.Timer reloadDebounce;
        FileSystemWatcher configWatcher;
        SettingsForm settingsForm;
        bool firstRun;

        public App(bool openSettingsNow)
        {
            configPath = Path.Combine(exeDir, "config.txt");
            firstRun = !File.Exists(configPath);
            if (firstRun)
            {
                File.WriteAllText(configPath, Config.DefaultText(), new UTF8Encoding(true));
            }
            cfg = ConfigLoader.Load(configPath);
            EnsureShortcut();

            MenuItem miSettings = new MenuItem(L.T("设置(&S)...", "&Settings..."), delegate { OpenSettings(); });
            MenuItem miEdit = new MenuItem(L.T("编辑配置文件(&E)", "Edit config &file"), delegate { try { Process.Start("notepad.exe", configPath); } catch { } });
            MenuItem miReload = new MenuItem(L.T("重新加载配置", "&Reload config"), delegate { ScheduleReload(); });
            MenuItem miExit = new MenuItem(L.T("退出(&X)", "E&xit"), delegate { ExitApp(); });
            ContextMenu menu = new ContextMenu(new MenuItem[] {
                miSettings, new MenuItem("-"), miEdit, miReload, new MenuItem("-"), miExit });

            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Text = L.T("ZCode 壁纸 (双击打开设置)", "ZCode Wallpaper (double-click for settings)");
            tray.ContextMenu = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenSettings(); };

            reloadDebounce = new System.Threading.Timer(
                delegate { DoReload(); }, null, Timeout.Infinite, Timeout.Infinite);
            WatchConfig();

            if (openSettingsNow) OpenSettings();
            Task.Run((Action)Startup);
        }

        public void OpenSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(this);
            }
            settingsForm.Show();
            settingsForm.Activate();
        }

        // 设置界面或配置文件变更后调用: 保存到磁盘 + 防抖应用
        public void ConfigChanged()
        {
            SaveConfig();
            reloadDebounce.Change(400, Timeout.Infinite);
        }

        public void SaveConfig()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# ============ ZCode 壁纸配置 ============");
            sb.AppendLine("# 修改并保存本文件, 壁纸会立即刷新, 无需重启。");
            sb.AppendLine("# 图片路径 (清空则关闭壁纸)");
            sb.AppendLine("path=" + cfg.ImagePath);
            sb.AppendLine("opacity=" + cfg.Opacity.ToString("0.0##"));
            sb.AppendLine("blur=" + cfg.Blur.ToString("0.##"));
            sb.AppendLine("brightness=" + cfg.Brightness.ToString("0.##"));
            sb.AppendLine("scale=" + cfg.Scale.ToString("0.##"));
            sb.AppendLine("fit=" + cfg.Fit);
            sb.AppendLine("position=" + cfg.Position);
            sb.AppendLine("zcodePath=" + cfg.ZcodePath);
            sb.AppendLine("port=" + cfg.Port);
            try { File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }

        void ExitApp()
        {
            try { tray.Visible = false; } catch { }
            lock (gate)
            {
                foreach (Target t in targets.Values) { try { t.Dispose(); } catch { } }
                targets.Clear();
            }
            Application.Exit();
        }

        // ---- 启动流程 ----
        async void Startup()
        {
            try
            {
                string zcodeExe = ResolveZcodePath();
                if (zcodeExe == null)
                {
                    Fatal(L.T("未找到 ZCode。请在 config.txt 中填写 zcodePath=\\...\\ZCode.exe", "ZCode not found. Set zcodePath=\\...\\ZCode.exe in config.txt"));
                    return;
                }

                if (firstRun)
                {
                    Notify(L.T("欢迎使用 ZCode 壁纸!", "Welcome to ZCode Wallpaper!"), L.T("请点击下方提示打开设置, 选择一张图片, 壁纸立即生效。桌面快捷方式已创建。", "Open the settings window below, pick an image and the wallpaper applies instantly. A desktop shortcut has been created."), 6000);
                    OpenSettings();
                }

                // 端口已通 -> 附加; 否则按需启动 ZCode
                if (!await PortAlive(1500))
                {
                    bool zcodeRunning = Process.GetProcessesByName("ZCode").Length > 0;
                    if (zcodeRunning)
                    {
                        Notify(L.T("壁纸未生效", "Wallpaper not applied"), L.T("检测到 ZCode 已在运行(未带壁纸)。请先完全退出 ZCode(含托盘图标), 再通过桌面\"ZCode Wallpaper\"图标启动。", "ZCode is already running (without wallpaper). Fully quit ZCode first (including the tray icon), then launch via the \"ZCode Wallpaper\" desktop icon."), 10000);
                        await Task.Delay(15000); // 留时间展示气泡
                        ExitApp();
                        return;
                    }
                    if (cfg.Launch)
                    {
                        ProcessStartInfo si = new ProcessStartInfo();
                        si.FileName = zcodeExe;
                        si.Arguments = "--remote-debugging-port=" + cfg.Port;
                        si.UseShellExecute = true;
                        Process.Start(si);
                    }
                    if (!await PortAlive(60000))
                    {
                        Fatal(L.T("等待 ZCode 调试端口超时(", "Timed out waiting for the ZCode debug port (") + cfg.Port + L.T(")。", ")"));
                        return;
                    }
                }

                string imageDataUrl = LoadImageDataUrl();
                if (imageDataUrl == null && cfg.ImagePath.Length > 0)
                {
                    Notify(L.T("图片不存在", "Image not found"), L.T("config.txt 里的图片路径无效: ", "Invalid image path in config.txt: ") + cfg.ImagePath, 5000);
                }
                string payload = BuildPayload(imageDataUrl);
                currentPayload = payload;
                await AttachAll(payload);
                MonitorLoop();
            }
            catch (Exception ex)
            {
                Fatal(L.T("启动失败: ", "Startup failed: ") + ex.Message);
            }
        }

        async Task<bool> PortAlive(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    WebRequest req = WebRequest.Create("http://127.0.0.1:" + cfg.Port + "/json/version");
                    req.Timeout = 800;
                    var res = req.GetResponse();
                    try { res.Dispose(); } catch { }
                    return true;
                }
                catch { }
                await Task.Delay(300);
            }
            return false;
        }

        string ResolveZcodePath()
        {
            if (cfg.ZcodePath.Length > 0)
            {
                if (File.Exists(cfg.ZcodePath)) return cfg.ZcodePath;
                string candidate = Path.Combine(cfg.ZcodePath, "ZCode.exe");
                if (File.Exists(candidate)) return candidate;
            }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string p1 = Path.Combine(pf, "ZCode", "ZCode.exe");
            if (File.Exists(p1)) return p1;
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string p2 = Path.Combine(pf86, "ZCode", "ZCode.exe");
            if (File.Exists(p2)) return p2;
            string lp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ZCode", "ZCode.exe");
            if (File.Exists(lp)) return lp;
            try
            {
                foreach (RegistryKeyView view in new RegistryKeyView[] { RegistryKeyView.HKLM, RegistryKeyView.HKCU })
                {
                    using (var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        view == RegistryKeyView.HKLM ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser,
                        Microsoft.Win32.RegistryView.Registry64))
                    {
                        using (var un = key.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (un == null) continue;
                            foreach (string sub in un.GetSubKeyNames())
                            {
                                using (var k = un.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string name = Convert.ToString(k.GetValue("DisplayName"));
                                    if (name == null || !name.Contains("ZCode")) continue;
                                    string loc = Convert.ToString(k.GetValue("InstallLocation"));
                                    if (loc == null || loc.Length == 0) continue;
                                    string exe = Path.Combine(loc, "ZCode.exe");
                                    if (File.Exists(exe)) return exe;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        enum RegistryKeyView { HKLM, HKCU }

        string LoadImageDataUrl()
        {
            if (cfg.ImagePath.Length == 0 || !File.Exists(cfg.ImagePath)) return null;
            string ext = Path.GetExtension(cfg.ImagePath).ToLowerInvariant();
            string mime = "image/png";
            if (ext == ".jpg" || ext == ".jpeg") mime = "image/jpeg";
            else if (ext == ".gif") mime = "image/gif";
            else if (ext == ".webp") mime = "image/webp";
            else if (ext == ".bmp") mime = "image/bmp";
            return "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(cfg.ImagePath));
        }

        // ---- 注入源码 ----
        const string ApplyJs = @"(function () {
  function apply(cfg) {
    var old = document.getElementById('zcode-wallpaper');
    if (old) old.remove();
    if (!cfg || !cfg.src) return 'removed';
    if (!document.documentElement) return 'no document';
    var q = String.fromCharCode(34);
    var posY = cfg.posY || 'center';
    var bg = document.createElement('div');
    bg.id = 'zcode-wallpaper';
    bg.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:none;'
      + 'background-image:url(' + q + cfg.src + q + ');'
      + 'background-size:' + (cfg.fit || 'cover') + ';'
      + 'background-position:center ' + posY + ';'
      + 'background-repeat:no-repeat;'
      + 'opacity:' + (cfg.opacity != null ? cfg.opacity : 0.25) + ';'
      + 'filter:blur(' + (cfg.blur || 0) + 'px) brightness(' + (cfg.brightness != null ? cfg.brightness : 1) + ');'
      + 'transform:scale(' + (cfg.scale || 1.08) + ');';
    document.documentElement.appendChild(bg);
    return 'ok';
  }
  if (typeof window !== 'undefined') window.__ZCW_APPLY__ = apply;
  var zcwCfg = __CONFIG__;
  if (document.documentElement) { apply(zcwCfg); }
  else { document.addEventListener('DOMContentLoaded', function () { apply(zcwCfg); }); }
})();";

        string BuildPayload(string imageDataUrl)
        {
            StringBuilder json = new StringBuilder("{");
            if (imageDataUrl != null)
            {
                json.Append("\"src\":" + Target.JsonQuote(imageDataUrl) + ",");
            }
            json.Append("\"opacity\":" + cfg.Opacity.ToString("0.0##"));
            json.Append(",\"blur\":" + cfg.Blur.ToString("0.##"));
            json.Append(",\"brightness\":" + cfg.Brightness.ToString("0.##"));
            json.Append(",\"scale\":" + cfg.Scale.ToString("0.##"));
            json.Append(",\"fit\":\"" + cfg.Fit + "\"");
            json.Append(",\"posY\":\"" + cfg.Position + "\"}");
            return ApplyJs.Replace("__CONFIG__", json.ToString());
        }

        // ---- 目标扫描 / 附加 / 监控 ----
        class PageTarget { public string Id; public string WsUrl; public string Url; }

        List<PageTarget> ListPageTargets()
        {
            List<PageTarget> list = new List<PageTarget>();
            string body = HttpGet("http://127.0.0.1:" + cfg.Port + "/json/list", 4000);
            if (body == null) return list;
            foreach (Match obj in Regex.Matches(body, "\\{[^{}]*\\}"))
            {
                string s = obj.Value;
                if (!Regex.IsMatch(s, "\"type\"\\s*:\\s*\"page\"")) continue;
                Match ws = Regex.Match(s, "\"webSocketDebuggerUrl\"\\s*:\\s*\"([^\"]+)\"");
                Match id = Regex.Match(s, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (!ws.Success) continue;
                PageTarget pt = new PageTarget();
                pt.WsUrl = ws.Groups[1].Value;
                pt.Id = id.Success ? id.Groups[1].Value : pt.WsUrl;
                Match u = Regex.Match(s, "\"url\"\\s*:\\s*\"([^\"]+)\"");
                pt.Url = u.Success ? u.Groups[1].Value : "";
                list.Add(pt);
            }
            return list;
        }

        string HttpGet(string url, int timeoutMs)
        {
            try
            {
                WebRequest req = WebRequest.Create(url);
                req.Timeout = timeoutMs;
                using (var res = req.GetResponse())
                using (var stream = res.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        async Task AttachAll(string payload)
        {
            foreach (PageTarget pt in ListPageTargets())
            {
                if (targets.ContainsKey(pt.Id)) continue;
                try
                {
                    Target t = await Target.Connect(pt.WsUrl);
                    await t.Inject(payload);
                    lock (gate) { targets[pt.Id] = t; }
                }
                catch { }
            }
        }

        async void MonitorLoop()
        {
            while (true)
            {
                await Task.Delay(4000);
                try
                {
                    string payload = currentPayload;
                    if (payload == null) continue;
                    await AttachAll(payload);
                }
                catch { }
                // ZCode 已退出 -> 注入器退出
                bool alive = false;
                try
                {
                    alive = HttpGet("http://127.0.0.1:" + cfg.Port + "/json/version", 1500) != null;
                }
                catch { }
                if (!alive)
                {
                    bool zcodeRunning = Process.GetProcessesByName("ZCode").Length > 0;
                    if (!zcodeRunning)
                    {
                        ExitApp();
                        return;
                    }
                }
            }
        }

        string currentPayload;

        // ---- 配置热更新 ----
        void WatchConfig()
        {
            configWatcher = new FileSystemWatcher(exeDir, "config.txt");
            configWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            configWatcher.Changed += delegate { reloadDebounce.Change(600, Timeout.Infinite); };
            try { configWatcher.EnableRaisingEvents = true; } catch { }
        }

        async void DoReload()
        {
            try
            {
                Config fresh = ConfigLoader.Load(configPath);
                string newImagePath = fresh.ImagePath;
                cfg = fresh;
                string imageDataUrl = LoadImageDataUrl();
                if (imageDataUrl == null && newImagePath.Length > 0)
                {
                    Notify(L.T("图片不存在", "Image not found"), L.T("config.txt 里的图片路径无效: ", "Invalid image path in config.txt: ") + newImagePath, 5000);
                }
                string payload = BuildPayload(imageDataUrl);
                currentPayload = payload;
                List<PageTarget> freshList = ListPageTargets();
                // 端口没通时(比如 ZCode 没在跑) 尝试启动
                if (freshList.Count == 0 && !await PortAlive(1500))
                {
                    return;
                }
                foreach (PageTarget pt in freshList)
                {
                    Target existing = null;
                    lock (gate) { targets.TryGetValue(pt.Id, out existing); }
                    try
                    {
                        if (existing != null && existing.IsOpen())
                        {
                            await existing.Inject(payload);
                        }
                        else
                        {
                            Target t = await Target.Connect(pt.WsUrl);
                            await t.Inject(payload);
                            lock (gate) { targets[pt.Id] = t; }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        void ScheduleReload() { reloadDebounce.Change(100, Timeout.Infinite); }

        // ---- 托盘 / 快捷方式 ----
        void Notify(string title, string text, int ms)
        {
            try { tray.BalloonTipTitle = title; tray.BalloonTipText = text; tray.ShowBalloonTip(ms); } catch { }
        }

        void Fatal(string msg)
        {
            MessageBox.Show(msg, L.T("ZCode 壁纸", "ZCode Wallpaper"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ExitApp();
        }

        void EnsureShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, "ZCode Wallpaper.lnk");
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object lnk = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lnkType = lnk.GetType();
                lnkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk,
                    new object[] { Path.Combine(exeDir, "ZCodeWallpaper.exe") });
                lnkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk,
                    new object[] { exeDir });
                lnkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, lnk,
                    new object[] { "带壁纸的 ZCode 启动器" });
                lnkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, new object[0]);
                string legacyLnk = Path.Combine(desktop, "ZCode 壁纸版.lnk");
                if (File.Exists(legacyLnk)) { try { File.Delete(legacyLnk); } catch { } }
            }
            catch { }
        }
    }

    public class SettingsForm : Form
    {
        App app;
        TextBox txtPath;
        Label lblOpacity, lblBlur, lblBrightness, lblScale;
        TrackBar trkOpacity, trkBlur, trkBrightness, trkScale;
        ComboBox cmbFit, cmbPos;
        System.Windows.Forms.Timer uiDebounce;

        public SettingsForm(App app)
        {
            this.app = app;
            Text = L.T("ZCode 壁纸设置", "ZCode Wallpaper Settings");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(470, 430);
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label l1 = new Label();
            l1.Text = L.T("图片路径", "Image path");
            l1.Location = new Point(12, 12);
            l1.AutoSize = true;
            Controls.Add(l1);

            txtPath = new TextBox();
            txtPath.Location = new Point(12, 32);
            txtPath.Size = new Size(352, 23);
            txtPath.Text = app.cfg.ImagePath;
            txtPath.Leave += delegate { CommitPath(); };
            Controls.Add(txtPath);

            Button btnBrowse = new Button();
            btnBrowse.Text = L.T("浏览...", "Browse...");
            btnBrowse.Location = new Point(372, 31);
            btnBrowse.Size = new Size(84, 25);
            btnBrowse.Click += delegate { Browse(); };
            Controls.Add(btnBrowse);

            lblOpacity = MakeLabel(L.T("不透明度", "Opacity"), 72);
            trkOpacity = MakeTrackbar(92, 2, 100, (int)Math.Round(app.cfg.Opacity * 100));
            lblBlur = MakeLabel(L.T("模糊度", "Blur"), 150);
            trkBlur = MakeTrackbar(170, 0, 40, (int)app.cfg.Blur);
            lblBrightness = MakeLabel(L.T("亮度", "Brightness"), 228);
            trkBrightness = MakeTrackbar(248, 50, 150, (int)Math.Round(app.cfg.Brightness * 100));
            lblScale = MakeLabel(L.T("缩放", "Scale"), 306);
            trkScale = MakeTrackbar(326, 100, 150, (int)Math.Round(app.cfg.Scale * 100));

            Label lblFit = new Label();
            lblFit.Text = L.T("填充:", "Fit:");
            lblFit.Location = new Point(12, 392);
            lblFit.AutoSize = true;
            Controls.Add(lblFit);

            cmbFit = new ComboBox();
            cmbFit.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFit.Items.Add(L.T("cover (铺满)", "cover (fill window)"));
            cmbFit.Items.Add(L.T("contain (完整显示)", "contain (whole image)"));
            cmbFit.SelectedIndex = app.cfg.Fit == "contain" ? 1 : 0;
            cmbFit.Location = new Point(58, 388);
            cmbFit.Size = new Size(140, 25);
            cmbFit.SelectedIndexChanged += delegate { CommitAll(); };
            Controls.Add(cmbFit);

            Label lblPos = new Label();
            lblPos.Text = L.T("位置:", "Position:");
            lblPos.Location = new Point(212, 392);
            lblPos.AutoSize = true;
            Controls.Add(lblPos);

            cmbPos = new ComboBox();
            cmbPos.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPos.Items.Add(L.T("上", "Top"));
            cmbPos.Items.Add(L.T("中", "Center"));
            cmbPos.Items.Add(L.T("下", "Bottom"));
            cmbPos.SelectedIndex = app.cfg.Position == "top" ? 0 : (app.cfg.Position == "bottom" ? 2 : 1);
            cmbPos.Location = new Point(252, 388);
            cmbPos.Size = new Size(56, 25);
            cmbPos.SelectedIndexChanged += delegate { CommitAll(); };
            Controls.Add(cmbPos);

            Button btnDefault = new Button();
            btnDefault.Text = L.T("恢复默认", "Reset");
            btnDefault.Location = new Point(318, 387);
            btnDefault.Size = new Size(72, 27);
            btnDefault.Click += delegate { ResetDefaults(); };
            Controls.Add(btnDefault);

            Button btnClose = new Button();
            btnClose.Text = L.T("关闭", "Close");
            btnClose.Location = new Point(396, 387);
            btnClose.Size = new Size(60, 27);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            uiDebounce = new System.Windows.Forms.Timer();
            uiDebounce.Interval = 350;
            uiDebounce.Tick += delegate { uiDebounce.Stop(); CommitAll(); };

            UpdateLabels();
        }

        Label MakeLabel(string text, int y)
        {
            Label l = new Label();
            l.Location = new Point(12, y);
            l.AutoSize = true;
            Controls.Add(l);
            return l;
        }

        TrackBar MakeTrackbar(int y, int min, int max, int value)
        {
            TrackBar t = new TrackBar();
            t.Location = new Point(12, y);
            t.Size = new Size(444, 40);
            t.Minimum = min;
            t.Maximum = max;
            t.TickStyle = TickStyle.None;
            t.Value = Math.Max(min, Math.Min(max, value));
            t.ValueChanged += delegate { OnSliderChanged(); };
            Controls.Add(t);
            return t;
        }

        void OnSliderChanged()
        {
            UpdateLabels();
            uiDebounce.Stop();
            uiDebounce.Start();
        }

        void UpdateLabels()
        {
            lblOpacity.Text = L.T("不透明度: ", "Opacity: ") + trkOpacity.Value + "% " + L.T("(越小壁纸越淡, 界面越清楚)", "(lower = fainter wallpaper, clearer UI)");
            lblBlur.Text = L.T("模糊度: ", "Blur: ") + trkBlur.Value + " px " + L.T("(只模糊壁纸)", "(wallpaper only)");
            lblBrightness.Text = L.T("亮度: ", "Brightness: ") + trkBrightness.Value + "%";
            lblScale.Text = L.T("缩放: ", "Scale: ") + trkScale.Value + "%";
        }

        void CommitPath()
        {
            string p = txtPath.Text.Trim();
            if (p != app.cfg.ImagePath)
            {
                app.cfg.ImagePath = p;
                app.ConfigChanged();
            }
        }

        void CommitAll()
        {
            CommitPath();
            app.cfg.Opacity = trkOpacity.Value / 100.0;
            app.cfg.Blur = trkBlur.Value;
            app.cfg.Brightness = trkBrightness.Value / 100.0;
            app.cfg.Scale = trkScale.Value / 100.0;
            app.cfg.Fit = cmbFit.SelectedIndex == 1 ? "contain" : "cover";
            app.cfg.Position = cmbPos.SelectedIndex == 0 ? "top" : (cmbPos.SelectedIndex == 2 ? "bottom" : "center");
            app.ConfigChanged();
        }

        void ResetDefaults()
        {
            trkOpacity.Value = 25;
            trkBlur.Value = 12;
            trkBrightness.Value = 100;
            trkScale.Value = 108;
            cmbFit.SelectedIndex = 0;
            cmbPos.SelectedIndex = 1;
            UpdateLabels();
            CommitAll();
        }

        void Browse()
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = L.T("选择壁纸图片", "Choose a wallpaper image");
            dlg.Filter = L.T("图片文件", "Image files") + "|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|" + L.T("所有文件", "All files") + "|*.*";
            dlg.CheckFileExists = true;
            if (Directory.Exists(app.cfg.ImagePath))
            {
                dlg.InitialDirectory = app.cfg.ImagePath;
            }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                txtPath.Text = dlg.FileName;
                CommitPath();
                UpdateLabels();
            }
        }
    }

    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (Mutex m = new Mutex(true, "Local\\ZCodeWallpaper.SingleInstance", out created))
            {
                if (!created) return;
                SetProcessDPIAware();
                bool openSettings = args != null && args.Length > 0 && args[0] == "--settings";
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(openSettings));
            }
        }
    }
}
