using System.Diagnostics;
using System.Text.Json;
using SizeMonitor.Interop;

string PathArg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException(name);
}

string target = PathArg("--path");
string snapshot = PathArg("--worker-snapshot");
if (File.Exists(Path.Combine(target, "abnormal.exit"))) return 23;
if (File.Exists(Path.Combine(target, "oversized.line"))) { Console.WriteLine(new string('x', 70 * 1024)); return 0; }
if (File.Exists(Path.Combine(target, "cancel.tree")))
{
    using Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -t 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;
    await File.WriteAllTextAsync(Path.Combine(target, "child.pid"), child.Id.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
Console.WriteLine(JsonSerializer.Serialize(new { type = "progress", dirs = 1, files = 2, bytes = 42 }));
var result = new ScanResultManaged
{
    Nodes = [new ScanNode { Size = 42, Parent = uint.MaxValue, FirstChild = uint.MaxValue, NextSibling = uint.MaxValue }],
    Names = [target], TotalBytes = 42, FileCount = 2, DirCount = 1, ElapsedSec = 0.01,
};
await ScanSnapshotStore.SaveAsync(snapshot, result);
Console.WriteLine(JsonSerializer.Serialize(new { type = "complete", scanner = "Directory", dirs = 1, files = 2, bytes = 42 }));
return 0;
