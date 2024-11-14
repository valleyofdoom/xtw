using System;
using System.Collections.Generic;
using System.Linq;

namespace xtw {
    internal class ComputeMetrics {
        private List<double> sortedDataset;
        private int size;
        private double average;

        public double Minimum() {
            return size > 0 ? sortedDataset[0] : 0;
        }

        public double Maximum() {
            return size > 0 ? sortedDataset[size - 1] : 0;
        }

        public double Average() {
            return average;
        }

        public double StandardDeviation() {
            if (size == 1) {
                return 0;
            }

            var standardDeviation = 0.0;

            foreach (var executionTimeUs in sortedDataset) {
                standardDeviation += Math.Pow(executionTimeUs - average, 2);
            }

            return Math.Sqrt(standardDeviation / (size - 1));
        }

        public double Percentile(double value) {
            return size > 0 ? sortedDataset[(int)Math.Ceiling(value / 100 * size) - 1] : 0;
        }

        public ComputeMetrics(List<double> dataset) {
            sortedDataset = dataset;
            sortedDataset.Sort();

            // cache values
            size = sortedDataset.Count;
            average = size > 0 ? sortedDataset.Sum() / size : 0;
        }
    }
}
