using System.Text.Json.Serialization;

namespace projectFrameCut.DraftStuff
{
    public class ClipElementUI
    {
        public string Id { get; set; }
        [JsonIgnore]
        public Border Clip { get; set; }
        [JsonIgnore]
        public Border LeftHandle { get; set; }
        [JsonIgnore]
        public Border RightHandle { get; set; }
        public ClipMovingStatus MovingStatus { get; set; } = ClipMovingStatus.Free;
        public double layoutX { get; set; }
        public double layoutY { get; set; }
        public double ghostLayoutX { get; set; }
        public double ghostLayoutY { get; set; }
        public double handleLayoutX { get; set; }
        public double defaultY { get; set; } = -1.0;
        public int? origTrack { get; set; } = null;
        public double origLength { get; set; } = 0;
        public double origX { get; set; } = 0;

        public uint lengthInFrame { get; set; } = 0;

        public bool isInfiniteLength { get; set; } = false;
        public uint maxFrameCount { get; set; } = 0;
        public uint relativeStartFrame { get; set; } =0u;
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
        ClipResized,
        TrackAdd
    }

    public enum ClipMovingStatus
    {
        Free,
        Move,
        Resize
    }
}