// Runtime-only control for the GitHub hang: does the .NET thread/wait machinery stall on this host
// with no MonoMod in the picture?
//
// The captured stall ended in System.Threading.Lock.SignalWaiterIfNecessary, which is only reachable
// from Lock.Exit, so this probe runs four groups and reports progress per group:
//   plain  - lock(object)                      (the previous control, known to pass)
//   lock   - lock(System.Threading.Lock)       (the primitive that appears in the stall)
//   event  - EventWaitHandle.Set + WaitOne(1)  (what MonoMod's log sink does per message)
//   waiter - Lock.EnterScope contention + Yield (forces waiter signalling in Lock.Exit)
using System.Diagnostics;
using System.Runtime.InteropServices;

const int Seconds = 45;

var plainGate = new object();
var lockGate = new Lock();
var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
var stop = new ManualResetEventSlim(false);

long plainProgress = 0, lockProgress = 0, eventProgress = 0, waiterProgress = 0;

Console.WriteLine($"lockstress2: for {Seconds}s (process={RuntimeInformation.ProcessArchitecture}, os={RuntimeInformation.OSArchitecture})");

void Spin(string name, Action bump, int count)
{
    for (int i = 0; i < count; i++)
    {
        var t = new Thread(() =>
        {
            while (!stop.IsSet)
            {
                bump();
            }
        })
        { Name = $"{name}-{i}", IsBackground = true };
        t.Start();
    }
}

Spin("plain", () => { lock (plainGate) { Interlocked.Increment(ref plainProgress); } }, 8);
Spin("lock", () => { lock (lockGate) { Interlocked.Increment(ref lockProgress); } }, 8);
Spin("event", () => { evt.Set(); evt.WaitOne(1); Interlocked.Increment(ref eventProgress); }, 4);

for (int i = 0; i < 4; i++)
{
    var t = new Thread(() =>
    {
        while (!stop.IsSet)
        {
            using (lockGate.EnterScope())
            {
                Interlocked.Increment(ref waiterProgress);
            }
            Thread.Yield();
        }
    })
    { Name = $"waiter-{i}", IsBackground = true };
    t.Start();
}

long lastPlain = 0, lastLock = 0, lastEvent = 0, lastWaiter = 0;
var sw = Stopwatch.StartNew();
int fullyStalled = 0, lockStalled = 0;
for (int s = 0; s < Seconds; s++)
{
    Thread.Sleep(1000);
    long p = Interlocked.Read(ref plainProgress), l = Interlocked.Read(ref lockProgress);
    long e = Interlocked.Read(ref eventProgress), w = Interlocked.Read(ref waiterProgress);
    Console.WriteLine($"t={s + 1,2}s plain +{p - lastPlain,-9} lock +{l - lastLock,-9} event +{e - lastEvent,-9} waiter +{w - lastWaiter,-9} wall={sw.ElapsedMilliseconds / 1000}s");

    if ((l - lastLock) == 0) { lockStalled++; Console.WriteLine("!!! Lock-group made no progress this second"); }
    else lockStalled = 0;
    if (p == lastPlain && l == lastLock && e == lastEvent && w == lastWaiter)
    {
        fullyStalled++;
        Console.WriteLine("!!! NO PROGRESS AT ALL this second");
    }
    else fullyStalled = 0;

    lastPlain = p; lastLock = l; lastEvent = e; lastWaiter = w;
    if (fullyStalled >= 3 || lockStalled >= 5) break;
}

stop.Set();
if (fullyStalled >= 3 || lockStalled >= 5)
{
    Console.WriteLine($"RESULT: WEDGED (fullyStalled={fullyStalled}, lockStalled={lockStalled})");
    Environment.Exit(2);
}
Console.WriteLine("RESULT: OK (progress every second)");
Environment.Exit(0);
