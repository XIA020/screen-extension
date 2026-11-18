using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class NearbyDevices
{
    private const string Protocol = "TabletDisplay/2";
    private const int Port = 47995;
    private readonly object sync = new object();
    private readonly object discovery = new object();
    private readonly Dictionary<Guid, Device> found = new Dictionary<Guid, Device>();
    private readonly Dictionary<Guid, int> preferredSlots = new Dictionary<Guid, int>();
    private readonly Dictionary<int, Reservation> reservations = new Dictionary<int, Reservation>();
    private readonly string preferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabletDisplay", "device-slots.json");

    public NearbyDevices()
    {
        try
        {
            if (!File.Exists(preferencePath)) return;
            var stored = new JavaScriptSerializer().Deserialize<Dictionary<string,int>>(File.ReadAllText(preferencePath));
            if (stored == null) return;
            foreach (var entry in stored)
            {
                Guid id;
                if (Guid.TryParseExact(entry.Key, "D", out id) && (entry.Value == 1 || entry.Value == 2))
                    preferredSlots[id] = entry.Value;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    public sealed class Device
    {
        public string id { get; set; }
        public string name { get; set; }
        internal IPAddress Address;
        internal IPAddress LocalAddress;
        internal DateTime LastSeen;
    }

    private sealed class Reservation
    {
        public Guid Id;
        public DateTime Expires;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Neighbor
    {
        [FieldOffset(0)] public int InterfaceIndex;
        [FieldOffset(16)] public int Address;
        [FieldOffset(20)] public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NeighborTable
    {
        public int Count;
        public Neighbor First;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable(IntPtr table, ref int size, bool sorted);

    private static IEnumerable<IPAddress> KnownNeighbors(int interfaceIndex)
    {
        int size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != 122 || size < 4 || size > 1024 * 1024) yield break;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) yield break;
            int count = Marshal.ReadInt32(buffer);
            int offset = Marshal.OffsetOf(typeof(NeighborTable), "First").ToInt32();
            int stride = Marshal.SizeOf(typeof(Neighbor));
            if (count < 0 || count > (size - offset) / stride) yield break;
            for (int index = 0; index < count; index++)
            {
                var row = (Neighbor)Marshal.PtrToStructure(IntPtr.Add(buffer,offset + index * stride), typeof(Neighbor));
                if (row.InterfaceIndex == interfaceIndex && row.Type == 3)
                    yield return new IPAddress(BitConverter.GetBytes(row.Address));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsUsbAdapter(NetworkInterface adapter)
    {
        string description = adapter.Description;
        return adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
            new[] { "USB", "RNDIS", "Remote NDIS", "CDC NCM" }.Any(name =>
                description.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static IEnumerable<IPAddress> UsbAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(IsUsbAdapter)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address).Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
    }

    public static bool IsUsbAddress(IPAddress address)
    {
        return UsbAddresses().Contains(address);
    }

    public static IPAddress ActiveHotspotAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.Description.IndexOf("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase) >= 0)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                !address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
    }

    public Device[] Scan(IPAddress localAddress, bool usb)
    {
        lock (discovery)
        {
            if (!usb)
            {
                IPAddress wifi = IPAddress.IsLoopback(localAddress) ? ActiveHotspotAddress() : localAddress;
                if (wifi != null) return ScanNetwork(wifi);
            }
            var addresses = UsbAddresses().Distinct().ToArray();
            if (addresses.Length == 0)
                throw new InvalidOperationException("未检测到 USB 网络。请用数据线连接，并在安卓设备开启「USB 共享网络」。");
            var devices = new Dictionary<string, Device>();
            foreach (var address in addresses)
                foreach (var device in ScanNetwork(address)) devices[device.id] = device;
            return devices.Values.OrderBy(device => device.name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    private Device[] ScanNetwork(IPAddress localAddress)
    {
        if (IPAddress.IsLoopback(localAddress) || localAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("请让电脑和扩展设备加入同一个网络。");
        var local = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(adapter => {
                var adapterProperties = adapter.GetIPProperties();
                return adapterProperties.UnicastAddresses.Select(address => new { adapterProperties, address });
            })
            .FirstOrDefault(entry => entry.address.Address.Equals(localAddress));
        if (local == null) throw new InvalidOperationException("找不到用来扫描设备的网络。");
        byte[] network = localAddress.GetAddressBytes(), mask = local.address.IPv4Mask.GetAddressBytes();
        byte[] broadcast = network.Select((value,index) => (byte)(value | ~mask[index])).ToArray();
        int interfaceIndex = local.adapterProperties.GetIPv4Properties().Index;
        var gateways = local.adapterProperties.GatewayAddresses.Select(entry => entry.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any));
        var destinations = new[] { new IPAddress(broadcast) }.Concat(gateways).Concat(KnownNeighbors(interfaceIndex))
            .Distinct().Take(32).ToArray();
        Guid nonce = Guid.NewGuid();
        var results = new Dictionary<Guid, Device>();
        using (var udp = new UdpClient(new IPEndPoint(localAddress, 0)))
        {
            udp.EnableBroadcast = true;
            byte[] request = Encoding.UTF8.GetBytes(Protocol + "\nscan\n" + nonce.ToString("D"));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                foreach (var destination in destinations)
                    udp.Send(request, request.Length, new IPEndPoint(destination, Port));
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 600)
                {
                    udp.Client.ReceiveTimeout = Math.Max(1, 600 - (int)timer.ElapsedMilliseconds);
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] answer;
                    try { answer = udp.Receive(ref remote); }
                    catch (SocketException error) {
                        if (error.SocketErrorCode == SocketError.TimedOut) break;
                        if (error.SocketErrorCode == SocketError.ConnectionReset) continue;
                        throw;
                    }
                    byte[] peer = remote.Address.GetAddressBytes();
                    if (remote.Port != Port || peer.Length != network.Length || remote.Address.Equals(localAddress) ||
                        peer.Where((value,index) => (value & mask[index]) != (network[index] & mask[index])).Any()) continue;
                    Device item;
                    string challenge;
                    if (TryRead(answer, nonce, remote.Address, out item, out challenge)) {
                        item.LocalAddress = localAddress;
                        results[new Guid(item.id)] = item;
                    }
                }
            }
        }
        lock (sync)
        {
            // Connected viewers no longer answer scans. Keep their last known
            // address for reconnect, which always verifies the device again.
            foreach (var device in results) found[device.Key] = device.Value;
            foreach (var stale in found.OrderByDescending(device => device.Value.LastSeen).Skip(128).ToArray())
                found.Remove(stale.Key);
        }
        return results.Values.OrderBy(device => device.name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool TryRead(byte[] response, Guid nonce, IPAddress address, out Device device, out string challenge)
    {
        device = null;
        challenge = null;
        if (response.Length > 512) return false;
        string[] parts = Encoding.UTF8.GetString(response).Split('\n');
        Guid receivedNonce, id;
        if ((parts.Length != 5 && parts.Length != 6) || parts[0] != Protocol || parts[1] != "found" ||
            !Guid.TryParseExact(parts[2], "D", out receivedNonce) || receivedNonce != nonce ||
            !Guid.TryParseExact(parts[3], "D", out id)) return false;
        string name = parts[4].Trim();
        if (name.Length == 0 || name.Length > 48 || name.Any(char.IsControl)) return false;
        Guid proof;
        if (parts.Length == 6 && Guid.TryParseExact(parts[5], "D", out proof)) challenge = proof.ToString("D");
        device = new Device {
            id = id.ToString("D"), name = name, Address = address, LastSeen = DateTime.UtcNow
        };
        return true;
    }

    public int Connect(Guid id, Func<int, bool> occupied, Func<int, Guid> prepare)
    {
        Device device;
        int slot;
        lock (sync)
        {
            if (!found.TryGetValue(id, out device))
                throw new InvalidOperationException("请重新扫描后再连接。");
            int preferred;
            preferredSlots.TryGetValue(id, out preferred);
            var order = preferred == 2 ? new[] { 2, 1 } : new[] { 1, 2 };
            slot = order.FirstOrDefault(candidate => {
                Reservation pending;
                return !occupied(candidate) && (!reservations.TryGetValue(candidate,out pending) ||
                    pending.Id == id || pending.Expires <= DateTime.UtcNow);
            });
            if (slot == 0) throw new InvalidOperationException("两路扩展屏都在使用中。");
            reservations[slot] = new Reservation { Id = id, Expires = DateTime.UtcNow.AddSeconds(120) };
        }
        try
        {
            Guid hostId = prepare(slot);
            lock (discovery) SendInvitation(device, slot, hostId);
            return slot;
        }
        catch
        {
            lock (sync) {
                Reservation pending;
                if (reservations.TryGetValue(slot,out pending) && pending.Id == id) reservations.Remove(slot);
            }
            throw;
        }
    }

    private static byte[] SignedInvitation(string invitation, string challenge)
    {
        try
        {
            var settings = new CspParameters(24) { KeyContainerName = "TabletDisplay.Invitation.v1",
                Flags = CspProviderFlags.UseNonExportableKey };
            using (var key = new RSACryptoServiceProvider(2048, settings))
            {
                var publicKey = key.ExportParameters(false);
                string payload = string.Join("\n", invitation, challenge, Convert.ToBase64String(publicKey.Modulus),
                    Convert.ToBase64String(publicKey.Exponent));
                var signature = key.SignData(Encoding.UTF8.GetBytes(payload), CryptoConfig.MapNameToOID("SHA256"));
                return Encoding.UTF8.GetBytes(payload + "\n" + Convert.ToBase64String(signature));
            }
        }
        catch (CryptographicException) { return Encoding.UTF8.GetBytes(invitation); }
    }

    private static void SendInvitation(Device device, int slot, Guid hostId)
    {
        // The Android listener starts again after each stream, losing prior scan challenges.
        Guid nonce = Guid.NewGuid();
        string probe = Protocol + "\nscan\n" + nonce.ToString("D");
        int port = slot == 1 ? 47989 : 49989;
        string invitation = string.Join("\n", Protocol, "connect", nonce.ToString("D"), device.id,
            hostId.ToString("D"), port.ToString());
        using (var udp = new UdpClient(new IPEndPoint(device.LocalAddress, 0)))
        {
            udp.Connect(device.Address, Port);
            udp.Client.ReceiveTimeout = 650;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    byte[] request = Encoding.UTF8.GetBytes(attempt < 2 ? probe + "\nproof" : probe);
                    udp.Send(request, request.Length);
                    var peer = new IPEndPoint(IPAddress.Any, 0);
                    Device confirmed;
                    string challenge;
                    if (!TryRead(udp.Receive(ref peer), nonce, peer.Address, out confirmed, out challenge) ||
                        confirmed.id != device.id) continue;
                    byte[] packet = challenge == null ? Encoding.UTF8.GetBytes(invitation) : SignedInvitation(invitation, challenge);
                    udp.Send(packet, packet.Length);
                    return;
                }
                catch (SocketException error)
                {
                    if (error.SocketErrorCode != SocketError.TimedOut && error.SocketErrorCode != SocketError.ConnectionReset)
                        throw;
                    if (error.SocketErrorCode == SocketError.ConnectionReset) System.Threading.Thread.Sleep(250);
                }
            }
        }
        throw new InvalidOperationException("设备暂时无法接收连接。请让它停在「屏幕扩展」首页后重试。");
    }

    public void OnConnected(int slot, string deviceId)
    {
        lock (sync)
        {
            Reservation pending;
            Guid id;
            if (!Guid.TryParseExact(deviceId, "D", out id) ||
                !reservations.TryGetValue(slot, out pending) || pending.Id != id) return;
            reservations.Remove(slot);
            int previous;
            if (preferredSlots.TryGetValue(id, out previous) && previous == slot) return;
            preferredSlots[id] = slot;
            string staged = preferencePath + ".new";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(preferencePath));
                File.WriteAllText(staged, new JavaScriptSerializer().Serialize(
                    preferredSlots.ToDictionary(entry => entry.Key.ToString("D"), entry => entry.Value)));
                if (File.Exists(preferencePath)) File.Replace(staged, preferencePath, null);
                else File.Move(staged, preferencePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public bool HasPendingConnection(int slot)
    {
        lock (sync)
        {
            Reservation pending;
            return reservations.TryGetValue(slot, out pending) && pending.Expires > DateTime.UtcNow;
        }
    }
}
