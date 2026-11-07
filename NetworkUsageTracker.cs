using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Tracks public IP traffic on physical Ethernet/Wi-Fi adapters and rolls the
/// daily total over using the computer's local date.
/// </summary>
internal sealed class NetworkUsageTracker
{
    public const long DailyAlertThresholdBytes = 5_000_000_000;
    private const uint InterfaceTypeSoftwareLoopback = 24;
    private const uint InterfaceTypeTunnel = 131;
    private const byte InterfaceFlagHardware = 0x01;

    private readonly string _statePath;
    private readonly string _legacyStatePath;
    private UsageState _state;
    private readonly NpcapCapture _capture;
    private long _pendingReceivedBytes;
    private long _pendingSentBytes;
    private string _persistenceError;

    public NetworkUsageTracker(string statePath, string legacyStatePath = null)
    {
        _statePath = statePath;
        _legacyStatePath = legacyStatePath;
        bool migratingLegacyState = !File.Exists(_statePath) && !string.IsNullOrWhiteSpace(_legacyStatePath) && File.Exists(_legacyStatePath);
        _state = LoadState(migratingLegacyState ? _legacyStatePath : _statePath);
        _state.History ??= new Dictionary<string, DailyUsage>(StringComparer.Ordinal);
        _state.PingSamples ??= new List<int>();
        foreach (DailyUsage dailyUsage in _state.History.Values)
            dailyUsage.PingSamples ??= new List<int>();

        // Packet-filtered Ethernet totals cannot be mixed with the old
        // interface counters, so start a clean history for this source.
        if (_state.CounterVersion != 6)
        {
            _state.ReceivedBytes = 0;
            _state.SentBytes = 0;
            _state.ThresholdAlertSent = false;
            _state.PingSamples.Clear();
            _state.History.Clear();
        }
        _state.CounterVersion = 6;

        if (_state.ResetTime < TimeSpan.Zero || _state.ResetTime >= TimeSpan.FromDays(1))
            _state.ResetTime = TimeSpan.Zero;

        DateOnly cycleDate = GetCycleDate(DateTime.Now, _state.ResetTime);
        if (_state.Date != cycleDate)
        {
            ArchiveCurrentUsage();
            _state.Date = cycleDate;
            _state.ReceivedBytes = 0;
            _state.SentBytes = 0;
            _state.ThresholdAlertSent = false;
        }

        Sample();
        SaveState();
        _capture = new NpcapCapture(AddCapturedTraffic);
    }

    public long ReceivedBytes => _state.ReceivedBytes;
    public long SentBytes => _state.SentBytes;
    public long TotalBytes => SaturatingAdd(_state.ReceivedBytes, _state.SentBytes);
    public bool ThresholdAlertSent => _state.ThresholdAlertSent;
    public bool ShouldShowThresholdAlert => TotalBytes > DailyAlertThresholdBytes && !_state.ThresholdAlertSent;
    public TimeSpan ResetTime => _state.ResetTime;
    public string CaptureStatus => !string.IsNullOrEmpty(_persistenceError)
        ? $"Usage save failed: {_persistenceError}"
        : _capture?.Status ?? "Starting Npcap capture";

    public DateOnly CurrentCycleDate => _state.Date;

    public DateOnly EarliestRecordedDate
    {
        get
        {
            DateOnly earliest = _state.Date;
            foreach (string key in _state.History.Keys)
                if (DateOnly.TryParseExact(key, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateOnly date) && date < earliest)
                    earliest = date;
            return earliest;
        }
    }

    public (long ReceivedBytes, long SentBytes) GetUsageForDate(DateOnly date)
    {
        long received = 0;
        long sent = 0;
        if (_state.History.TryGetValue(date.ToString("yyyy-MM-dd"), out DailyUsage archived))
        {
            received = archived.ReceivedBytes;
            sent = archived.SentBytes;
        }
        if (date == _state.Date)
        {
            received = SaturatingAdd(received, _state.ReceivedBytes);
            sent = SaturatingAdd(sent, _state.SentBytes);
        }
        return (received, sent);
    }

    public bool HasUsageForDate(DateOnly date) =>
        date == _state.Date || _state.History.ContainsKey(date.ToString("yyyy-MM-dd"));

    public int[] GetPingSamplesForDate(DateOnly date)
    {
        if (date == _state.Date)
            return _state.PingSamples.ToArray();
        return _state.History.TryGetValue(date.ToString("yyyy-MM-dd"), out DailyUsage archived)
            ? archived.PingSamples.ToArray()
            : Array.Empty<int>();
    }

    public void RecordPingSample(int milliseconds)
    {
        _state.PingSamples.Add(milliseconds);
        if (_state.PingSamples.Count > 60)
            _state.PingSamples.RemoveRange(0, _state.PingSamples.Count - 60);
    }

    public void SetResetTime(TimeSpan resetTime)
    {
        if (resetTime < TimeSpan.Zero || resetTime >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(resetTime));
        if (_state.ResetTime == resetTime)
            return;

        Sample();
        ArchiveCurrentUsage();
        _state.ResetTime = resetTime;
        _state.Date = GetCycleDate(DateTime.Now, resetTime);
        _state.ReceivedBytes = 0;
        _state.SentBytes = 0;
        _state.ThresholdAlertSent = false;
        _state.PingSamples.Clear();
        _state.LastUptimeMilliseconds = Environment.TickCount64;
        SaveState();
    }

    public void MarkThresholdAlertSent()
    {
        _state.ThresholdAlertSent = true;
        SaveState();
    }

    public void Sample()
    {
        DateOnly cycleDate = GetCycleDate(DateTime.Now, _state.ResetTime);
        if (_state.Date != cycleDate)
        {
            ArchiveCurrentUsage();
            _state.Date = cycleDate;
            _state.ReceivedBytes = 0;
            _state.SentBytes = 0;
            _state.ThresholdAlertSent = false;
            _state.PingSamples.Clear();
        }

        long received = Interlocked.Exchange(ref _pendingReceivedBytes, 0);
        long sent = Interlocked.Exchange(ref _pendingSentBytes, 0);
        _state.ReceivedBytes = SaturatingAdd(_state.ReceivedBytes, received);
        _state.SentBytes = SaturatingAdd(_state.SentBytes, sent);
        _state.LastUptimeMilliseconds = Environment.TickCount64;
        SaveState();
    }

    private void AddCapturedTraffic(long received, long sent)
    {
        if (received > 0)
            Interlocked.Add(ref _pendingReceivedBytes, received);
        if (sent > 0)
            Interlocked.Add(ref _pendingSentBytes, sent);
    }

    internal static HashSet<Guid> GetPhysicalInterfaceIds()
    {
        var ids = new HashSet<Guid>();
        IntPtr table = IntPtr.Zero;
        uint result = GetIfTable2(out table);
        if (result != 0 || table == IntPtr.Zero)
        {
            if (table != IntPtr.Zero)
                FreeMibTable(table);
            return null;
        }

        try
        {
            int count = Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<MibIfRow2>();
            const int firstRowOffset = 8; // ULONG count followed by 8-byte-aligned rows.
            for (int i = 0; i < count; i++)
            {
                IntPtr rowPointer = IntPtr.Add(table, firstRowOffset + i * rowSize);
                MibIfRow2 row = Marshal.PtrToStructure<MibIfRow2>(rowPointer);
                if (!IsTrackableInterface(row.Type, row.InterfaceAndOperStatusFlags, row.OperStatus))
                    continue;
                ids.Add(row.InterfaceGuid);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            FreeMibTable(table);
        }
        return ids;
    }

    // Track physical network interfaces only. Packet capture uses these IDs to
    // avoid loopback, VPN tunnel, and other software adapters.
    internal static bool IsTrackableInterface(uint interfaceType, byte interfaceFlags, uint operationalStatus) =>
        (interfaceFlags & InterfaceFlagHardware) != 0 &&
        operationalStatus == 1 &&
        interfaceType != InterfaceTypeSoftwareLoopback &&
        interfaceType != InterfaceTypeTunnel;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    private UsageState LoadState(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UsageState>(File.ReadAllText(path)) ?? new UsageState();
        }
        catch (Exception ex)
        {
            _persistenceError = $"could not read saved usage ({ex.Message})";
        }
        return new UsageState();
    }

    private void SaveState()
    {
        try
        {
            string directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state));
            File.Move(temporaryPath, _statePath, true);
            _persistenceError = null;
        }
        catch (Exception ex)
        {
            _persistenceError = ex.Message;
        }
    }

    private static long SaturatingAdd(long total, long amount) =>
        amount > long.MaxValue - total ? long.MaxValue : total + amount;

    private void ArchiveCurrentUsage()
    {
        string key = _state.Date.ToString("yyyy-MM-dd");
        if (!_state.History.TryGetValue(key, out DailyUsage archived))
            archived = new DailyUsage();
        archived.ReceivedBytes = SaturatingAdd(archived.ReceivedBytes, _state.ReceivedBytes);
        archived.SentBytes = SaturatingAdd(archived.SentBytes, _state.SentBytes);
        archived.PingSamples = new List<int>(_state.PingSamples);
        _state.History[key] = archived;
    }

    private static DateOnly GetCycleDate(DateTime localNow, TimeSpan resetTime)
    {
        DateTime cycleStartDate = localNow.TimeOfDay < resetTime
            ? localNow.Date.AddDays(-1)
            : localNow.Date;
        return DateOnly.FromDateTime(cycleStartDate);
    }

    private sealed class UsageState
    {
        public UsageState() { }

        public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
        public TimeSpan ResetTime { get; set; } = TimeSpan.Zero;
        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
        public bool ThresholdAlertSent { get; set; }
        public long LastUptimeMilliseconds { get; set; }
        public int CounterVersion { get; set; }
        public Dictionary<string, DailyUsage> History { get; set; } = new(StringComparer.Ordinal);
        public List<int> PingSamples { get; set; } = new();
    }

    public void Dispose()
    {
        _capture?.Dispose();
        Sample();
    }

    private sealed class DailyUsage
    {
        public DailyUsage() { }

        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
        public List<int> PingSamples { get; set; } = new();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}
