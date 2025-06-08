using System.Collections.Generic;

namespace xtw {
    public class Module {
        public DpcIsrData DpcIsrData;
        public Dictionary<string, DpcIsrData> FunctionsData = new Dictionary<string, DpcIsrData>();

        public Module(int processorCount) {
            DpcIsrData = new DpcIsrData(processorCount);
        }
    }
}
