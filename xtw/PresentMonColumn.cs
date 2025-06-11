namespace xtw {
    public class PresentMonColumn {
        public string Application { get; set; }
        public string PresentRuntime { get; set; }
        public int SyncInterval { get; set; }
        public int PresentFlags { get; set; }
        public int AllowsTearing { get; set; }
        public string PresentMode { get; set; }
        public string MsBetweenSimulationStart { get; set; }
        public double MsRenderPresentLatency { get; set; }
        public double MsBetweenPresents { get; set; }
        public double MsBetweenAppStart { get; set; }
        public double MsCPUBusy { get; set; }
        public double MsCPUWait { get; set; }
        public double MsInPresentAPI { get; set; }
        public double MsGPULatency { get; set; }
        public double MsGPUTime { get; set; }
        public double MsGPUBusy { get; set; }
        public double MsGPUWait { get; set; }
        public double MsUntilDisplayed { get; set; }
        public double MsBetweenDisplayChange { get; set; }
        public string MsAnimationError { get; set; }
        public string MsAllInputToPhotonLatency { get; set; }
        public string MsClickToPhotonLatency { get; set; }
    }
}
