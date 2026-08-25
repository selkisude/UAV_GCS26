namespace UAV_GCS
{
    public class TargetBoxData
    {
        public bool Tracking { get; set; } = false;
        public int CenterX { get; set; } = 0;
        public int CenterY { get; set; } = 0;
        public int Width { get; set; } = 0;
        public int Height { get; set; } = 0;
    }
}