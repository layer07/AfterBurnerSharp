# AfterBurnerSharp

AfterBurnerSharp reads MSI Afterburner's hardware monitoring shared memory in realtime and exposes every sensor through Dictionaries, Lists and JSON.

Single file, static class, no dependencies. 1.4µs per read cycle. Sibling of [HWHash](https://www.nuget.org/packages/HWHash).

## Installation

```
NuGet\Install-Package AfterBurnerSharp
```

## Quick Start

```csharp
using static AfterBurnerSharp;

AfterBurnerSharp.SetDelay(500); // optional, default 1000ms
if (!AfterBurnerSharp.Launch())
    Console.WriteLine(AfterBurnerSharp.LastFailureReason);

// ordered like Afterburner
List<AB_SENSOR> sensors = AfterBurnerSharp.GetOrderedList();

// minified version
List<AB_SENSOR_MINI> mini = AfterBurnerSharp.GetOrderedListMini();

// curated subset of common sensors
List<AB_SENSOR> relevant = AfterBurnerSharp.GetRelevantList();

// your GPUs: device, family, driver, BIOS
IReadOnlyList<AB_GPU> gpus = AfterBurnerSharp.Gpus;

// JSON export (pass true for ordered)
string json = AfterBurnerSharp.GetJsonString(true);
string jsonMini = AfterBurnerSharp.GetJsonStringMini(true);

// get pushed instead of polling
AfterBurnerSharp.Updated += () => Console.WriteLine(AfterBurnerSharp.GetJsonStringMini(true));
bool live = AfterBurnerSharp.IsConnected;
DateTimeOffset last = AfterBurnerSharp.LastUpdate; // stops moving when data goes stale

// stop polling
AfterBurnerSharp.Stop();
await AfterBurnerSharp.StopAsync(); // graceful, awaits last cycle
```

## Structs

### AB_SENSOR

```csharp
public readonly record struct AB_SENSOR(
    string ReadingType,
    uint SensorIndex,
    uint SensorID,          // Afterburner source id
    ulong UniqueID,
    string NameDefault,
    string NameCustom,      // localized name
    string Unit,
    double ValueNow,
    double ValueMin,        // session min
    double ValueMax,        // session max
    double ValueAvg,        // running average
    double ValuePrev,
    string ParentNameDefault,
    string ParentNameCustom,
    uint ParentID,
    uint ParentInstance,
    ulong ParentUniqueID,
    int IndexOrder,
    float MinLimit,
    float MaxLimit,
    uint Flags,
    uint Instance,          // GPU index, CPU core index, 0xFFFFFFFF = aggregate
    string Format           // e.g. "%.1f"
);
```

### AB_SENSOR_MINI

```csharp
public readonly record struct AB_SENSOR_MINI(
    ulong UniqueID,
    string NameCustom,
    string Unit,
    double ValuePrev,
    double ValueNow,
    [property: JsonIgnore] int IndexOrder,
    [property: JsonIgnore] string ReadingType
);
```

### AB_GPU

```csharp
public readonly record struct AB_GPU(
    int Index,
    string GpuId,
    string Family,
    string Device,
    string Driver,
    string Bios,
    uint MemoryAmount
);
```

### AfterBurnerStats

```csharp
public readonly record struct AfterBurnerStats(
    double CollectionTime,
    long CollectionTimeTicks,
    uint TotalGpus,
    uint TotalEntries,
    uint AfterburnerPollTime
);
```

## Performance

Live, i9-14900KF, RTX 3080, Afterburner 4.6.6, 120 sensors, .NET 9. Before = the provider it was ported from.

| Operation | Before | AfterBurnerSharp | Speedup |
| --- | ---: | ---: | ---: |
| Read cycle, nothing changed | 130.35µs | 1.42µs | 92x |
| Read cycle, every sensor changed | 130.35µs | 10.36µs | 12.6x |
| `GetRelevantList()` | 31.44µs | 4.97µs | 6.3x |
| `GetOrderedList()` | 16.18µs | 12.98µs | 1.2x |
| `GetJsonString(true)` | 161.75µs | 159.15µs | 1.0x |
| `GetJsonStringMini(true)` | 75.66µs | 71.66µs | 1.1x |

Afterburner refreshes about once per second, so polling at that rate means every sensor changed: the 10µs row.

## Notes

- Afterburner only publishes the current value. Min/Max are tracked from `Launch()`, Avg is a running average.
- Framerate/Frametime come from RTSS. No game hooked = 0, not 3.4e38.
- `Temp limit`, `Power limit`, `Voltage limit`, `No load limit` are throttle flags (0 or 1), typed `Flag`.
- UniqueID is written to JSON as a string, safe for JavaScript.
- IDs are namespaced, HWHash and AfterBurnerSharp can share one dictionary.

## Relevant Sensors

`GetRelevantList()` returns a curated subset stored in a `FrozenSet<string>`, per-core sensors included ("CPU7 usage" matches "CPU usage"):

GPU temperature, GPU usage, Memory usage, Core clock, Memory clock, Power, Fan speed, Fan tachometer, GPU voltage, CPU temperature, CPU usage, CPU clock, CPU power, RAM usage, Commit charge, Framerate, Frametime

## Requirements

- .NET 9.0+
- MSI Afterburner running with monitoring enabled
- Windows

## Changelog

### 1.0.0

First public release. Ported from the Afterburner provider inside SysPulse, rebuilt on the HWHash 1.6.0 engine.

- Persistent view, raw pointer reads, no strings on the hot path
- Writes skipped when a sensor didn't change
- Reconnects on its own when Afterburner restarts, even after a crash (frozen data detected in 5s)
- Parent and type from Afterburner's source ids
- `Updated`, `IsConnected`, `LastUpdate`, `LastFailureReason`, `Gpus`
- JSON via source generators

## License

[GLWTPL](https://github.com/layer07/AfterBurnerSharp/blob/main/LICENSE)

Not affiliated with or endorsed by MSI. The name only describes the data source.

## Contact

Questions, bugs or ideas? Feel free to reach out.

**Magnus Leatti** · [magnusleatti@kernelriot.com](mailto:magnusleatti@kernelriot.com)
