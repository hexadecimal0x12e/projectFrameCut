using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Drawing.Vector.ImportExport;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock
{
    /// <summary>
    /// SVG 代码块渲染器。
    /// 将 ```svg 代码块渲染为光栅化后的缩放预览图，
    /// 利用 <c>projectFrameCut.Drawing.Vector</c> 提供的矢量光栅化引擎，
    /// 并在顶部提供"预览图"和"源代码"两种视图模式的切换按钮。
    public class SvgCodeBlockRenderer : CodeBlockRenderer
    {
        // ===== 颜色常量 =====
        private static readonly Color ActiveTabColor = Color.FromArgb("#4A90D9");
        private static readonly Color InactiveBorderColor = Color.FromArgb("#D0D0D0");
        private static readonly Color InactiveTextColor = Color.FromArgb("#888888");
        private static readonly Color HeaderBackgroundColor = Color.FromArgb("#08000000");

        // ===== 布局常量 =====
        private const double ContentHeight = 400;
        private const double ButtonHeight = 28;
        private const double ResizeHandleHeight = 10;
        private const double MinContentHeight = 100;
        private const double MaxContentHeight = 1200;
        private const double WidthResizeHandleWidth = 8;
        private const double MinContentWidth = 200;
        private const double MaxContentWidth = 1600;

        // ===== 光栅化常量 =====
        private const int DefaultRasterWidth = 800;
        private const int DefaultRasterHeight = 600;

        public override string Language => "svg";

        public override bool SupportsStreaming => false;

        public override View Render(string code)
        {
            var previewImage = CreatePreviewImage(code);
            var sourceView = CreateSourceView(code);

            // ContentView 用于在预览图/源代码之间切换
            var contentArea = new ContentView
            {
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };

            // 默认显示预览图
            contentArea.Content = previewImage;

            // 当前内容区域高度，在拖拽缩放时会更新
            double currentHeight = ContentHeight;

            // 顶部操作栏
            var headerBar = CreateHeaderBar(contentArea, previewImage, sourceView);

            // 分隔线
            var separator = new BoxView
            {
                Color = Markdown2XAML.CodeBlockBorderColor,
                HeightRequest = 1,
                HorizontalOptions = LayoutOptions.Fill,
            };

            // 可拖拽的缩放手柄（高度）
            var resizeHandle = new Grid
            {
                HeightRequest = ResizeHandleHeight,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#18FFFFFF"),
            };

            // 视觉指示：三条居中横线
            for (int i = -1; i <= 1; i++)
            {
                resizeHandle.Children.Add(new BoxView
                {
                    HeightRequest = 1.5,
                    WidthRequest = 20,
                    Color = Color.FromArgb("#60FFFFFF"),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    TranslationY = i * 3.5,
                });
            }

            var heightPan = new PanGestureRecognizer();
            double dragStartHeight = currentHeight;
            heightPan.PanUpdated += (_, e) =>
            {
                switch (e.StatusType)
                {
                    case GestureStatus.Started:
                        dragStartHeight = currentHeight;
                        break;
                    case GestureStatus.Running:
                        currentHeight = Math.Clamp(dragStartHeight + e.TotalY, MinContentHeight, MaxContentHeight);
                        previewImage.HeightRequest = currentHeight;
                        sourceView.MaximumHeightRequest = currentHeight;
                        break;
                }
            };
            resizeHandle.GestureRecognizers.Add(heightPan);

            // ===== 代码块主体（带圆角的边框） =====
            var borderBlock = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = Markdown2XAML.CodeBlockCornerRadius },
                BackgroundColor = Markdown2XAML.CodeBlockBackgroundColor,
                Stroke = Markdown2XAML.CodeBlockBorderColor,
                StrokeThickness = 1,
                Padding = new Thickness(0),
                Content = new VerticalStackLayout
                {
                    Spacing = 0,
                    Children = { headerBar, separator, contentArea, resizeHandle }
                }
            };

            // ===== 外层 Grid：代码块 + 宽度手柄 =====
            var outerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                Margin = new Thickness(0, 4),
            };

            // 宽度缩放手柄（右侧纵向条）
            var widthHandle = CreateWidthResizeHandle(outerGrid);

            // 组装到外层 Grid
            Grid.SetColumn(borderBlock, 0);
            outerGrid.Children.Add(borderBlock);
            Grid.SetColumn(widthHandle, 1);
            outerGrid.Children.Add(widthHandle);

            return outerGrid;
        }

        /// <summary>
        /// 创建顶部操作栏：语言标签 + 视图切换按钮。
        /// </summary>
        private Grid CreateHeaderBar(ContentView contentArea, View previewView, View sourceView)
        {
            var headerBar = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),    // 语言标签
                    new ColumnDefinition(GridLength.Star),     // 弹性间距
                    new ColumnDefinition(GridLength.Auto),    // 预览按钮
                    new ColumnDefinition(GridLength.Auto),    // 源代码按钮
                },
                Padding = new Thickness(12, 6),
                ColumnSpacing = 8,
                BackgroundColor = HeaderBackgroundColor,
            };

            // ---- 语言标签 ----
            var langLabel = new Label
            {
                Text = Language,
                FontSize = 11,
                TextColor = Markdown2XAML.CodeBlockTextColor.WithAlpha(0.6f),
                FontFamily = "MarkdownCodeBlock",
                VerticalOptions = LayoutOptions.Center,
            };
            Grid.SetColumn(langLabel, 0);
            headerBar.Children.Add(langLabel);

            // ---- 两个切换按钮 ----
            var previewBtn = CreateToggleButton(
                Localize.APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Preivew, true);
            var sourceBtn = CreateToggleButton(
                Localize.APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Code, false);

            Grid.SetColumn(previewBtn, 2);
            Grid.SetColumn(sourceBtn, 3);
            headerBar.Children.Add(previewBtn);
            headerBar.Children.Add(sourceBtn);

            // 预览按钮点击 → 显示光栅化后的 SVG 图像
            var previewTap = new TapGestureRecognizer();
            previewTap.Tapped += (_, _) =>
            {
                contentArea.Content = previewView;
                SetActiveButton(previewBtn, true);
                SetActiveButton(sourceBtn, false);
            };
            previewBtn.GestureRecognizers.Add(previewTap);

            // 源代码按钮点击 → 显示 SVG 标记源码
            var sourceTap = new TapGestureRecognizer();
            sourceTap.Tapped += (_, _) =>
            {
                contentArea.Content = sourceView;
                SetActiveButton(previewBtn, false);
                SetActiveButton(sourceBtn, true);
            };
            sourceBtn.GestureRecognizers.Add(sourceTap);

            return headerBar;
        }

        /// <summary>
        /// 创建一个切换按钮。
        /// </summary>
        private static Border CreateToggleButton(string text, bool isActive)
        {
            var label = new Label
            {
                Text = text,
                FontSize = 12,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                TextColor = isActive ? Colors.White : InactiveTextColor,
            };

            return new Border
            {
                Content = label,
                BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent,
                Stroke = isActive ? ActiveTabColor : InactiveBorderColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                Padding = new Thickness(10, 2),
                HeightRequest = ButtonHeight,
                VerticalOptions = LayoutOptions.Center,
            };
        }

        /// <summary>
        /// 更新按钮的激活/非激活视觉状态。
        /// </summary>
        private static void SetActiveButton(Border button, bool isActive)
        {
            if (button.Content is Label label)
            {
                label.TextColor = isActive ? Colors.White : InactiveTextColor;
            }
            button.BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent;
            button.Stroke = isActive ? ActiveTabColor : InactiveBorderColor;
        }

        /// <summary>
        /// 创建光栅化后的 SVG 预览图。
        /// 使用 <see cref="CPUVectorPictureRasterizer"/> 将 SVG 解析为位图，
        /// 再通过 <see cref="ImageHelper.ToImageSource"/> 转换为 MAUI <see cref="Image"/>。
        /// </summary>
        private static Image CreatePreviewImage(string svgCode)
        {
            if (string.IsNullOrWhiteSpace(svgCode))
            {
                return new Image
                {
                    Source = null,
                    HeightRequest = ContentHeight,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                };
            }

            try
            {
                // 第 1 步：将 SVG 标记解析为 VectorPicture（矢量数据模型）
                var vectorPicture = SVGToVectorElement.ImportFromSvg(svgCode);

                // 第 2 步：光栅化为位图（IPicture，实际为 Picture16bpp）
                var rasterizer = new CPUVectorPictureRasterizer();
                using var rasterized = rasterizer.Convert(
                    vectorPicture,
                    DefaultRasterWidth,
                    DefaultRasterHeight,
                    transparentBackground: true,
                    aaMode: AntiAliasMode.SSAA2x);

                // 第 3 步：转换为 MAUI ImageSource
                var imageSource = rasterized.ToImageSource();

                return new Image
                {
                    Source = imageSource,
                    Aspect = Aspect.AspectFit,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    HeightRequest = ContentHeight,
                };
            }
            catch (Exception)
            {
                // 解析或光栅化失败时，显示错误提示
                return new Image
                {
                    Source = null,
                    HeightRequest = ContentHeight,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                };
            }
        }

        /// <summary>
        /// 创建源代码视图（带滚动），显示原始 SVG 标记。
        /// </summary>
        private static View CreateSourceView(string code)
        {
            return new ScrollView
            {
                Content = new Label
                {
                    Text = code,
                    FontFamily = "MarkdownCodeBlock",
                    FontSize = 13,
                    TextColor = Markdown2XAML.CodeBlockTextColor,
                },
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                MaximumHeightRequest = ContentHeight,
                Padding = Markdown2XAML.CodeBlockPadding,
            };
        }

        /// <summary>
        /// 创建横向（宽度）缩放手柄。
        /// </summary>
        private static Grid CreateWidthResizeHandle(Grid targetGrid)
        {
            var widthHandle = new Grid
            {
                WidthRequest = WidthResizeHandleWidth,
                VerticalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#18FFFFFF"),
            };

            // 视觉指示：三条竖线居中排列
            for (int i = -1; i <= 1; i++)
            {
                widthHandle.Children.Add(new BoxView
                {
                    WidthRequest = 1.5,
                    HeightRequest = 20,
                    Color = Color.FromArgb("#60FFFFFF"),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    TranslationX = i * 3.5,
                });
            }

            var widthPan = new PanGestureRecognizer();
            double dragStartWidth = 0;
            widthPan.PanUpdated += (_, e) =>
            {
                switch (e.StatusType)
                {
                    case GestureStatus.Started:
                        dragStartWidth = targetGrid.Width > 0 ? targetGrid.Width : targetGrid.WidthRequest;
                        targetGrid.HorizontalOptions = LayoutOptions.Start;
                        targetGrid.WidthRequest = dragStartWidth;
                        break;
                    case GestureStatus.Running:
                        targetGrid.WidthRequest = Math.Clamp(dragStartWidth + e.TotalX, MinContentWidth, MaxContentWidth);
                        break;
                }
            };
            widthHandle.GestureRecognizers.Add(widthPan);

            return widthHandle;
        }
    }
}
