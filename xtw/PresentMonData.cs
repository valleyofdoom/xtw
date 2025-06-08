using System.Collections.Generic;

namespace xtw {
    public class PresentMonData {
        public HashSet<string> PresentRuntime = new HashSet<string>();
        public HashSet<string> PresentModes = new HashSet<string>();
        public CachedSumList FrameTimes = new CachedSumList();
        public CachedSumList CPUBusy = new CachedSumList();
        public CachedSumList CPUWait = new CachedSumList();
        public CachedSumList GPUBusy = new CachedSumList();
        public CachedSumList GPUWait = new CachedSumList();
        public CachedSumList DisplayedTime = new CachedSumList();
        public CachedSumList AnimationError = new CachedSumList();
        public CachedSumList AnimationTime = new CachedSumList();
    }
}
