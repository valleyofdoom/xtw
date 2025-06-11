using System.Collections.Generic;

namespace xtw {
    public class PresentMonData {
        public HashSet<string> PresentRuntime = new HashSet<string>();
        public HashSet<int> SyncInterval = new HashSet<int>();
        public HashSet<int> PresentFlags = new HashSet<int>();
        public HashSet<int> AllowsTearing = new HashSet<int>();
        public HashSet<string> PresentMode = new HashSet<string>();
        public CachedSumList MsBetweenSimulationStart = new CachedSumList();
        public CachedSumList MsRenderPresentLatency = new CachedSumList();
        public CachedSumList MsBetweenPresents = new CachedSumList();
        public CachedSumList MsBetweenAppStart = new CachedSumList();
        public CachedSumList MsCPUBusy = new CachedSumList();
        public CachedSumList MsCPUWait = new CachedSumList();
        public CachedSumList MsInPresentAPI = new CachedSumList();
        public CachedSumList MsGPULatency = new CachedSumList();
        public CachedSumList MsGPUTime = new CachedSumList();
        public CachedSumList MsGPUBusy = new CachedSumList();
        public CachedSumList MsGPUWait = new CachedSumList();
        public CachedSumList MsUntilDisplayed = new CachedSumList();
        public CachedSumList MsBetweenDisplayChange = new CachedSumList();
        public CachedSumList MsAnimationError = new CachedSumList();
        public CachedSumList MsAllInputToPhotonLatency = new CachedSumList();
        public CachedSumList MsClickToPhotonLatency = new CachedSumList();
    }
}
