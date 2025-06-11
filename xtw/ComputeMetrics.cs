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

            var squaredDeviations = 0.0;

            for (var i = 0; i < size; i++) {
                squaredDeviations += Math.Pow(sortedDataset[i] - average, 2);
            }

            return Math.Sqrt(squaredDeviations / (size - 1));
        }

        public double Percentile(double value) {
            return size > 0 ? sortedDataset[(int)Math.Ceiling(value / 100 * size) - 1] : 0;
        }

        public int Size() {
            return size;
        }

        public ComputeMetrics(List<double> dataset, double? total = null) {
            sortedDataset = dataset;
            sortedDataset.Sort();

            // for caching purposes
            var sum = total ?? sortedDataset.Sum();

            // cache values
            size = sortedDataset.Count;
            average = size > 0 ? sum / size : 0;
        }
    }
}
