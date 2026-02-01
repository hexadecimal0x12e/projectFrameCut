using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.DraftStuff
{
	public class BindableEffectBindingView : ContentView
	{
		private readonly ClipElementUI clip;
		private readonly EventHandler<PropertyPanelPropertyChangedEventArgs>? handler;

		private readonly AbsoluteLayout graphLayer;
		private readonly GraphicsView lineLayer;
		private readonly BindingGraphDrawable drawable;

		private readonly Dictionary<string, NodeVisual> nodeById = new();
		private string? selectedOutputId;
		private BoxView? selectedOutputAnchor;

		private const double NodeWidth = 240;
		private const double NodeMinHeight = 90;

		public BindableEffectBindingView(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs>? handler = null)
		{
			this.clip = clip;
			this.handler = handler;

			drawable = new BindingGraphDrawable(GetConnections);
			lineLayer = new GraphicsView
			{
				Drawable = drawable,
				InputTransparent = true
			};

			graphLayer = new AbsoluteLayout
			{
				HeightRequest = 320,
				BackgroundColor = Colors.Transparent
			};

			AbsoluteLayout.SetLayoutBounds(lineLayer, new Rect(0, 0, 1, 1));
			AbsoluteLayout.SetLayoutFlags(lineLayer, AbsoluteLayoutFlags.SizeProportional);
			graphLayer.Children.Add(lineLayer);

			BuildNodes();

			var hint = new Label
			{
				Text = "点输出端口，再点输入端口以连接。",
				FontSize = 12,
				Opacity = 0.7
			};

			var clearSelButton = new Button
			{
				Text = "清除选择",
				FontSize = 12,
				Padding = new Thickness(8, 2),
				HorizontalOptions = LayoutOptions.End
			};
			clearSelButton.Clicked += (_, __) => ClearSelection();

			Content = new VerticalStackLayout
			{
				Spacing = 6,
				Children =
				{
					new Grid
					{
						ColumnDefinitions =
						{
							new ColumnDefinition { Width = GridLength.Star },
							new ColumnDefinition { Width = GridLength.Auto }
						},
						Children =
						{
							hint,
							clearSelButton
						}
					},
					new Border
					{
						Stroke = Colors.Gray,
						StrokeThickness = 0.5,
						StrokeShape = new RoundRectangle { CornerRadius = 8 },
						Background = new SolidColorBrush(Colors.Transparent),
						Padding = 6,
						Content = graphLayer
					}
				}
			};

			lineLayer.SizeChanged += (_, __) => lineLayer.Invalidate();
		}

		private void BuildNodes()
		{
			graphLayer.Children.Clear();
			AbsoluteLayout.SetLayoutBounds(lineLayer, new Rect(0, 0, 1, 1));
			AbsoluteLayout.SetLayoutFlags(lineLayer, AbsoluteLayoutFlags.SizeProportional);
			graphLayer.Children.Add(lineLayer);
			nodeById.Clear();

			var bindableEffects = clip.Effects?.Values
				.Where(e => e is IBindableArgumentEffect)
				.OrderBy(e => e.Index)
				.Cast<IBindableArgumentEffect>()
				.ToList() ?? new List<IBindableArgumentEffect>();

			if (bindableEffects.Count == 0)
			{
				var empty = new Label
				{
					Text = "没有可绑定的参数化效果。",
					Opacity = 0.6,
					HorizontalOptions = LayoutOptions.Center,
					VerticalOptions = LayoutOptions.Center
				};
				AbsoluteLayout.SetLayoutBounds(empty, new Rect(0.5, 0.5, 1, 1));
				AbsoluteLayout.SetLayoutFlags(empty, AbsoluteLayoutFlags.PositionProportional);
				graphLayer.Children.Add(empty);
				return;
			}

			int idx = 0;
			foreach (var effect in bindableEffects)
			{
				if (string.IsNullOrWhiteSpace(effect.Id))
				{
					effect.Id = Guid.NewGuid().ToString();
				}

				var node = CreateNode(effect);
				nodeById[effect.Id] = node;

				int col = idx % 2;
				int row = idx / 2;
				double x = 20 + col * (NodeWidth + 40);
				double y = 20 + row * (NodeMinHeight + 40);

				AbsoluteLayout.SetLayoutBounds(node.Root, new Rect(x, y, NodeWidth, node.Root.HeightRequest));
				AbsoluteLayout.SetLayoutFlags(node.Root, AbsoluteLayoutFlags.None);
				graphLayer.Children.Add(node.Root);
				idx++;
			}

			int rows = (bindableEffects.Count + 1) / 2;
			graphLayer.HeightRequest = Math.Max(320, 40 + rows * (NodeMinHeight + 40));
		}

		private NodeVisual CreateNode(IBindableArgumentEffect effect)
		{
			var inputNames = GetInputAnchorNames(effect);
			var outputName = GetOutputAnchorName(effect);

			var inputsStack = new VerticalStackLayout { Spacing = 6 };
			var inputAnchors = new List<AnchorInfo>();

			for (int i = 0; i < inputNames.Count; i++)
			{
				var anchor = CreateAnchor(Colors.Coral);
				var nameLabel = new Label { Text = inputNames[i], FontSize = 12 };

				var clearBtn = new Button
				{
					Text = "×",
					FontSize = 12,
					Padding = new Thickness(4, 0),
					HeightRequest = 20,
					WidthRequest = 20
				};
				int idx = i;
				clearBtn.Clicked += (_, __) =>
				{
					ClearInputBinding(effect, idx);
					lineLayer.Invalidate();
					handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("EffectBindingChanged", null, null));
				};

				var row = new HorizontalStackLayout
				{
					Spacing = 6,
					Children = { anchor, nameLabel, clearBtn }
				};

				var ctx = new AnchorInfo(effect, idx, AnchorKind.Input);
				anchor.BindingContext = ctx;
				var tap = new TapGestureRecognizer();
				tap.Tapped += (_, __) => OnInputAnchorTapped(ctx);
				anchor.GestureRecognizers.Add(tap);

				inputsStack.Children.Add(row);
				inputAnchors.Add(ctx with { Anchor = anchor });
			}

			var outputAnchor = outputName != null ? CreateAnchor(Colors.DodgerBlue) : null;
			if (outputAnchor != null)
			{
				var outputTap = new TapGestureRecognizer();
				outputTap.Tapped += (_, __) => OnOutputAnchorTapped(effect, outputAnchor);
				outputAnchor.GestureRecognizers.Add(outputTap);
			}

			var outputStack = new VerticalStackLayout
			{
				Spacing = 4,
				HorizontalOptions = LayoutOptions.End,
				Children =
				{
					outputAnchor ?? new BoxView { WidthRequest = 0, HeightRequest = 0, IsVisible = false },
					new Label { Text = outputName ?? string.Empty, FontSize = 12, HorizontalOptions = LayoutOptions.End }
				}
			};

			var title = new Label
			{
				Text = string.IsNullOrWhiteSpace(effect.Name) ? effect.TypeName : effect.Name,
				FontAttributes = FontAttributes.Bold,
				FontSize = 14,
				LineBreakMode = LineBreakMode.TailTruncation
			};
			var idLabel = new Label
			{
				Text = effect.Id,
				FontSize = 10,
				Opacity = 0.5,
				LineBreakMode = LineBreakMode.MiddleTruncation
			};

			var nodeBody = new Grid
			{
				ColumnDefinitions =
				{
					new ColumnDefinition { Width = GridLength.Auto },
					new ColumnDefinition { Width = GridLength.Star },
					new ColumnDefinition { Width = GridLength.Auto }
				},
				RowDefinitions =
				{
					new RowDefinition { Height = GridLength.Auto }
				},
				Padding = 8
			};

			var centerStack = new VerticalStackLayout
			{
				Spacing = 4,
				Children = { title, idLabel }
			};

			nodeBody.Children.Add(inputsStack);
			nodeBody.Children.Add(centerStack);
			nodeBody.Children.Add(outputStack);
			Grid.SetColumn(centerStack, 1);
			Grid.SetColumn(outputStack, 2);

			double height = Math.Max(NodeMinHeight, 50 + inputNames.Count * 26);

			var root = new Border
			{
				Background = new SolidColorBrush(Colors.Transparent),
				Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.6f)),
				StrokeThickness = 1,
				StrokeShape = new RoundRectangle { CornerRadius = 8 },
				Content = nodeBody,
				WidthRequest = NodeWidth,
				HeightRequest = height
			};

			AttachDrag(root);

			root.SizeChanged += (_, __) => lineLayer.Invalidate();
			if (outputAnchor != null) outputAnchor.SizeChanged += (_, __) => lineLayer.Invalidate();
			foreach (var item in inputAnchors)
			{
				item.Anchor!.SizeChanged += (_, __) => lineLayer.Invalidate();
			}

			return new NodeVisual(effect, root, outputAnchor, inputAnchors);
		}

		private BoxView CreateAnchor(Color color)
		{
			return new BoxView
			{
				WidthRequest = 12,
				HeightRequest = 12,
				CornerRadius = 6,
				Color = color,
				VerticalOptions = LayoutOptions.Center
			};
		}

		private void AttachDrag(Border node)
		{
			double startX = 0, startY = 0;
			var pan = new PanGestureRecognizer();
			pan.PanUpdated += (_, e) =>
			{
				if (e.StatusType == GestureStatus.Started)
				{
					var bounds = AbsoluteLayout.GetLayoutBounds(node);
					startX = bounds.X;
					startY = bounds.Y;
				}
				else if (e.StatusType == GestureStatus.Running)
				{
					var bounds = AbsoluteLayout.GetLayoutBounds(node);
					AbsoluteLayout.SetLayoutBounds(node, new Rect(startX + e.TotalX, startY + e.TotalY, bounds.Width, bounds.Height));
					lineLayer.Invalidate();
				}
			};
			node.GestureRecognizers.Add(pan);
		}

		private void OnOutputAnchorTapped(IBindableArgumentEffect effect, BoxView anchor)
		{
			ClearSelection();
			selectedOutputId = effect.Id;
			selectedOutputAnchor = anchor;
			anchor.Color = Colors.DeepSkyBlue;
			lineLayer.Invalidate();
		}

		private void OnInputAnchorTapped(AnchorInfo ctx)
		{
			if (string.IsNullOrWhiteSpace(selectedOutputId)) return;
			if (ctx.Effect.Id == selectedOutputId) return;

			BindInput(ctx.Effect, ctx.InputIndex, selectedOutputId);
			ClearSelection();
			lineLayer.Invalidate();
			handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("EffectBindingChanged", null, null));
		}

		private void BindInput(IBindableArgumentEffect effect, int inputIndex, string providerId)
		{
			if (effect is IBindableArgumentEffectManyToOneValueProcesser mpe)
			{
				var ids = EnsureIdsLength(mpe.BindedArgumentProviderIDs, GetInputAnchorNames(effect).Count);
				ids[inputIndex] = providerId;
				mpe.BindedArgumentProviderIDs = ids;
				return;
			}

			if (effect is IBindableArgumentEffectManyInputResultGenerator mpg)
			{
				var ids = EnsureIdsLength(mpg.BindedArgumentProviderIDs, GetInputAnchorNames(effect).Count);
				ids[inputIndex] = providerId;
				mpg.BindedArgumentProviderIDs = ids;
				return;
			}

			effect.BindedArgumentProviderID = providerId;
		}

		private void ClearInputBinding(IBindableArgumentEffect effect, int inputIndex)
		{
			if (effect is IBindableArgumentEffectManyToOneValueProcesser mpe)
			{
				if (mpe.BindedArgumentProviderIDs == null || mpe.BindedArgumentProviderIDs.Length <= inputIndex) return;
				mpe.BindedArgumentProviderIDs[inputIndex] = null!;
				return;
			}

			if (effect is IBindableArgumentEffectManyInputResultGenerator mpg)
			{
				if (mpg.BindedArgumentProviderIDs == null || mpg.BindedArgumentProviderIDs.Length <= inputIndex) return;
				mpg.BindedArgumentProviderIDs[inputIndex] = null!;
				return;
			}

			effect.BindedArgumentProviderID = null;
		}

		private string[] EnsureIdsLength(string[]? source, int length)
		{
			if (length <= 0) length = 1;
			if (source == null || source.Length != length)
			{
				var result = new string[length];
				if (source != null)
				{
					for (int i = 0; i < Math.Min(source.Length, result.Length); i++)
						result[i] = source[i];
				}
				return result;
			}
			return source;
		}

		private void ClearSelection()
		{
			if (selectedOutputAnchor != null)
			{
				selectedOutputAnchor.Color = Colors.DodgerBlue;
			}
			selectedOutputAnchor = null;
			selectedOutputId = null;
		}

		private List<string> GetInputAnchorNames(IBindableArgumentEffect effect)
		{
			if (effect is IBindableArgumentEffectOneToOneValueProcesser oneToOne)
				return new List<string> { oneToOne.InputAnchorName };
			if (effect is IBindableArgumentEffectOneInputResultGenerator oneIn)
				return new List<string> { oneIn.InputAnchorName };
			if (effect is IBindableArgumentEffectManyToOneValueProcesser manyToOne)
				return manyToOne.InputAnchorDisplayNames?.ToList() ?? new List<string> { "Input" };
			if (effect is IBindableArgumentEffectManyInputResultGenerator manyIn)
				return manyIn.InputAnchorDisplayNames?.ToList() ?? new List<string> { "Input" };
			return new List<string>();
		}

		private string? GetOutputAnchorName(IBindableArgumentEffect effect)
		{
			if (effect is IBindableArgumentEffectValueProvider vp) return vp.OutputAnchorName;
			if (effect is IBindableArgumentEffectOneToOneValueProcesser oneToOne) return oneToOne.OutputAnchorName;
			if (effect is IBindableArgumentEffectManyToOneValueProcesser manyToOne) return manyToOne.OutputAnchorName;
			return null;
		}

		private IEnumerable<ConnectionLine> GetConnections()
		{
			foreach (var node in nodeById.Values)
			{
				var effect = node.Effect;
				if (node.InputAnchors.Count == 0) continue;

				for (int i = 0; i < node.InputAnchors.Count; i++)
				{
					string? providerId = GetProviderId(effect, i);
					if (string.IsNullOrWhiteSpace(providerId)) continue;
					if (!nodeById.TryGetValue(providerId, out var providerNode)) continue;
					if (providerNode.OutputAnchor == null) continue;

					var start = GetAnchorCenter(providerNode.OutputAnchor);
					var end = GetAnchorCenter(node.InputAnchors[i].Anchor!);
					if (start == null || end == null) continue;

					yield return new ConnectionLine(start.Value, end.Value);
				}
			}
		}

		private string? GetProviderId(IBindableArgumentEffect effect, int inputIndex)
		{
			if (effect is IBindableArgumentEffectManyToOneValueProcesser mpe)
			{
				if (mpe.BindedArgumentProviderIDs == null || mpe.BindedArgumentProviderIDs.Length <= inputIndex) return null;
				return mpe.BindedArgumentProviderIDs[inputIndex];
			}
			if (effect is IBindableArgumentEffectManyInputResultGenerator mpg)
			{
				if (mpg.BindedArgumentProviderIDs == null || mpg.BindedArgumentProviderIDs.Length <= inputIndex) return null;
				return mpg.BindedArgumentProviderIDs[inputIndex];
			}
			return effect.BindedArgumentProviderID;
		}

		private Point? GetAnchorCenter(VisualElement anchor)
		{
			if (anchor.Width <= 0 || anchor.Height <= 0) return null;
			var abs = GetAbsolutePosition(anchor, graphLayer);
			return new Point(abs.X + anchor.Width / 2, abs.Y + anchor.Height / 2);
		}

		private Point GetAbsolutePosition(VisualElement element, VisualElement ancestor)
		{
			double x = element.X + element.TranslationX;
			double y = element.Y + element.TranslationY;

			VisualElement? parent = element.Parent as VisualElement;
			while (parent != null && parent != ancestor)
			{
				if (parent is ScrollView sv)
				{
					x -= sv.ScrollX;
					y -= sv.ScrollY;
				}
				x += parent.X + parent.TranslationX;
				y += parent.Y + parent.TranslationY;
				parent = parent.Parent as VisualElement;
			}

			return new Point(x, y);
		}

		private sealed record AnchorInfo(IBindableArgumentEffect Effect, int InputIndex, AnchorKind Kind)
		{
			public BoxView? Anchor { get; init; }
		}

		private enum AnchorKind
		{
			Input,
			Output
		}

		private sealed record NodeVisual(IBindableArgumentEffect Effect, Border Root, BoxView? OutputAnchor, List<AnchorInfo> InputAnchors);

		private readonly struct ConnectionLine
		{
			public ConnectionLine(Point start, Point end)
			{
				Start = start;
				End = end;
			}
			public Point Start { get; }
			public Point End { get; }
		}

		private sealed class BindingGraphDrawable : IDrawable
		{
			private readonly Func<IEnumerable<ConnectionLine>> connectionsProvider;

			public BindingGraphDrawable(Func<IEnumerable<ConnectionLine>> connectionsProvider)
			{
				this.connectionsProvider = connectionsProvider;
			}

			public void Draw(ICanvas canvas, RectF dirtyRect)
			{
				canvas.StrokeColor = Colors.DeepSkyBlue;
				canvas.StrokeSize = 2;
				canvas.Alpha = 0.8f;

				foreach (var line in connectionsProvider())
				{
					canvas.DrawLine((float)line.Start.X, (float)line.Start.Y, (float)line.End.X, (float)line.End.Y);
				}
			}
		}
	}
}
