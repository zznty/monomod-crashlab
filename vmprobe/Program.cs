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

try
{
var task = Native.task_self_trap();

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
