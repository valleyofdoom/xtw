using System;
using System.Collections.Generic;
using System.Linq;

namespace xtw {
    internal class ComputeMetrics {
        private List<double> sortedDataset;
        private int size;
        private double average;

        public double Minimum() {
            return sortedDataset[0];
        }

        public double Maximum() {
            return sortedDataset[size - 1];
        }

        public double Average() {
            return average;
        }

        public double StandardDeviation() {
            var standardDeviation = 0.0;

            foreach (var executionTimeUs in sortedDataset) {
                standardDeviation += Math.Pow(executionTimeUs - average, 2);
            }

            return Math.Sqrt(standardDeviation / (size - 1));
        }

        public double Percentile(double value) {
            return sortedDataset[(int)Math.Ceiling(value / 100 * size) - 1];
        }

        public ComputeMetrics(List<double> dataset) {
            sortedDataset = dataset;
            sortedDataset.Sort();

            // cache values
            size = sortedDataset.Count;
            average = sortedDataset.Sum() / size;
        }
    }
}
