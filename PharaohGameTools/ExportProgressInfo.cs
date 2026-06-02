namespace PharaohGameTools
{
    public sealed class ExportProgressInfo
    {
        public required string StageText { get; init; }
        public required int ProcessedFrames { get; init; }
        public required int TotalFrames { get; init; }
        public required double Percent { get; init; }
    }
}
