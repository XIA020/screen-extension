using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using TabletDisplay;

internal sealed class ControlPanel : IDisposable
{
    private readonly HttpListener server = new HttpListener();
    private readonly CameraPreview owner;
    private readonly NearbyDevices nearby = new NearbyDevices();
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly NotifyIcon tray;
    private readonly Icon trayIcon;
    private string existingSunshineAuthorization;
    private readonly System.Windows.Forms.Timer lease;
    private readonly System.Threading.Timer displayTimer;
    private readonly object displayLifecycle = new object();
    private readonly DateTime[] idleSince = new DateTime[2];
    private readonly DateTime[] captureChecked = new DateTime[2];
    private readonly Rectangle[] lastCapture = new Rectangle[2];
    private readonly object captureLock = new object();
    private readonly CaptureRecovery[] recoveries = { new CaptureRecovery(), new CaptureRecovery() };
    private readonly string primaryLog;
    private string primaryTarget;
    private long lastPage;
    private const string Origin = "http://127.0.0.1:47998";
    private static readonly string SecondaryRuntimeDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabletDisplay", "second-display");
    private static readonly string DeviceNamesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabletDisplay", "device-names.json");
    private static readonly string PrimaryTargetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabletDisplay", "primary-target");

    public ControlPanel(CameraPreview owner)
    {
        this.owner = owner;
        if (IsManagedDisplay())
        {
            var profile = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed.json")));
            if (!profile.TryGetValue("primaryTarget", out primaryTarget) && File.Exists(PrimaryTargetPath))
                primaryTarget = File.ReadAllText(PrimaryTargetPath);
            string config;
            if (profile.TryGetValue("sunshineConfig", out config))
                primaryLog = Path.Combine(Path.GetDirectoryName(config), "sunshine.log");
        }
        else primaryLog = @"D:\APP\chuanliu\Sunshine2\config\sunshine.log";
        server.Prefixes.Add(Origin + "/");
        server.Start();
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开控制面板", null, (sender, args) => Open());
        menu.Items.Add("配对管理", null, (sender, args) => Open("#paired-devices"));
        menu.Items.Add("退出控制面板", null, (sender, args) => owner.Dispose());
        trayIcon = Icon.ExtractAssociatedIcon(typeof(ControlPanel).Assembly.Location);
        tray = new NotifyIcon {
            Icon = trayIcon ?? SystemIcons.Application, Text = "屏幕扩展控制台", Visible = true, ContextMenuStrip = menu
        };
        tray.DoubleClick += (sender, args) => Open();
        lease = new System.Windows.Forms.Timer { Interval = 500 };
        lease.Tick += (sender, args) => {
            if (owner.CameraEnabled && System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref lastPage) > System.Diagnostics.Stopwatch.Frequency * 6)
                owner.StopCameras();
        };
        lease.Start();
        displayTimer = new System.Threading.Timer(CheckIdleDisplays, null, 2000, 2000);
        new Thread(Listen) { IsBackground = true }.Start();
    }

    internal void Open(string fragment = "")
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PageUrl + fragment) { UseShellExecute = true });
    }

    public string PageUrl { get { return Origin + "/?key=" + token; } }

    private void Listen()
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try { context = server.GetContext(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            string route = context.Request.Url.AbsolutePath;
            bool isPairingRequest = route == "/pair" || route == "/paired-devices";
            if (isPairingRequest || context.Request.HttpMethod == "POST" &&
                (route == "/invite" || route == "/second-display" || route == "/second-display/stop" ||
                 route == "/devices/scan" || route == "/devices/connect"))
            {
                ThreadPool.QueueUserWorkItem(ignored => Respond(context));
                continue;
            }
            Respond(context);
        }
    }

    private void Respond(HttpListenerContext context)
    {
        using (var response = context.Response)
        {
            try { Handle(context); }
            catch (Exception) { response.StatusCode = 500; }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' blob:; frame-ancestors 'none'";
        if (request.UserHostName != "127.0.0.1:47998" || request.QueryString["key"] != token ||
            (request.Headers["Origin"] != null && request.Headers["Origin"] != Origin))
        {
            response.StatusCode = 403;
            return;
        }

        string route = request.Url.AbsolutePath;
        bool secondDisplay = request.QueryString["screen"] == "2";
        int slot = secondDisplay ? 2 : 1;
        if (request.HttpMethod == "GET")
        {
            switch (route)
            {
                case "/":
                    Reply(response, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(typeof(ControlPanel).Assembly.Location), "ControlPanel.html")), "text/html; charset=utf-8");
                    return;
                case "/state":
                    Interlocked.Exchange(ref lastPage, System.Diagnostics.Stopwatch.GetTimestamp());
                    string secondaryDisplay = ReadSecondaryDisplay();
                    string display = ReadPrimaryDisplay(secondaryDisplay);
                    var main = ScreenArrangement.Read(Screen.PrimaryScreen.DeviceName);
                    int refreshRate;
                    var second = ScreenArrangement.Read(display, out refreshRate);
                    var primaryDevice = owner.GetDeviceState(1);
                    var secondaryDevice = owner.GetDeviceState(2);
                    if (primaryDevice.Available) nearby.OnConnected(1, primaryDevice.Id);
                    if (secondaryDevice.Available) nearby.OnConnected(2, secondaryDevice.Id);
                    var state = DeviceProperties(primaryDevice);
                    var state2 = DeviceProperties(secondaryDevice);
                    state["displayMode"] = ReadDisplayMode(1, primaryDevice, display, secondaryDisplay, main);
                    state2["displayMode"] = ReadDisplayMode(2, secondaryDevice, secondaryDisplay, secondaryDisplay, main);
                    state["secondary"] = state2;
                    state["address"] = owner.LocalAddress;
                    state["refreshRate"] = refreshRate;
                    state["main"] = Rect(main);
                    state["extended"] = Rect(second);
                    state["display"] = display;
                    state["canRepositionIdleDisplays"] = true;
                    state["layout"] = ReadDesktopLayout(display, secondaryDisplay, primaryDevice.Name, secondaryDevice.Name);
                    Reply(response, new JavaScriptSerializer().Serialize(state));
                    return;
                case "/frame":
                    byte[] frame = owner.GetFrame(slot);
                    if (frame == null) response.StatusCode = 204;
                    else Reply(response, frame, "image/jpeg");
                    return;
                case "/resolution":
                    HandleResolution(request, response, slot, false);
                    return;
                case "/pair":
                    RelayPairing(response, null, secondDisplay: secondDisplay);
                    return;
                case "/paired-devices":
                    RelayPairing(response, null, upstreamPath: "/api/clients/list", secondDisplay: secondDisplay);
                    return;
                case "/paired-devices/names":
                    Reply(response, new JavaScriptSerializer().Serialize(ReadDeviceNames()));
                    return;
            }
        }
        else if (request.HttpMethod == "POST")
        {
            switch (route)
            {
                case "/devices/scan":
                    ReplyDeviceAction(response, () => new { devices = nearby.Scan(IPAddress.Parse(owner.LocalAddress),
                        request.QueryString["transport"] == "usb") });
                    return;
                case "/devices/connect":
                    Guid discoveredId;
                    if (!Guid.TryParseExact(request.QueryString["id"], "D", out discoveredId))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    ReplyDeviceAction(response, () => new {
                        slot = nearby.Connect(discoveredId, IsSlotOccupied, PrepareSlot)
                    });
                    return;
                case "/invite":
                    LaunchInvitation(response);
                    return;
                case "/second-display":
                    RunDisplayScript(response, "Start-SecondDisplay.ps1", "");
                    return;
                case "/second-display/stop":
                    RunDisplayScript(response, "Start-SecondDisplay.ps1", "-Stop");
                    return;
                case "/paired-devices/name":
                    Guid namedDevice;
                    string displayName = (request.QueryString["name"] ?? "").Trim();
                    if (!Guid.TryParseExact(request.QueryString["id"], "D", out namedDevice) ||
                        displayName.Length == 0 || displayName.Length > 40 || Regex.IsMatch(displayName, @"[\p{Cc}]"))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    var names = ReadDeviceNames();
                    names[namedDevice.ToString("D")] = displayName;
                    Directory.CreateDirectory(Path.GetDirectoryName(DeviceNamesPath));
                    File.WriteAllText(DeviceNamesPath, new JavaScriptSerializer().Serialize(names), Encoding.UTF8);
                    response.StatusCode = 204;
                    return;
                case "/paired-devices/unpair":
                    Guid deviceId;
                    if (!Guid.TryParseExact(request.QueryString["id"], "D", out deviceId))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    RelayPairing(response, new JavaScriptSerializer().Serialize(new {
                        uuid = request.QueryString["id"]
                    }), upstreamPath: "/api/clients/unpair", secondDisplay: secondDisplay);
                    return;
                case "/pair-access":
                    if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "existing-sunshine.json")) ||
                        request.ContentLength64 <= 0 || request.ContentLength64 > 4096)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    System.Collections.Specialized.NameValueCollection credentials;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        credentials = System.Web.HttpUtility.ParseQueryString(reader.ReadToEnd());
                    string username = credentials["username"] ?? "";
                    string password = credentials["password"] ?? "";
                    if (username.Length == 0 || username.Contains(":") || password.Length == 0)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    RelayPairing(response, null, BasicAuthorization(username + ":" + password));
                    return;
                case "/pair":
                    if (request.ContentLength64 <= 0 || request.ContentLength64 > 1024)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    System.Collections.Specialized.NameValueCollection fields;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        fields = System.Web.HttpUtility.ParseQueryString(reader.ReadToEnd());
                    string pin = fields["pin"] ?? "";
                    string id = fields["id"] ?? "";
                    string pairingName = (fields["name"] ?? "").Trim();
                    if (!Regex.IsMatch(pin, @"\A[0-9]{4}\z") || !Regex.IsMatch(id, @"\A[0-9a-fA-F]{32}\z") ||
                        pairingName.Length > 40 || Regex.IsMatch(pairingName, @"[\p{Cc}]"))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    RelayPairing(response, new JavaScriptSerializer().Serialize(new {
                        pin = pin, pairing_id = id, name = pairingName.Length == 0 ? "未命名设备" : pairingName
                    }), secondDisplay: secondDisplay);
                    return;
                case "/camera":
                    int command;
                    if (!int.TryParse(request.QueryString["command"], out command) || command < 0 || command > 3)
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    Interlocked.Exchange(ref lastPage, System.Diagnostics.Stopwatch.GetTimestamp());
                    response.StatusCode = owner.SetCamera(slot, command) ? 204 : 409;
                    return;
                case "/audio":
                    bool play;
                    if (!bool.TryParse(request.QueryString["enabled"], out play))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    response.StatusCode = owner.SetAudioMode(slot, play) ? 204 : 409;
                    return;
                case "/disconnect":
                    Guid disconnectId;
                    if (!Guid.TryParseExact(request.QueryString["id"], "D", out disconnectId))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    response.StatusCode = owner.Disconnect(slot, disconnectId.ToString("D")) ? 204 : 409;
                    return;
                case "/touch":
                    bool enabled;
                    if (!bool.TryParse(request.QueryString["trackpad"], out enabled))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    response.StatusCode = owner.SetTrackpadMode(slot, enabled) ? 204 : 409;
                    return;
                case "/stream":
                    int width, height, fps;
                    if (!int.TryParse(request.QueryString["width"], out width) ||
                        !int.TryParse(request.QueryString["height"], out height) ||
                        !int.TryParse(request.QueryString["fps"], out fps) ||
                        !((width == 1280 && height == 720) || (width == 1920 && height == 1080) ||
                          (width == 2560 && height == 1440)) || (fps != 30 && fps != 60))
                    {
                        response.StatusCode = 400;
                        return;
                    }
                    response.StatusCode = owner.SetStreamMode(slot, width, height, fps) ? 204 : 409;
                    return;
                case "/layout":
                    lock (displayLifecycle) ApplyDesktopLayout(request, response);
                    return;
                case "/resolution":
                    HandleResolution(request, response, slot, true);
                    return;
            }
        }
        response.StatusCode = 404;
    }

    private sealed class SecondaryRuntime
    {
        public string target { get; set; }
        public int processId { get; set; }
        public string display { get; set; }
        public string displayId { get; set; }
    }

    private static string ReadSecondaryDisplay()
    {
        try
        {
            string path = Path.Combine(SecondaryRuntimeDirectory, "runtime.json");
            if (!File.Exists(path)) return null;
            var runtime = new JavaScriptSerializer().Deserialize<SecondaryRuntime>(File.ReadAllText(path));
            Guid captureId;
            if (runtime == null || runtime.processId <= 0 || string.IsNullOrWhiteSpace(runtime.display) ||
                !Guid.TryParse(runtime.displayId, out captureId)) return null;
            if (!string.IsNullOrEmpty(runtime.target))
                return TabletDisplay.DisplayPaths.ActiveVirtualDisplay(runtime.target);
            using (var process = Process.GetProcessById(runtime.processId))
            {
                string expected = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed.json"))
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "TabletDisplaySunshine", "sunshine.exe")
                    : @"D:\APP\chuanliu\Sunshine2\sunshine.exe";
                return string.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase) &&
                    process.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(path) ? runtime.display : null;
            }
        }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private string ReadPrimaryDisplay(string secondaryDisplay)
    {
        if (primaryTarget != null)
            return TabletDisplay.DisplayPaths.ActiveVirtualDisplay(primaryTarget);
        string display = ScreenArrangement.FindExtendedDisplay();
        if (secondaryDisplay == null || !string.Equals(display, secondaryDisplay, StringComparison.OrdinalIgnoreCase))
            return display;
        var candidates = ScreenArrangement.MovableDisplays()
            .Where(candidate => !string.Equals(candidate, secondaryDisplay, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private Rectangle ReadStreamCapture(int slot)
    {
        lock (captureLock) return ReadStreamCaptureCore(slot);
    }

    private Rectangle ReadStreamCaptureCore(int slot)
    {
        int index = slot - 1;
        DateTime now = DateTime.UtcNow;
        if (now - captureChecked[index] < TimeSpan.FromSeconds(1)) return lastCapture[index];
        captureChecked[index] = now;
        lastCapture[index] = Rectangle.Empty;
        string log = slot == 1 ? primaryLog : Path.Combine(SecondaryRuntimeDirectory, "sunshine.log");
        if (string.IsNullOrEmpty(log)) return Rectangle.Empty;
        try
        {
            using (var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                file.Seek(Math.Max(0L, file.Length - 65536L), SeekOrigin.Begin);
                string tail = reader.ReadToEnd();
                int connected = tail.LastIndexOf("CLIENT CONNECTED", StringComparison.Ordinal);
                int disconnected = tail.LastIndexOf("CLIENT DISCONNECTED", StringComparison.Ordinal);
                if (connected < 0 || disconnected > connected) return Rectangle.Empty;
                var captures = Regex.Matches(tail.Substring(connected),
                    @"Capture size\s*:\s*(\d+)x(\d+)\s+Offset\s*:\s*(-?\d+)x(-?\d+)");
                if (captures.Count == 0) return Rectangle.Empty;
                var values = captures[captures.Count - 1].Groups;
                lastCapture[index] = new Rectangle(int.Parse(values[3].Value), int.Parse(values[4].Value),
                    int.Parse(values[1].Value), int.Parse(values[2].Value));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lastCapture[index];
    }

    private string ReadDisplayMode(int slot, CameraPreview.DeviceState device, string source, string second, Rectangle main)
    {
        if (!device.Available && !device.Legacy)
            return null;
        Rectangle independent = ScreenArrangement.Read(source);
        bool extended = !independent.IsEmpty && !independent.IntersectsWith(main) &&
            ScreenArrangement.MovableDisplays().Contains(source);
        Rectangle capture = ReadStreamCapture(slot);
        if (!extended)
        {
            bool mtt = slot == 2 || IsManagedDisplay();
            string excluded = slot == 1 ? DisplayPaths.VirtualTarget(second, true) : primaryTarget;
            if (DisplayPaths.HasClonedVirtualOutput(mtt, slot == 1 ? primaryTarget : null, excluded)) return "duplicate";
        }
        if (!capture.IsEmpty && capture == main) return "primary";
        if (extended && (capture.IsEmpty || capture == independent)) return "extended";
        return capture.IsEmpty ? "unknown" : "other";
    }

    private static Dictionary<string, object> DeviceProperties(CameraPreview.DeviceState device)
    {
        return new Dictionary<string, object> {
            { "available", device.Available }, { "legacy", device.Legacy }, { "device", device.Name },
            { "deviceId", device.Id }, { "canDisconnect", device.CanDisconnect }, { "usesUsbNetwork", device.UsesUsbNetwork },
            { "enabled", device.Enabled }, { "switchable", device.Switchable },
            { "receiving", device.Receiving }, { "denied", device.Denied }, { "trackpad", device.Trackpad },
            { "audio", device.Audio }, { "savedAudio", device.SavedAudio },
            { "activeMode", Mode(device.Active) }, { "savedMode", Mode(device.Saved) }
        };
    }

    private static void ReplyDeviceAction(HttpListenerResponse response, Func<object> action)
    {
        try { Reply(response, new JavaScriptSerializer().Serialize(action())); }
        catch (InvalidOperationException error)
        {
            response.StatusCode = 409;
            Reply(response, new JavaScriptSerializer().Serialize(new { error = error.Message }));
        }
        catch (System.Net.Sockets.SocketException)
        {
            response.StatusCode = 503;
            Reply(response, "{\"error\":\"暂时无法访问设备所在网络。\"}");
        }
    }

    private bool IsSlotOccupied(int slot)
    {
        var state = owner.GetDeviceState(slot);
        return state.Available || state.Legacy || HasActiveStreamTransport(slot);
    }

    private static bool HasActiveStreamTransport(int slot)
    {
        // The host can keep its Desktop application open after the viewer
        // disconnects. The video/control/audio UDP listeners track streaming.
        int videoPort = slot == 1 ? 47998 : 49998;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Any(listener => listener.Port >= videoPort && listener.Port <= videoPort + 2);
        }
        catch (NetworkInformationException) { return true; }
    }

    private static XmlElement ReadHostInfo(int slot)
    {
        var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" +
            (slot == 1 ? "47989" : "49989") + "/serverinfo");
        request.Proxy = null;
        request.Timeout = request.ReadWriteTimeout = 600;
        try
        {
            using (var response = request.GetResponse())
            using (var body = response.GetResponseStream())
            {
                var xml = new XmlDocument { XmlResolver = null };
                xml.Load(body);
                return xml.DocumentElement;
            }
        }
        catch (WebException) { return null; }
        catch (XmlException) { return null; }
    }

    private string FindPrimaryTarget()
    {
        if (primaryTarget != null) return primaryTarget;
        string display = ReadPrimaryDisplay(ReadSecondaryDisplay());
        primaryTarget = TabletDisplay.DisplayPaths.VirtualTarget(display, IsManagedDisplay());
        if (primaryTarget != null && IsManagedDisplay())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrimaryTargetPath));
                File.WriteAllText(PrimaryTargetPath, primaryTarget);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return primaryTarget;
    }

    private static bool IsManagedDisplay()
    {
        return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "managed.json"));
    }

    private void CheckIdleDisplays(object ignored)
    {
        if (!Monitor.TryEnter(displayLifecycle)) return;
        try
        {
            string second = ReadSecondaryDisplay();
            bool managed = IsManagedDisplay();
            for (int slot = 1; slot <= 2; slot++)
            {
                var device = owner.GetDeviceState(slot);
                if (device.Available) nearby.OnConnected(slot, device.Id);
                CheckCaptureRecovery(slot, device, slot == 1 ? ReadPrimaryDisplay(second) : second, managed);
                bool exists = slot == 1
                    ? ReadPrimaryDisplay(second) != null || !managed && TabletDisplay.DisplayPaths.AvailableVirtualCount(false) > 0
                    : File.Exists(Path.Combine(SecondaryRuntimeDirectory, "runtime.json")) ||
                        !managed && TabletDisplay.DisplayPaths.AvailableMttCount() > 0;
                int index = slot - 1;
                if (!exists || nearby.HasPendingConnection(slot) || IsSlotOccupied(slot))
                {
                    idleSince[index] = DateTime.MinValue;
                    continue;
                }
                DateTime now = DateTime.UtcNow;
                if (idleSince[index] == DateTime.MinValue) idleSince[index] = now;
                if (now - idleSince[index] < TimeSpan.FromSeconds(8)) continue;
                idleSince[index] = now;
                if (slot == 1)
                {
                    FindPrimaryTarget();
                    if (managed) TabletDisplay.DisplayPaths.DisableVirtual(ReadPrimaryDisplay(second), true);
                    else
                    {
                        var result = ExecuteDisplayScript("launch.ps1", "-ReleaseDisplay");
                        if (result.status) primaryTarget = null;
                        else Trace.TraceWarning(result.error);
                    }
                }
                else
                {
                    var result = ExecuteDisplayScript("Start-SecondDisplay.ps1", "-Stop -IfIdle");
                    if (!result.status) Trace.TraceWarning(result.error);
                }
            }
        }
        catch (Exception ex) { Trace.TraceWarning(ex.Message); }
        finally { Monitor.Exit(displayLifecycle); }
    }

    private sealed class CaptureRecovery
    {
        public string DeviceId, Display;
        public Rectangle Bounds;
        public DateTime NextAttempt;
        public int Attempts;
    }

    private void CheckCaptureRecovery(int slot, CameraPreview.DeviceState device, string display, bool managed)
    {
        var recovery = recoveries[slot - 1];
        Rectangle main = ScreenArrangement.Read(Screen.PrimaryScreen.DeviceName);
        Rectangle bounds = ScreenArrangement.Read(display);
        if (!device.CanSelectDisplay || string.IsNullOrEmpty(device.Id) || bounds.IsEmpty || main.IsEmpty ||
            bounds.IntersectsWith(main) || !ScreenArrangement.MovableDisplays().Contains(display) ||
            ReadStreamCapture(slot) != main)
        {
            recovery.DeviceId = null;
            return;
        }
        DateTime now = DateTime.UtcNow;
        if (recovery.DeviceId != device.Id || recovery.Display != display || recovery.Bounds != bounds)
        {
            recovery.DeviceId = device.Id;
            recovery.Display = display;
            recovery.Bounds = bounds;
            recovery.NextAttempt = now.AddSeconds(1);
            recovery.Attempts = 0;
            return;
        }
        if (recovery.Attempts >= 2 || now < recovery.NextAttempt) return;
        recovery.Attempts++;
        recovery.NextAttempt = now.AddSeconds(5);
        int index = FindCaptureIndex(display, managed);
        if (index >= 0 && ScreenArrangement.Read(display) == bounds)
            owner.SelectDisplay(slot, device.Id, index);
    }

    private static int FindCaptureIndex(string display, bool managed)
    {
        string root = managed ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "TabletDisplaySunshine") : @"D:\APP\chuanliu\Sunshine2";
        string program = Path.Combine(root, "tools", "dxgi-info.exe");
        if (!File.Exists(program)) return -1;
        var topology = DisplayPaths.ReadDesktopLayout();
        using (var probe = Process.Start(new ProcessStartInfo(program) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        }))
        {
            var output = probe.StandardOutput.ReadToEndAsync();
            if (!probe.WaitForExit(2000))
            {
                probe.Kill();
                return -1;
            }
            if (probe.ExitCode != 0 || !output.Wait(500)) return -1;
            var names = Regex.Matches(output.Result, @"Output Name\s*:\s*(\S+)\s+AttachedToDesktop\s*:\s*yes")
                .Cast<Match>().Select(match => match.Groups[1].Value).ToArray();
            // Only use the enumeration if all current desktop sources are present exactly once.
            var expected = new HashSet<string>(topology.Sources.Select(source => source.Name), StringComparer.OrdinalIgnoreCase);
            if (names.Length != expected.Count || !expected.SetEquals(names) ||
                topology.Revision != DisplayPaths.ReadDesktopLayout().Revision) return -1;
            int index = Array.FindIndex(names, name => string.Equals(name, display, StringComparison.OrdinalIgnoreCase));
            return index <= 12 ? index : -1;
        }
    }

    private Guid PrepareSlot(int slot)
    {
        lock (displayLifecycle)
        {
            var info = ReadHostInfo(slot);
            if (slot == 2 && (info == null || ReadSecondaryDisplay() == null))
            {
                string target = FindPrimaryTarget();
                var result = ExecuteDisplayScript("Start-SecondDisplay.ps1", target == null ? "" :
                    "-FirstTargetBase64 " + Convert.ToBase64String(Encoding.UTF8.GetBytes(target)));
                if (!result.status) throw new InvalidOperationException(result.error ?? "暂时无法准备扩展屏。");
                info = ReadHostInfo(slot);
            }
            string currentSecond = ReadSecondaryDisplay();
            string first = ReadPrimaryDisplay(currentSecond);
            if (slot == 1 && (first == null || !ScreenArrangement.MovableDisplays().Contains(first)))
            {
                bool managed = IsManagedDisplay();
                string excluded = DisplayPaths.VirtualTarget(currentSecond, true);
                string display = TabletDisplay.DisplayPaths.EnableVirtual(managed, FindPrimaryTarget(), excluded);
                if (display == null && !managed)
                {
                    var result = ExecuteDisplayScript("launch.ps1", "-PrepareDisplay");
                    if (!result.status) throw new InvalidOperationException(result.error);
                    display = ReadPrimaryDisplay(ReadSecondaryDisplay());
                }
                if (display == null)
                    throw new InvalidOperationException("电脑未能启用扩展屏。请检查虚拟屏驱动。");
                FindPrimaryTarget();
            }
            string targetDisplay = slot == 1 ? ReadPrimaryDisplay(ReadSecondaryDisplay()) : ReadSecondaryDisplay();
            Rectangle bounds = ScreenArrangement.Read(targetDisplay);
            Rectangle main = ScreenArrangement.Read(Screen.PrimaryScreen.DeviceName);
            if (bounds.IsEmpty || main.IsEmpty || bounds.IntersectsWith(main) ||
                !ScreenArrangement.MovableDisplays().Contains(targetDisplay) ||
                !DisplayPaths.ReadDesktopLayout().Sources.Any(source => !source.Primary &&
                    string.Equals(source.Name, targetDisplay, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("电脑未能建立独立扩展屏。请重试连接。");
            var uuid = info == null ? null : info.SelectSingleNode("uniqueid");
            Guid hostId;
            if (uuid == null || !Guid.TryParse(uuid.InnerText, out hostId))
                throw new InvalidOperationException("暂时无法读取电脑的连接信息。");
            return hostId;
        }
    }

    private void HandleResolution(HttpListenerRequest request, HttpListenerResponse response, int slot, bool apply)
    {
        lock (displayLifecycle)
        {
            string second = ReadSecondaryDisplay();
            string display = slot == 1 ? ReadPrimaryDisplay(second) : second;
            var device = owner.GetDeviceState(slot);
            var layout = DisplayPaths.ReadDesktopLayout();
            if (!device.Available || string.IsNullOrEmpty(device.Id) || device.Id != request.QueryString["id"] ||
                string.IsNullOrEmpty(display) || !string.Equals(display, request.QueryString["display"], StringComparison.OrdinalIgnoreCase) ||
                !ScreenArrangement.MovableDisplays().Contains(display) ||
                !layout.Sources.Any(source => !source.Primary && source.Name == display))
            {
                response.StatusCode = 409;
                return;
            }
            if (!apply)
            {
                Reply(response, new JavaScriptSerializer().Serialize(new {
                    modes = ScreenArrangement.Resolutions(display).Select(mode => new {
                        w = mode.Size.Width, h = mode.Size.Height, rates = mode.Rates
                    })
                }));
                return;
            }
            int width, height;
            if (!int.TryParse(request.QueryString["width"], out width) || !int.TryParse(request.QueryString["height"], out height) ||
                width <= 0 || height <= 0)
            {
                response.StatusCode = 400;
                return;
            }
            int? refreshRate = null;
            string requestedRate = request.QueryString["rate"];
            if (requestedRate != null)
            {
                int rate;
                if (!int.TryParse(requestedRate, out rate) || rate < 2)
                {
                    response.StatusCode = 400;
                    return;
                }
                refreshRate = rate;
            }
            int result = ScreenArrangement.SetResolution(display, width, height, refreshRate);
            var previousMode = layout.Sources.First(source =>
                string.Equals(source.Name, display, StringComparison.OrdinalIgnoreCase));
            if (result == 0 && (previousMode.Width != width || previousMode.Height != height))
            {
                // Windows may reflow neighbouring displays after applying the new mode.
                Thread.Sleep(650);
                ScreenArrangement.PreservePositions(layout, display);
            }
            if (result != 0) response.StatusCode = 422;
            Reply(response, new JavaScriptSerializer().Serialize(new { result }));
        }
    }

    private static object ReadDesktopLayout(string firstDisplay, string secondaryDisplay,
        string primaryName, string secondaryName)
    {
        try
        {
            var layout = DisplayPaths.ReadDesktopLayout();
            var movable = ScreenArrangement.MovableDisplays();
            bool reliableFirst = firstDisplay != null && (secondaryDisplay != null || movable.Count == 1 ||
                MonitorProbe.ExtendedVirtualDisplays("Parsec").Contains(firstDisplay, StringComparer.OrdinalIgnoreCase));
            return new {
                revision = layout.Revision,
                screens = layout.Sources.Select(screen => {
                    bool isMovable = !screen.Primary && movable.Contains(screen.Name);
                    bool isFirst = isMovable && reliableFirst &&
                        string.Equals(screen.Name, firstDisplay, StringComparison.OrdinalIgnoreCase);
                    bool isSecond = isMovable && string.Equals(screen.Name, secondaryDisplay, StringComparison.OrdinalIgnoreCase);
                    int rate;
                    ScreenArrangement.Read(screen.Name, out rate);
                    return new {
                    id = screen.Name, x = screen.X, y = screen.Y, w = screen.Width, h = screen.Height,
                    primary = screen.Primary, movable = isMovable,
                    slot = isFirst ? 1 : isSecond ? 2 : 0, refreshRate = rate,
                    name = screen.Primary ? "主屏" :
                        isFirst && !string.IsNullOrWhiteSpace(primaryName) ? primaryName :
                        isSecond && !string.IsNullOrWhiteSpace(secondaryName) ? secondaryName :
                        isMovable ? "扩展屏" : "显示器"
                    };
                })
            };
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private sealed class LayoutPosition
    {
        public string id { get; set; }
        public int? x { get; set; }
        public int? y { get; set; }
    }

    private sealed class LayoutRequest
    {
        public string revision { get; set; }
        public LayoutPosition[] positions { get; set; }
    }

    private void ApplyDesktopLayout(HttpListenerRequest request, HttpListenerResponse response)
    {
        response.StatusCode = 400;
        if (request.ContentLength64 <= 0 || request.ContentLength64 > 16384) return;
        LayoutRequest update;
        try
        {
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                update = new JavaScriptSerializer().Deserialize<LayoutRequest>(reader.ReadToEnd());
        }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        if (update == null || update.positions == null || update.positions.Length == 0) return;

        var layout = DisplayPaths.ReadDesktopLayout();
        if (!string.Equals(update.revision, layout.Revision, StringComparison.Ordinal))
        {
            response.StatusCode = 409;
            return;
        }
        var movable = ScreenArrangement.MovableDisplays();
        var positions = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in update.positions)
        {
            if (position == null || position.id == null || !position.x.HasValue || !position.y.HasValue ||
                Math.Abs((long)position.x.Value) > 30000 || Math.Abs((long)position.y.Value) > 30000 ||
                !movable.Contains(position.id) || positions.ContainsKey(position.id) ||
                !layout.Sources.Any(screen => !screen.Primary &&
                    string.Equals(screen.Name, position.id, StringComparison.OrdinalIgnoreCase))) return;
            positions.Add(position.id, Tuple.Create(position.x.Value, position.y.Value));
        }
        int primary = Array.FindIndex(layout.Sources, screen => screen.Primary);
        if (primary < 0) return;
        var rectangles = layout.Sources.Select(screen => {
            Tuple<int, int> position;
            return positions.TryGetValue(screen.Name, out position)
                ? new Rectangle(position.Item1, position.Item2, screen.Width, screen.Height)
                : new Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
        }).ToArray();
        if (!ScreenArrangement.ValidLayout(rectangles, primary))
        {
            // A prepared virtual output may still be attached to Windows while
            // its companion is offline. Move only these verified idle outputs.
            string secondary = ReadSecondaryDisplay();
            string first = ReadPrimaryDisplay(secondary);
            var idle = new List<int>();
            foreach (int slot in new[] { 1, 2 })
            {
                string name = slot == 1 ? first : secondary;
                if (name == null || !movable.Contains(name) || positions.ContainsKey(name) || !IsSlotIdle(slot)) continue;
                int index = Array.FindIndex(layout.Sources, screen => !screen.Primary &&
                    string.Equals(screen.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) idle.Add(index);
            }
            var occupied = rectangles.Where((rectangle,index) => !idle.Contains(index)).ToList();
            if (idle.Count == 0 || !ScreenArrangement.ValidLayout(occupied.ToArray(), occupied.IndexOf(rectangles[primary])))
            {
                RejectLayout(response);
                return;
            }
            foreach (int index in idle)
            {
                Rectangle parked = ScreenArrangement.ParkIdleDisplay(rectangles[index], occupied);
                if (parked.IsEmpty)
                {
                    RejectLayout(response);
                    return;
                }
                rectangles[index] = parked;
                occupied.Add(parked);
                positions.Add(layout.Sources[index].Name, Tuple.Create(parked.X, parked.Y));
            }
            if (!ScreenArrangement.ValidLayout(rectangles, primary))
            {
                RejectLayout(response);
                return;
            }
        }

        int result = layout.ApplyPositions(positions);
        response.StatusCode = 200;
        Reply(response, "{\"result\":" + result.ToString(CultureInfo.InvariantCulture) + "}");
    }

    private static void RejectLayout(HttpListenerResponse response)
    {
        response.StatusCode = 422;
        Reply(response, "{\"error\":\"这个位置与 Windows 当前的完整屏幕布局冲突。请稍移一点后再试。\"}");
    }

    private bool IsSlotIdle(int slot)
    {
        return !IsSlotOccupied(slot) && ReadHostInfo(slot) != null;
    }

    private static string BasicAuthorization(string credentials)
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
    }

    private static Dictionary<string, string> ReadDeviceNames()
    {
        if (!File.Exists(DeviceNamesPath)) return new Dictionary<string, string>();
        return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(DeviceNamesPath));
    }

    private static void LaunchInvitation(HttpListenerResponse response)
    {
        RunDisplayScript(response, "launch.ps1", "-Invite");
    }

    private sealed class DisplayScriptResult
    {
        public bool status { get; set; }
        public string error { get; set; }
    }

    private static void RunDisplayScript(HttpListenerResponse response, string scriptName, string arguments)
    {
        Reply(response, new JavaScriptSerializer().Serialize(ExecuteDisplayScript(scriptName, arguments)));
    }

    private static DisplayScriptResult ExecuteDisplayScript(string scriptName, string arguments)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string script = Path.Combine(directory, scriptName);
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        using (var launcher = Process.Start(new ProcessStartInfo(powershell,
            "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + arguments) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true
        }))
        {
            string output = launcher.StandardOutput.ReadToEnd();
            launcher.WaitForExit();
            string error = launcher.ExitCode == 0 ? null :
                (Array.FindLast(output.Split('\n'), line => !string.IsNullOrWhiteSpace(line)) ?? "").Trim();
            return new DisplayScriptResult { status = launcher.ExitCode == 0, error = error };
        }
    }

    private void RelayPairing(HttpListenerResponse response, string body, string suppliedAuthorization = null,
        string upstreamPath = "/api/pin", bool secondDisplay = false)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string settings = Path.Combine(directory, "managed.json");
        bool managed = File.Exists(settings);
        if (!managed) settings = Path.Combine(directory, "existing-sunshine.json");
        string secondary = SecondaryRuntimeDirectory;
        if (secondDisplay ? !File.Exists(Path.Combine(secondary, "runtime.json")) : !File.Exists(settings))
        {
            response.StatusCode = 404;
            return;
        }
        try
        {
            string authorization = secondDisplay ? null : suppliedAuthorization ?? existingSunshineAuthorization;
            if (managed || secondDisplay)
            {
                string secret = File.ReadAllText(Path.Combine(secondDisplay ? secondary : directory, "pairing-secret"));
                byte[] password = ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser);
                authorization = BasicAuthorization("tabletdisplay:" + Encoding.UTF8.GetString(password));
            }
            else if (authorization == null)
            {
                string savedAuthorization = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TabletDisplay", "existing-pairing-access");
                if (File.Exists(savedAuthorization))
                {
                    byte[] encrypted = Convert.FromBase64String(File.ReadAllText(savedAuthorization));
                    byte[] credentials = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    authorization = BasicAuthorization(Encoding.UTF8.GetString(credentials));
                }
            }
            if (authorization == null)
            {
                response.StatusCode = 401;
                return;
            }
            string certificatePath = secondDisplay ? Path.Combine(secondary, "certificate.pem") :
                new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(settings))["sunshineCertificate"];
            string certificate = File.ReadAllText(certificatePath)
                .Replace("-----BEGIN CERTIFICATE-----", "").Replace("-----END CERTIFICATE-----", "");
            using (var expected = new X509Certificate2(Convert.FromBase64String(certificate)))
            {
                string port = secondDisplay ? "49990" : "47990";
                var request = (HttpWebRequest)WebRequest.Create("https://127.0.0.1:" + port + upstreamPath);
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.ServicePoint.Expect100Continue = false;
                // Sunshine waits up to 10 seconds by default for the client's pairing handshake.
                request.Timeout = request.ReadWriteTimeout = body != null && upstreamPath == "/api/pin" ? 15000 : 4000;
                request.ServerCertificateValidationCallback = (sender, actual, chain, errors) =>
                    actual != null && expected.GetCertHashString() == actual.GetCertHashString();
                request.Headers[HttpRequestHeader.Authorization] = authorization;
                if (body != null)
                {
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    request.ContentLength = payload.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);
                }
                using (var reply = request.GetResponse())
                using (var reader = new StreamReader(reply.GetResponseStream(), Encoding.UTF8))
                {
                    if (!managed && suppliedAuthorization != null)
                        existingSunshineAuthorization = suppliedAuthorization;
                    Reply(response, reader.ReadToEnd());
                }
            }
        }
        catch (WebException error)
        {
            var upstream = error.Response as HttpWebResponse;
            int? upstreamStatus = upstream == null ? (int?)null : (int)upstream.StatusCode;
            if (error.Response != null) error.Response.Dispose();
            if (!secondDisplay && !managed && upstreamStatus == 401)
            {
                if (suppliedAuthorization == null) existingSunshineAuthorization = null;
                response.StatusCode = 401;
                return;
            }
            response.StatusCode = 502;
            Reply(response, new JavaScriptSerializer().Serialize(new { error = error.Status.ToString(), upstreamStatus }));
        }
        catch (IOException) { response.StatusCode = 503; }
        catch (UnauthorizedAccessException) { response.StatusCode = 503; }
        catch (CryptographicException) { response.StatusCode = 503; }
    }

    private static object Rect(Rectangle value)
    {
        return new { x = value.X, y = value.Y, w = value.Width, h = value.Height };
    }

    private static object Mode(CameraPreview.StreamMode mode)
    {
        return mode == null ? null : new { width = mode.Width, height = mode.Height, fps = mode.Fps };
    }

    private static void Reply(HttpListenerResponse response, string json)
    {
        Reply(response, Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");
    }

    private static void Reply(HttpListenerResponse response, byte[] bytes, string type)
    {
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        displayTimer.Dispose();
        lease.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Dispose();
        if (trayIcon != null) trayIcon.Dispose();
        server.Close();
    }
}


