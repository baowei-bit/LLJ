using System.IO;
using ElectricalTestingMachineMain.Model;
using ElectricalTestingMachineMain.Service;
using ElectricalTestingMachineMain.ViewModel.Monitoring;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var config = ConfigData.Instance;
config.ScanMode = ScanMode.Manual;
config.ScanCodeLength = 5;
config.Station2.Enabled = true;
config.Station3.Enabled = true;
config.Recipes.Clear();
var batch = ScanBatchService.Instance;
batch.Clear();
var writes = new List<int>();
var vm = new MonitoringViewModel(station =>
{
    Check(batch.HasSerialNumber(station), "SN must be captured before notifying PLC");
    writes.Add(station);
    return Task.FromResult((true, "scan flag written"));
});
var focus = new List<int>();
vm.ScanFocusRequested += station => focus.Add(station.StationNo);
for (int station = 1; station <= 3; station++)
{
    vm.Stations[station - 1].ScanInput = $"ABCD{station}";
    await vm.SubmitScanAndAdvanceAsync(vm.Stations[station - 1], true);
}
Check(writes.SequenceEqual(new[] { 1, 2, 3 }), "Each station must write its own flag");
Check(focus.SequenceEqual(new[] { 2, 3 }), "Length-based focus order changed");
Check(batch.Recipe is null && vm.Stations.All(s => s.SerialNumber == $"ABCD{s.StationNo}"), "Scanning still requires a recipe");
await vm.SubmitScanAsync(vm.Stations[0]);
Check(writes.Count == 4, "Repeat scan must reassert status (including retry after write failure)");
vm.ClearStationScanCommand.Execute(vm.Stations[1]);
Check(writes.Count == 4 && !batch.HasSerialNumber(2) && batch.HasSerialNumber(1), "Clear must be local and station-specific");
string? warning = null;
vm.DuplicateScanRejected += message => warning = message;
vm.Stations[1].ScanInput = " ABCD1 ";
await vm.SubmitScanAsync(vm.Stations[1]);
Check(warning != null && writes.Count == 4 && vm.Stations[1].ScanInput == "" && batch.GetSerialNumber(1) == "ABCD1", "Duplicate handling failed");
vm.Stations[1].ScanInput = "B";
await vm.SubmitScanAsync(vm.Stations[1]);
Check(batch.GetSerialNumber(2) == "B", "Short code still subject to old recipe substring rule");
Console.WriteLine("PASS: no recipe matching, per-station writes, SN-before-PLC order, duplicate retry, local clear and short codes.");

var failing = new MonitoringViewModel(_ => Task.FromResult((false, "offline")));
failing.Stations[0].ScanInput = "12345";
var failureFocus = new List<int>();
failing.ScanFocusRequested += station => failureFocus.Add(station.StationNo);
await failing.SubmitScanAndAdvanceAsync(failing.Stations[0], true);
Check(failureFocus.SequenceEqual(new[] { 2 }) && failing.ScanStatus.Contains("未下发"), "Failure blocked focus or appeared successful");
Check(failing.Stations[0].ScanInput == "12345" && batch.GetSerialNumber(1) == "12345", "Failed write lost SN");
config.ScanMode = ScanMode.Disabled;
Check(!batch.HasSerialNumber(1), "Switching modes must clear the previous batch");
int beforeDisabled = writes.Count;
await vm.SubmitScanAsync(vm.Stations[0]);
Check(writes.Count == beforeDisabled, "Disabled scan wrote a PLC flag");
config.ScanMode = ScanMode.Manual;
failing.Stations[0].ScanInput = "12345";
await failing.SubmitScanAsync(failing.Stations[0]);
Console.WriteLine("PASS: failure retains code and error; focus independent from PLC; disabled mode prevents writes.");

// Test-start consumes only local scan state and preserves the in-flight SN.
config.MesEnabled = false;
config.ResultDirectory = Path.Combine(Path.GetTempPath(), "ETM-scan-status-" + Guid.NewGuid().ToString("N"));
var results = SaveResultService.Instance;
var time = DateTime.Now;
int beforeStart = writes.Count;
results.RecordStationStart(time, "test station A", 1);
vm.RefreshConsumedStation(1);
Check(!batch.HasSerialNumber(1) && writes.Count == beforeStart, "Start must not clear a PLC register");
results.RecordStationStart(time, "test station B", 2);
vm.RefreshConsumedStation(2);
results.RecordStationStart(time, "test station C", 3);
vm.RefreshConsumedStation(3);
Check(focus.Last() == 1 && vm.Stations.All(s => !s.HasScannedSerialNumber), "Next round did not reset to first enabled station");
vm.Stations[0].ScanInput = "NEXT";
await vm.SubmitScanAsync(vm.Stations[0]);
results.RecordEnd(time.AddSeconds(1), 12, 34, "OK", "PLC recipe", 1, "test station A", 1, uploadToMes: false);
Check(results.ResumedProcessData.Last().SerialNumber == "12345", "Next round changed previous test SN");
Console.WriteLine("PASS: test-start local clearing, return to first station and immutable in-flight SN.");
