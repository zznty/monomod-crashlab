// Runtime-only control for the GitHub hang: does the .NET thread/waitsubsystem machinery stall on
// this host with no MonoMod in the picture?
//
// Mirrors the shape of the captured stall: a hot plain-object monitor contended by many threads
// (the holder was stuck in Monitor.Exit -> waiter signalling -> LowLevelLock) plus threads churning
// an EventWaitHandle, which is what MonoMod's log sink does once per message.
using System.Diagnostics;

const int Lockers = 8;
const int Eventers = 4;
const int Seconds = 45;

var gate = new object();
var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
var stop = new ManualResetEventSlim(false);
long progress = 0;
int wedged = 0;

Console.WriteLine($"lockstress: {Lockers} lockers + {Eventers} eventers for {Seconds}s");

var threads = new List<Thread>();
for (int i = 0; i < Lockers; i++)
{
    var t = new Thread(() =>
    {
        while (!stop.IsSet)
        {
            lock (gate)
            {
                Interlocked.Increment(ref progress);
            }
        }
    })
    { Name = $"locker-{i}", IsBackground = true };
    threads.Add(t);
}
for (int i = 0; i < Eventers; i++)
{
    var t = new Thread(() =>
    {
        while (!stop.IsSet)
        {
            evt.Set();
            evt.WaitOne(1);
            Interlocked.Increment(ref progress);
        }
    })
    { Name = $"eventer-{i}", IsBackground = true };
    threads.Add(t);
}

foreach (var t in threads) t.Start();

var sw = Stopwatch.StartNew();
long lastSample = 0;
for (int s = 0; s < Seconds; s++)
{
    Thread.Sleep(1000);
    var now = Interlocked.Read(ref progress);
    var delta = now - lastSample;
    lastSample = now;
    Console.WriteLine($"t={s + 1,2}s progress +{delta,10} (total {now}, {sw.ElapsedMilliseconds / 1000}s wall)");
    if (delta == 0)
    {
        wedged++;
        Console.WriteLine("!!! NO PROGRESS in the last second");
        if (wedged >= 3) break;
    }
    else
    {
        wedged = 0;
    }
}

stop.Set();
if (wedged >= 3)
{
    Console.WriteLine("RESULT: WEDGED (3 consecutive stalled seconds) - runtime stall reproduces without MonoMod");
    Environment.Exit(2);
}
Console.WriteLine("RESULT: OK (progress every second)");
Environment.Exit(0);
