using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace projectFrameCut.DraftStuff
{
    public class ClipElementUI
    {
        public required string Id { get; set; }
        [JsonIgnore]
        public required Border Clip { get; set; }
        [JsonIgnore]
        public required Border LeftHandle { get; set; }
        [JsonIgnore]
        public required Border RightHandle { get; set; }

        public string displayName { get; set; } = "Clip";

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

        [SetsRequiredMembers]
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        public ClipElementUI()
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        {
        }   
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