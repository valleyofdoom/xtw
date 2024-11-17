using System.Collections.Generic;

namespace xtw {
    public class Data {
        public List<double> ElapsedTimesUs = new List<double>();
        public Dictionary<int, double> ElapsedTimeUsByProcessor = new Dictionary<int, double>();
        public double SumElapsedTimesUs = 0;

        public Dictionary<int, int> CountByProcessor = new Dictionary<int, int>();
        public double SumCount = 0;

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
