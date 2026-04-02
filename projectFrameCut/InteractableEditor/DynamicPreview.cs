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
	private readonly ContentView _outputHost;
	private readonly Label _placeholder;
	private IClip[]? _clips;
	private string? _preferredClipId;
	private uint _currentFrame;
	private long _renderVersion;

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

	public async Task UpdateDraft(DraftStructureJSON json)
	{
		ArgumentNullException.ThrowIfNull(json);

		DisposeClips();
		_clips = await Task.Run(() => DraftImportAndExportHelper.JSONToIClips(json, true, 8));
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
		var prepared = await PrepareRequestsAsync(requests, targetWidth, targetHeight, targetWidth, targetHeight, frameIndex).ConfigureAwait(false);

		if (Dispatcher.IsDispatchRequired)
		{
			return await Dispatcher.DispatchAsync(() => ApplyPreparedRequests(prepared, renderVersion));
		}

		return ApplyPreparedRequests(prepared, renderVersion);
	}

	public void Dispose()
	{
		Interlocked.Increment(ref _renderVersion);
		DisposeClips();
		_outputHost.Content = null;
	}

	private static async Task<IReadOnlyList<PreparedPreview>> PrepareRequestsAsync(IReadOnlyList<PreviewRequest> requests, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex)
	{
		if (requests.Count == 0)
		{
			return [];
		}

		var preparationTasks = requests
			.Reverse()
			.Select(request => Task.Run(() => GenerateClipPreviewPrepared(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex)))
			.ToArray();

		return await Task.WhenAll(preparationTasks).ConfigureAwait(false);
	}

	private bool ApplyPreparedRequests(IReadOnlyList<PreparedPreview> prepared, long renderVersion)
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

			generatedView.HorizontalOptions = LayoutOptions.Fill;
			generatedView.VerticalOptions = LayoutOptions.Fill;
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

		_outputHost.Content = finalView;
		_outputHost.IsVisible = finalView is not null;
		_placeholder.Text = lastErrorMessage ?? string.Empty;
		_placeholder.IsVisible = finalView is null && !string.IsNullOrWhiteSpace(lastErrorMessage);
		IsVisible = finalView is not null || _placeholder.IsVisible;

		return finalView is not null;
	}

	private static PreparedPreview GenerateClipPreviewPrepared(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex)
	{
		var generatedView = GenerateClipPreview(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, out var message);
		return new PreparedPreview(generatedView, message, request.Clip);
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

	private static View? GenerateClipPreview(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, out string? message)
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

		if (clip.EffectsInstances?.Any() != true)
		{
			return generatedView;
		}


		foreach (var effect in clip.EffectsInstances
			.Where(e => e.Enabled)
			.OrderBy(e => e.Index))
		{
			generatedView = ApplyEffectPreview(generatedView, effect, canvasWidth, canvasHeight, frameIndex);
		}

		return generatedView;
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
			Aspect = Aspect.AspectFit,
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

	private sealed record PreparedPreview(Microsoft.Maui.IView? View, string? ErrorMessage, IClip? Source);
}