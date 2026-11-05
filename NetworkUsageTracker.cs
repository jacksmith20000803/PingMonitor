using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Tracks Windows network interface byte counters and rolls the daily total over
/// using the computer's local date.
/// </summary>
internal sealed class NetworkUsageTracker
{
    public const long DailyAlertThresholdBytes = 5_000_000_000;

    private readonly string _statePath;
    private UsageState _state;

    public NetworkUsageTracker(string statePath)
    {
        _statePath = statePath;
        _state = LoadState();
        _state.Adapters ??= new Dictionary<string, AdapterCounters>(StringComparer.OrdinalIgnoreCase);
        _state.History ??= new Dictionary<string, DailyUsage>(StringComparer.Ordinal);

        // Earlier versions summed software and VPN adapters, which can overlap
        // with hardware counters. Start a clean daily total for the new source.
        if (_state.CounterVersion < 3 || _state.CounterVersion > 4)
        {
            _state.ReceivedBytes = 0;
            _state.SentBytes = 0;
            _state.ThresholdAlertSent = false;
            _state.Adapters.Clear();
            _state.History.Clear();
        }
        _state.CounterVersion = 4;

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
            _state.Adapters.Clear();
        }

        // If Windows restarted while the widget was closed, interface counters
        // may have restarted too. In that case, establish new baselines.
        if (_state.LastUptimeMilliseconds > Environment.TickCount64)
            _state.Adapters.Clear();

        Sample();
    }

    public long ReceivedBytes => _state.ReceivedBytes;
    public long SentBytes => _state.SentBytes;
    public long TotalBytes => SaturatingAdd(_state.ReceivedBytes, _state.SentBytes);
    public bool ThresholdAlertSent => _state.ThresholdAlertSent;
    public bool ShouldShowThresholdAlert => TotalBytes > DailyAlertThresholdBytes && !_state.ThresholdAlertSent;
    public TimeSpan ResetTime => _state.ResetTime;

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

    public void SetResetTime(TimeSpan resetTime)
    {
        if (resetTime < TimeSpan.Zero || resetTime >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(resetTime));
        if (_state.ResetTime == resetTime)
            return;

        ArchiveCurrentUsage();
        _state.ResetTime = resetTime;
        _state.Date = GetCycleDate(DateTime.Now, resetTime);
        _state.ReceivedBytes = 0;
        _state.SentBytes = 0;
        _state.ThresholdAlertSent = false;
        _state.Adapters = ReadCounters() ?? new Dictionary<string, AdapterCounters>(StringComparer.OrdinalIgnoreCase);
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
        }

        var current = ReadCounters();
        if (current is null)
            return;

        foreach (var item in current)
        {
            if (_state.Adapters.TryGetValue(item.Key, out var previous))
            {
                // A decrease means that the interface counter reset or wrapped.
                // Establish a fresh baseline rather than inventing a delta.
                if (item.Value.Received >= previous.Received)
                    _state.ReceivedBytes = SaturatingAdd(_state.ReceivedBytes, item.Value.Received - previous.Received);
                if (item.Value.Sent >= previous.Sent)
                    _state.SentBytes = SaturatingAdd(_state.SentBytes, item.Value.Sent - previous.Sent);
            }
        }

        _state.Adapters = current;
        _state.LastUptimeMilliseconds = Environment.TickCount64;
        SaveState();
    }

    private static Dictionary<string, AdapterCounters> ReadCounters()
    {
        var counters = new Dictionary<string, AdapterCounters>(StringComparer.OrdinalIgnoreCase);
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
                bool hardwareInterface = (row.InterfaceAndOperStatusFlags & 0x01) != 0;
                if (!hardwareInterface || row.OperStatus != 1 || row.Type is 24 or 131)
                    continue;

                counters[row.InterfaceGuid.ToString("B")] = new AdapterCounters
                {
                    Received = ToCounterValue(row.InOctets),
                    Sent = ToCounterValue(row.OutOctets)
                };
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
        return counters;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);

    private UsageState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
                return JsonSerializer.Deserialize<UsageState>(File.ReadAllText(_statePath)) ?? new UsageState();
        }
        catch { }
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
        }
        catch { }
    }

    private static long SaturatingAdd(long total, long amount) =>
        amount > long.MaxValue - total ? long.MaxValue : total + amount;

    private static long ToCounterValue(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private void ArchiveCurrentUsage()
    {
        string key = _state.Date.ToString("yyyy-MM-dd");
        if (!_state.History.TryGetValue(key, out DailyUsage archived))
            archived = new DailyUsage();
        archived.ReceivedBytes = SaturatingAdd(archived.ReceivedBytes, _state.ReceivedBytes);
        archived.SentBytes = SaturatingAdd(archived.SentBytes, _state.SentBytes);
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
        public Dictionary<string, AdapterCounters> Adapters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DailyUsage> History { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AdapterCounters
    {
        public long Received { get; set; }
        public long Sent { get; set; }
    }

    private sealed class DailyUsage
    {
        public DailyUsage() { }

        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
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
