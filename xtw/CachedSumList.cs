using System;
using System.Collections.Generic;

namespace xtw {
    public class CachedSumList : List<double> {
        public double Sum { get; private set; }

        public CachedSumList() {
            Sum = 0;
        }

        public new void Add(double item) {
            base.Add(item);
            Sum += item;
        }

        public new bool Remove(double item) {
            throw new NotImplementedException();
        }

        public new void Clear() {
            throw new NotImplementedException();
        }

        public new void Insert(int index, double item) {
            throw new NotImplementedException();
        }
    }
}
