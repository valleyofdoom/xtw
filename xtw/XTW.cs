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

namespace xtw {
    internal class XTW {
        private static void ShowBanner() {
            var version = Assembly.GetExecutingAssembly().GetName().Version;

            Console.WriteLine($"XTW Version {version.Major}.{version.Minor}.{version.Build} - GPLv3\nGitHub - https://github.com/valleyofdoom\n");
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

        public static int Main() {
            // create logger
            var log = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();

            // parse arguments
            Arguments args = null;

            _ = Parser.Default.ParseArguments<Arguments>(Environment.GetCommandLineArgs()).WithParsed(parsedArgs => {
                args = parsedArgs;
            });

            if (args == null) {
                log.Error("CLI arguments error");
                return 1;
            }

            if (!args.NoBanner) {
                ShowBanner();
            }

            if (!IsAdmin()) {
                log.Error("administrator privileges required");
                return 1;
            }

            if (!File.Exists(args.EtlFile)) {
                log.Error($"{args.EtlFile} not exists");
                return 1;
            }

            // load etl
            var traceProcessor = TraceProcessor.Create(args.EtlFile);

            // select events
            var traceMetadata = traceProcessor.UseMetadata();
            var pendingInterruptHandlingData = traceProcessor.UseInterruptHandlingData();

            // setup callback for progress tracking
            var currentPass = 0;
            var progress = new Progress<TraceProcessingProgress>(traceProgress => {
                if (traceProgress.CurrentPass + 1 > currentPass) {
                    currentPass++;
                    log.Information($"processing trace (pass {currentPass}/{traceProgress.TotalPasses})");
                }
            });

            // process etl
            traceProcessor.Process(progress);

            // initialize data dictionary

            /*
            structure of modulesData:

            {
                "modules": {
                    "ISR": {
                        "module.sys": {
                            "raw_dataset": [],
                            "count_by_cpu": {
                                "0": 0,
                                "1": 0
                            }
                        }
                    },
                    "DPC": {
                        "module.sys": {
                            "raw_dataset": [],
                            "count_by_cpu": {
                                "0": 0,
                                "1": 0
                            }
                        }
                    }
                }
            }
             */

            var modulesData = new Dictionary<InterruptHandlingType, Dictionary<string, Data>> {
                { InterruptHandlingType.InterruptServiceRoutine, new Dictionary<string, Data>()},
                { InterruptHandlingType.DeferredProcedureCall, new Dictionary<string, Data>()}
            };

            // get result of selected events
            var interruptHandlingData = pendingInterruptHandlingData.Result;
            if (interruptHandlingData.Activity.Count == 0) {
                log.Error("no interrupt handling data in provided trace");
                return 1;
            }

            foreach (var activity in interruptHandlingData.Activity) {
                var activityEvent = activity.Interval;
                var module = activityEvent.HandlerImage.FileName;
                var elapsedTimeUs = (activityEvent.StopTime.Nanoseconds - activityEvent.StartTime.Nanoseconds) / 1000;

                // add module to dict if it does not exist yet
                if (!modulesData[activityEvent.Type].ContainsKey(module)) {
                    modulesData[activityEvent.Type].Add(module, new Data());

                    // populate processors for data block
                    for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                        modulesData[activityEvent.Type][module].CountByProcessor.Add(processor, 0);
                    }

                }

                modulesData[activityEvent.Type][module].RawDataset.Add(elapsedTimeUs);
                modulesData[activityEvent.Type][module].CountByProcessor[activityEvent.Processor]++;
            }

            string[] tableHeadings = { "Max", "Avg", "Min", "STDEV", "99 %ile", "99.9 %ile" };

            // print metrics
            foreach (var interruptType in modulesData.Keys) {
                // system metrics
                var systemData = new Data();

                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    systemData.CountByProcessor.Add(processor, 0);
                }

                var shortInterruptType = interruptType == InterruptHandlingType.InterruptServiceRoutine ? "ISR" : "DPC";
                var modules = modulesData[interruptType];

                // make count by cpu table
                Console.WriteLine(GetTitle($"{shortInterruptType} Module Count by CPU:"));

                // print table headings
                Console.Write($"{"Module",-22}");
                for (var processor = 0; processor < traceMetadata.ProcessorCount; processor++) {
                    Console.Write($"CPU {processor,-8}");
                }
                Console.WriteLine("Total");

                foreach (var module in modules.Keys) {
                    var moduleData = modules[module];

                    var moduleTotal = 0;

                    Console.Write($"{module,-22}");
                    foreach (var processor in moduleData.CountByProcessor.Keys) {
                        var count = moduleData.CountByProcessor[processor];
                        Console.Write($"{count,-12}");
                        moduleTotal += count;

                        // keep track of total cpu count for system stats
                        systemData.CountByProcessor[processor] += count;
                    }
                    Console.Write($"{moduleTotal,-12}\n");
                }

                var systemTotal = 0;

                Console.Write($"{"Total",-22}");
                foreach (var count in systemData.CountByProcessor.Values) {
                    Console.Write($"{count,-12}");
                    systemTotal += count;
                }
                Console.Write($"{systemTotal,-12}\n");

                // make module elapsed times table
                Console.WriteLine(GetTitle($"\n\n{shortInterruptType} Module Elapsed Times (usecs):"));

                // print table headings
                Console.Write($"{"Module",-22}");
                foreach (var tableHeading in tableHeadings) {
                    Console.Write($"{tableHeading,-12}");
                }
                Console.WriteLine();

                foreach (var module in modules.Keys) {
                    var moduleData = modules[module];

                    // keep track of all elapsed times for system stats
                    systemData.RawDataset.AddRange(moduleData.RawDataset);

                    var metrics = new ComputeMetrics(moduleData.RawDataset);

                    Console.WriteLine($"{module,-22:F2}{metrics.Maximum(),-12:F2}{metrics.Average(),-12:F2}{metrics.Minimum(),-12:F2}{metrics.StandardDeviation(),-12:F2}{metrics.Percentile(99),-12:F2}{metrics.Percentile(99.9),-12:F2}");
                }

                // make system elapsed times table
                Console.WriteLine(GetTitle($"\n\n{shortInterruptType} System Elapsed Times (usecs):"));

                foreach (var tableHeading in tableHeadings) {
                    Console.Write($"{tableHeading,-12}");
                }
                Console.WriteLine();

                var systemMetrics = new ComputeMetrics(systemData.RawDataset);
                Console.WriteLine($"{systemMetrics.Maximum(),-12:F2}{systemMetrics.Average(),-12:F2}{systemMetrics.Minimum(),-12:F2}{systemMetrics.StandardDeviation(),-12:F2}{systemMetrics.Percentile(99),-12:F2}{systemMetrics.Percentile(99.9),-12:F2}\n\n");
            }

            return 0;
        }
    }
}
