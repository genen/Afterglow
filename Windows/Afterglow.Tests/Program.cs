using System.Globalization;
using System.Text;
using Afterglow.Core;

var failures = new List<string>(); var passed = 0;
void Check(string name, bool ok) { if (ok) passed++; else failures.Add(name); }
bool Close(double? actual, double expected) => actual is { } value && Math.Abs(value - expected) < 0.02;
void Reject(string name, string json)
{
    try { TraceFile.Decode(Encoding.UTF8.GetBytes(json)); failures.Add(name); }
    catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { passed++; }
}
Check("rate uses elapsed time", CounterMath.Rate(1200, 200, .5) == 2000);
Check("counter reset is unavailable", CounterMath.Rate(1, 200, 1) is null);
Check("zero elapsed is unavailable", CounterMath.Rate(200, 200, 0) is null);
Check("zero delta is measured zero", CounterMath.Rate(200, 200, 1) == 0);
Check("infinite interval rejected", CounterMath.Rate(200, 200, double.PositiveInfinity) is null);
var cpu = CounterMath.Cpu(140, 200, 160, 100, 100, 100);
Check("CPU excludes kernel idle", cpu.Cpu == 75 && cpu.Kernel == 37.5);
Check("CPU reset rejected", CounterMath.Cpu(0, 0, 0, 1, 1, 1).Cpu is null);
Check("CPU invalid idle rejected", CounterMath.Cpu(200, 100, 100, 0, 0, 0).Cpu is null);
var row = new ProcessRow(42, 100, "example", "owner", 1, null, 1024, 0, null, "C:\\example.exe", "Running");
Check("live matching identity", CounterMath.CanTerminate(row, 100, 99, false));
Check("PID reuse refused", !CounterMath.CanTerminate(row, 101, 99, false));
Check("self refused", !CounterMath.CanTerminate(row, 100, 42, false));
Check("system refused", !CounterMath.CanTerminate(row with { Pid = 4 }, 100, 99, false));
Check("unknown creation refused", !CounterMath.CanTerminate(row with { Started = 0 }, 0, 99, false));
Check("replay refused", !CounterMath.CanTerminate(row, 100, 99, true));
Check("unavailable differs from zero", Format.Bytes(null) == "Unavailable" && Format.Bytes(0) == "0 B");
Check("nonfinite display unavailable", Format.Percent(double.NaN) == "Unavailable");
Check("CSV escapes quotes", Format.CsvField("a\"b") == "\"a\"\"b\"");
Check("CSV formula guard", Format.CsvField("  =1+1").StartsWith("\"'"));
Check("CSV tab guard", Format.CsvField("\t=1").StartsWith("\"'"));
var savedCulture = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
Check("CSV numeric values invariant", Format.Csv([row with { Cpu = 1.25 }]).Contains("\"1.25\"")); CultureInfo.CurrentCulture = savedCulture;
var sample = new Sample(DateTimeOffset.UtcNow, 0, null, 1024, 2048, 1024, null, 0, null, 0, "AC power", null, "example", []);
var frame = new Frame(sample, [row]);
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".afterglowtrace");
try
{
    await TraceFile.Save(path, [frame]); var trace = await TraceFile.Load(path);
    Check("trace metadata", trace.Version == 2 && trace.Platform == "windows");
    Check("trace missing / zero preserved", trace.Frames[0].Sample.Receive is null && trace.Frames[0].Sample.Send == 0);
    Check("trace identity preserved", trace.Frames[0].Processes[0].Started == 100);
    var json = await File.ReadAllTextAsync(path);
    Check("display text excluded from wire format", !json.Contains("cpuText") && !json.Contains("identity"));
    Reject("null sample refused", json.Replace("\"sample\":{", "\"sample\":null,\"unused\":{"));
    Reject("null processes refused", json.Replace("\"processes\":[", "\"processes\":null,\"unusedRows\":["));
    Reject("out of range CPU refused", json.Replace("\"cpu\":0", "\"cpu\":101"));
    Reject("null network array refused", json.Replace("\"interfaces\":[]", "\"interfaces\":null"));
    await TraceFile.Save(path, [frame], "macos");
    Check("re-export preserves source platform", (await TraceFile.Load(path)).Platform == "macos");
    try { await TraceFile.Save(path, [frame, frame with { Sample = sample with { Time = sample.Time.AddSeconds(-1) } }]); failures.Add("unordered time refused"); }
    catch (InvalidDataException) { passed++; }
}
finally { if (File.Exists(path)) File.Delete(path); }
Reject("unknown version refused", "{\"version\":99}");
Reject("empty recording refused", "{\"version\":2,\"app\":\"Afterglow\",\"platform\":\"windows\",\"frames\":[]}");
Reject("null frame refused", "{\"version\":2,\"app\":\"Afterglow\",\"platform\":\"windows\",\"frames\":[null]}");
var mac = TraceFile.Decode(Encoding.UTF8.GetBytes("""
{"version":1,"app":"Afterglow","frames":[{"sample":{"time":0,"cpu":50,"memory":1024,"total":2048,"topCPU":"test"},"processes":[{"pid":42,"name":"test","user":"me","cpu":150,"memory":1024}]}]}
"""));
Check("Mac epoch imported", mac.Frames[0].Sample.Time == new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));
Check("Mac identities never actionable", mac.Frames[0].Processes[0].Started == 0);
Check("Mac CPU scale preserved", mac.Frames[0].Processes[0].Cpu == 150);
Check("energy score weights CPU and memory", Sensors.Energy(100, 0, 1_000) == 85 && Sensors.Energy(0, 1_000, 1_000) == 15);
Check("available memory sets pressure", Sensors.Pressure(50, 100) == 0 && Sensors.Pressure(30, 100) == 1 && Sensors.Pressure(10, 100) == 4 && Sensors.PressureName(2) == "Urgent");
Check("network port swaps byte order", Sensors.NetworkPort(0x0000BB01) == 443);
var cores = Sensors.Cores([("0,_Total", 90), ("0,1", 20), ("1,0", 40), ("0,0", 10)], [("0,0", 4), ("0,1", 5), ("1,0", 6)]);
Check("logical processors skip totals and sort", cores.Length == 3 && cores[0].Name == "0,0" && cores[1].Total == 20 && cores[2].Name == "1,0" && cores[0].Privileged == 4);
var disks = Sensors.DisksFrom([("_Total", 9), ("0 C:", 100), ("1 D:", Sensors.DiskRateCap + 1)], [("0 C:", 40), ("1 D:", 5)], [("0 C:", 75), ("1 D:", 90)]);
Check("disk totals cap and use the busiest device", disks.Read == 100 && disks.Write == 45 && disks.Busy == 25 && disks.Disks.Length == 2 && disks.Disks[1].Read is null);
var gpu = Sensors.Aggregate([
    ("pid_10_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 25),
    ("pid_11_luid_0x00000000_0x00000001_phys_0_eng_1_engtype_3D", 30),
    ("pid_10_luid_0x00000000_0x00000001_phys_0_eng_2_engtype_Copy", 80)
], new Dictionary<ulong, (string Name, double Dedicated)> { [1] = ("Test GPU", 1024) });
Check("GPU uses the busiest engine and names the adapter", gpu.Utilization == 80 && gpu.ThreeD == 55 && gpu.Copy == 80 && gpu.Name == "Test GPU" && gpu.ByProcess[10] == 80 && gpu.ByProcess[11] == 30);
var clamped = Sensors.Aggregate([
    ("pid_1_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 60),
    ("pid_2_luid_0x0_0x2_phys_0_eng_0_engtype_3D", 50)
]);
Check("GPU engine sum clamps at 100", clamped.Utilization == 100 && clamped.ThreeD == 100);
var treeRoot = row with { Pid = 1, Parent = 0 };
var tree = Sensors.TerminationOrder(treeRoot, [treeRoot, row with { Pid = 2, Parent = 1 }, row with { Pid = 3, Parent = 2 }, row with { Pid = 9, Parent = 0 }]);
Check("termination walks leaves first", tree.Select(p => p.Pid).SequenceEqual([3, 2, 1]));
Check("deci-kelvin temperature", Close(Sensors.ThermalCelsius(3085), 35.35));
Check("kelvin temperature", Close(Sensors.ThermalCelsius(308.5), 35.35));
Check("celsius temperature", Sensors.ThermalCelsius(35.35) == 35.35);
Check("deci-celsius temperature", Sensors.ThermalCelsius(450) == 45);
Check("blank temperature rejected", Sensors.ThermalCelsius(0) is null && Sensors.ThermalCelsius(9000) is null);
Check("core word is not score", Sensors.IsCpuZone("CPU Core #1") && Sensors.IsCpuZone("Core #2") && !Sensors.IsCpuZone("score"));
var cpuZone = Sensors.Thermals([("TZ00", 3000), ("CPU Package", 3280)], []);
Check("named CPU zone is the headline", Close(cpuZone.Temperature, 54.85) && cpuZone.Temps.Length == 2 && Close(cpuZone.TempMin, 54.85));
var unnamed = Sensors.Thermals([("TZ00", 3300), ("TZ01", 3100)], []);
Check("unnamed zones use the hottest", Close(unnamed.Temperature, 56.85) && Close(unnamed.TempMin, 36.85));
var precise = Sensors.Thermals([("TZ00", 3000)], [("TZ00", 320.15)]);
Check("high precision overrides coarse", Close(precise.Temperature, 47) && precise.Temps.Length == 1);
var monitor = Sensors.FromCelsius([("CPU Package", 61.2), ("System", 34)]);
Check("monitor CPU package preferred", Close(monitor.Temperature, 61.2) && monitor.Temps.Length == 2);
Check("temperature caption", Sensors.ThermalCaption(null, null, null) == "Temperature sensors unavailable" && Sensors.ThermalCaption(61.2, 61.2, ["CPU Package"]).StartsWith("CPU"));
var legacy = TraceFile.Decode(Encoding.UTF8.GetBytes("""
{"version":2,"app":"Afterglow","platform":"windows","frames":[{"sample":{"time":"2026-09-29T00:00:00Z","cpu":1,"powerSource":"AC power","topCpu":"x","interfaces":[]},"processes":[{"pid":7,"started":8,"name":"n","user":"u","session":1,"path":"","status":"Running"}]}]}
"""));
Check("older v2 trace defaults new fields", legacy.Frames[0].Sample.Cores is null && legacy.Frames[0].Sample.Gpu is null && legacy.Frames[0].Processes[0].Parent == 0 && legacy.Frames[0].Processes[0].Windowed == false);
foreach (var fixture in args)
{
    var imported = await TraceFile.Load(fixture);
    Check("Existing Mac fixture imports: " + Path.GetFileName(fixture), imported.Platform == "macos" && imported.Frames.Length > 0);
}
var buffer = new RecordingBuffer();
for (var i = 0; i < RecordingBuffer.MaxFrames; i++) if (!buffer.Add(frame)) throw new Exception("Premature recording limit");
Check("recording frame cap", !buffer.Add(frame)); buffer.Clear();
Check("clear resets budget", buffer.Frames.Count == 0 && buffer.Add(frame));
buffer.Clear(); Check("process budget cap", !buffer.Add(frame with { Processes = new ProcessRow[RecordingBuffer.MaxProcesses + 1] }));
Console.WriteLine($"{passed} checks passed; {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
return failures.Count == 0 ? 0 : 1;
