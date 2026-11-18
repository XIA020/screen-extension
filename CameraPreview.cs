using System;
using System.Drawing;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

internal sealed class CameraPreview : Form
{
    private const string OpenDevicesEvent = @"Local\TabletDisplay.OpenDevices";
    private readonly TcpListener listener;
    private readonly IPAddress primaryAddress;
    private TcpListener frameListener;
    private byte[] latestJpeg;
    private string requestedName;
    private string cameraName;
    private Process cameraHost;
    private bool connected;
    private int cameraSlot, closingCameraSlot, pendingCameraSlot;
    private readonly ControlSession[] sessions = new ControlSession[2];
    private readonly object controlLock = new object();
    private int receiveClients;
    private bool disposed;
    private ControlPanel panel;
    public string LocalAddress { get { return primaryAddress.ToString(); } }

    internal sealed class StreamMode
    {
        public readonly int Width, Height, Fps;
        public StreamMode(int width, int height, int fps)
        {
            Width = width;
            Height = height;
            Fps = fps;
        }
    }

    private sealed class ControlSession
    {
        public TcpClient Client;
        public string Name, Id;
        public bool UsesUsbNetwork;
        public bool Remote, CanSwitch, Denied, CanDisconnect, CanSelectDisplay;
        public bool? Trackpad, Audio, SavedAudio;
        public StreamMode Active, Saved;
    }

    internal sealed class DeviceState
    {
        public bool Available, Legacy, Enabled, Switchable, Receiving, Denied, CanDisconnect, CanSelectDisplay;
        public bool UsesUsbNetwork;
        public string Name, Id;
        public bool? Trackpad, Audio, SavedAudio;
        public StreamMode Active, Saved;
    }

    public DeviceState GetDeviceState(int slot)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return new DeviceState {
                Available = session != null && session.Remote,
                Legacy = session != null && !session.Remote,
                Name = session == null ? null : session.Name,
                Id = session == null ? null : session.Id,
                UsesUsbNetwork = session != null && session.UsesUsbNetwork,
                CanDisconnect = session != null && session.CanDisconnect,
                CanSelectDisplay = session != null && session.CanSelectDisplay,
                Enabled = cameraSlot == slot || pendingCameraSlot == slot,
                Switchable = session != null && session.CanSwitch,
                Receiving = (cameraSlot == slot || session != null && !session.Remote) && connected,
                Denied = session != null && session.Denied,
                Trackpad = session == null ? null : session.Trackpad,
                Audio = session == null ? null : session.Audio,
                SavedAudio = session == null ? null : session.SavedAudio,
                Active = session == null ? null : session.Active,
                Saved = session == null ? null : session.Saved
            };
        }
    }

    public bool CameraEnabled { get { lock (controlLock) return cameraSlot != 0 || pendingCameraSlot != 0; } }

    public byte[] GetFrame(int slot)
    {
        lock (controlLock)
        {
            return cameraSlot == slot || slot == 1 && sessions[0] != null && !sessions[0].Remote ? latestJpeg : null;
        }
    }

    public bool SetTrackpadMode(int slot, bool enabled)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return session != null && session.Trackpad.HasValue && Send(session, writer => {
                writer.Write((byte)5);
                writer.Write(enabled);
            });
        }
    }

    public bool SetAudioMode(int slot, bool enabled)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return session != null && session.SavedAudio.HasValue && Send(session, writer => {
                writer.Write((byte)6);
                writer.Write(enabled);
            });
        }
    }

    public bool Disconnect(int slot, string deviceId)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return session != null && session.CanDisconnect && session.Id == deviceId &&
                Send(session, writer => writer.Write((byte)7));
        }
    }

    public bool SelectDisplay(int slot, string deviceId, int index)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return session != null && session.CanSelectDisplay && session.Id == deviceId &&
                index >= 0 && index <= 12 && Send(session, writer => {
                    writer.Write((byte)8);
                    writer.Write((byte)index);
                });
        }
    }

    public bool SetStreamMode(int slot, int width, int height, int fps)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            return session != null && session.Active != null && Send(session, writer => {
                writer.Write((byte)4);
                writer.Write(IPAddress.HostToNetworkOrder(width));
                writer.Write(IPAddress.HostToNetworkOrder(height));
                writer.Write(IPAddress.HostToNetworkOrder(fps));
            });
        }
    }

    private static bool Send(ControlSession session, Action<BinaryWriter> write)
    {
        if (session == null || !session.Remote) return false;
        try
        {
            using (var writer = new BinaryWriter(session.Client.GetStream(), Encoding.UTF8, true)) write(writer);
            return true;
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (SocketException) { return false; }
    }

    private bool StartCamera(int slot)
    {
        var session = sessions[slot - 1];
        if (!Send(session, writer => writer.Write((byte)1))) return false;
        session.Denied = false;
        cameraSlot = slot;
        return true;
    }

    public bool SetCamera(int slot, int command)
    {
        lock (controlLock)
        {
            var session = sessions[slot - 1];
            if (session == null || !session.Remote) return false;
            if (command == 2)
                return cameraSlot == slot && session.CanSwitch && Send(session, writer => writer.Write((byte)2));
            if (command == 1)
            {
                if (cameraSlot == slot || pendingCameraSlot == slot) return true;
                if (sessions[0] != null && !sessions[0].Remote) return false;
                if (cameraSlot == 0 && closingCameraSlot == 0) return StartCamera(slot);
                if (cameraSlot != 0)
                {
                    if (!Send(sessions[cameraSlot - 1], writer => writer.Write((byte)0))) return false;
                    closingCameraSlot = cameraSlot;
                    cameraSlot = 0;
                    ClearFrame();
                }
                session.Denied = false;
                pendingCameraSlot = slot;
                return true;
            }

            if (pendingCameraSlot == slot) pendingCameraSlot = 0;
            session.Denied = false;
            if (cameraSlot != slot) return true;
            if (!Send(session, writer => writer.Write((byte)command))) return false;
            closingCameraSlot = cameraSlot;
            cameraSlot = 0;
            ClearFrame();
            return true;
        }
    }

    public void StopCameras()
    {
        lock (controlLock)
        {
            pendingCameraSlot = 0;
            if (cameraSlot != 0) SetCamera(cameraSlot, 3);
        }
    }

    private void FinishCameraStop(int slot)
    {
        if (closingCameraSlot != slot) return;
        closingCameraSlot = 0;
        int next = pendingCameraSlot;
        pendingCameraSlot = 0;
        if (next != 0) StartCamera(next);
    }

    private void ClearFrame()
    {
        Volatile.Write(ref latestJpeg, null);
        connected = false;
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    private CameraPreview(IPAddress address, int port)
    {
        primaryAddress = address;
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        frameListener = new TcpListener(IPAddress.Loopback, 47997);
        try { frameListener.Start(); }
        catch { listener.Stop(); throw; }
        new Thread(ServeLocalFrames) { IsBackground = true }.Start();
        try { panel = new ControlPanel(this); }
        catch {
            listener.Stop();
            if (frameListener != null) frameListener.Stop();
            throw;
        }

        var worker = new Thread(Receive) { IsBackground = true };
        worker.Start();
        var refresh = new System.Windows.Forms.Timer { Interval = 500 };
        refresh.Tick += (sender, args) => { if (frameListener != null) UpdateVirtualCamera(); };
        refresh.Start();
        Disposed += (sender, args) =>
        {
            refresh.Dispose();
            panel.Dispose();
            lock (controlLock)
            {
                disposed = true;
                pendingCameraSlot = 0;
                foreach (var session in sessions)
                    if (session != null) session.Client.Close();
            }
            listener.Stop();
            if (frameListener != null) frameListener.Stop();
            StopVirtualCamera();
        };
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--open-devices")
        {
            EventWaitHandle runningPanel;
            if (!EventWaitHandle.TryOpenExisting(OpenDevicesEvent, out runningPanel)) return 1;
            using (runningPanel) runningPanel.Set();
            return 0;
        }
        IPAddress address;
        int port;
        if (args.Length != 2 || !IPAddress.TryParse(args[0], out address) ||
            !int.TryParse(args[1], out port) || port < 1 || port > 65535)
            return 2;

        Application.EnableVisualStyles();
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try
        {
            using (var openDevices = new EventWaitHandle(false, EventResetMode.AutoReset, OpenDevicesEvent))
            using (var window = new CameraPreview(address, port))
            {
                var application = new ApplicationContext();
                window.Disposed += (sender, e) => application.ExitThread();
                using (var openTimer = new System.Windows.Forms.Timer { Interval = 250 })
                {
                    openTimer.Tick += (sender, e) => {
                        if (openDevices.WaitOne(0)) window.panel.Open("#devices");
                    };
                    openTimer.Start();
                    Application.Run(application);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "无法启动屏幕扩展控制台");
            return 1;
        }
    }

    private void Receive()
    {
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }

            var local = ((IPEndPoint)client.Client.LocalEndPoint).Address;
            bool usesUsbNetwork = NearbyDevices.IsUsbAddress(local);
            if (!primaryAddress.Equals(local) && !usesUsbNetwork &&
                !local.Equals(NearbyDevices.ActiveHotspotAddress()))
            {
                client.Close();
                continue;
            }

            if (Interlocked.Increment(ref receiveClients) <= 8)
                ThreadPool.QueueUserWorkItem(ignored => ReceiveClient(client, usesUsbNetwork));
            else
            {
                Interlocked.Decrement(ref receiveClients);
                client.Close();
            }
        }
    }

    private void ReceiveClient(TcpClient client, bool usesUsbNetwork)
    {
        int slot = 1;
        ControlSession session = null;
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                using (var reader = new BinaryReader(client.GetStream()))
                {
                    int protocol = ReadNumber(reader);
                    bool supportsSelectDisplay = protocol == 0x54444338;
                    bool supportsDisconnect = supportsSelectDisplay || protocol == 0x54444337;
                    bool supportsAudio = supportsDisconnect || protocol == 0x54444336;
                    bool supportsTrackpad = supportsAudio || protocol == 0x54444335;
                    bool supportsSettings = supportsTrackpad || protocol == 0x54444334;
                    bool remote = supportsSettings || protocol == 0x54444333;
                    if (!remote && protocol != 0x54444331 && protocol != 0x54444332) return;

                    var candidate = new ControlSession { Client = client, Remote = remote,
                        CanDisconnect = supportsDisconnect, CanSelectDisplay = supportsSelectDisplay,
                        UsesUsbNetwork = usesUsbNetwork };
                    if (supportsAudio)
                    {
                        slot = reader.ReadByte();
                        Guid id;
                        if ((slot != 1 && slot != 2) ||
                            !Guid.TryParseExact(Encoding.ASCII.GetString(reader.ReadBytes(36)), "D", out id)) return;
                        candidate.Id = id.ToString("D");
                    }
                    if (remote || protocol == 0x54444332) candidate.Name = ReadDeviceName(reader);
                    if (remote) candidate.CanSwitch = reader.ReadBoolean();
                    if (supportsSettings)
                    {
                        candidate.Active = ReadMode(reader);
                        candidate.Saved = ReadMode(reader);
                    }
                    if (supportsTrackpad) candidate.Trackpad = reader.ReadBoolean();
                    if (supportsAudio)
                    {
                        candidate.Audio = reader.ReadBoolean();
                        candidate.SavedAudio = reader.ReadBoolean();
                    }
                    client.ReceiveTimeout = remote ? 10000 : 2000;
                    client.SendTimeout = 500;
                    lock (controlLock)
                    {
                        if (disposed || sessions[slot - 1] != null || !remote && (cameraSlot != 0 || closingCameraSlot != 0))
                            return;
                        sessions[slot - 1] = session = candidate;
                        if (supportsAudio) client.GetStream().WriteByte(0x7e);
                    }
                    while (true)
                    {
                        int length = ReadNumber(reader);
                        if (length == -2) continue;
                        if (length == -6 && supportsAudio)
                        {
                            bool enabled = reader.ReadBoolean();
                            lock (controlLock) session.SavedAudio = enabled;
                            continue;
                        }
                        if (length == -5 && supportsTrackpad)
                        {
                            bool trackpad = reader.ReadBoolean();
                            lock (controlLock) session.Trackpad = trackpad;
                            continue;
                        }
                        if (length == -4 && supportsSettings)
                        {
                            var saved = ReadMode(reader);
                            lock (controlLock) session.Saved = saved;
                            continue;
                        }
                        if (length == 0 || length == -1 || length == -3)
                        {
                            lock (controlLock)
                            {
                                session.Denied = length == -1;
                                if (cameraSlot == slot && length != 0) cameraSlot = 0;
                                if (cameraSlot == slot || !remote || closingCameraSlot == slot || length != 0 && cameraSlot == 0)
                                    ClearFrame();
                                if (length == 0 || length == -3) FinishCameraStop(slot);
                            }
                            continue;
                        }
                        if (length < 0 || length > 512 * 1024) break;
                        byte[] jpeg = reader.ReadBytes(length);
                        if (jpeg.Length != length) break;
                        using (var data = new MemoryStream(jpeg))
                        using (Image.FromStream(data, false, true))
                        {
                            lock (controlLock)
                            {
                                if (remote && cameraSlot != slot) continue;
                                Volatile.Write(ref latestJpeg, jpeg);
                                Volatile.Write(ref requestedName, "屏幕扩展");
                                connected = true;
                            }
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (ArgumentException) { }
            catch (SocketException) { }
            finally
            {
                lock (controlLock)
                {
                    if (session != null)
                    {
                        sessions[slot - 1] = null;
                        if (pendingCameraSlot == slot) pendingCameraSlot = 0;
                        if (cameraSlot == slot || slot == 1 && !session.Remote)
                        {
                            if (cameraSlot == slot) cameraSlot = 0;
                            ClearFrame();
                        }
                        FinishCameraStop(slot);
                    }
                }
                Interlocked.Decrement(ref receiveClients);
            }
        }
    }

    private void ServeLocalFrames()
    {
        while (true)
        {
            try
            {
                using (var client = frameListener.AcceptTcpClient())
                using (var writer = new BinaryWriter(client.GetStream()))
                {
                    client.SendTimeout = 250;
                    byte[] jpeg = Volatile.Read(ref latestJpeg);
                    writer.Write(jpeg == null ? 0 : jpeg.Length);
                    if (jpeg != null) writer.Write(jpeg);
                }
            }
            catch (IOException) { }
            catch (SocketException) { return; }
            catch (InvalidOperationException) { return; }
        }
    }

    private void UpdateVirtualCamera()
    {
        string name = Volatile.Read(ref requestedName);
        if (name == null || name == cameraName) return;
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            @"virtual-camera\Host\bin\Release\net8.0-windows\TabletCameraHost.exe");
        if (!File.Exists(path)) return;
        StopVirtualCamera();
        cameraName = name;
        var start = new ProcessStartInfo(path, "\"" + name + " 摄像头\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        try
        {
            cameraHost = Process.Start(start);
            cameraHost.BeginOutputReadLine();
            cameraHost.BeginErrorReadLine();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void StopVirtualCamera()
    {
        if (cameraHost == null) return;
        try
        {
            if (!cameraHost.HasExited)
            {
                cameraHost.StandardInput.Close();
                if (!cameraHost.WaitForExit(1500)) cameraHost.Kill();
            }
        }
        catch (IOException) { }
        finally
        {
            cameraHost.Dispose();
            cameraHost = null;
        }
    }

    private static string ReadDeviceName(BinaryReader reader)
    {
        int length = ReadNumber(reader);
        if (length < 1 || length > 512) throw new IOException("Invalid tablet name.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        string name = Encoding.UTF8.GetString(bytes);
        var clean = new StringBuilder();
        foreach (char letter in name)
        {
            if (!char.IsControl(letter) && letter != '"' && letter != '\\')
                clean.Append(letter);
            if (clean.Length == 48) break;
        }
        name = clean.ToString().Trim();
        return name.Length == 0 ? null : name;
    }


    private static int ReadNumber(BinaryReader reader)
    {
        return IPAddress.NetworkToHostOrder(reader.ReadInt32());
    }

    private static StreamMode ReadMode(BinaryReader reader)
    {
        return new StreamMode(ReadNumber(reader), ReadNumber(reader), ReadNumber(reader));
    }
}


