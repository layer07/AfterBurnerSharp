// SPDX-License-Identifier: GPL-2.0-or-later
/*
 * AfterBurnerSharp.cs
 *
 * Version: @(#)AfterBurnerSharp.cs 1.0.0 22/10/2026
 *
 * Description: MSI Afterburner Hardware Monitoring Shared Memory Interface.
 *              Reads sensor values and GPU metadata from the MAHM shared
 *              memory published by MSI Afterburner ("MAHMSharedMemory").
 *              Exposes them in a DTO shape aligned with HWHash.
 *
 *              Not affiliated with or endorsed by MSI or MSI Afterburner.
 *              "Afterburner" appears in the name descriptively to identify
 *              the upstream data source. All trademarks belong to their
 *              respective owners.
 *
 * Author: Magnus Leatti (Forbannet)
 * URL: https://kernelriot.com
 * Github: /layer07
 *
 *        ██▓    ▄▄▄     ▓██   ██▓▓█████  ██▀███
 *       ▓██▒   ▒████▄    ▒██  ██▒▓█   ▀ ▓██ ▒ ██▒
 *       ▒██░   ▒██  ▀█▄   ▒██ ██░▒███   ▓██ ░▄█ ▒
 *       ▒██░   ░██▄▄▄▄██  ░ ▐██▓░▒▓█ ▄ ▒██▀▀█▄
 *       ░██████▒▓█   ▓██▒ ░ ██▒▓░░▒████▒░██▓ ▒██▒
 *       ░ ▒░▓  ░▒▒   ▓▒█░  ██▒▒▒ ░░ ▒░ ░░ ▒▓ ░▒▓░
 *       ░ ░ ▒  ░ ▒   ▒▒ ░▓██ ░▒░  ░ ░  ░  ░▒ ░ ▒░
 *         ░ ░    ░   ▒   ▒ ▒ ░░     ░     ░░   ░
 *           ░  ░     ░  ░░ ░        ░  ░   ░
 */
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class AfterBurnerSharp
{
    private const string SHARED_MEM_PATH = "MAHMSharedMemory"; // session-local, no Global\
    private const int MAX_PATH_LEN = 260;

    // 'MAHM' as a C multi-char constant, which lands in memory as the bytes "MHAM". 0xDEAD on shutdown.
    private const uint SIG_VALID = 0x4D41484D;
    private const uint SIG_SHUTDOWN = 0xDEAD;
    private const uint MIN_VERSION = 0x00020000; // v2.0 added the per-entry gpu index and source id
    private const uint NO_INSTANCE = 0xFFFFFFFF;

    // A crashed Afterburner never writes 0xDEAD, and our open handle keeps its dead section alive under the name,
    // so a restarted Afterburner can't publish. If its poll timestamp stops moving this long, let go and reopen.
    private static readonly TimeSpan STALE_AFTER = TimeSpan.FromSeconds(5);

    // native offsets inside an entry: 5 strings, then data, minLimit, maxLimit, flags, gpu, srcId
    private static readonly int OFF_DATA = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.data));
    private static readonly int OFF_MIN_LIMIT = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.minLimit));
    private static readonly int OFF_MAX_LIMIT = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.maxLimit));
    private static readonly int OFF_FLAGS = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.dwFlags));
    private static readonly int OFF_GPU = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.gpu));
    private static readonly int OFF_SRC_ID = (int)Marshal.OffsetOf<MAHM_ENTRY>(nameof(MAHM_ENTRY.srcId));
    private static readonly int SIZE_HEADER = Marshal.SizeOf<MAHM_HEADER>();
    private static readonly int SIZE_ENTRY = Marshal.SizeOf<MAHM_ENTRY>(), SIZE_GPU = Marshal.SizeOf<MAHM_GPU_ENTRY>();

    private static readonly object _sync = new();
    private static readonly object _statsLock = new();

    private static MemoryMappedFile? _memMap;
    private static MemoryMappedViewAccessor? _view;
    private static unsafe byte* _base;
    private static MAHM_HEADER _layout;
    private static AfterBurnerStats _stats;
    private static long _pollTime;
    private static uint _lastHeaderTime;
    private static long _lastHeaderTimeChange;
    private static bool _lastReadOk;
    private static CancellationTokenSource? _pollingCTS;
    private static Task? _pollingTask;
    private static AB_GPU[] _gpus = [];
    private static ulong[] _slotKey = [], _slotUid = [];
    private static int _sensorSetVersion;
    private static RelevantCache? _relevant;

    public static readonly ConcurrentDictionary<ulong, AB_SENSOR> Sensors = new();
    public static readonly ConcurrentDictionary<ulong, AB_SENSOR_MINI> SensorsMini = new();

    private static readonly FrozenSet<string> RelevantSensorsSet = new HashSet<string>
    {
        "GPU temperature", "GPU usage", "Memory usage", "Core clock", "Memory clock",
        "Power", "Fan speed", "Fan tachometer", "GPU voltage",
        "CPU temperature", "CPU usage", "CPU clock", "CPU power",
        "RAM usage", "Commit charge", "Framerate", "Frametime"
    }.ToFrozenSet();

    /// <summary>Raised on the polling thread after every successful read cycle.</summary>
    public static event Action? Updated;

    /// <summary>True while Afterburner's shared memory is open and valid.</summary>
    public static unsafe bool IsConnected => _base != null;

    /// <summary>Afterburner's own timestamp of its last poll. Stops moving when the data goes stale.</summary>
    public static DateTimeOffset LastUpdate => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _pollTime));

    /// <summary>Why the last connect or read failed, empty when healthy.</summary>
    public static string LastFailureReason { get; private set; } = "";

    /// <summary>GPUs reported by Afterburner, index matches AB_SENSOR.Instance for GPU sensors.</summary>
    public static IReadOnlyList<AB_GPU> Gpus => _gpus;

    public static AB_HEADER_INFO HeaderInfo
    {
        get
        {
            var h = _layout;
            return new(h.signature, h.version, h.headerSize, h.entryCount, h.entrySize, h.time, h.gpuEntryCount, h.gpuEntrySize);
        }
    }

    private static int _delayMs = 1000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetDelay() => _delayMs;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SetDelay(int ms)
    {
        if (ms < 20 || ms > 60000) return false;
        _delayMs = ms;
        return true;
    }

    public static bool Launch()
    {
        lock (_sync)
        {
            if (_pollingCTS != null) return true;

            ReadSensors();
            if (!_lastReadOk)
            {
                Debug.WriteLine($"[AfterBurnerSharp] {LastFailureReason}");
                return false;
            }

            _pollingCTS = new CancellationTokenSource();
            _pollingTask = PollSensorsAsync(_pollingCTS.Token);
            return true;
        }
    }

    public static void Stop()
    {
        lock (_sync)
        {
            _pollingCTS?.Cancel();
            _pollingCTS = null;
        }
    }

    public static async Task StopAsync()
    {
        Task? task;
        lock (_sync)
        {
            _pollingCTS?.Cancel();
            _pollingCTS = null;
            task = _pollingTask;
        }
        if (task != null) await task;
    }

    public static string GetJsonString(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedList(), AfterBurnerJsonContext.Default.ListAB_SENSOR) :
                JsonSerializer.Serialize(Sensors, AfterBurnerJsonContext.Default.ConcurrentDictionaryUInt64AB_SENSOR);

    public static string GetJsonStringMini(bool order = false) =>
        order ? JsonSerializer.Serialize(GetOrderedListMini(), AfterBurnerJsonContext.Default.ListAB_SENSOR_MINI) :
                JsonSerializer.Serialize(SensorsMini, AfterBurnerJsonContext.Default.ConcurrentDictionaryUInt64AB_SENSOR_MINI);

    public static AfterBurnerStats GetStats()
    {
        lock (_statsLock) return _stats;
    }

    public static List<AB_SENSOR> GetOrderedList() => Ordered(Sensors, static s => s.IndexOrder);

    public static List<AB_SENSOR_MINI> GetOrderedListMini() => Ordered(SensorsMini, static s => s.IndexOrder);

    // IndexOrder is the dense slot in Afterburner's entry array, so each value drops straight into place: O(n), no sort.
    // Falls back to a sort if the orders are ever not a clean 0..n-1 permutation.
    private static List<T> Ordered<T>(ConcurrentDictionary<ulong, T> source, Func<T, int> order)
    {
        var list = new List<T>(source.Count);
        CollectionsMarshal.SetCount(list, list.Capacity);
        var span = CollectionsMarshal.AsSpan(list);
        var placed = new bool[span.Length];
        bool dense = true;
        int n = 0;

        foreach (var kv in source)
        {
            int i = order(kv.Value);
            if (n == span.Length || (uint)i >= (uint)span.Length || placed[i]) { dense = false; break; }
            span[i] = kv.Value;
            placed[i] = true;
            n++;
        }

        if (dense && n == span.Length) return list;

        list.Clear();
        foreach (var kv in source) list.Add(kv.Value);
        list.Sort((a, b) => order(a).CompareTo(order(b)));
        return list;
    }

    public static List<AB_SENSOR> GetRelevantList()
    {
        // versioned so a build that races with a sensor-set change can never be cached as current
        int version = Volatile.Read(ref _sensorSetVersion);
        var cache = _relevant;
        if (cache == null || cache.Version != version)
            _relevant = cache = new RelevantCache(version, BuildRelevant());

        var list = new List<AB_SENSOR>(cache.Items.Length);

        foreach (var (uid, clean) in cache.Items)
        {
            if (Sensors.TryGetValue(uid, out var sensor))
                list.Add(sensor with { NameCustom = clean });
        }

        return list;
    }

    private static (ulong, string)[] BuildRelevant()
    {
        var found = new List<AB_SENSOR>();

        foreach (var kv in Sensors)
        {
            if (RelevantSensorsSet.Contains(StripPrefix(kv.Value.NameDefault)))
                found.Add(kv.Value);
        }

        found.Sort(static (a, b) => a.IndexOrder.CompareTo(b.IndexOrder));
        return found.Select(s => (s.UniqueID, s.NameDefault.Replace(" ", "").Replace("/", "").Replace("\\", "") + s.SensorIndex)).ToArray();
    }

    private static async Task PollSensorsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_delayMs));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (timer.Period.TotalMilliseconds != _delayMs)
                    timer.Period = TimeSpan.FromMilliseconds(_delayMs);

                long start = Stopwatch.GetTimestamp();
                ReadSensors();
                long elapsed = Stopwatch.GetTimestamp() - start;

                double ms = elapsed * 1000.0 / Stopwatch.Frequency;
                lock (_statsLock) _stats = _stats with { CollectionTime = ms, CollectionTimeTicks = elapsed };

                if (_lastReadOk && Updated is { } handler)
                {
                    try { handler(); }
                    catch (Exception ex) { Debug.WriteLine($"[AfterBurnerSharp] Updated handler threw: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static unsafe void ReadSensors()
    {
        lock (_sync)
        {
            _lastReadOk = false;

            try
            {
                if (_base == null && !Connect()) return;

                var h = Unsafe.ReadUnaligned<MAHM_HEADER>(_base);
                if (!IsUsable(h))
                {
                    // Afterburner exited (0xDEAD) or restarted: drop the handle so it can recreate the section, retry next cycle
                    Disconnect();
                    if (!Connect()) return;
                    h = Unsafe.ReadUnaligned<MAHM_HEADER>(_base);
                    if (!IsUsable(h)) return;
                }

                long now = Stopwatch.GetTimestamp();
                if (h.time != _lastHeaderTime)
                {
                    _lastHeaderTime = h.time;
                    _lastHeaderTimeChange = now;
                }
                else if (Stopwatch.GetElapsedTime(_lastHeaderTimeChange, now) > STALE_AFTER)
                {
                    Debug.WriteLine("[AfterBurnerSharp] Afterburner stopped updating, reopening shared memory.");
                    Disconnect();
                    if (!Connect())
                    {
                        LastFailureReason = $"Afterburner stopped updating for {STALE_AFTER.TotalSeconds:F0}s (crashed?), waiting for it to come back.";
                        return;
                    }
                    h = Unsafe.ReadUnaligned<MAHM_HEADER>(_base);
                    if (!IsUsable(h)) return;
                }

                if (!SameLayout(h, _layout) || !UpdatePass(h))
                    RebuildPass(h);

                lock (_statsLock) _stats = _stats with { TotalGpus = h.gpuEntryCount, TotalEntries = h.entryCount, AfterburnerPollTime = h.time };
                Interlocked.Exchange(ref _pollTime, h.time);
                LastFailureReason = "";
                _lastReadOk = true;
            }
            catch (Exception ex)
            {
                LastFailureReason = $"Error reading sensors: {ex.Message}";
                Debug.WriteLine($"[AfterBurnerSharp] {LastFailureReason}");
            }
        }
    }

    // Hot path. No strings, no marshaling: verify each slot still holds the same (srcId, gpu) as last rebuild,
    // read the raw floats, skip the write (and the dictionary node allocation) when nothing changed.
    // Returns false when the slots moved under us (sensors reordered in Afterburner) so the caller rebuilds.
    private static unsafe bool UpdatePass(in MAHM_HEADER h)
    {
        byte* entries = _base + h.headerSize;
        int count = (int)h.entryCount, stride = (int)h.entrySize;

        for (int i = 0; i < count; i++)
        {
            byte* e = entries + (long)i * stride;
            ulong key = ((ulong)Unsafe.ReadUnaligned<uint>(e + OFF_SRC_ID) << 32) | Unsafe.ReadUnaligned<uint>(e + OFF_GPU);
            if (key != _slotKey[i]) return false;

            ulong uid = _slotUid[i];
            if (!Sensors.TryGetValue(uid, out var s)) return false;

            double value = CleanValue(Unsafe.ReadUnaligned<float>(e + OFF_DATA));
            float minLimit = Unsafe.ReadUnaligned<float>(e + OFF_MIN_LIMIT);
            float maxLimit = Unsafe.ReadUnaligned<float>(e + OFF_MAX_LIMIT);
            uint flags = Unsafe.ReadUnaligned<uint>(e + OFF_FLAGS);

            UpdateValues(uid, s, value, minLimit, maxLimit, flags);
        }

        return true;
    }

    // MAHM only carries the current value: min/max are kept per session, avg is an EMA (alpha 0.1).
    // Within 1e-9 relative the avg snaps to the value: a steady sensor stops being rewritten after ~200 cycles
    // instead of crawling through denormals for thousands of them.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Ema(double avg, double value)
    {
        double next = avg + 0.1 * (value - avg);
        return Math.Abs(value - next) <= 1e-9 * Math.Max(Math.Abs(value), 1.0) ? value : next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateValues(ulong uid, in AB_SENSOR s, double value, float minLimit, float maxLimit, uint flags)
    {
        double min = Math.Min(s.ValueMin, value);
        double max = Math.Max(s.ValueMax, value);
        double avg = Ema(s.ValueAvg, value);

        if (!Same(s.ValueNow, value) || !Same(s.ValuePrev, s.ValueNow) || !Same(s.ValueMin, min) ||
            !Same(s.ValueMax, max) || !Same(s.ValueAvg, avg) || !Same(s.MinLimit, minLimit) ||
            !Same(s.MaxLimit, maxLimit) || s.Flags != flags)
        {
            Sensors[uid] = s with
            {
                ValuePrev = s.ValueNow,
                ValueNow = value,
                ValueMin = min,
                ValueMax = max,
                ValueAvg = avg,
                MinLimit = minLimit,
                MaxLimit = maxLimit,
                Flags = flags
            };
        }

        if (SensorsMini.TryGetValue(uid, out var mini) &&
            (!Same(mini.ValueNow, value) || !Same(mini.ValuePrev, mini.ValueNow)))
        {
            SensorsMini[uid] = mini with
            {
                ValuePrev = mini.ValueNow,
                ValueNow = value
            };
        }
    }

    // Cold path: first connect, layout change or reordered slots. Full parse, names refreshed,
    // session min/max/avg carried over for sensors that survive, vanished ones removed.
    private static unsafe void RebuildPass(in MAHM_HEADER h)
    {
        BuildGpus(h);

        byte* entries = _base + h.headerSize;
        int count = (int)h.entryCount, stride = (int)h.entrySize;
        var slotKey = new ulong[count];
        var slotUid = new ulong[count];
        var seen = new HashSet<ulong>(count);

        for (int i = 0; i < count; i++)
        {
            var e = Marshal.PtrToStructure<MAHM_ENTRY>((IntPtr)(entries + (long)i * stride));

            // (srcId, instance) identifies a sensor and survives reordering; plugins can repeat it, so fall back to the slot
            ulong uid = MakeUID(e.srcId, e.gpu);
            if (!seen.Add(uid))
            {
                uid = MakeUID(e.srcId, 0x80000000u | (uint)i);
                seen.Add(uid);
            }

            slotKey[i] = ((ulong)e.srcId << 32) | e.gpu;
            slotUid[i] = uid;
            AddOrReplace(uid, e, i);
        }

        foreach (var uid in Sensors.Keys)
        {
            if (!seen.Contains(uid))
            {
                Sensors.TryRemove(uid, out _);
                SensorsMini.TryRemove(uid, out _);
            }
        }

        _slotKey = slotKey;
        _slotUid = slotUid;
        _layout = h;
        Interlocked.Increment(ref _sensorSetVersion);
    }

    private static void AddOrReplace(ulong uid, in MAHM_ENTRY e, int order)
    {
        string name = CleanString(e.szSrcName);
        string local = CleanString(e.szLocalizedSrcName);
        string units = CleanString(e.szSrcUnits);
        string format = CleanString(e.szRecommendedFormat);
        double value = CleanValue(e.data);

        string readingType = TypeOf(e.srcId, units, name);
        (string parentName, uint parentId) = ResolveParent(e.srcId, e.gpu);

        string display = string.IsNullOrEmpty(local) ? name : local;
        bool known = Sensors.TryGetValue(uid, out var old);

        Sensors[uid] = new AB_SENSOR(
            readingType,
            (uint)order,
            e.srcId,
            uid,
            name,
            display,
            units,
            value,
            known ? Math.Min(old.ValueMin, value) : value,
            known ? Math.Max(old.ValueMax, value) : value,
            known ? Ema(old.ValueAvg, value) : value,
            known ? old.ValueNow : value,
            parentName,
            parentName,
            parentId,
            0,
            MakeUID(parentId, 0),
            order,
            e.minLimit,
            e.maxLimit,
            e.dwFlags,
            e.gpu,
            format
        );

        double prevMini = SensorsMini.TryGetValue(uid, out var oldMini) ? oldMini.ValueNow : value;
        SensorsMini[uid] = new AB_SENSOR_MINI(uid, display, units, prevMini, value, order, readingType);
    }

    private static unsafe void BuildGpus(in MAHM_HEADER h)
    {
        var gpus = new AB_GPU[h.gpuEntryCount];
        byte* basePtr = _base + h.headerSize + (long)h.entryCount * h.entrySize;

        for (int i = 0; i < gpus.Length; i++)
        {
            var g = Marshal.PtrToStructure<MAHM_GPU_ENTRY>((IntPtr)(basePtr + (long)i * h.gpuEntrySize));
            gpus[i] = new AB_GPU(i, CleanString(g.szGpuId), CleanString(g.szFamily), CleanString(g.szDevice),
                                 CleanString(g.szDriver), CleanString(g.szBIOS), g.dwMemAmount);
        }

        _gpus = gpus;
    }

    // Afterburner writes FLT_MAX when a sensor has no reading (classic case: Framerate with no game hooked to RTSS).
    // Clamp to 0 so widgets don't render garbage.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CleanValue(float v) => float.IsNaN(v) || float.IsInfinity(v) || v >= 3.0e38f ? 0.0 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    private static string CleanString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Strip null terminators, CR, LF that occasionally leak from Afterburner's ANSI buffer.
        int end = s.AsSpan().IndexOfAny('\0', '\r', '\n');
        return end < 0 ? s : s[..end];
    }

    // Source ids are Afterburner's MONITORING_SOURCE_ID_* constants: 0x00-0x7F GPU side, 0x80+ CPU/system, 0xE0+ plugins.
    private static string TypeOf(uint srcId, string units, string name)
    {
        if (srcId is >= 0x70 and <= 0x7F) return "Flag"; // Temp/Power/Voltage/No load limit: throttle reasons, 0 or 1
        if (srcId == 0x50) return "Framerate";
        if (srcId == 0x51) return "Frametime";

        if (string.IsNullOrEmpty(units))
            return name.EndsWith("limit", StringComparison.OrdinalIgnoreCase) ? "Flag" : "None";

        // MAHM unit strings are ANSI, so "°C" may come through as "\xB0C": match by tail.
        if (units.EndsWith('C') || units.Contains('°')) return "Temperature";
        if (units == "V" || units.EndsWith("mV")) return "Voltage";
        if (units == "RPM") return "Fan";
        if (units == "A" || units.EndsWith("mA")) return "Current";
        if (units == "W" || units.EndsWith("mW")) return "Power";
        if (units is "MHz" or "GHz" or "Hz") return "Frequency";
        if (units == "%") return "Usage";
        if (units is "MB" or "GB" or "KB") return "Memory";
        if (units == "FPS") return "Framerate";
        if (units == "ms") return "Frametime";

        return "Other";
    }

    // MAHM's "gpu" field is really an instance index: the GPU for GPU sources, the core for per-core CPU sources,
    // 0 for RAM, 0xFFFFFFFF for aggregates. So the parent comes from the source id, not from the index alone.
    private static (string Name, uint Id) ResolveParent(uint srcId, uint instance)
    {
        bool gpuSource = srcId < 0x50 || srcId is >= 0x60 and <= 0x7F || srcId == 0xE0;
        if (!gpuSource || instance >= (uint)_gpus.Length)
            return ("System", 0);

        string device = _gpus[instance].Device;
        return (string.IsNullOrEmpty(device) ? $"GPU{instance}" : device, instance + 1); // +1 so System (0) doesn't collide with GPU 0
    }

    private static string StripPrefix(string name)
    {
        // "GPU1 temperature" -> "GPU temperature"; "CPU1 usage" -> "CPU usage"
        if (name.Length < 5) return name;
        if ((name.StartsWith("GPU") || name.StartsWith("CPU")) && char.IsDigit(name[3]))
        {
            int sp = name.IndexOf(' ', 3);
            if (sp > 0) return name[..3] + name[sp..];
        }
        return name;
    }

    // One persistent view for the lifetime of the connection instead of a map/copy per poll.
    // Probes with OpenFileMappingW first so a missing Afterburner costs no first-chance exception per cycle.
    private static unsafe bool Connect()
    {
        var probe = OpenFileMappingW(FILE_MAP_READ, false, SHARED_MEM_PATH);
        if (probe == IntPtr.Zero)
        {
            LastFailureReason = "MSI Afterburner shared memory not found (Afterburner not running, or shared memory disabled).";
            return false;
        }
        CloseHandle(probe);

        try
        {
            _memMap = MemoryMappedFile.OpenExisting(SHARED_MEM_PATH, MemoryMappedFileRights.Read);
            _view = _memMap.CreateViewAccessor(0L, 0L, MemoryMappedFileAccess.Read);

            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _base = ptr + _view.PointerOffset;

            var h = Unsafe.ReadUnaligned<MAHM_HEADER>(_base);
            if (IsUsable(h))
            {
                // a fresh connection gets a full stale window before it can be judged frozen
                _lastHeaderTime = h.time;
                _lastHeaderTimeChange = Stopwatch.GetTimestamp();
                return true;
            }
        }
        catch (Exception ex)
        {
            LastFailureReason = $"Error opening shared memory: {ex.Message}";
        }

        Disconnect();
        return false;
    }

    private static unsafe void Disconnect()
    {
        if (_base != null)
        {
            _view!.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view?.Dispose();
        _view = null;
        _memMap?.Dispose();
        _memMap = null;
        _layout = default;
    }

    private static bool IsUsable(in MAHM_HEADER h)
    {
        string reason =
            h.signature == SIG_SHUTDOWN ? "Afterburner is shutting down (signature 0xDEAD)." :
            h.signature != SIG_VALID ? $"Bad signature 0x{h.signature:X8}, expected 0x{SIG_VALID:X8}." :
            h.version < MIN_VERSION ? $"Unsupported MAHM version 0x{h.version:X8}, need 0x{MIN_VERSION:X8} or newer." :
            h.headerSize < SIZE_HEADER || h.entrySize < SIZE_ENTRY || (h.gpuEntryCount > 0 && h.gpuEntrySize < SIZE_GPU) ? "Unexpected MAHM entry sizes." :
            (ulong)h.headerSize + (ulong)h.entryCount * h.entrySize + (ulong)h.gpuEntryCount * h.gpuEntrySize > (ulong)_view!.Capacity ? "MAHM layout exceeds the mapped view." :
            "";

        if (reason.Length == 0) return true;
        LastFailureReason = reason;
        return false;
    }

    private static bool SameLayout(in MAHM_HEADER a, in MAHM_HEADER b) =>
        a.headerSize == b.headerSize && a.entryCount == b.entryCount && a.entrySize == b.entrySize &&
        a.gpuEntryCount == b.gpuEntryCount && a.gpuEntrySize == b.gpuEntrySize;

    // UID namespace: keeps AfterBurnerSharp sensor IDs from ever colliding with HWHash (native HWiNFO low values).
    // All UIDs live in the top band 0x4D53_xxxx_xxxx_xxxx ('MS' prefix). Low 48 bits = (srcId, instance).
    private const ulong UID_NAMESPACE = 0x4D53_0000_0000_0000UL;
    private const ulong UID_PAYLOAD_MASK = 0x0000_FFFF_FFFF_FFFFUL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MakeUID(uint a, uint b) => UID_NAMESPACE | ((((ulong)a << 32) | b) & UID_PAYLOAD_MASK);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint FILE_MAP_READ = 0x0004;

    private sealed record RelevantCache(int Version, (ulong UID, string CleanName)[] Items);

    public readonly record struct AfterBurnerStats(
        double CollectionTime,
        long CollectionTimeTicks,
        uint TotalGpus,
        uint TotalEntries,
        uint AfterburnerPollTime
    );

    public readonly record struct AB_SENSOR(
        string ReadingType,
        uint SensorIndex,
        uint SensorID,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        ulong UniqueID,
        string NameDefault,
        string NameCustom,
        string Unit,
        double ValueNow,
        double ValueMin,
        double ValueMax,
        double ValueAvg,
        double ValuePrev,
        string ParentNameDefault,
        string ParentNameCustom,
        uint ParentID,
        uint ParentInstance,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        ulong ParentUniqueID,
        int IndexOrder,
        float MinLimit,
        float MaxLimit,
        uint Flags,
        uint Instance,
        string Format
    );

    public readonly record struct AB_SENSOR_MINI(
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
        ulong UniqueID,
        string NameCustom,
        string Unit,
        double ValuePrev,
        double ValueNow,
        [property: JsonIgnore] int IndexOrder,
        [property: JsonIgnore] string ReadingType
    );

    public readonly record struct AB_GPU(
        int Index,
        string GpuId,
        string Family,
        string Device,
        string Driver,
        string Bios,
        uint MemoryAmount
    );

    public readonly record struct AB_HEADER_INFO(
        uint Signature, uint Version, uint HeaderSize,
        uint EntryCount, uint EntrySize, uint PollTime,
        uint GpuEntryCount, uint GpuEntrySize
    );

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MAHM_HEADER
    {
        public uint signature;     // 'MAHM' (bytes "MHAM") or 0xDEAD
        public uint version;       // (MAJOR << 16) | MINOR
        public uint headerSize;
        public uint entryCount;
        public uint entrySize;
        public uint time;          // last Afterburner poll, unix seconds
        public uint gpuEntryCount;
        public uint gpuEntrySize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct MAHM_ENTRY
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szSrcName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szSrcUnits;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szLocalizedSrcName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szLocalizedSrcUnits;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szRecommendedFormat;
        public float data;
        public float minLimit;
        public float maxLimit;
        public uint dwFlags;     // bit 0 show in OSD, bit 1 LCD, bit 2 tray
        public uint gpu;         // instance index, 0xFFFFFFFF = aggregate
        public uint srcId;       // MONITORING_SOURCE_ID_*
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct MAHM_GPU_ENTRY
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szGpuId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szDevice;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szDriver;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH_LEN)]
        public string szBIOS;
        public uint dwMemAmount;
    }
}

// Source-generated serializers: no runtime reflection, trim/AOT friendly.
[JsonSerializable(typeof(List<AfterBurnerSharp.AB_SENSOR>))]
[JsonSerializable(typeof(List<AfterBurnerSharp.AB_SENSOR_MINI>))]
[JsonSerializable(typeof(ConcurrentDictionary<ulong, AfterBurnerSharp.AB_SENSOR>))]
[JsonSerializable(typeof(ConcurrentDictionary<ulong, AfterBurnerSharp.AB_SENSOR_MINI>))]
internal partial class AfterBurnerJsonContext : JsonSerializerContext { }
