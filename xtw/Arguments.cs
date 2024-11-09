using CommandLine;

namespace xtw {
    public class Arguments {
        [Option("etl_file", HelpText = "Specify path to the ETL file for processing.", Required = true)]
        public string EtlFile { get; set; }

        [Option("no_banner", HelpText = "Hides startup banner.")]
        public bool NoBanner { get; set; }
    }
}
