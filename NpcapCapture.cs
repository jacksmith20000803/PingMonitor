using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>Counts internet-bound IP frames on physical Ethernet and Wi-Fi adapters.</summary>
internal sealed class NpcapCapture : IDisposable
{
    private readonly Action<long, long> _onTraffic;
    private readonly Dictionary<Guid, CaptureWorker> _workers = new();
    private readonly object _statusLock = new();
    private readonly Thread _supervisor;
    private volatile bool _stopping;
    private string _status;

    public NpcapCapture(Action<long, long> onTraffic)
    {
        _onTraffic = onTraffic;
        try
        {
            NpcapNative.Load();
            _status = "Waiting for a physical network connection";
            _supervisor = new Thread(SuperviseCapture)
            {
                IsBackground = true,
                Name = "PingMonitor capture supervisor"
            };
            _supervisor.Start();
        }
        catch (Exception ex)
        {
            _status = $"Capture unavailable: {ex.Message}";
        }
    }

    public string Status
    {
        get
        {
            lock (_statusLock)
                return _status ?? "Waiting for a physical network connection";
        }
    }

    private void SuperviseCapture()
    {
        while (!_stopping)
        {
            try { StartWorkers(); }
            catch (Exception ex) { SetStatus($"Capture temporarily unavailable; retrying: {ex.Message}"); }
            if (!_stopping) Thread.Sleep(2000);
        }
        StopAllWorkers();
    }

    private void StartWorkers()
    {
        HashSet<Guid> physicalIds = NetworkUsageTracker.GetPhysicalInterfaceIds()
            ?? throw new InvalidOperationException("Windows could not enumerate physical network adapters.");
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => Guid.TryParse(adapter.Id, out Guid id) && physicalIds.Contains(id))
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .ToDictionary(adapter => Guid.Parse(adapter.Id));

        var targets = new Dictionary<Guid, CaptureTarget>();
        IntPtr devices = IntPtr.Zero;
        byte[] error = new byte[256];
        try
        {
            if (NpcapNative.pcap_findalldevs(out devices, error) != 0)
                throw new InvalidOperationException(NativeError(error));

            for (IntPtr current = devices; current != IntPtr.Zero;)
            {
                PcapDevice device = Marshal.PtrToStructure<PcapDevice>(current);
                string name = Marshal.PtrToStringAnsi(device.Name) ?? string.Empty;
                if (TryGetAdapterGuid(name, out Guid id) && interfaces.TryGetValue(id, out NetworkInterface adapter))
                {
                    var localAddresses = adapter.GetIPProperties().UnicastAddresses
                        .Select(item => item.Address)
                        .Where(address => address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .ToArray();
                    var prefixes = adapter.GetIPProperties().UnicastAddresses
                        .Select(ToPrefix)
                        .Where(prefix => prefix != null)
                        .ToArray();
                    targets[id] = new CaptureTarget(name, adapter.Name, localAddresses, prefixes);
                }
                current = device.Next;
            }
        }
        finally
        {
            if (devices != IntPtr.Zero)
                NpcapNative.pcap_freealldevs(devices);
        }

        foreach (Guid id in _workers.Keys.ToArray())
        {
            CaptureWorker existing = _workers[id];
            bool available = targets.TryGetValue(id, out CaptureTarget target);
            bool ended = existing.Finished || !existing.Thread.IsAlive;
            bool changed = available && existing.ConfigKey != target.ConfigKey;
            if (!available || ended || changed)
            {
                StopWorker(existing);
                _workers.Remove(id);
            }
        }

        var errors = new List<string>();
        foreach ((Guid id, CaptureTarget target) in targets)
        {
            if (_workers.ContainsKey(id)) continue;
            try { StartWorker(id, target); }
            catch (Exception ex) { errors.Add($"{target.AdapterName}: {ex.Message}"); }
        }

        int active = _workers.Count(pair => pair.Value.Thread.IsAlive && !pair.Value.Finished);
        SetStatus(active > 0
            ? errors.Count == 0 ? $"Capturing on {active} physical adapter(s)" : $"Capturing on {active} adapter(s); retrying: {string.Join("; ", errors)}"
            : errors.Count == 0 ? "Waiting for a physical Ethernet/Wi-Fi connection" : $"Capture temporarily unavailable; retrying: {string.Join("; ", errors)}");
    }

    private void StartWorker(Guid id, CaptureTarget target)
    {
        byte[] error = new byte[256];
        IntPtr handle = NpcapNative.pcap_open_live(target.DeviceName, 65535, 0, 500, error);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(NativeError(error));

        int linkType = NpcapNative.pcap_datalink(handle);
        if (linkType != 1)
        {
            NpcapNative.pcap_close(handle);
            throw new InvalidOperationException($"Unsupported link type {linkType}.");
        }

        var worker = new CaptureWorker(handle, target);
        var thread = new Thread(() => CaptureLoop(worker))
        {
            IsBackground = true,
            Name = $"PingMonitor packet capture: {target.AdapterName}"
        };
        worker.Thread = thread;
        _workers.Add(id, worker);
        thread.Start();
    }

    private void CaptureLoop(CaptureWorker worker)
    {
        try
        {
            while (!worker.Stopping)
            {
                int result = NpcapNative.pcap_next_ex(worker.Handle, out IntPtr header, out IntPtr data);
                if (result == 0)
                    continue;
                if (result == -2)
                    break;
                if (result < 0)
                {
                    worker.Error = Marshal.PtrToStringAnsi(NpcapNative.pcap_geterr(worker.Handle));
                    break;
                }

                int capturedLength = unchecked((int)(uint)Marshal.ReadInt32(header, 8));
                int frameLength = unchecked((int)(uint)Marshal.ReadInt32(header, 12));
                if (capturedLength <= 0 || capturedLength > 65535 || frameLength <= 0)
                    continue;

                byte[] frame = new byte[capturedLength];
                Marshal.Copy(data, frame, 0, capturedLength);
                if (TryClassifyEthernetFrame(frame, frameLength, worker.LocalAddresses, worker.Prefixes, out bool received))
                    _onTraffic(received ? frameLength : 0, received ? 0 : frameLength);
            }
        }
        catch (Exception ex)
        {
            worker.Error = ex.Message;
        }
        finally
        {
            worker.Finished = true;
            NpcapNative.pcap_close(worker.Handle);
        }
    }

    private void SetStatus(string status)
    {
        lock (_statusLock)
            _status = status;
    }

    internal static bool TryClassifyEthernetFrame(byte[] frame, int frameLength, IPAddress[] localAddresses, IpPrefix[] prefixes, out bool received)
    {
        received = false;
        if (frame == null || frameLength <= 0 || frame.Length < 14)
            return false;

        int offset = 14;
        ushort etherType = ReadUInt16(frame, 12);
        while (etherType is 0x8100 or 0x88A8 or 0x9100)
        {
            if (frame.Length < offset + 4)
                return false;
            etherType = ReadUInt16(frame, offset + 2);
            offset += 4;
        }

        IPAddress source;
        IPAddress destination;
        if (etherType == 0x0800)
        {
            if (frame.Length < offset + 20 || frame[offset] >> 4 != 4)
                return false;
            int headerLength = (frame[offset] & 0x0F) * 4;
            int ipLength = ReadUInt16(frame, offset + 2);
            if (headerLength < 20 || ipLength < headerLength || frame.Length < offset + headerLength)
                return false;
            source = new IPAddress(frame.AsSpan(offset + 12, 4));
            destination = new IPAddress(frame.AsSpan(offset + 16, 4));
        }
        else if (etherType == 0x86DD)
        {
            if (frame.Length < offset + 40 || frame[offset] >> 4 != 6)
                return false;
            source = new IPAddress(frame.AsSpan(offset + 8, 16));
            destination = new IPAddress(frame.AsSpan(offset + 24, 16));
        }
        else
        {
            return false;
        }

        bool sourceIsLocal = localAddresses.Any(address => address.Equals(source));
        bool destinationIsLocal = localAddresses.Any(address => address.Equals(destination));
        if (sourceIsLocal && IsPublicEndpoint(destination) && !MatchesConnectedPrefix(destination, prefixes))
        {
            received = false;
            return true;
        }
        if (destinationIsLocal && IsPublicEndpoint(source) && !MatchesConnectedPrefix(source, prefixes))
        {
            received = true;
            return true;
        }
        return false;
    }

    internal static bool IsPublicEndpoint(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            int a = bytes[0], b = bytes[1], c = bytes[2];
            if (a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b is >= 64 and <= 127) ||
                (a == 169 && b == 254) ||
                (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && b == 168) ||
                (a == 192 && b == 0 && c == 0) ||
                (a == 192 && b == 0 && c == 2) ||
                (a == 198 && b == 18) || (a == 198 && b == 19) ||
                (a == 198 && b == 51 && c == 100) ||
                (a == 203 && b == 0 && c == 113))
                return false;
            return true;
        }

        if (bytes.Length != 16)
            return false;
        // Global unicast IPv6 uses 2000::/3. Exclude documentation and special-use ranges.
        if ((bytes[0] & 0xE0) != 0x20)
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 &&
            ((bytes[3] & 0xF0) == 0x10 || (bytes[3] & 0xF0) == 0x20))
            return false;
        return true;
    }

    private static bool MatchesConnectedPrefix(IPAddress address, IpPrefix[] prefixes) =>
        prefixes.Any(prefix => prefix.Contains(address));

    private static IpPrefix ToPrefix(UnicastIPAddressInformation address)
    {
        int prefixLength = address.PrefixLength;
        return prefixLength >= 0 && prefixLength <= address.Address.GetAddressBytes().Length * 8
            ? new IpPrefix(address.Address, prefixLength)
            : null;
    }

    private static bool TryGetAdapterGuid(string deviceName, out Guid id)
    {
        id = Guid.Empty;
        int start = deviceName.LastIndexOf('{');
        int end = deviceName.LastIndexOf('}');
        return start >= 0 && end > start && Guid.TryParse(deviceName.Substring(start, end - start + 1), out id);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static string NativeError(byte[] error) => Encoding.ASCII.GetString(error).TrimEnd('\0');

    public void Dispose()
    {
        _stopping = true;
        if (_supervisor != null && Thread.CurrentThread != _supervisor)
            _supervisor.Join();
        else
            StopAllWorkers();
    }

    private void StopAllWorkers()
    {
        foreach (CaptureWorker worker in _workers.Values.ToArray())
            StopWorker(worker);
        _workers.Clear();
    }

    private static void StopWorker(CaptureWorker worker)
    {
        worker.Stopping = true;
        if (!worker.Finished)
        {
            try { NpcapNative.pcap_breakloop(worker.Handle); } catch { }
        }
        if (worker.Thread != null && Thread.CurrentThread != worker.Thread)
            worker.Thread.Join();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PcapDevice
    {
        public IntPtr Next;
        public IntPtr Name;
        public IntPtr Description;
        public IntPtr Addresses;
        public uint Flags;
    }

    internal sealed class IpPrefix
    {
        private readonly byte[] _network;
        private readonly int _prefixLength;

        public IpPrefix(IPAddress address, int prefixLength)
        {
            _network = address.GetAddressBytes();
            _prefixLength = prefixLength;
        }

        public int PrefixLength => _prefixLength;
        public bool MatchesAddress(IPAddress address) => _network.SequenceEqual(address.GetAddressBytes());

        public bool Contains(IPAddress address)
        {
            byte[] candidate = address.GetAddressBytes();
            if (candidate.Length != _network.Length)
                return false;
            int wholeBytes = _prefixLength / 8;
            int remainingBits = _prefixLength % 8;
            for (int i = 0; i < wholeBytes; i++)
                if (candidate[i] != _network[i]) return false;
            if (remainingBits == 0)
                return true;
            int mask = 0xFF << (8 - remainingBits);
            return (candidate[wholeBytes] & mask) == (_network[wholeBytes] & mask);
        }
    }

    private sealed class CaptureWorker
    {
        public CaptureWorker(IntPtr handle, CaptureTarget target)
        {
            Handle = handle;
            AdapterName = target.AdapterName;
            LocalAddresses = target.LocalAddresses;
            Prefixes = target.Prefixes;
            ConfigKey = target.ConfigKey;
        }

        public IntPtr Handle { get; }
        public string AdapterName { get; }
        public IPAddress[] LocalAddresses { get; }
        public IpPrefix[] Prefixes { get; }
        public Thread Thread { get; set; }
        public volatile bool Stopping;
        public volatile bool Finished;
        public string Error { get; set; }
        public string ConfigKey { get; }
    }

    private sealed class CaptureTarget
    {
        public CaptureTarget(string deviceName, string adapterName, IPAddress[] localAddresses, IpPrefix[] prefixes)
        {
            DeviceName = deviceName;
            AdapterName = adapterName;
            LocalAddresses = localAddresses;
            Prefixes = prefixes;
            ConfigKey = deviceName + "|" + string.Join(";", localAddresses.Select(address => address + "/" +
                (prefixes.FirstOrDefault(prefix => prefix.MatchesAddress(address))?.PrefixLength ?? -1)));
        }

        public string DeviceName { get; }
        public string AdapterName { get; }
        public IPAddress[] LocalAddresses { get; }
        public IpPrefix[] Prefixes { get; }
        public string ConfigKey { get; }
    }

    private static class NpcapNative
    {
        private static readonly object LoadLock = new();
        private static bool _loaded;

        public static void Load()
        {
            lock (LoadLock)
            {
                if (_loaded) return;
                NativeLibrary.SetDllImportResolver(typeof(NpcapNative).Assembly, ResolveLibrary);
                _loaded = true;
            }
        }

        private static IntPtr ResolveLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, "wpcap.dll", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] candidates =
            {
                Path.Combine(windows, "System32", "Npcap", "wpcap.dll"),
                Path.Combine(windows, "System32", "wpcap.dll")
            };
            foreach (string candidate in candidates)
                if (File.Exists(candidate))
                    return NativeLibrary.Load(candidate);
            throw new DllNotFoundException("Npcap is not installed. Install Npcap from https://npcap.com/ and restart PingMonitor.");
        }

        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_findalldevs")]
        public static extern int pcap_findalldevs(out IntPtr allDevices, [Out] byte[] errorBuffer);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_freealldevs")]
        public static extern void pcap_freealldevs(IntPtr allDevices);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_open_live", CharSet = CharSet.Ansi)]
        public static extern IntPtr pcap_open_live(string device, int snapLength, int promiscuous, int timeoutMilliseconds, [Out] byte[] errorBuffer);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_datalink")]
        public static extern int pcap_datalink(IntPtr handle);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_next_ex")]
        public static extern int pcap_next_ex(IntPtr handle, out IntPtr packetHeader, out IntPtr packetData);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_geterr")]
        public static extern IntPtr pcap_geterr(IntPtr handle);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_breakloop")]
        public static extern void pcap_breakloop(IntPtr handle);
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pcap_close")]
        public static extern void pcap_close(IntPtr handle);
    }
}
