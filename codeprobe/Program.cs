// Where does the runtime put JITted code, and how far apart is it? A rel32 detour needs source and target
// within +-2GB, so the spread of code addresses decides whether MonoMod has to allocate a near trampoline.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

var addrs = new List<ulong>();
foreach (var asm in new[] { typeof(object).Assembly, Assembly.GetExecutingAssembly() })
{
    foreach (var t in asm.GetTypes())
    {
        if (t.IsGenericTypeDefinition || t.IsAbstract) continue;
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() is null) continue;
            try
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
                addrs.Add((ulong)m.MethodHandle.GetFunctionPointer());
            }
            catch { }
            if (addrs.Count >= 4000) break;
        }
        if (addrs.Count >= 4000) break;
    }
    if (addrs.Count >= 4000) break;
}

addrs.Sort();
if (addrs.Count == 0) { Console.WriteLine("no methods probed"); return; }

double gb(ulong v) => v / (1024.0 * 1024 * 1024);
var min = addrs[0];
var max = addrs[^1];
var span = max - min;
int over2gb = 0;
for (int i = 0; i + 1 < addrs.Count; i++)
    if (addrs[i + 1] - addrs[i] > 2UL << 30) over2gb++;

Console.WriteLine($"probed={addrs.Count}");
Console.WriteLine($"min=0x{min:x16} max=0x{max:x16} spanGB={gb(span):F2}");
Console.WriteLine($"distinct4kPages={addrs.Select(a => a >> 12).Distinct().Count()}");
Console.WriteLine($"adjacentGapsOver2GB={over2gb}");
// how many pairs are further apart than rel32 allows?
int far = 0, pairs = 0;
var rnd = new Random(7);
for (int i = 0; i < 200000; i++)
{
    var a = addrs[rnd.Next(addrs.Count)];
    var b = addrs[rnd.Next(addrs.Count)];
    pairs++;
    var d = a > b ? a - b : b - a;
    if (d > (ulong)int.MaxValue) far++;
}
Console.WriteLine($"randomPairs={pairs} beyondRel32={far} ({100.0 * far / pairs:F1}%)");
var buckets = addrs.GroupBy(a => a >> 30).OrderBy(g => g.Key).Select(g => $"{(g.Key << 30) / (1UL << 30)}GB:{g.Count()}");
Console.WriteLine("1GB buckets: " + string.Join(" ", buckets));
