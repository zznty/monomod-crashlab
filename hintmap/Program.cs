// Does the kernel honour a non-fixed hint near a target address on this image, and how fast?
// If it does, a near-range page can be obtained in ~1 syscall instead of a region-by-region walk.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const int PROT_READ = 1, PROT_WRITE = 2;
const int MAP_PRIVATE = 0x0002, MAP_FAILED = -1;
var mapAnon = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x1000 : 0x20;
const ulong page = 16384;


var target = (ulong)typeof(Target).GetMethod(nameof(Target.Code), BindingFlags.Public | BindingFlags.Static).MethodHandle.GetFunctionPointer();
target &= ~(page - 1);
Console.WriteLine($"target page = 0x{target:x16}");

int inRange = 0, outOfRange = 0, failed = 0;
var sw = Stopwatch.StartNew();
for (int i = 0; i < 200; i++)
{
    var hint = (IntPtr)(long)(target + (ulong)(i - 100) * page);
    var got = Native.mmap(hint, (nuint)page, PROT_READ | PROT_WRITE, MAP_PRIVATE | mapAnon, -1, IntPtr.Zero);
    if (got == (IntPtr)MAP_FAILED) { failed++; continue; }

    var addr = (ulong)got;
    var distance = addr > target ? addr - target : target - addr;
    if (distance <= int.MaxValue) inRange++; else outOfRange++;
    Native.munmap(got, (nuint)page);
}
sw.Stop();
Console.WriteLine($"probes=200 inRange={inRange} outOfRange={outOfRange} failed={failed} totalMs={sw.ElapsedMilliseconds} perProbeUs={(double)sw.Elapsed.TotalMicroseconds / 200:F1}");
Environment.Exit(0);



static class Native
{
    [DllImport("libc", SetLastError = true)]
    public static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, IntPtr offset);
    [DllImport("libc", SetLastError = true)]
    public static extern int munmap(IntPtr addr, nuint length);
}

static class Target
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Code() => 1;
}
