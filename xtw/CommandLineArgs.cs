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

        [Option("output-directory", HelpText = "Path for store the files generating during the trace.")]
        public string OutputDirectory { get; set; }

        [Option("session-name", HelpText = "Specifies a name to identify and reference the current session in the traces folder.")]
        public string SessionName { get; set; }

        [Option("merge", HelpText = "Merge trace files e.g. --merge trace1.etl trace2.etl ... merged.etl")]
        public IEnumerable<string> MergeETLs { get; set; }

        [Option("open-report", HelpText = "Open the generated report in the system's default text editor.")]
        public bool OpenReport { get; set; }
    }
}
