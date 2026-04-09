using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using IPicture = projectFrameCut.Shared.IPicture;

namespace projectFrameCut.InteractableEditor;

public sealed class DynamicPreview : ContentView, IDisposable
{
	public sealed record PreparedPreview(string ClipId, View? View, string? ErrorMessage, IClip? Source);

	private readonly ContentView _outputHost;
	private readonly Label _placeholder;
	private IClip[]? _clips;
	private string? _preferredClipId;
	private uint _currentFrame;
	private long _renderVersion;
	private int _viewportWidth;
	private int _viewportHeight;

	public DynamicPreview()
	{
		_placeholder = new Label
		{
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.Center,
			VerticalTextAlignment = TextAlignment.Center,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#66000000"),
			Padding = new Thickness(12, 8),
			IsVisible = false
		};

		_outputHost = new ContentView
		{
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Fill,
			IsVisible = false
		};

		Content = new Grid
		{
			Children =
			{
				_outputHost,
				_placeholder,
			}
		};

		HorizontalOptions = LayoutOptions.Fill;
		VerticalOptions = LayoutOptions.Fill;
		InputTransparent = true;
		IsVisible = false;
	}

	public ContentView OutputView => _outputHost;

	public IClip[]? Clips => _clips;

	public uint CurrentFrame => _currentFrame;

	public async Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight)
	{
		_currentFrame = frameIndex;
		var requests = ResolveRequests(frameIndex);
		var canvasWidth = ResolveCanvasSize(_outputHost.Width, Width, _viewportWidth, targetWidth);
		var canvasHeight = ResolveCanvasSize(_outputHost.Height, Height, _viewportHeight, targetHeight);
		return await PrepareRequestsAsync(requests, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout: false).ConfigureAwait(false);
	}

	public async Task UpdateDraft(DraftStructureJSON json)
	{
		ArgumentNullException.ThrowIfNull(json);

		DisposeClips();
		_clips = await Task.Run(() => DraftImportAndExportHelper.JSONToIClips(json, true, 8));
	}

	public void UpdateCanvasSize(double width, double height)
	{
		if (width > 0)
		{
			_viewportWidth = Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero));
		}

		if (height > 0)
		{
			_viewportHeight = Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero));
		}
	}

	public void SetPreferredClipId(string? clipId)
	{
		_preferredClipId = clipId;
	}

	public async Task<bool> RenderFrame(uint frameIndex, int targetWidth, int targetHeight)
	{
		_currentFrame = frameIndex;
		var renderVersion = Interlocked.Increment(ref _renderVersion);
		var requests = ResolveRequests(frameIndex);
		var viewportWidth = ResolveCanvasSize(_outputHost.Width, Width, _viewportWidth, targetWidth);
		var viewportHeight = ResolveCanvasSize(_outputHost.Height, Height, _viewportHeight, targetHeight);
		var prepared = await PrepareRequestsAsync(requests, targetWidth, targetHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout: true).ConfigureAwait(false);

		if (Dispatcher.IsDispatchRequired)
		{
			return await Dispatcher.DispatchAsync(() => ApplyPreparedRequests(prepared, renderVersion, viewportWidth, viewportHeight, targetWidth, targetHeight));
		}

		return ApplyPreparedRequests(prepared, renderVersion, viewportWidth, viewportHeight, targetWidth, targetHeight);
	}

	public void Dispose()
	{
		Interlocked.Increment(ref _renderVersion);
		DisposeClips();
		_outputHost.Content = null;
	}

	private static async Task<IReadOnlyList<PreparedPreview>> PrepareRequestsAsync(IReadOnlyList<PreviewRequest> requests, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout)
	{
		if (requests.Count == 0)
		{
			return [];
		}

		var preparationTasks = requests
			.Reverse()
			.Select(request => Task.Run(() => GenerateClipPreviewPrepared(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout)))
			.ToArray();

		return await Task.WhenAll(preparationTasks).ConfigureAwait(false);
	}

	private bool ApplyPreparedRequests(IReadOnlyList<PreparedPreview> prepared, long renderVersion, int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
	{
		if (renderVersion != Interlocked.Read(ref _renderVersion))
		{
			return false;
		}

		if (prepared.Count == 0)
		{
			_outputHost.Content = null;
			_outputHost.IsVisible = false;
			_placeholder.Text = string.Empty;
			_placeholder.IsVisible = false;
			IsVisible = false;
			return false;
		}

		Microsoft.Maui.Controls.Grid composite = new()
		{
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Fill,
			InputTransparent = true
		};

		var renderedCount = 0;
		string? lastErrorMessage = null;

		foreach (var result in prepared)
		{
			if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
			{
				lastErrorMessage = result.ErrorMessage;
			}

			if (result.View is not Microsoft.Maui.Controls.View generatedView)
			{
				continue;
			}

			generatedView.ZIndex = (int)((result.Source?.LayerIndex ?? 1) * 100);
			composite.Children.Add(generatedView);
			renderedCount++;
		}

		Microsoft.Maui.Controls.View? finalView = null;
		if (renderedCount == 1)
		{
			if (composite.Children[0] is Microsoft.Maui.Controls.View singleView)
			{
				finalView = singleView;
			}
		}
		else if (renderedCount > 1)
		{
			finalView = composite as Microsoft.Maui.Controls.View;
		}

		var alignedView = BuildViewportAlignedView(finalView, viewportWidth, viewportHeight, targetWidth, targetHeight);
		_outputHost.Content = alignedView;
		_outputHost.IsVisible = alignedView is not null;
		_placeholder.Text = lastErrorMessage ?? string.Empty;
		_placeholder.IsVisible = alignedView is null && !string.IsNullOrWhiteSpace(lastErrorMessage);
		IsVisible = alignedView is not null || _placeholder.IsVisible;

		return alignedView is not null;
	}

	private static int ResolveCanvasSize(double hostSize, double selfSize, int cachedSize, int fallbackSize)
	{
		if (hostSize > 0)
		{
			return Math.Max(1, (int)Math.Round(hostSize, MidpointRounding.AwayFromZero));
		}

		if (selfSize > 0)
		{
			return Math.Max(1, (int)Math.Round(selfSize, MidpointRounding.AwayFromZero));
		}

		if (cachedSize > 0)
		{
			return cachedSize;
		}

		return Math.Max(1, fallbackSize);
	}

	private static View? BuildViewportAlignedView(View? view, int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
	{
		if (view is null)
		{
			return null;
		}

		var logicalWidth = Math.Max(1, targetWidth);
		var logicalHeight = Math.Max(1, targetHeight);
		var viewportRect = CalculateAspectFitRect(Math.Max(1, viewportWidth), Math.Max(1, viewportHeight), logicalWidth, logicalHeight);
		var scale = viewportRect.Width / logicalWidth;
		if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
		{
			scale = 1d;
		}

		var logicalCanvas = new Grid
		{
			WidthRequest = logicalWidth,
			HeightRequest = logicalHeight,
			HorizontalOptions = LayoutOptions.Start,
			VerticalOptions = LayoutOptions.Start,
			InputTransparent = true
		};
		logicalCanvas.Children.Add(view);

		return new ContentView
		{
			Content = logicalCanvas,
			WidthRequest = logicalWidth,
			HeightRequest = logicalHeight,
			HorizontalOptions = LayoutOptions.Start,
			VerticalOptions = LayoutOptions.Start,
			InputTransparent = true,
			AnchorX = 0,
			AnchorY = 0,
			Scale = scale,
			TranslationX = viewportRect.X,
			TranslationY = viewportRect.Y
		};
	}

	private static Rect CalculateAspectFitRect(int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
	{
		if (viewportWidth <= 0 || viewportHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
		{
			return new Rect(0, 0, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
		}

		double ratioViewport = (double)viewportWidth / viewportHeight;
		double ratioTarget = (double)targetWidth / targetHeight;
		double drawW;
		double drawH;
		double offX;
		double offY;

		if (ratioTarget > ratioViewport)
		{
			drawW = viewportWidth;
			drawH = drawW / ratioTarget;
			offX = 0;
			offY = (viewportHeight - drawH) / 2d;
		}
		else
		{
			drawH = viewportHeight;
			drawW = drawH * ratioTarget;
			offX = (viewportWidth - drawW) / 2d;
			offY = 0;
		}

		return new Rect(offX, offY, drawW, drawH);
	}

	private static PreparedPreview GenerateClipPreviewPrepared(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout)
	{
		var generatedView = GenerateClipPreview(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, out var message, applyClipTargetLayout);
		return new PreparedPreview(request.Clip.Id, generatedView, message, request.Clip);
	}

	private IReadOnlyList<PreviewRequest> ResolveRequests(uint frameIndex)
	{
		if (_clips is null || _clips.Length == 0)
		{
			return [];
		}

		var activeClips = GetActiveClips(frameIndex).ToList();
		if (activeClips.Count == 0)
		{
			return [];
		}

		var preferredClip = TryGetPreferredClip(frameIndex);
		if (preferredClip is not null)
		{
			var preferredIndex = activeClips.FindIndex(clip => clip.Id == preferredClip.Id);
			if (preferredIndex > 0)
			{
				activeClips.RemoveAt(preferredIndex);
				activeClips.Insert(0, preferredClip);
			}
		}

		return activeClips
			.Select(clip => new PreviewRequest(clip, ResolveProvider(clip)))
			.ToArray();
	}

	private static View? GenerateClipPreview(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, out string? message, bool applyClipTargetLayout)
	{
		message = null;
		var clip = request.Clip;
		if (clip is null)
		{
			return null;
		}

		View? generatedView = null;
		if (request.Provider is not null)
		{
			try
			{
				generatedView = request.Provider.Generate(clip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
			}
			catch (Exception ex)
			{
				message = $"Failed to generate dynamic preview: {ex.Message}";
			}
		}

		if (generatedView is null)
		{
			try
			{
				generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex);
			}
			catch (Exception ex)
			{
				message = $"Failed to render fallback frame: {ex.Message}";
				return null;
			}
		}

		if (generatedView is null)
		{
			return null;
		}

		if (clip.EffectsInstances?.Any() == true)
		{
			foreach (var effect in clip.EffectsInstances
				.Where(e => e.Enabled)
				.OrderBy(e => e.Index))
			{
				var isLegacyLayoutEffect = IsLegacyInternalLayoutEffect(effect);
				if (isLegacyLayoutEffect)
				{
					// In prepared-preview mode, InteractableEditor owns clip placement/size.
					// Applying legacy internal place/resize here causes double layout scaling.
					if (!applyClipTargetLayout)
					{
						continue;
					}

					if (HasExplicitTargetRect(clip))
					{
						continue;
					}
				}

				generatedView = ApplyEffectPreview(generatedView, effect, canvasWidth, canvasHeight, frameIndex);
			}
		}

		if (!applyClipTargetLayout)
		{
			return generatedView;
		}

		return ApplyClipTargetLayoutPreview(generatedView, clip, canvasWidth, canvasHeight);
	}

	private static View ApplyClipTargetLayoutPreview(View input, IClip clip, int canvasWidth, int canvasHeight)
	{
		if (!HasExplicitTargetRect(clip))
		{
			return ApplyImplicitClipAutoCenterPreview(input, clip, canvasHeight);
		}

		var width = clip.TargetWidth > 0 ? clip.TargetWidth : Math.Max(1, canvasWidth);
		var height = clip.TargetHeight > 0 ? clip.TargetHeight : Math.Max(1, canvasHeight);

		input.WidthRequest = Math.Max(1, width);
		input.HeightRequest = Math.Max(1, height);
		input.HorizontalOptions = LayoutOptions.Start;
		input.VerticalOptions = LayoutOptions.Start;
		input.TranslationX = clip.TargetX;
		input.TranslationY = clip.TargetY;
		return input;
	}

	private static View ApplyImplicitClipAutoCenterPreview(View input, IClip clip, int canvasHeight)
	{
		if (HasExplicitTargetRect(clip) || HasLegacyInternalPlaceResizeEffects(clip))
		{
			return input;
		}

		if (Math.Abs(input.TranslationY) > 0.01d)
		{
			return input;
		}

		var requestedHeight = input.HeightRequest;
		if (requestedHeight <= 0 || requestedHeight >= canvasHeight)
		{
			return input;
		}

		input.TranslationY += (canvasHeight - requestedHeight) / 2d;
		return input;
	}

	private static bool HasExplicitTargetRect(IClip clip)
		=> clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

	private static bool HasLegacyInternalPlaceResizeEffects(IClip clip)
	{
		if (clip.EffectsInstances?.Any() != true)
		{
			return false;
		}

		return clip.EffectsInstances.Any(IsLegacyInternalLayoutEffect);
	}

	private static bool IsLegacyInternalLayoutEffect(IEffect effect)
	{
		if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
			|| string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
		{
			return true;
		}

		if (!string.Equals(effect.FromPlugin, InternalPluginBase.InternalPluginBaseID, StringComparison.Ordinal))
		{
			return false;
		}

		if (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private IClip? TryGetPreferredClip(uint frameIndex)
	{
		if (string.IsNullOrWhiteSpace(_preferredClipId) || _clips is null)
		{
			return null;
		}

		var clip = _clips.FirstOrDefault(c => c.Id == _preferredClipId);
		if (clip is null)
		{
			return null;
		}

		return IsClipVisibleAtFrame(clip, frameIndex) ? clip : null;
	}

    [DebuggerStepThrough()]
    private IEnumerable<IClip> GetActiveClips(uint frameIndex)
	{
		return (_clips ?? [])
			.Where(c => c.ClipType != ClipMode.AudioClip && c.ClipType != ClipMode.MarkingClip)
			.Where(c => IsClipVisibleAtFrame(c, frameIndex))
			.OrderByDescending(c => c.LayerIndex)
			.ThenByDescending(c => c.SubLayerIndex);
	}

	[DebuggerStepThrough()]
	private static bool IsClipVisibleAtFrame(IClip clip, uint frameIndex)
	{
		if (clip.ExtendToWholeDraft)
		{
			return true;
		}

		try
		{
			return clip.GetRelativeFrameIndex(frameIndex) is not null;
		}
		catch (IndexOutOfRangeException)
		{
			return false;
		}
	}

    [DebuggerStepThrough()]
    private static IClipDynamicPreviewProvider? ResolveProvider(IClip clip)
	{
		if (PluginManager.LoadedPlugins.TryGetValue(clip.FromPlugin, out var ownerPlugin)
			&& ownerPlugin is IApplicationPluginBase appPlugin)
		{
			var provider = ResolveProviderFromDictionary(appPlugin.ClipDynamicPreviewProvider, clip);
			if (provider is not null)
			{
				return provider;
			}
		}

		return null;
	}

	private static IClipDynamicPreviewProvider? ResolveProviderFromDictionary(IReadOnlyDictionary<string, IClipDynamicPreviewProvider> providers, IClip clip)
	{
		if (providers.Count == 0)
		{
			return null;
		}

		if (!string.IsNullOrWhiteSpace(clip.TypeName)
			&& providers.TryGetValue(clip.TypeName, out var typedProvider)
			&& IsProviderAvailable(typedProvider, clip))
		{
			return typedProvider;
		}

		var clipModeName = clip.ClipType.ToString();
		if (providers.TryGetValue(clipModeName, out var modeProvider) && IsProviderAvailable(modeProvider, clip))
		{
			return modeProvider;
		}

		return providers.Values.FirstOrDefault(provider => IsProviderAvailable(provider, clip));
	}

	private static bool IsProviderAvailable(IClipDynamicPreviewProvider provider, IClip clip)
	{
		try
		{
			return provider.IsAvailable(clip);
		}
		catch
		{
			return false;
		}
	}

	private static View GenerateFrameFallbackView(IClip clip, int targetWidth, int targetHeight, uint frameIndex)
	{
		var frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
		return new Image
		{
			Source = frame.ToImageSource(),
			Aspect = Aspect.Fill,
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Fill,
		};
	}

	private static View ApplyEffectPreview(View input, IEffect effect, int targetWidth, int targetHeight, uint frameIndex)
	{
		var provider = ResolveEffectProvider(effect, input.GetType());
		if (provider is null)
		{
			return input;
		}

		try
		{
			return provider.Generate(effect, input, input.GetType(), targetWidth, targetHeight, frameIndex) ?? input;
		}
		catch
		{
			return input;
		}
	}

	private static IEffectDynamicPreviewProvider? ResolveEffectProvider(IEffect effect, Type typeOfInput)
	{
		if (PluginManager.LoadedPlugins.TryGetValue(effect.FromPlugin, out var ownerPlugin)
			&& ownerPlugin is IApplicationPluginBase appPlugin)
		{
			var provider = ResolveEffectProviderFromDictionary(appPlugin.EffectDynamicPreviewProvider, effect, typeOfInput);
			if (provider is not null)
			{
				return provider;
			}
		}

		return null;
	}

	private static IEffectDynamicPreviewProvider? ResolveEffectProviderFromDictionary(IReadOnlyDictionary<string, IEffectDynamicPreviewProvider> providers, IEffect effect, Type typeOfInput)
	{
		if (providers.Count == 0)
		{
			return null;
		}

		if (!string.IsNullOrWhiteSpace(effect.TypeName)
			&& providers.TryGetValue(effect.TypeName, out var typedProvider)
			&& IsEffectProviderAvailable(typedProvider, effect, typeOfInput))
		{
			return typedProvider;
		}

		return providers.Values.FirstOrDefault(provider => IsEffectProviderAvailable(provider, effect, typeOfInput));
	}

	private static bool IsEffectProviderAvailable(IEffectDynamicPreviewProvider provider, IEffect effect, Type typeOfInput)
	{
		try
		{
			return provider.IsAvailable(effect, typeOfInput);
		}
		catch
		{
			return false;
		}
	}

	private void DisposeClips()
	{
		if (_clips is null)
		{
			return;
		}

		foreach (var clip in _clips)
		{
			try
			{
				clip.Dispose();
			}
			catch
			{
			}
		}

		_clips = null;
	}

	private sealed record PreviewRequest(IClip Clip, IClipDynamicPreviewProvider? Provider);

}