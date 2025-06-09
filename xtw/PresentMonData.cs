using System.Collections.Generic;

namespace xtw {
    public class PresentMonData {
        public HashSet<string> PresentRuntime = new HashSet<string>();
        public HashSet<int> SyncInterval = new HashSet<int>();
        public HashSet<int> PresentFlags = new HashSet<int>();
        public HashSet<int> AllowsTearing = new HashSet<int>();
        public HashSet<string> PresentMode = new HashSet<string>();
        public CachedSumList FrameTime = new CachedSumList();
        public CachedSumList CPUBusy = new CachedSumList();
        public CachedSumList CPUWait = new CachedSumList();
        public CachedSumList GPULatency = new CachedSumList();
        public CachedSumList GPUTime = new CachedSumList();
        public CachedSumList GPUBusy = new CachedSumList();
        public CachedSumList GPUWait = new CachedSumList();
        public CachedSumList DisplayLatency = new CachedSumList();
        public CachedSumList DisplayedTime = new CachedSumList();
        public CachedSumList AnimationError = new CachedSumList();
        public CachedSumList AllInputToPhotonLatency = new CachedSumList();
        public CachedSumList ClickToPhotonLatency = new CachedSumList();
    }
}
