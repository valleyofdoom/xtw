using System;
using System.Collections.Generic;
using System.Reflection;
using CommandLine;
using Serilog;
using Serilog.Events;
using System.Security.Principal;
using Microsoft.Windows.EventTracing;
using System.IO;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Windows.EventTracing.Symbols;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Session;
using System.Threading;
using CsvHelper;
using System.Globalization;
using System.Diagnostics;

namespace xtw {
    internal class XTW {
        private static Version VERSION = Assembly.GetExecutingAssembly().GetName().Version;
        private static string BRANCH = "    └───";

        private static void ShowBanner() {
            Console.WriteLine($"XTW Version {VERSION.Major}.{VERSION.Minor}.{VERSION.Build} - GPLv3\nGitHub - https://github.com/valleyofdoom\n");
        }

        private static bool IsAdmin() {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static string GetTitle(string title) {
            var line = new string('-', title.Length);

            return $"{title}\n{line}";
        }

        private static void StartTrace(int loggingSeconds, string sessionName, string etlPath) {
            using (var ets = new TraceEventSession(sessionName, etlPath)) {
                ets.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Interrupt |
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                    KernelTraceEventParser.Keywords.ImageLoad |
                    KernelTraceEventParser.Keywords.Process |
                    KernelTraceEventParser.Keywords.Thread |
                    KernelTraceEventParser.Keywords.ContextSwitch |
                    KernelTraceEventParser.Keywords.Dispatcher
                );

                // https://github.com/GameTechDev/PresentMon/blob/main/Tools/etl_collection_timed.cmd

                ets.EnableProvider("Microsoft-Windows-DxgKrnl");
                ets.EnableProvider("Microsoft-Windows-D3D9");
                ets.EnableProvider("Microsoft-Windows-DXGI");
                ets.EnableProvider("Microsoft-Windows-Dwm-Core");
                ets.EnableProvider(Guid.Parse("8c9dd1ad-e6e5-4b07-b455-684a9d879900")); // dwm_win7
                ets.EnableProvider("Microsoft-Windows-Win32k");

                ets.CaptureState(Guid.Parse("{802EC45A-1E99-4B83-9920-87C98277BA9D}")); // Microsoft-Windows-DxgKrnl
                ets.CaptureState(Guid.Parse("{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}")); // Microsoft-Windows-DXGI

                Thread.Sleep(loggingSeconds * 1000);
            }
        }

        private static string GetFormattedInterruptType(InterruptHandlingType interryptHandlingType) {
            return interryptHandlingType == InterruptHandlingType.InterruptServiceRoutine ? "ISR" : "DPC";
        }

        public static async Task<int> Main() {
            // create logger
            var log = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();

            if (!IsAdmin()) {
                log.Error("administrator privileges required");
                return 1;
            }

            // parse arguments
            CommandLineArgs args = null;

            _ = Parser.Default.ParseArguments<CommandLineArgs>(Environment.GetCommandLineArgs()).WithParsed(parsedArgs => {
                args = parsedArgs;
            });

            if (args == null) {
                return 1;
            }

            // either an etl file or record time must be specified
            if (args.EtlFile == null && args.Timed == 0) {
                Console.WriteLine("run xtw --help to see options");
                return 0;
            }

            if (!args.NoBanner) {
                ShowBanner();
            }

            // cd to directory of program
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

            var etlFile = "";

            if (args.EtlFile != null) {
                etlFile = args.EtlFile;
            } else {
                etlFile = "xtw.etl";

                Thread.Sleep(args.Delay * 1000); // 0 if not specified

                log.Information($"collecting trace for {args.Timed}s");
                StartTrace(args.Timed, "xtw", etlFile);
            }

            if (args.Symbols) {
                // add required symbols metadata
                // https://stackoverflow.com/questions/65351589/windows-performance-analyzer-cannot-load-symbols/65532497#65532497

                log.Information("adding symbols metadata");
                var mergeETLs = new string[] { etlFile };
                var mergedETL = "xtw-merged.etl";
                ETWKernelControl.Merge(mergeETLs, mergedETL, EVENT_TRACE_MERGE_EXTENDED_DATA.IMAGEID);

                // remove trace without metadata
                File.Delete(etlFile);
                // rename merged file to the original name
                File.Move(mergedETL, etlFile);
            }

            if (!File.Exists(etlFile)) {
                log.Error($"trace {etlFile} not exists");
                return 1;
            }

            // get presentmon data
            var csvFile = "xtw.csv";

            var presentmonProcess = Process.Start(new ProcessStartInfo {
                FileName = "PresentMon.exe",
                Arguments = $"--etl_file {etlFile} --output_file {csvFile}",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            presentmonProcess.WaitForExit();

            if (!File.Exists(csvFile)) {
                log.Error($"presentmon csv error: {etlFile} not exists");
                return 1;
            }

            // load the etl
            var traceProcessor = TraceProcessor.Create(etlFile);

            // select events
            var traceMetadata = traceProcessor.UseMetadata();
            var pendingSymbols = traceProcessor.UseSymbols();
            var pendingInterruptHandlingData = traceProcessor.UseInterruptHandlingData();

            log.Information("loading etl file");

            // setup callback for progress tracking
            var currentPass = 0;

            var progressCallback = new Progress<TraceProcessingProgress>(progress => {
                if (progress.CurrentPass + 1 > currentPass) {
                    currentPass++;
                    log.Information($"processing trace (pass {currentPass}/{progress.TotalPasses})");
                }
            });

            traceProcessor.Process(progressCallback);

            // get result of selected events
            var interruptHandlingData = pendingInterruptHandlingData.Result;

            if (interruptHandlingData.Activity.Count == 0) {
                log.Error($"no interrupt handling data in {etlFile}");
                return 1;
            }

            if (args.Symbols) {
                var symbols = pendingSymbols.Result;

                var symbolsProgressCallback = new Progress<SymbolLoadingProgress>(progress => {
                    var percentage = (double)progress.ImagesProcessed / progress.ImagesTotal * 100;

                    log.Information($"loading symbols {percentage:F3}% ({progress.ImagesProcessed}/{progress.ImagesTotal})");
                });

                await symbols.LoadSymbolsAsync(SymCachePath.Automatic, SymbolPath.Automatic, symbolsProgressCallback);

                if (!symbols.AreSymbolsLoaded) {
                    log.Error("symbols are not loaded");
                    return 1;
                }
            }

            var reportLines = new List<string>();
            var metricsRightPadding = 20;
            string[] metricsTableHeadings = { "Max", "Avg", "Min", "STDEV", "99 %ile", "99.9 %ile" };

            // initialize data dictionary

            /*
            structure of modulesData:

            {
                "modules": {
                    "ISR": {
                        "module.sys": {
                            "elapsed_times_us": [],
                            "elapsedtime_us_by_processor": {
                                "0": 0,
                                "1": 0
                            },
                            "count_by_processor": {
                                "0": 0,
                                "1": 0
                            },
                            "start_times_ms": [],
                            "functions_data": { }
                        }
                    },
                    "DPC": {
                        "module.sys": {
                            "elapsed_times_us": [],
                            "elapsedtime_us_by_processor": {
                                "0": 0,
                                "1": 0
                            },
                            "count_by_processor": {
                                "0": 0,
                                "1": 0
                            },
                            "start_times_ms": [],
                            "functions_data": { }
                        }
                    }
                }
            }
             */

            // to keep track of overall system ISR/DPC metrics
            var systemData = new Dictionary<InterruptHandlingType, Module> {
                { InterruptHandlingType.InterruptServiceRoutine, new Module(traceMetadata.ProcessorCount)},
                { InterruptHandlingType.DeferredProcedureCall, new Module(traceMetadata.ProcessorCount)}
            };

            var modulesData = new Dictionary<InterruptHandlingType, Dictionary<string, Module>> {
                { InterruptHandlingType.InterruptServiceRoutine, new Dictionary<string, Module>()},
                { InterruptHandlingType.DeferredProcedureCall, new Dictionary<string, Module>()}
            };

            log.Information("parsing raw data");

            for (var i = 0; i < interruptHandlingData.Activity.Count; i++) {
                var interval = interruptHandlingData.Activity[i].Interval;
                var module = interval.HandlerImage.FileName;
                var elapsedTimeUsec = (double)(interval.StopTime.Nanoseconds - interval.StartTime.Nanoseconds) / 1000;

                // try get function name, requires symbols to be loaded
                var functionName = "";
                try {
                    functionName = interval.HandlerStackFrame.Symbol.FunctionName;
                } catch (NullReferenceException) { }

                // populate data for module
                if (!modulesData[interval.Type].ContainsKey(module)) {
                    modulesData[interval.Type][module] = new Module(traceMetadata.ProcessorCount);
                }

                modulesData[interval.Type][module].DpcIsrData.ElapsedTimesUs.Add(elapsedTimeUsec);
                modulesData[interval.Type][module].DpcIsrData.ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;

                modulesData[interval.Type][module].DpcIsrData.CountByProcessor[interval.Processor]++;
                modulesData[interval.Type][module].DpcIsrData.SumCount++;

                var startTimeMs = interval.StartTime.Nanoseconds / 1e+6;
                modulesData[interval.Type][module].DpcIsrData.StartTimesMs.Add(startTimeMs);

                // keep track of system data and cache everything
                systemData[interval.Type].DpcIsrData.ElapsedTimesUs.Add(elapsedTimeUsec);
                systemData[interval.Type].DpcIsrData.ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;

                systemData[interval.Type].DpcIsrData.CountByProcessor[interval.Processor]++;
                systemData[interval.Type].DpcIsrData.SumCount++;

                systemData[interval.Type].DpcIsrData.StartTimesMs.Add(startTimeMs);

                // populate data for module functions
                if (functionName != "") {
                    if (!modulesData[interval.Type][module].FunctionsData.ContainsKey(functionName)) {
                        modulesData[interval.Type][module].FunctionsData[functionName] = new DpcIsrData(traceMetadata.ProcessorCount);
                    }

                    modulesData[interval.Type][module].FunctionsData[functionName].ElapsedTimesUs.Add(elapsedTimeUsec);
                    modulesData[interval.Type][module].FunctionsData[functionName].ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;

                    modulesData[interval.Type][module].FunctionsData[functionName].CountByProcessor[interval.Processor]++;
                    modulesData[interval.Type][module].FunctionsData[functionName].SumCount++;

                    modulesData[interval.Type][module].FunctionsData[functionName].StartTimesMs.Add(startTimeMs);
                }
            }

            reportLines.Add(GetTitle($"XTW Version {VERSION.Major}.{VERSION.Minor}.{VERSION.Build}") + "\n");

            var traceSeconds = (traceMetadata.StopTime - traceMetadata.StartTime).Seconds;
            reportLines.Add($"OS Version: {traceMetadata.OSVersion}\n");
            reportLines.Add($"Trace duration: {traceSeconds} second(s)\n");
            reportLines.Add($"Trace Path: {traceMetadata.TracePath}\n");
            reportLines.Add($"Lost Buffers: {traceMetadata.LostBufferCount}\n");
            reportLines.Add($"Lost Events: {traceMetadata.LostEventCount}\n");
            reportLines.Add("\n\n\n");

            var moduleRightPadding = 0;

            // get shortest right padding for module/symbol names
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];
                var shortestModuleNameLength = 0;

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    if (moduleName.Length > shortestModuleNameLength) {
                        shortestModuleNameLength = moduleName.Length;
                    }

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        if (functionName.Length > shortestModuleNameLength) {
                            shortestModuleNameLength = functionName.Length;
                        }
                    }
                }

                // this will give us the space between module and first column
                // also a symbols check to account for module function indent
                moduleRightPadding = shortestModuleNameLength + 10 + (args.Symbols ? 4 : 0);
            }

            // TABLE
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];

                reportLines.Add(GetTitle($"Total {GetFormattedInterruptType(interruptType)} Usage by CPU (usecs and count)") + "\n\n");

                reportLines.Add($"    {"Module".PadRight(moduleRightPadding)}");
                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    reportLines.Add($"CPU {processor.ToString().PadRight(metricsRightPadding - 4)}"); // -4 due to the table-wide indent
                }
                reportLines.Add("Total\n");

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    // this is for the last column, to have stats for all cores
                    var totalModuleElapsedTime = 0.0;
                    var totalModuleInterruptCount = 0;

                    reportLines.Add($"    {moduleName.PadRight(moduleRightPadding)}");

                    for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                        var elapsedTime = moduleData.DpcIsrData.ElapsedTimeUsByProcessor[processor];
                        var count = moduleData.DpcIsrData.CountByProcessor[processor];

                        var processorModuleTotals = count > 0 ? $"{elapsedTime:F3} ({count})" : "-";
                        reportLines.Add(processorModuleTotals.PadRight(metricsRightPadding));

                        totalModuleElapsedTime += elapsedTime;
                        totalModuleInterruptCount += count;
                    }

                    var moduleTotals = totalModuleInterruptCount > 0 ? $"{totalModuleElapsedTime:F3} ({totalModuleInterruptCount})" : "-";
                    reportLines.Add(moduleTotals + "\n");

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        var totalFunctionElapsedTime = 0.0;
                        var totalFunctionCount = 0;

                        reportLines.Add($"    {BRANCH}{functionName.PadRight(moduleRightPadding - BRANCH.Length)}");

                        for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                            var elapsedTime = functionData.ElapsedTimeUsByProcessor[processor];
                            var count = functionData.CountByProcessor[processor];

                            var processorFunctionTotals = count > 0 ? $"{elapsedTime:F3} ({count})" : "-";
                            reportLines.Add(processorFunctionTotals.PadRight(metricsRightPadding));

                            totalFunctionElapsedTime += elapsedTime;
                            totalFunctionCount += count;
                        }

                        var functionTotals = totalFunctionCount > 0 ? $"{totalFunctionElapsedTime:F3} ({totalFunctionCount})" : "-";
                        reportLines.Add($"{functionTotals.PadRight(metricsRightPadding)}\n");
                    }
                }

                // write system total row
                reportLines.Add($"\n    {"Total".PadRight(moduleRightPadding)}");

                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    var elapsedTime = systemData[interruptType].DpcIsrData.ElapsedTimeUsByProcessor[processor];
                    var count = systemData[interruptType].DpcIsrData.CountByProcessor[processor];

                    var processorSystemTotals = count > 0 ? $"{elapsedTime:F3} ({count})" : "-";
                    reportLines.Add(processorSystemTotals.PadRight(metricsRightPadding).PadRight(metricsRightPadding));
                }

                var systemTotals = systemData[interruptType].DpcIsrData.SumCount > 0 ? $"{systemData[interruptType].DpcIsrData.ElapsedTimesUs.Sum:F3} ({systemData[interruptType].DpcIsrData.SumCount})" : "-";
                reportLines.Add(systemTotals + "\n\n");
            }

            reportLines.Add("\n\n"); // space between sections

            // TABLE
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];
                reportLines.Add(GetTitle($"{GetFormattedInterruptType(interruptType)} Interval (ms)") + "\n\n");

                reportLines.Add($"    {"Module".PadRight(moduleRightPadding)}");
                for (var i = 0; i < metricsTableHeadings.Length; i++) {
                    // don't add padding to last column
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? metricsRightPadding : 0;
                    reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                }
                reportLines.Add("\n");

                var systemIntervalsMs = new List<double>();
                var sumSystemIntervalsMs = 0.0;

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    var moduleIntervalsMs = new List<double>();
                    var sumModuleIntervalMs = 0.0;

                    // sort start times to calculate deltas
                    moduleData.DpcIsrData.StartTimesMs.Sort();

                    for (var i = 1; i < moduleData.DpcIsrData.StartTimesMs.Count; i++) {
                        var msBetweenEvents = moduleData.DpcIsrData.StartTimesMs[i] - moduleData.DpcIsrData.StartTimesMs[i - 1];
                        moduleIntervalsMs.Add(msBetweenEvents);
                        sumModuleIntervalMs += msBetweenEvents;

                        // keep track of system intervals
                        systemIntervalsMs.Add(msBetweenEvents);
                        sumSystemIntervalsMs += msBetweenEvents;
                    }

                    var moduleIntervalMetrics = new ComputeMetrics(moduleIntervalsMs, sumModuleIntervalMs);
                    reportLines.Add(
                        $"    " +
                        $"{moduleName.PadRight(moduleRightPadding)}" +
                        $"{moduleIntervalMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Average():F3}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Percentile(99.9):F3}" +
                        $"\n"
                    );

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        var functionIntervalsMs = new List<double>();
                        var sumFunctionIntervalsMs = 0.0;

                        // sort start times to calculate deltas
                        functionData.StartTimesMs.Sort();

                        for (var i = 1; i < functionData.StartTimesMs.Count; i++) {
                            var msBetweenEvents = functionData.StartTimesMs[i] - functionData.StartTimesMs[i - 1];
                            functionIntervalsMs.Add(msBetweenEvents);
                            sumFunctionIntervalsMs += msBetweenEvents;
                        }

                        var functionIntervalMetrics = new ComputeMetrics(functionIntervalsMs, sumFunctionIntervalsMs);
                        reportLines.Add(
                            $"    {BRANCH}{functionName.PadRight(moduleRightPadding - BRANCH.Length)}" +
                            $"{functionIntervalMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Average():F3}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Percentile(99.9):F3}" +
                            $"\n"
                        );
                    }
                }

                var systemIntervalMetrics = new ComputeMetrics(systemIntervalsMs, sumSystemIntervalsMs);
                reportLines.Add(
                    $"\n    {"System Summary".PadRight(moduleRightPadding)}" +
                    $"{systemIntervalMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Percentile(99.9):F3}" +
                    $"\n\n"
                );
            }

            reportLines.Add("\n\n"); // space between sections

            // TABLE
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];

                reportLines.Add(GetTitle($"{GetFormattedInterruptType(interruptType)} Elapsed Times (usecs)") + "\n\n");

                reportLines.Add($"    {"Module".PadRight(moduleRightPadding)}");
                for (var i = 0; i < metricsTableHeadings.Length; i++) {
                    // don't add padding to last column
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? metricsRightPadding : 0;
                    reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                }
                reportLines.Add("\n");

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    var moduleElapsedMetrics = new ComputeMetrics(moduleData.DpcIsrData.ElapsedTimesUs, moduleData.DpcIsrData.ElapsedTimesUs.Sum);
                    reportLines.Add(
                        $"    {moduleName.PadRight(moduleRightPadding)}" +
                        $"{moduleElapsedMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Average():F3}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Percentile(99.9):F3}" +
                        $"\n"
                    );

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        var functionElapsedMetrics = new ComputeMetrics(functionData.ElapsedTimesUs, functionData.ElapsedTimesUs.Sum);
                        reportLines.Add(
                            $"    {BRANCH}{functionName.PadRight(moduleRightPadding - BRANCH.Length)}" +
                            $"{functionElapsedMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Average():F3}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Percentile(99.9):F3}" +
                            $"\n"
                        );
                    }
                }

                var systemMetrics = new ComputeMetrics(systemData[interruptType].DpcIsrData.ElapsedTimesUs, systemData[interruptType].DpcIsrData.ElapsedTimesUs.Sum);
                reportLines.Add(
                    $"\n    {"System Summary".PadRight(moduleRightPadding)}" +
                    $"{systemMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Percentile(99.9):F3}" +
                    $"\n\n"
                );
            }

            reportLines.Add("\n\n"); // space between sections

            // TABLE
            var presentmonData = new Dictionary<string, PresentMonData>();

            using (var reader = new StreamReader(csvFile)) {
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture)) {
                    var records = csv.GetRecords<PresentMonColumn>();
                    foreach (var record in records) {

                        // create entry for process if it doesn't exist
                        if (!presentmonData.ContainsKey(record.Application)) {
                            presentmonData[record.Application] = new PresentMonData();
                        }

                        presentmonData[record.Application].PresentRuntime.Add(record.PresentRuntime);
                        presentmonData[record.Application].SyncInterval.Add(record.SyncInterval);
                        presentmonData[record.Application].PresentFlags.Add(record.PresentFlags);
                        presentmonData[record.Application].AllowsTearing.Add(record.AllowsTearing);
                        presentmonData[record.Application].PresentMode.Add(record.PresentMode);
                        presentmonData[record.Application].FrameTime.Add(record.FrameTime);
                        presentmonData[record.Application].CPUBusy.Add(record.CPUBusy);
                        presentmonData[record.Application].CPUWait.Add(record.CPUWait);
                        presentmonData[record.Application].GPULatency.Add(record.GPULatency);
                        presentmonData[record.Application].GPUTime.Add(record.GPUTime);
                        presentmonData[record.Application].GPUBusy.Add(record.GPUBusy);
                        presentmonData[record.Application].GPUWait.Add(record.GPUWait);

                        if (record.DisplayLatency != "NA") {
                            var displayLatency = double.Parse(record.DisplayLatency);
                            presentmonData[record.Application].DisplayLatency.Add(displayLatency);
                        }

                        if (record.DisplayedTime != "NA") {
                            var displayedTime = double.Parse(record.DisplayedTime);
                            presentmonData[record.Application].DisplayedTime.Add(displayedTime);
                        }

                        if (record.AnimationError != "NA") {
                            var animationError = double.Parse(record.AnimationError);
                            presentmonData[record.Application].AnimationError.Add(animationError);
                        }

                        if (record.AllInputToPhotonLatency != "NA") {
                            var allInputToPhotonLatency = double.Parse(record.AllInputToPhotonLatency);
                            presentmonData[record.Application].AllInputToPhotonLatency.Add(allInputToPhotonLatency);
                        }

                        if (record.ClickToPhotonLatency != "NA") {
                            var clickToPhotonLatency = double.Parse(record.ClickToPhotonLatency);
                            presentmonData[record.Application].ClickToPhotonLatency.Add(clickToPhotonLatency);
                        }
                    }
                }
            }

            foreach (var processName in presentmonData.Keys) {
                reportLines.Add(GetTitle($"PresentMon - {processName}") + "\n\n");

                var processData = presentmonData[processName];

                reportLines.Add(
                    $"    Present Runtime: {string.Join(", ", processData.PresentRuntime),-10}" +
                    $"Sync Interval: {string.Join(",", processData.SyncInterval),-10}" +
                    $"Present Flags: {string.Join(",", processData.PresentFlags),-10}" +
                    $"Allows Tearing: {string.Join(",", processData.AllowsTearing),-10}" +
                    $"Present Mode: {string.Join(", ", processData.PresentMode)}" +
                    $"\n"
                );

                // space before metric table
                reportLines.Add("\n");

                reportLines.Add($"    {"Metric",-30}");
                for (var i = 0; i < metricsTableHeadings.Length; i++) {
                    // don't add padding to last column
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? metricsRightPadding : 0;
                    reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                }
                reportLines.Add("\n");

                var frametimeMetrics = new ComputeMetrics(processData.FrameTime, processData.FrameTime.Sum);
                reportLines.Add(
                    $"    {"Frame Time",-30}" +
                    $"{frametimeMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{frametimeMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{frametimeMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{frametimeMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{frametimeMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{frametimeMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var cpuBusyMetrics = new ComputeMetrics(processData.CPUBusy, processData.CPUBusy.Sum);
                reportLines.Add(
                    $"    {"CPU Busy",-30}" +
                    $"{cpuBusyMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{cpuBusyMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{cpuBusyMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{cpuBusyMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{cpuBusyMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{cpuBusyMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var cpuWaitMetrics = new ComputeMetrics(processData.CPUWait, processData.CPUWait.Sum);
                reportLines.Add(
                    $"    {"CPU Wait",-30}" +
                    $"{cpuWaitMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{cpuWaitMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{cpuWaitMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{cpuWaitMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{cpuWaitMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{cpuWaitMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var gpuLatencyMetrics = new ComputeMetrics(processData.GPULatency, processData.GPULatency.Sum);
                reportLines.Add(
                    $"    {"GPU Latency",-30}" +
                    $"{gpuLatencyMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuLatencyMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{gpuLatencyMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuLatencyMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{gpuLatencyMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{gpuLatencyMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var gpuTimeMetrics = new ComputeMetrics(processData.GPUTime, processData.GPUTime.Sum);
                reportLines.Add(
                    $"    {"GPU Time",-30}" +
                    $"{gpuTimeMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuTimeMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{gpuTimeMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuTimeMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{gpuTimeMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{gpuTimeMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var gpuBusyMetrics = new ComputeMetrics(processData.GPUBusy, processData.GPUBusy.Sum);
                reportLines.Add(
                    $"    {"GPU Busy",-30}" +
                    $"{gpuBusyMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuBusyMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{gpuBusyMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuBusyMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{gpuBusyMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{gpuBusyMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var gpuWaitMetrics = new ComputeMetrics(processData.GPUWait, processData.GPUWait.Sum);
                reportLines.Add(
                    $"    {"GPU Wait",-30}" +
                    $"{gpuWaitMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuWaitMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{gpuWaitMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{gpuWaitMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{gpuWaitMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{gpuWaitMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var displayLatencyMetrics = new ComputeMetrics(processData.DisplayLatency, processData.DisplayLatency.Sum);
                reportLines.Add(
                    $"    {"Display Latency",-30}" +
                    $"{displayLatencyMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{displayLatencyMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{displayLatencyMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{displayLatencyMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{displayLatencyMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{displayLatencyMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var displayedTimeMetrics = new ComputeMetrics(processData.DisplayedTime, processData.DisplayedTime.Sum);
                reportLines.Add(
                    $"    {"Displayed Time",-30}" +
                    $"{displayedTimeMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{displayedTimeMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{displayedTimeMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{displayedTimeMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{displayedTimeMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{displayedTimeMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var animationErrorMetrics = new ComputeMetrics(processData.AnimationError, processData.AnimationError.Sum);
                reportLines.Add(
                    $"    {"Animation Error",-30}" +
                    $"{animationErrorMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{animationErrorMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{animationErrorMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{animationErrorMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{animationErrorMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{animationErrorMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var allInputToPhotonLatencyMetrics = new ComputeMetrics(processData.AllInputToPhotonLatency, processData.AllInputToPhotonLatency.Sum);
                reportLines.Add(
                    $"    {"Input-to-Photon Latency",-30}" +
                    $"{allInputToPhotonLatencyMetrics.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{allInputToPhotonLatencyMetrics.Average():F3}".PadRight(metricsRightPadding) +
                    $"{allInputToPhotonLatencyMetrics.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{allInputToPhotonLatencyMetrics.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{allInputToPhotonLatencyMetrics.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{allInputToPhotonLatencyMetrics.Percentile(99.9):F3}" +
                    $"\n"
                );

                var clickToPhotonLatency = new ComputeMetrics(processData.ClickToPhotonLatency, processData.ClickToPhotonLatency.Sum);
                reportLines.Add(
                    $"    {"Click-to-Photon Latency",-30}" +
                    $"{clickToPhotonLatency.Maximum():F3}".PadRight(metricsRightPadding) +
                    $"{clickToPhotonLatency.Average():F3}".PadRight(metricsRightPadding) +
                    $"{clickToPhotonLatency.Minimum():F3}".PadRight(metricsRightPadding) +
                    $"{clickToPhotonLatency.StandardDeviation():F3}".PadRight(metricsRightPadding) +
                    $"{clickToPhotonLatency.Percentile(99):F3}".PadRight(metricsRightPadding) +
                    $"{clickToPhotonLatency.Percentile(99.9):F3}" +
                    $"\n"
                );

                reportLines.Add("\n");
            }

            // write output file
            var outputFile = args.OutputFile ?? "xtw-report.txt";

            using (var writer = new StreamWriter(outputFile)) {
                for (var i = 0; i < reportLines.Count; i++) {
                    writer.Write($"{reportLines[i]}");
                }
            }

            log.Information($"report saved in {outputFile}");

            Console.WriteLine();

            return 0;
        }
    }
}
