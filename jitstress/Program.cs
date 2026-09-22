// Runtime-only control #3: does the stall need *managed JIT churn* (what MonoMod's JIT hook does),
// or is monitor + EventWaitHandle churn alone enough?
//
// Groups (progress reported per group so a stall is attributable):
//   jit    - Expression.Compile() in a loop: forces a real JIT compile of a fresh dynamic method
//            (the closest runtime-only analogue of CompileMethodHook being on the compile path)
//   mon    - plain-object monitor contention with a hold long enough that waiters always exist,
//            so every release runs the signal-waiter path (Lock.SignalWaiterIfNecessary -> Set)
//   event  - EventWaitHandle.Set + WaitOne(1) churn (what a per-message log sink does)
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const int Seconds = 45;

var gate = new object();
var evt = new EventWaitHandle(false, EventResetMode.AutoReset);
var stop = new ManualResetEventSlim(false);

long jitProgress = 0, monProgress = 0, eventProgress = 0, jitErrors = 0;

Console.WriteLine($"jitstress: for {Seconds}s (process={RuntimeInformation.ProcessArchitecture}, os={RuntimeInformation.OSArchitecture})");

void Spin(string name, Action bump, int count, Action? extra = null)
{
    for (int i = 0; i < count; i++)
    {
        var t = new Thread(() =>
        {
            extra?.Invoke();
            while (!stop.IsSet) bump();
        })
        { Name = $"{name}-{i}", IsBackground = true };
        t.Start();
    }
}

// Managed JIT churn: every Compile() creates and JITs a fresh dynamic method.
Spin("jit", () =>
{
    try
    {
        var body = Expression.Add(Expression.Constant(1), Expression.Constant(2));
        var f = Expression.Lambda<Func<int>>(body).Compile();
        if (f() != 3) Interlocked.Increment(ref jitErrors);
        Interlocked.Increment(ref jitProgress);
    }
    catch { Interlocked.Increment(ref jitErrors); }
}, 6);

// Monitor contention with waiters present: hold long enough that others queue.
Spin("mon", () =>
{
    lock (gate)
    {
        Interlocked.Increment(ref monProgress);
        Thread.SpinWait(20_000);
    }
}, 6);

Spin("event", () => { evt.Set(); evt.WaitOne(1); Interlocked.Increment(ref eventProgress); }, 3);

long lastJit = 0, lastMon = 0, lastEvent = 0;
var sw = Stopwatch.StartNew();
int stalled = 0;
for (int s = 0; s < Seconds; s++)
{
    Thread.Sleep(1000);
    long j = Interlocked.Read(ref jitProgress), m = Interlocked.Read(ref monProgress), e = Interlocked.Read(ref eventProgress);
    Console.WriteLine($"t={s + 1,2}s jit +{j - lastJit,-7} mon +{m - lastMon,-9} event +{e - lastEvent,-7} errors={Interlocked.Read(ref jitErrors)} wall={sw.ElapsedMilliseconds / 1000}s");
    if (j == lastJit && m == lastMon && e == lastEvent)
    {
        stalled++;
        Console.WriteLine("!!! NO PROGRESS AT ALL this second");
    }
    else stalled = 0;
    lastJit = j; lastMon = m; lastEvent = e;
    if (stalled >= 3) break;
}

stop.Set();
if (stalled >= 3)
{
    Console.WriteLine("RESULT: WEDGED (no progress for 3 consecutive seconds)");
    Environment.Exit(2);
}
Console.WriteLine("RESULT: OK (progress every second)");
Environment.Exit(0);
