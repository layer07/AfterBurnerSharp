# AfterBurnerSharp
## _AfterBurnerSharp reads MSI Afterburner's hardware monitoring shared memory in realtime and hands every sensor to you as Dictionaries, Lists and JSON._

[![GLWTPL](https://img.shields.io/badge/GLWT-Public_License-red.svg)](https://github.com/me-shaon/GLWTPL)
[![NuGet](https://img.shields.io/nuget/v/AfterBurnerSharp.svg)](https://www.nuget.org/packages/AfterBurnerSharp)
![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)

Sibling of [HWHash](https://github.com/layer07/HWHash). Same shape, same speed obsession, different data source.

- 🦄 Single file static class with no external dependencies.
- 💨 1.4µs per read cycle when nothing changed, ~10µs when every sensor did.
- 🔌 Survives Afterburner restarts and crashes, reconnects on its own.
- 🎮 GPU, CPU, RAM and RTSS framerate, whatever Afterburner monitors.
- ✨ It simply works.

## Features

- Unique ID for each sensor, stable across restarts and reordering in Afterburner
- IDs live in their own namespace, so HWHash and AfterBurnerSharp can feed one dictionary without collisions
- Each sensor is parented to its GPU or to System, using Afterburner's own source IDs instead of guessing from names
- Session Min / Max and a running Average, since Afterburner itself only publishes the current value
- GPU info: device, family, driver, BIOS
- `Updated` event after every cycle, `IsConnected` and `LastUpdate` to catch stale data
- Exports to a List or JSON string in both Full and Minified versions
**check the minified struct version below.*

Installation
---
Nuget package is available:
```c#
NuGet\Install-Package AfterBurnerSharp
```

Usage
---

It is as simple as:
```c#
AfterBurnerSharp.Launch();
```
---
Options
---

| Option | Default |
| ------ | ------ |
| Delay | ![](https://img.shields.io/static/v1?label=&message=1000ms&color=b0a2f9) |

---
How to configure
---
*Make sure you set the parameters **before** Launching the AfterBurnerSharp thread.*

Delay:
```c#
//update the Dictionary every 500ms (twice per second)
AfterBurnerSharp.SetDelay(500);
```

Then -> ```Launch()```
```c#
AfterBurnerSharp.SetDelay(500);
if (!AfterBurnerSharp.Launch())
    Console.WriteLine(AfterBurnerSharp.LastFailureReason);
```
---
Stopping
---
```c#
//Fire and forget
AfterBurnerSharp.Stop();

//Graceful shutdown (awaits last poll cycle)
await AfterBurnerSharp.StopAsync();
```
---
Basic Functions
---
```c#
using static AfterBurnerSharp;

//Returns a List<AB_SENSOR> in the same order as Afterburner
List<AB_SENSOR> MySensors = AfterBurnerSharp.GetOrderedList();
//Same as above but in a minified version
List<AB_SENSOR_MINI> MySensorsMini = AfterBurnerSharp.GetOrderedListMini();
//Your GPUs, index matches AB_SENSOR.Instance on GPU sensors
IReadOnlyList<AB_GPU> MyGpus = AfterBurnerSharp.Gpus;
```
Live Updates
---
```c#
//Get pushed instead of polling, fires on the polling thread after every cycle
AfterBurnerSharp.Updated += () => Console.WriteLine(AfterBurnerSharp.GetJsonStringMini(true));

//Is the shared memory open and valid?
bool live = AfterBurnerSharp.IsConnected;
//Afterburner's own timestamp, stops moving when the data goes stale
DateTimeOffset last = AfterBurnerSharp.LastUpdate;
```
JSON Functions
---
```c#
//Returns a JSON string containing all sensors information (full/mini)
string _Json = AfterBurnerSharp.GetJsonString();
string _JsonMini = AfterBurnerSharp.GetJsonStringMini();
//If set to true, it will return a ordered list*
string _JsonOrdered = AfterBurnerSharp.GetJsonString(true);
//Same for the minified version
string _JsonMiniOrdered = AfterBurnerSharp.GetJsonStringMini(true);
```
*UniqueID is written as a string, the 64 bit IDs don't survive JavaScript's number type.*

Default Struct
---
Field order matches HWHash's `HWINFO_HASH`, with Afterburner extras at the end.
```c#
public readonly record struct AB_SENSOR(
    string ReadingType,
    uint SensorIndex,
    uint SensorID,          // Afterburner source id (MONITORING_SOURCE_ID_*)
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
    float MinLimit,         // graph limits set in Afterburner
    float MaxLimit,
    uint Flags,             // bit 0 OSD, bit 1 LCD, bit 2 tray
    uint Instance,          // GPU index, CPU core index, 0xFFFFFFFF = aggregate
    string Format           // Afterburner's recommended format, e.g. "%.1f"
);
```
Minified Struct
---
The minified version is more suitable for 'realtime' monitoring, since it is packed in a much smaller package.
```c#
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
GPU Struct
---
```c#
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
Relevant Sensor List
---
If you prefer to avoid manually searching for sensor IDs and wish to access a curated List<AB_SENSOR> of relevant sensors directly, use this function.

Per-core sensors count too, "CPU7 usage" matches "CPU usage".
```c#
List<AB_SENSOR> MyRelevantSensors = AfterBurnerSharp.GetRelevantList();
```

Default relevant sensors:
```
GPU temperature, GPU usage, Memory usage, Core clock, Memory clock,
Power, Fan speed, Fan tachometer, GPU voltage,
CPU temperature, CPU usage, CPU clock, CPU power,
RAM usage, Commit charge, Framerate, Frametime
```

Performance
---
You can access the performance metrics by invoking the following method:
```c#
AfterBurnerStats _Stats = AfterBurnerSharp.GetStats();
```
 AfterBurnerStats *struct*
```c#
public readonly record struct AfterBurnerStats(
    double CollectionTime,
    long CollectionTimeTicks,
    uint TotalGpus,
    uint TotalEntries,
    uint AfterburnerPollTime
);
```
Measured live against a running Afterburner. Rig: i9-14900KF, RTX 3080, Afterburner 4.6.6, 120 sensors, .NET 9.

Before is the provider this library was ported from, same data, same machine.

| Operation | Before | AfterBurnerSharp | Speedup | Alloc before | Alloc after |
| ------ | ------: | ------: | ------: | ------: | ------: |
| Read cycle, nothing changed | 130.35µs | **1.42µs** | 92x | 65 KB | 14 B |
| Read cycle, every sensor changed | 130.35µs | **10.36µs** | 12.6x | 65 KB | 34.5 KB |
| `GetRelevantList()` | 31.44µs | **4.97µs** | 6.3x | 73.8 KB | 16.9 KB |
| `GetOrderedList()` | 16.18µs | **12.98µs** | 1.2x | 32.7 KB | 18.7 KB |
| `GetOrderedListMini()` | 15.81µs | **13.05µs** | 1.2x | 13.5 KB | 7.1 KB |
| `GetJsonString(true)` | 161.75µs | **159.15µs** | 1.0x | 166.2 KB | 160.0 KB |
| `GetJsonStringMini(true)` | 75.66µs | **71.66µs** | 1.1x | 54.8 KB | 48.4 KB |

*Afterburner refreshes about once per second, so if you poll at that rate or slower, the "every sensor changed" row is the one you live in.*

`CollectionTime` reported by the poller runs higher than the table (~30-40µs here): after sleeping a whole interval the caches are cold. Still nothing.

Notes on Afterburner Data
---
Afterburner only publishes the current value of each sensor, so Min and Max are tracked from the moment you call `Launch()`, and Average is a running average (alpha 0.1, settles over ~20 cycles).

Framerate and Frametime come from RTSS. With no game hooked, Afterburner writes a "no data" marker, AfterBurnerSharp reports 0 instead of 3.4e38.

The `Temp limit`, `Power limit`, `Voltage limit` and `No load limit` sensors are throttle flags, 0 or 1, typed as `Flag`.

Afterburner's "gpu" field is really an instance index: the GPU for GPU sensors, the core for per-core CPU sensors. `Instance` keeps the raw value, the parent is resolved from the source id.

Changelog
---

### 1.0.0
First public release. Ported from the MSI Afterburner provider inside SysPulse and rebuilt on the HWHash 1.6.0 engine.

- Persistent shared memory view, raw pointer reads, no strings on the hot path
- Writes skipped when a sensor didn't change
- Reconnects on its own when Afterburner restarts, even after a crash (frozen data detected in 5s)
- Parent and type resolved from Afterburner's source ids
- `Updated` event, `IsConnected`, `LastUpdate`, `LastFailureReason`, `Gpus`
- JSON via source generators, trim/AOT friendly

### License
This project is licensed under [GLWTPL](./LICENSE)

Not affiliated with or endorsed by MSI. MSI Afterburner is a trademark of its respective owner, the name is used only to describe the data source.

### Contact
Questions, bugs or ideas? Feel free to reach out.

**Magnus Leatti** · [magnusleatti@kernelriot.com](mailto:magnusleatti@kernelriot.com)
