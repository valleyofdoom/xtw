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

namespace xtw {
    internal class XTW {
        private static Version VERSION = Assembly.GetExecutingAssembly().GetName().Version;

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
                );

                Thread.Sleep(loggingSeconds * 1000);
            }
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
                Thread.Sleep(args.Delay * 1000); // 0 if not specified
                log.Information($"collecting trace for {args.Timed}s");
                StartTrace(args.Timed, "xtw", "xtw_raw.etl");

                // add required symbols metadata
                // https://stackoverflow.com/questions/65351589/windows-performance-analyzer-cannot-load-symbols/65532497#65532497

                var mergeETLs = new string[] { "xtw_raw.etl" };
                ETWKernelControl.Merge(mergeETLs, "xtw.etl", EVENT_TRACE_MERGE_EXTENDED_DATA.IMAGEID);

                // remove trace without metadata
                File.Delete("xtw_raw.etl");

                etlFile = "xtw.etl";
            }

            if (!File.Exists(etlFile)) {
                log.Error($"trace {etlFile} not exists");
                return 1;
            }

            // load the etl
            var traceProcessor = TraceProcessor.Create(etlFile);

            // select events
            var traceMetadata = traceProcessor.UseMetadata();
            var pendingSymbols = traceProcessor.UseSymbols();
            var pendingInterruptHandlingData = traceProcessor.UseInterruptHandlingData();

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

                    log.Information($"loading symbols {percentage:F2}% ({progress.ImagesProcessed}/{progress.ImagesTotal})");
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
            var systemData = new Dictionary<InterruptHandlingType, ModuleData> {
                { InterruptHandlingType.InterruptServiceRoutine, new ModuleData(traceMetadata.ProcessorCount)},
                { InterruptHandlingType.DeferredProcedureCall, new ModuleData(traceMetadata.ProcessorCount)}
            };

            var modulesData = new Dictionary<InterruptHandlingType, Dictionary<string, ModuleData>> {
                { InterruptHandlingType.InterruptServiceRoutine, new Dictionary<string, ModuleData>()},
                { InterruptHandlingType.DeferredProcedureCall, new Dictionary<string, ModuleData>()}
            };

            foreach (var activity in interruptHandlingData.Activity) {
                var interval = activity.Interval;
                var module = interval.HandlerImage.FileName;
                var elapsedTimeUsec = (double)(interval.StopTime.Nanoseconds - interval.StartTime.Nanoseconds) / 1000;

                // try get function name, requires symbols to be loaded
                var functionName = "";
                try {
                    functionName = interval.HandlerStackFrame.Symbol.FunctionName;
                } catch (NullReferenceException) { }

                // populate data for module
                if (!modulesData[interval.Type].ContainsKey(module)) {
                    modulesData[interval.Type][module] = new ModuleData(traceMetadata.ProcessorCount);
                }

                modulesData[interval.Type][module].Data.ElapsedTimesUs.Add(elapsedTimeUsec);
                modulesData[interval.Type][module].Data.ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;
                modulesData[interval.Type][module].Data.SumElapsedTimesUs += elapsedTimeUsec;

                modulesData[interval.Type][module].Data.CountByProcessor[interval.Processor]++;
                modulesData[interval.Type][module].Data.SumCount++;

                var startTimeMs = interval.StartTime.Nanoseconds / 1e+6;
                modulesData[interval.Type][module].Data.StartTimesMs.Add(startTimeMs);

                // keep track of system data and cache everything
                systemData[interval.Type].Data.ElapsedTimesUs.Add(elapsedTimeUsec);
                systemData[interval.Type].Data.ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;
                systemData[interval.Type].Data.SumElapsedTimesUs += elapsedTimeUsec;

                systemData[interval.Type].Data.CountByProcessor[interval.Processor]++;
                systemData[interval.Type].Data.SumCount++;

                systemData[interval.Type].Data.StartTimesMs.Add(startTimeMs);

                // populate data for module functions
                if (functionName != "") {
                    if (!modulesData[interval.Type][module].FunctionsData.ContainsKey(functionName)) {
                        modulesData[interval.Type][module].FunctionsData[functionName] = new Data(traceMetadata.ProcessorCount);
                    }

                    modulesData[interval.Type][module].FunctionsData[functionName].ElapsedTimesUs.Add(elapsedTimeUsec);
                    modulesData[interval.Type][module].FunctionsData[functionName].ElapsedTimeUsByProcessor[interval.Processor] += elapsedTimeUsec;
                    modulesData[interval.Type][module].FunctionsData[functionName].SumElapsedTimesUs += elapsedTimeUsec;

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
            reportLines.Add("\n\n");

            // print metrics
            foreach (var interruptType in modulesData.Keys) {
                var modules = modulesData[interruptType];

                // get shortest right padding for module/symbol names
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
                var moduleRightPadding = shortestModuleNameLength + 10 + (args.Symbols ? 4 : 0);

                var formattedInterruptType =
                    interruptType == InterruptHandlingType.InterruptServiceRoutine
                    ? "Interrupts (ISRs)"
                    : "Deferred Procedure Calls (DPCs)";

                // TABLE: ISR/DPC - Total Elapsed Time (usecs) and Count by CPU
                reportLines.Add(GetTitle($"{formattedInterruptType} - Total Elapsed Time (usecs) and Count by CPU") + "\n\n");

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
                        var elapsedTime = moduleData.Data.ElapsedTimeUsByProcessor[processor];
                        var count = moduleData.Data.CountByProcessor[processor];

                        var processorModuleTotals = count > 0 ? $"{elapsedTime:F2} ({count})" : "-";
                        reportLines.Add(processorModuleTotals.PadRight(metricsRightPadding));

                        totalModuleElapsedTime += elapsedTime;
                        totalModuleInterruptCount += count;
                    }

                    var moduleTotals = totalModuleInterruptCount > 0 ? $"{totalModuleElapsedTime:F2} ({totalModuleInterruptCount})" : "-";
                    reportLines.Add(moduleTotals + "\n");

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        var totalFunctionElapsedTime = 0.0;
                        var totalFunctionCount = 0;

                        reportLines.Add($"        └───{functionName.PadRight(moduleRightPadding - 8)}"); // -8 due to the table-wide indent and branch

                        for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                            var elapsedTime = functionData.ElapsedTimeUsByProcessor[processor];
                            var count = functionData.CountByProcessor[processor];

                            var processorFunctionTotals = count > 0 ? $"{elapsedTime:F2} ({count})" : "-";
                            reportLines.Add(processorFunctionTotals.PadRight(metricsRightPadding));

                            totalFunctionElapsedTime += elapsedTime;
                            totalFunctionCount += count;
                        }

                        var functionTotals = totalFunctionCount > 0 ? $"{totalFunctionElapsedTime:F2} ({totalFunctionCount})" : "-";
                        reportLines.Add($"{functionTotals.PadRight(metricsRightPadding)}\n");
                    }
                }

                // write system total row
                reportLines.Add($"\n    {"Total".PadRight(moduleRightPadding)}");

                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    var elapsedTime = systemData[interruptType].Data.ElapsedTimeUsByProcessor[processor];
                    var count = systemData[interruptType].Data.CountByProcessor[processor];

                    var processorSystemTotals = count > 0 ? $"{elapsedTime:F2} ({count})" : "-";
                    reportLines.Add(processorSystemTotals.PadRight(metricsRightPadding).PadRight(metricsRightPadding));
                }

                var systemTotals = systemData[interruptType].Data.SumCount > 0 ? $"{systemData[interruptType].Data.SumElapsedTimesUs:F2} ({systemData[interruptType].Data.SumCount})" : "-";
                reportLines.Add(systemTotals + "\n");

                // TABLE: ISR/DPC - Interval (ms)
                reportLines.Add(GetTitle($"\n\n{formattedInterruptType} - Interval (ms)") + "\n");

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
                    moduleData.Data.StartTimesMs.Sort();

                    for (var i = 1; i < moduleData.Data.StartTimesMs.Count; i++) {
                        var msBetweenEvents = moduleData.Data.StartTimesMs[i] - moduleData.Data.StartTimesMs[i - 1];
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
                        $"{moduleIntervalMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Average():F2}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                        $"{moduleIntervalMetrics.Percentile(99.9):F2}" +
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
                            $"        └───{functionName.PadRight(moduleRightPadding - 8)}" + // -8 due to the table-wide indent and branch
                            $"{functionIntervalMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Average():F2}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                            $"{functionIntervalMetrics.Percentile(99.9):F2}" +
                            $"\n"
                        );
                    }
                }

                var systemIntervalMetrics = new ComputeMetrics(systemIntervalsMs, sumSystemIntervalsMs);
                reportLines.Add(
                    $"\n    {"System Summary".PadRight(moduleRightPadding)}" +
                    $"{systemIntervalMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Average():F2}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                    $"{systemIntervalMetrics.Percentile(99.9):F2}" +
                    $"\n\n\n"
                );

                // TABLE: ISR/DPC - Elapsed Time (usecs)
                reportLines.Add(GetTitle($"{formattedInterruptType} - Elapsed Time (usecs)") + "\n");

                reportLines.Add($"    {"Module".PadRight(moduleRightPadding)}");
                for (var i = 0; i < metricsTableHeadings.Length; i++) {
                    // don't add padding to last column
                    var rightPadding = i != metricsTableHeadings.Length - 1 ? metricsRightPadding : 0;
                    reportLines.Add(metricsTableHeadings[i].PadRight(rightPadding));
                }
                reportLines.Add("\n");

                foreach (var moduleName in modules.Keys) {
                    var moduleData = modules[moduleName];

                    var moduleElapsedMetrics = new ComputeMetrics(moduleData.Data.ElapsedTimesUs, moduleData.Data.SumElapsedTimesUs);
                    reportLines.Add(
                        $"    {moduleName.PadRight(moduleRightPadding)}" +
                        $"{moduleElapsedMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Average():F2}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                        $"{moduleElapsedMetrics.Percentile(99.9):F2}" +
                        $"\n"
                    );

                    foreach (var functionName in moduleData.FunctionsData.Keys) {
                        var functionData = moduleData.FunctionsData[functionName];

                        var functionElapsedMetrics = new ComputeMetrics(functionData.ElapsedTimesUs, functionData.SumElapsedTimesUs);
                        reportLines.Add(
                            $"        └───{functionName.PadRight(moduleRightPadding - 8)}" + // -8 due to the table-wide indent and branch
                            $"{functionElapsedMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Average():F2}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                            $"{functionElapsedMetrics.Percentile(99.9):F2}" +
                            $"\n"
                        );
                    }
                }

                var systemMetrics = new ComputeMetrics(systemData[interruptType].Data.ElapsedTimesUs, systemData[interruptType].Data.SumElapsedTimesUs);
                reportLines.Add(
                    $"\n    {"System Summary".PadRight(moduleRightPadding)}" +
                    $"{systemMetrics.Maximum():F2}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Average():F2}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Minimum():F2}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.StandardDeviation():F2}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Percentile(99):F2}".PadRight(metricsRightPadding) +
                    $"{systemMetrics.Percentile(99.9):F2}" +
                    $"\n\n"
                );

                reportLines.Add("\n");
            }

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
