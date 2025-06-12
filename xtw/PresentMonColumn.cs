namespace xtw {
    public class PresentMonColumn {
        public string Application { get; set; }
        public string PresentRuntime { get; set; }
        public string SyncInterval { get; set; }
        public string PresentFlags { get; set; }
        public string AllowsTearing { get; set; }
        public string PresentMode { get; set; }
        public string MsBetweenSimulationStart { get; set; }
        public string MsRenderPresentLatency { get; set; }
        public string MsBetweenPresents { get; set; }
        public string MsBetweenAppStart { get; set; }
        public string MsCPUBusy { get; set; }
        public string MsCPUWait { get; set; }
        public string MsInPresentAPI { get; set; }
        public string MsGPULatency { get; set; }
        public string MsGPUTime { get; set; }
        public string MsGPUBusy { get; set; }
        public string MsGPUWait { get; set; }
        public string MsUntilDisplayed { get; set; }
        public string MsBetweenDisplayChange { get; set; }
        public string MsAnimationError { get; set; }
        public string MsAllInputToPhotonLatency { get; set; }
        public string MsClickToPhotonLatency { get; set; }
    }
}
