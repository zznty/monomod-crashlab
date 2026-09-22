// Deliberate hang target: proves the capture path works before it is trusted in a hunt.
// Stays alive forever with one spinning managed thread, so both the native sampler and the
// managed stack reporter have something unambiguous to find.
Console.WriteLine($"hangder ready pid={Environment.ProcessId}");

var spinner = new Thread(static () =>
{
    var x = 0;
    while (true)
    {
        x = (x + 1) & 0x3ff;
    }
})
{
    Name = "hangder-spin",
    IsBackground = false,
};
spinner.Start();

Thread.Sleep(Timeout.Infinite);
