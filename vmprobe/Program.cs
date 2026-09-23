// Is the process's VM subsystem healthy and is MonoMod's range search simply enumerating too much?
// Times (a) mach_vm_region_recurse - the call the search is built on - and counts how many regions a
// +-2GB window contains, (b) plain mmap and malloc/free in the same process for comparison.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const ulong window = 1UL << 31;   // +-2GB, the rel32 bound detours need

var total = Stopwatch.StartNew();

var target = (ulong)typeof(Target).GetMethod(nameof(Target.Code), BindingFlags.Public | BindingFlags.Static).MethodHandle.GetFunctionPointer();
var page = 16384UL;
Console.WriteLine($"target page = 0x{target & ~(page - 1):x16}");

var task = Native.task_self_trap();

try
{

// (a) walk the region map outward from the target, exactly like the allocator's search does
var addr = target & ~(page - 1);
ulong size = 0;
var depth = int.MaxValue;
var count = 64;
var info = Marshal.AllocHGlobal(1024);
long regions = 0, queryTicks = 0;
var sw = Stopwatch.StartNew();
while (true)
{
    var distance = addr > target ? addr - target : target - addr;
    if (distance > window) break;

    var before = Stopwatch.GetTimestamp();
    var kr = Native.mach_vm_region_recurse(task, ref addr, ref size, ref depth, info, ref count);
    queryTicks += Stopwatch.GetTimestamp() - before;
    if (kr != 0 || size == 0) break;

    regions++;
    addr += size;
}
sw.Stop();
var usPerQuery = (double)queryTicks / Stopwatch.Frequency * 1e6 / Math.Max(regions, 1);
Console.WriteLine($"region walk: regions={regions} wallMs={sw.ElapsedMilliseconds} usPerQuery={usPerQuery:F1} projectedMsForRegions={usPerQuery * regions / 1000:F0}");

// MonoMod passes InfoSize = vm_region_submap_short_info_64.Count (12 ints = 48 bytes); the probe above passed
// 64 ints. If a too-small info buffer makes the kernel reject the call, every query fails and the allocator's
// search degrades to stepping one page at a time across the whole window (~480k probes).
foreach (var cnt in new[] { 12, 13, 14, 16, 32, 64 })
{
    var a2 = target & ~(page - 1);
    ulong s2 = 0; var d2 = int.MaxValue; var c2 = cnt;
    var infoBuf = Marshal.AllocHGlobal(1024);
    var kr = Native.mach_vm_region_recurse(task, ref a2, ref s2, ref d2, infoBuf, ref c2);
    Console.WriteLine($"query with count={cnt}: kr={kr} returnedSize=0x{s2:x} (0 = failure)");
    Marshal.FreeHGlobal(infoBuf);
}
}
catch (Exception e)
{
    Console.WriteLine($"region walk unavailable: {e.GetType().Name} {e.Message}");
}

// (b) is the rest of the VM path healthy?
var mallocSw = Stopwatch.StartNew();
for (int i = 0; i < 2000; i++) { var h = Marshal.AllocHGlobal(64); Marshal.FreeHGlobal(h); }
mallocSw.Stop();

var mmapSw = Stopwatch.StartNew();
for (int i = 0; i < 200; i++)
{
    var got = Native.mmap(IntPtr.Zero, (nuint)page, 3, 0x1002, -1, IntPtr.Zero);
    if (got != (IntPtr)(-1)) Native.munmap(got, (nuint)page);
}
mmapSw.Stop();
Console.WriteLine($"malloc64/free: {mallocSw.Elapsed.TotalMicroseconds / 2000:F2}us per pair; mmap/munmap page: {mmapSw.Elapsed.TotalMicroseconds / 200:F2}us per pair");
// MonoMod's own walk semantics, run from both a code address and a native (libSystem) address:
//   success + mapped   -> advance page = baseAddr + allocSize (whole region, upward) / baseAddr - PageSize (downward)
//   success + free gap -> try map; on failure advance as above
//   failure            -> advance one page
// Count how many probes it takes to cross the +-2GB window in each direction.
foreach (var (label, startAddr) in new[] { ("code", target), ("native", (ulong)System.Runtime.InteropServices.NativeLibrary.GetExport(System.Runtime.InteropServices.NativeLibrary.Load("libSystem.B.dylib"), "malloc")) })
{
    long probes = 0, queryFailures = 0;
    foreach (var goingUp in new[] { true, false })
    {
        var p2 = startAddr & ~(page - 1);
        while (true)
        {
            var distance = p2 > startAddr ? p2 - startAddr : startAddr - p2;
            if (distance > window) break;
            probes++;
            var a3 = p2; ulong s3 = 0; var d3 = int.MaxValue; var c3 = 12;
            var info3 = Marshal.AllocHGlobal(64);
            var kr3 = Native.mach_vm_region_recurse(task, ref a3, ref s3, ref d3, info3, ref c3);
            Marshal.FreeHGlobal(info3);
            if (kr3 != 0 || s3 == 0) { queryFailures++; p2 = goingUp ? p2 + page : p2 - page; }
            else { p2 = goingUp ? a3 + s3 : a3 - page; }
            if (probes > 2_000_000) break;
        }
    }
    Console.WriteLine($"walk from {label} (0x{startAddr:x16}): probes={probes} queryFailures={queryFailures}");
}

Console.WriteLine($"total probe time: {total.ElapsedMilliseconds}ms");

static class Native
{
    [DllImport("libSystem.dylib")] public static extern int task_self_trap();
    [DllImport("libSystem.dylib")]
    public static extern int mach_vm_region_recurse(int task, ref ulong address, ref ulong size, ref int depth, IntPtr info, ref int count);
    [DllImport("libc")] public static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, IntPtr offset);
    [DllImport("libc")] public static extern int munmap(IntPtr addr, nuint length);
}

static class Target
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static int Code() => 1;
}
