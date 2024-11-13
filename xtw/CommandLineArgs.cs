using CommandLine;

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

        [Option("output-file", HelpText = "Path for output file.")]
        public string OutputFile { get; set; }
    }
}
