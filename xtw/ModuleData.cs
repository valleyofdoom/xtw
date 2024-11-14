using System.Collections.Generic;

namespace xtw {
    public class ModuleData {
        public Data Data;
        public Dictionary<string, Data> FunctionsData = new Dictionary<string, Data>();

        public ModuleData(int processorCount) {
            Data = new Data(processorCount);
        }
    }
}
