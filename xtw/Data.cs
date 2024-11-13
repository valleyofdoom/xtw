using System.Collections.Generic;

namespace xtw {
    public class Data {
        public List<double> ElapsedTimesUs = new List<double>();
        public Dictionary<int, double> ElapsedTimeUsByProcessor = new Dictionary<int, double>();
        public Dictionary<int, int> CountByProcessor = new Dictionary<int, int>();
        public List<double> StartTimesMs = new List<double>();

        public Data(int processorCount) {
            // populate CPUs
            for (var processor = 0; processor < processorCount; processor++) {
                ElapsedTimeUsByProcessor.Add(processor, 0);
                CountByProcessor.Add(processor, 0);
            }
        }
    }
}
