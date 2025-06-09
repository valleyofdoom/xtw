namespace xtw {
    public class PresentMonColumn {
        public string Application { get; set; }
        public string PresentRuntime { get; set; }
        public int SyncInterval { get; set; }
        public int PresentFlags { get; set; }
        public int AllowsTearing { get; set; }
        public string PresentMode { get; set; }
        public double FrameTime { get; set; }
        public double CPUBusy { get; set; }
        public double CPUWait { get; set; }
        public double GPULatency { get; set; }
        public double GPUTime { get; set; }
        public double GPUBusy { get; set; }
        public double GPUWait { get; set; }
        public string DisplayLatency { get; set; }
        public string DisplayedTime { get; set; }
        public string AnimationError { get; set; }
        public string AllInputToPhotonLatency { get; set; }
        public string ClickToPhotonLatency { get; set; }
    }
}
