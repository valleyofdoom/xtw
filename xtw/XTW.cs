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
using System.Linq;

namespace xtw {
    internal class XTW {
        private static Version VERSION = Assembly.GetExecutingAssembly().GetName().Version;
        private static string BRANCH = "    └───";
        private static int METRICS_RIGHT_PADDING = 20;


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
                    KernelTraceEventParser.Keywords.ImageLoad
                //KernelTraceEventParser.Keywords.Process |
                //KernelTraceEventParser.Keywords.Thread |
                //KernelTraceEventParser.Keywords.ContextSwitch |
                //KernelTraceEventParser.Keywords.Dispatcher
                );

                // https://github.com/GameTechDev/PresentMon/blob/main/Tools/etl_collection_timed.cmd

                ets.EnableProvider("Microsoft-Windows-DxgKrnl");
                //ets.EnableProvider("Microsoft-Windows-D3D9");
                ets.EnableProvider("Microsoft-Windows-DXGI");
                //ets.EnableProvider("Microsoft-Windows-Dwm-Core");
                //ets.EnableProvider(Guid.Parse("8c9dd1ad-e6e5-4b07-b455-684a9d879900")); // dwm_win7
                ets.EnableProvider("Microsoft-Windows-Win32k");

                ets.CaptureState(Guid.Parse("{802EC45A-1E99-4B83-9920-87C98277BA9D}")); // Microsoft-Windows-DxgKrnl
                //ets.CaptureState(Guid.Parse("{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}")); // Microsoft-Windows-DXGI

                Thread.Sleep(loggingSeconds * 1000);
            }
        }

        private static string GetFormattedInterruptType(InterruptHandlingType interryptHandlingType) {
            return interryptHandlingType == InterruptHandlingType.InterruptServiceRoutine ? "ISR" : "DPC";
        }

        private static string GetComputedMetrics(ComputeMetrics computeMetrics, string name, int nameRightPadding, bool isBranched = false) {
            var finalString = "    ";

            if (isBranched) {
                finalString += $"{BRANCH}{name.PadRight(nameRightPadding - BRANCH.Length)}";
            } else {
                finalString += $"{name.PadRight(nameRightPadding)}";
            }

            if (computeMetrics.Size() == 0) {
                finalString += "-".PadRight(METRICS_RIGHT_PADDING) +
                    "-".PadRight(METRICS_RIGHT_PADDING) +
                    "-".PadRight(METRICS_RIGHT_PADDING) +
                    "-".PadRight(METRICS_RIGHT_PADDING) +
                    "-".PadRight(METRICS_RIGHT_PADDING) +
                    "-".PadRight(METRICS_RIGHT_PADDING);
            } else {
                finalString += $"{computeMetrics.Maximum():F3}".PadRight(METRICS_RIGHT_PADDING) +
                    $"{computeMetrics.Average():F3}".PadRight(METRICS_RIGHT_PADDING) +
                    $"{computeMetrics.Minimum():F3}".PadRight(METRICS_RIGHT_PADDING) +
                    $"{computeMetrics.StandardDeviation():F3}".PadRight(METRICS_RIGHT_PADDING) +
                    $"{computeMetrics.Percentile(99):F3}".PadRight(METRICS_RIGHT_PADDING) +
                    $"{computeMetrics.Percentile(99.9):F3}";
            }

            return finalString;
        }

        public static async Task<int> Main() {
            // create logger
            var log = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Information)
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

            if (args.MergeETLs.Count() > 0) {
                if (args.MergeETLs.Count() < 3) {
                    log.Error("--merge requires at least 3 trace paths e.g. --merge trace1.etl trace2.etl ... merged.etl");
                    return 1;
                }

                var mergeETLs = new List<string>();
                var mergedETL = args.MergeETLs.Last();

                foreach (var tracePath in args.MergeETLs) {
                    // last item
                    if (tracePath == mergedETL) {
                        continue;
                    }

                    if (!File.Exists(tracePath)) {
                        log.Error($"{tracePath} not exists");
                        return 1;
                    }

                    mergeETLs.Add(tracePath);
                }

                ETWKernelControl.Merge(mergeETLs.ToArray(), mergedETL, EVENT_TRACE_MERGE_EXTENDED_DATA.NONE);

                if (!File.Exists(mergedETL)) {
                    log.Error($"failed to merge traces, {mergedETL} not exists");
                    return 1;
                }

                log.Information($"successfully merged traces to: {mergedETL}");
                return 0;
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

                // remove previous ETL file
                if (File.Exists(etlFile)) {
                    File.Delete(etlFile);
                }

                if (args.Delay > 0) {
                    log.Information($"waiting {args.Delay}s before starting trace");
                }
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

            // remove previous CSV file
            if (File.Exists(csvFile)) {
                File.Delete(csvFile);
            }

            var presentmonProcess = Process.Start(new ProcessStartInfo {
                FileName = "PresentMon.exe",
                Arguments = $"--etl_file {etlFile} --output_file {csvFile}",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            presentmonProcess.WaitForExit();

            // used to determine whether to generate the section too
            var hasPresentMonData = File.Exists(csvFile);

            if (!hasPresentMonData) {
                log.Warning($"presentmon was unable to extract data from {etlFile}");
            }

            // allow lost events
            var settings = new TraceProcessorSettings { AllowLostEvents = true };
            // load the etl
            var traceProcessor = TraceProcessor.Create(etlFile, settings);

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

            if (traceMetadata.LostEventCount > 0) {
                log.Warning($"{traceMetadata.LostEventCount} events were lost, consider increasing buffer size");
            }

            if (traceMetadata.LostBufferCount > 0) {
                log.Warning($"{traceMetadata.LostBufferCount} buffers were lost, consider increasing buffer size");
            }

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
                var module = interval.HandlerImage != null ? interval.HandlerImage.FileName : "Unknown";
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
                    reportLines.Add($"CPU {processor.ToString().PadRight(METRICS_RIGHT_PADDING - 4)}"); // -4 due to the table-wide indent
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
                        reportLines.Add(processorModuleTotals.PadRight(METRICS_RIGHT_PADDING));

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
                            reportLines.Add(processorFunctionTotals.PadRight(METRICS_RIGHT_PADDING));

                            totalFunctionElapsedTime += elapsedTime;
                            totalFunctionCount += count;
                        }

                        var functionTotals = totalFunctionCount > 0 ? $"{totalFunctionElapsedTime:F3} ({totalFunctionCount})" : "-";
                        reportLines.Add($"{functionTotals.PadRight(METRICS_RIGHT_PADDING)}\n");
                    }
                }

                // write system total row
                reportLines.Add($"\n    {"Total".PadRight(moduleRightPadding)}");

                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    var elapsedTime = systemData[interruptType].DpcIsrData.ElapsedTimeUsByProcessor[processor];
                    var count = systemData[interruptType].DpcIsrData.CountByProcessor[processor];

                    var processorSystemTotals = count > 0 ? $"{elapsedTime:F3} ({count})" : "-";
                    reportLines.Add(processorSystemTotals.PadRight(METRICS_RIGHT_PADDING).PadRight(METRICS_RIGHT_PADDING));
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
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? METRICS_RIGHT_PADDING : 0;
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

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(moduleIntervalsMs, sumModuleIntervalMs),
                        moduleName,
                        moduleRightPadding
                        ) + "\n");

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

                        reportLines.Add(GetComputedMetrics(
                            new ComputeMetrics(functionIntervalsMs, sumFunctionIntervalsMs),
                            functionName,
                            moduleRightPadding,
                            true
                            ) + "\n");
                    }
                }

                reportLines.Add("\n" + GetComputedMetrics(
                    new ComputeMetrics(systemIntervalsMs, sumSystemIntervalsMs),
                    "System Summary",
                    moduleRightPadding
                    ) + "\n\n");
            }

            reportLines.Add("\n\n"); // space between sections

            // TABLE
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];

                reportLines.Add(GetTitle($"{GetFormattedInterruptType(interruptType)} Elapsed Times (usecs)") + "\n\n");

                reportLines.Add($"    {"Module".PadRight(moduleRightPadding)}");
                for (var i = 0; i < metricsTableHeadings.Length; i++) {
                    // don't add padding to last column
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? METRICS_RIGHT_PADDING : 0;
                    reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                }
                reportLines.Add("\n");

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(moduleData.DpcIsrData.ElapsedTimesUs, moduleData.DpcIsrData.ElapsedTimesUs.Sum),
                        moduleName,
                        moduleRightPadding
                        ) + "\n");

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        reportLines.Add(GetComputedMetrics(
                            new ComputeMetrics(functionData.ElapsedTimesUs, functionData.ElapsedTimesUs.Sum),
                            functionName,
                            moduleRightPadding,
                            true
                            ) + "\n");
                    }
                }

                reportLines.Add("\n" + GetComputedMetrics(
                    new ComputeMetrics(systemData[interruptType].DpcIsrData.ElapsedTimesUs, systemData[interruptType].DpcIsrData.ElapsedTimesUs.Sum),
                    "System Summary",
                    moduleRightPadding
                    ) + "\n\n");
            }

            reportLines.Add("\n\n"); // space between sections

            // TABLE

            if (hasPresentMonData) {
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

                            if (record.SyncInterval != "NA") {
                                presentmonData[record.Application].SyncInterval.Add(
                                     int.Parse(record.SyncInterval));
                            }

                            if (record.PresentFlags != "NA") {
                                presentmonData[record.Application].PresentFlags.Add(
                                     int.Parse(record.PresentFlags));
                            }

                            if (record.AllowsTearing != "NA") {
                                presentmonData[record.Application].AllowsTearing.Add(
                                     int.Parse(record.AllowsTearing));
                            }

                            presentmonData[record.Application].PresentMode.Add(record.PresentMode);

                            if (record.MsBetweenSimulationStart != "NA") {
                                presentmonData[record.Application].MsBetweenSimulationStart.Add(
                                     double.Parse(record.MsBetweenSimulationStart));
                            }

                            if (record.MsRenderPresentLatency != "NA") {
                                presentmonData[record.Application].MsRenderPresentLatency.Add(
                                     double.Parse(record.MsRenderPresentLatency));
                            }

                            if (record.MsBetweenPresents != "NA") {
                                presentmonData[record.Application].MsBetweenPresents.Add(
                                     double.Parse(record.MsBetweenPresents));
                            }

                            if (record.MsBetweenAppStart != "NA") {
                                presentmonData[record.Application].MsBetweenAppStart.Add(
                                     double.Parse(record.MsBetweenAppStart));
                            }

                            if (record.MsCPUBusy != "NA") {
                                presentmonData[record.Application].MsCPUBusy.Add(
                                     double.Parse(record.MsCPUBusy));
                            }

                            if (record.MsCPUWait != "NA") {
                                presentmonData[record.Application].MsCPUWait.Add(
                                     double.Parse(record.MsCPUWait));
                            }

                            if (record.MsInPresentAPI != "NA") {
                                presentmonData[record.Application].MsInPresentAPI.Add(
                                     double.Parse(record.MsInPresentAPI));
                            }

                            if (record.MsGPULatency != "NA") {
                                presentmonData[record.Application].MsGPULatency.Add(
                                     double.Parse(record.MsGPULatency));
                            }

                            if (record.MsGPUTime != "NA") {
                                presentmonData[record.Application].MsGPUTime.Add(
                                     double.Parse(record.MsGPUTime));
                            }

                            if (record.MsGPUBusy != "NA") {
                                presentmonData[record.Application].MsGPUBusy.Add(
                                     double.Parse(record.MsGPUBusy));
                            }

                            if (record.MsUntilDisplayed != "NA") {
                                presentmonData[record.Application].MsUntilDisplayed.Add(
                                     double.Parse(record.MsUntilDisplayed));
                            }

                            if (record.MsGPUTime != "NA") {
                                presentmonData[record.Application].MsGPUTime.Add(
                                     double.Parse(record.MsGPUTime));
                            }

                            if (record.MsBetweenDisplayChange != "NA") {
                                presentmonData[record.Application].MsBetweenDisplayChange.Add(
                                     double.Parse(record.MsBetweenDisplayChange));
                            }

                            if (record.MsAnimationError != "NA") {
                                presentmonData[record.Application].MsAnimationError.Add(
                                     double.Parse(record.MsAnimationError));
                            }

                            if (record.MsAllInputToPhotonLatency != "NA") {
                                presentmonData[record.Application].MsAllInputToPhotonLatency.Add(
                                    double.Parse(record.MsAllInputToPhotonLatency));
                            }

                            if (record.MsClickToPhotonLatency != "NA") {
                                presentmonData[record.Application].MsClickToPhotonLatency.Add(
                                    double.Parse(record.MsClickToPhotonLatency));
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

                    var processRightPadding = 30;

                    reportLines.Add($"    {"Metric".PadRight(processRightPadding)}");
                    for (var i = 0; i < metricsTableHeadings.Length; i++) {
                        // don't add padding to last column
                        var rightPadding = i != metricsTableHeadings.Length - 1 ? METRICS_RIGHT_PADDING : 0;
                        reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                    }
                    reportLines.Add("\n");


                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsBetweenSimulationStart, processData.MsBetweenSimulationStart.Sum),
                        "MsBetweenSimulationStart",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsRenderPresentLatency, processData.MsRenderPresentLatency.Sum),
                        "RenderPresentLatency",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsBetweenPresents, processData.MsBetweenPresents.Sum),
                        "MsBetweenPresents",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add("\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsBetweenAppStart, processData.MsBetweenAppStart.Sum),
                        "Frame Time",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsCPUBusy, processData.MsCPUBusy.Sum),
                        "CPU Busy",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsCPUWait, processData.MsCPUWait.Sum),
                        "CPU Wait",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsInPresentAPI, processData.MsInPresentAPI.Sum),
                        "MsInPresentAPI",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add("\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsGPULatency, processData.MsGPULatency.Sum),
                        "GPU Latency",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsGPUTime, processData.MsGPUTime.Sum),
                        "GPU Time",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsGPUBusy, processData.MsGPUBusy.Sum),
                        "GPU Busy",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsGPUWait, processData.MsGPUWait.Sum),
                        "GPU Wait",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add("\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsUntilDisplayed, processData.MsUntilDisplayed.Sum),
                        "MsUntilDisplayed",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsBetweenDisplayChange, processData.MsBetweenDisplayChange.Sum),
                        "Displayed Time",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsAnimationError, processData.MsAnimationError.Sum),
                        "Animation Error",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add("\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsAllInputToPhotonLatency, processData.MsAllInputToPhotonLatency.Sum),
                        "Input To Photon Latency",
                        processRightPadding
                        ) + "\n");

                    reportLines.Add(GetComputedMetrics(
                        new ComputeMetrics(processData.MsClickToPhotonLatency, processData.MsClickToPhotonLatency.Sum),
                        "Click To Photon Latency",
                        processRightPadding
                        ) + "\n");
                }

                reportLines.Add("\n\n\n"); // space between sections
            }

            // write output file
            var outputFile = args.OutputReportFile ?? "xtw-report.txt";

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
