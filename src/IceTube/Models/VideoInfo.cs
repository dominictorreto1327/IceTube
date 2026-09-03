namespace IceTube.Models
{
    public sealed class VideoInfo
    {
        public string Title { get; set; }
        public string VideoId { get; set; }
        public string SourceUrl { get; set; }
        public double DurationSeconds { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public string FormatId { get; set; }
        public string VideoStreamUrl { get; set; }
        public string AudioStreamUrl { get; set; }
        public string UserAgent { get; set; }
        public string Referer { get; set; }

        public string DisplayFormat
        {
            get
            {
                string codec = string.IsNullOrWhiteSpace(VideoCodec) ? "H.264" : VideoCodec;
                string resolution = Height > 0 ? Height + "p" : "未知分辨率";
                string fps = Fps > 0 ? System.Math.Round(Fps) + "fps" : "未知帧率";
                return codec + " / " + resolution + " / " + fps;
            }
        }
    }
}
