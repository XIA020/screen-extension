using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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

    public Device[] Scan(IPAddress localAddress)
    {
        lock (discovery) return ScanNetwork(localAddress);
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
        var destinations = new[] { new IPAddress(broadcast) }.Concat(KnownNeighbors(interfaceIndex))
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
                    if (TryRead(answer, nonce, remote.Address, out item)) results[new Guid(item.id)] = item;
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

    private static bool TryRead(byte[] response, Guid nonce, IPAddress address, out Device device)
    {
        device = null;
        if (response.Length > 512) return false;
        string[] parts = Encoding.UTF8.GetString(response).Split('\n');
        Guid receivedNonce, id;
        if (parts.Length != 5 || parts[0] != Protocol || parts[1] != "found" ||
            !Guid.TryParseExact(parts[2], "D", out receivedNonce) || receivedNonce != nonce ||
            !Guid.TryParseExact(parts[3], "D", out id)) return false;
        string name = parts[4].Trim();
        if (name.Length == 0 || name.Length > 48 || name.Any(char.IsControl)) return false;
        device = new Device {
            id = id.ToString("D"), name = name, Address = address, LastSeen = DateTime.UtcNow
        };
        return true;
    }

    public int Connect(Guid id, IPAddress localAddress, Func<int, bool> occupied, Func<int, Guid> prepare)
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
            lock (discovery) SendInvitation(device, localAddress, slot, hostId);
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

    private static void SendInvitation(Device device, IPAddress localAddress, int slot, Guid hostId)
    {
        // The Android listener starts again after each stream, losing prior scan challenges.
        Guid nonce = Guid.NewGuid();
        byte[] probe = Encoding.UTF8.GetBytes(Protocol + "\nscan\n" + nonce.ToString("D"));
        int port = slot == 1 ? 47989 : 49989;
        byte[] invitation = Encoding.UTF8.GetBytes(string.Join("\n", Protocol, "connect",
            nonce.ToString("D"), device.id, hostId.ToString("D"), port.ToString()));
        using (var udp = new UdpClient(new IPEndPoint(localAddress, 0)))
        {
            udp.Connect(device.Address, Port);
            udp.Client.ReceiveTimeout = 650;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    udp.Send(probe, probe.Length);
                    var peer = new IPEndPoint(IPAddress.Any, 0);
                    Device confirmed;
                    if (!TryRead(udp.Receive(ref peer), nonce, peer.Address, out confirmed) ||
                        confirmed.id != device.id) continue;
                    udp.Send(invitation, invitation.Length);
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
