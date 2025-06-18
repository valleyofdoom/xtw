using CommandLine;
using System.Collections.Generic;

namespace xtw {
    public class CommandLineArgs {
        [Option("etl-file", HelpText = "Specify path to the ETL file for processing.")]
        public string EtlFile { get; set; }

        [Option("no-banner", HelpText = "Hides startup banner.")]
        public bool NoBanner { get; set; }

        [Option("symbols", HelpText = "Load symbols.")]
        public bool Symbols { get; set; }

        [Option("delay", HelpText = "Delay trace collection for the specified duration.")]
        public int Delay { get; set; }

        [Option("timed", HelpText = "Collect trace events for the specified duration.")]
        public int Timed { get; set; }

        [Option("output-report-file", HelpText = "Path for report output file.")]
        public string OutputReportFile { get; set; }

        [Option("merge", HelpText = "Merge trace files e.g. --merge trace1.etl trace2.etl ... merged.etl")]
        public IEnumerable<string> MergeETLs { get; set; }
    }
}
