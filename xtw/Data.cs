using System.Collections.Generic;

namespace xtw {
    public class Data {
        public List<double> RawDataset = new List<double>();
        public Dictionary<int, int> CountByProcessor = new Dictionary<int, int>();
        public List<double> StartTimesMs= new List<double>();
    }
}
