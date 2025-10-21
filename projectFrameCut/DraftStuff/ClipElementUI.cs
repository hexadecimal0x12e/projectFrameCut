namespace projectFrameCut.DraftStuff
{
    public class ClipElementUI
    {
        public string Id { get; set; }
        public Border Clip { get; set; }
        public Border LeftHandle { get; set; }
        public Border RightHandle { get; set; }
        public ClipMovingStatus MovingStatus { get; set; } = ClipMovingStatus.Free;
        public double layoutX { get; set; }
        public double layoutY { get; set; }
        public double ghostLayoutX { get; set; }
        public double ghostLayoutY { get; set; }
        public double handleLayoutX { get; set; }
        public double defaultY { get; set; } = -1.0;
        public int? origTrack { get; set; } = null;
        public double? maxFrameCount { get; set; } = null;
    }


    public class ClipUpdateEventArgs : EventArgs
    {
        public ClipUpdateEventArgs() { }

        public string? SourceId { get; set; }

        public ClipUpdateReason? Reason { get; set; }
    }

    public enum ClipUpdateReason
    {
        Unknown,
        ClipItselfMove,
        ClipResized
    }

    public enum ClipMovingStatus
    {
        Free,
        Move,
        Resize
    }
}