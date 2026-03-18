using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.InteractableEditor;

public sealed class DynamicPreview : ContentView, IDisposable
{
	private readonly ContentView _outputHost;
	private readonly Label _placeholder;
	private IClip[]? _clips;
	private string? _preferredClipId;
	private uint _currentFrame;

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
		var request = ResolveRequest(frameIndex);

		if (Dispatcher.IsDispatchRequired)
		{
			return await Dispatcher.DispatchAsync(() => ApplyRequest(request, targetWidth, targetHeight, frameIndex));
		}

		return ApplyRequest(request, targetWidth, targetHeight, frameIndex);
	}

	public void Dispose()
	{
		DisposeClips();
		_outputHost.Content = null;
	}

	private bool ApplyRequest(PreviewRequest request, int targetWidth, int targetHeight, uint frameIndex)
	{
		View? generatedView = null;
		var message = request.Message;

		if (request.Provider is not null && request.Clip is not null)
		{
			try
			{
				generatedView = request.Provider.Generate(request.Clip, targetWidth, targetHeight, frameIndex);
			}
			catch (Exception ex)
			{
				message = $"Failed to generate dynamic preview: {ex.Message}";
			}
		}

		if (generatedView is not null)
		{
			generatedView.HorizontalOptions = LayoutOptions.Fill;
			generatedView.VerticalOptions = LayoutOptions.Fill;
		}

		_outputHost.Content = generatedView;
		_outputHost.IsVisible = generatedView is not null;
		_placeholder.Text = message ?? string.Empty;
		_placeholder.IsVisible = generatedView is null && !string.IsNullOrWhiteSpace(message);
		IsVisible = generatedView is not null || _placeholder.IsVisible;

		return generatedView is not null;
	}

	private PreviewRequest ResolveRequest(uint frameIndex)
	{
		if (_clips is null || _clips.Length == 0)
		{
			return new PreviewRequest(null, null, null);
		}

		var preferredClip = TryGetPreferredClip(frameIndex);
		if (preferredClip is not null)
		{
			var preferredProvider = ResolveProvider(preferredClip);
			if (preferredProvider is not null)
			{
				return new PreviewRequest(preferredClip, preferredProvider, null);
			}
		}

		foreach (var clip in GetActiveClips(frameIndex))
		{
			var provider = ResolveProvider(clip);
			if (provider is not null)
			{
				return new PreviewRequest(clip, provider, null);
			}
		}

		return new PreviewRequest(null, null, null);
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

	private IEnumerable<IClip> GetActiveClips(uint frameIndex)
	{
		return (_clips ?? [])
			.Where(c => c.ClipType != ClipMode.AudioClip && c.ClipType != ClipMode.MarkingClip)
			.Where(c => IsClipVisibleAtFrame(c, frameIndex))
			.OrderByDescending(c => c.LayerIndex)
			.ThenByDescending(c => c.SubLayerIndex);
	}

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

	private static IClipDynamicPreviewProvider? ResolveProvider(IClip clip)
	{
		if (PluginManager.LoadedPlugins.TryGetValue(clip.FromPlugin, out var ownerPlugin)
			&& ownerPlugin is IApplicationPluginBase appPlugin)
		{
			var provider = ResolveProviderFromDictionary(appPlugin.DynamicPreviewProvider, clip);
			if (provider is not null)
			{
				return provider;
			}
		}

		foreach (var plugin in PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>())
		{
			var provider = ResolveProviderFromDictionary(plugin.DynamicPreviewProvider, clip);
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

	private sealed record PreviewRequest(IClip? Clip, IClipDynamicPreviewProvider? Provider, string? Message);
}