using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Shared;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        public uint relativeStartFrame { get; set; } = 0u;

        public float sourceSecondPerFrame { get; set; } = 1f;
        public float SecondPerFrameRatio { get; set; } = 1f;

        public ClipMode ClipType { get; set; } = ClipMode.Special;
        public string? sourcePath { get; set; } = null;

        public Dictionary<string, projectFrameCut.Shared.IEffect> Effects { get; set; } = new();
        public Dictionary<string, object> ExtraData { get; set; } = new();

        public void ApplySpeedRatio()
        {
            Clip.WidthRequest = origLength * SecondPerFrameRatio;
        }



        [SetsRequiredMembers]
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        public ClipElementUI()
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        {
        }

        private static double _defaultClipHeight = 62;

        public static ClipElementUI CreateClip(
    double startX,
    double width,
    int trackIndex,
    string? id = null,
    string? labelText = null,
    Brush? background = null,
    Border? prototype = null,
    uint relativeStart = 0,
    uint maxFrames = 0)
        {

            string cid = id ?? Guid.NewGuid().ToString();

            // Build UI
            var clipBorder = new Border
            {
                Stroke = prototype?.Stroke ?? Colors.Gray,
                StrokeThickness = prototype?.StrokeThickness ?? 2,
                Background = background ?? prototype?.Background ?? new SolidColorBrush(Colors.CornflowerBlue),
                WidthRequest = width,
                HeightRequest = prototype?.HeightRequest > 0 ? prototype!.HeightRequest : _defaultClipHeight,
                StrokeShape = prototype?.StrokeShape ?? new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                    StrokeThickness = 8
                }
            };

            var leftHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 25,
                HeightRequest = 55,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                }
            };

            var rightHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 25,
                HeightRequest = 55,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                }
            };

            var element = new ClipElementUI
            {
                Id = cid,
                displayName = labelText ?? "Unnamed Clip",
                layoutX = 0,
                layoutY = 0,
                Clip = clipBorder,
                LeftHandle = leftHandle,
                RightHandle = rightHandle,
                maxFrameCount = maxFrames,
                relativeStartFrame = relativeStart,
                isInfiniteLength = width <= 0,
                origLength = width,
                origTrack = trackIndex,
                origX = startX
            };

            var cont = new HorizontalStackLayout
            {
                Children =
            {
                new Label
                {
                    Text = string.IsNullOrWhiteSpace(labelText) ? $"Clip {cid[^4..]}" : labelText
                }
            },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };

            Grid.SetColumn(element.LeftHandle, 0);
            Grid.SetColumn(element.RightHandle, 2);
            Grid.SetColumn(cont, 1);

            element.Clip.Content = new Grid
            {
                Children =
            {
                element.LeftHandle,
                cont,
                element.RightHandle
            },
                ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) }
            }
            };

            element.Clip.BindingContext = element;
            element.LeftHandle.BindingContext = element;
            element.RightHandle.BindingContext = element;


            return element;
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