using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Asset;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace projectFrameCut.ViewModels
{
    public class AssetViewModel : INotifyPropertyChanged
    {
        private List<AssetItem> _allAssets = new();
        public ObservableCollection<AssetItem> Assets { get; } = new();

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged();
                    FilterAssets();
                }
            }
        }

        private int _orderOption = 0; // 0: By add date, 1: By name
        public int OrderOption
        {
            get => _orderOption;
            set
            {
                if (_orderOption != value)
                {
                    _orderOption = value;
                    OnPropertyChanged();
                    FilterAssets();
                }
            }
        }

        private AssetItem? _selectedAsset;
        public AssetItem? SelectedAsset
        {
            get => _selectedAsset;
            set
            {
                if (_selectedAsset != value)
                {
                    _selectedAsset = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand SelectAssetCommand { get; }
        public ICommand RefreshCommand { get; }

        public AssetViewModel()
        {
            SelectAssetCommand = new Command<AssetItem>(OnSelectAsset);
            RefreshCommand = new Command(LoadAssets);
            LoadAssets();
        }

        public void LoadAssets()
        {
            _allAssets.Clear();
            Assets.Clear();
            if (AssetDatabase.Assets != null)
            {
                foreach (var currentAsset in AssetDatabase.Assets.Values)
                {
                    if (FixAsset(currentAsset, false, out var modified)) AssetDatabase.Assets.AddOrUpdate(modified.AssetId, (_) => modified, (_, _) => modified);
                    _allAssets.Add(modified);
                }
            }
            FilterAssets();
        }

        public static bool FixAsset(AssetItem input, bool force, out AssetItem result)
        {
            result = input;
            if (input.AssetType == AssetType.Video)
            {
                if (force || input.Duration is null || input.Duration <= 0 || input.Width * input.Height <= 0 || input.SecondPerFrame == 0 || input.BitPerPixel <= 0)
                {
                    if (Guid.TryParse(input.AssetId ?? "", out var id) && !string.IsNullOrWhiteSpace(input.Path) && File.Exists(input.Path))
                    {
                        try
                        {
                            var vid = PluginManager.CreateVideoSource(input.Path, 8);
                            var bpp = FFmpegHelper.DetectVideoBitDepth(input.Path);
                            result = input with { Duration = vid.TotalFrames, Width = vid.Width, Height = vid.Height, SecondPerFrame = (float)(1 / vid.Fps), BitPerPixel = bpp };
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Auto fix asset info for {input}");
                        }
                    }
                }
            }
            else if (input.AssetType == AssetType.Audio)
            {
                if (force || input.Duration is null || input.Duration <= 0)
                {
                    if (Guid.TryParse(input.AssetId ?? "", out var id) && !string.IsNullOrWhiteSpace(input.Path) && File.Exists(input.Path))
                    {
                        try
                        {
                            var aud = PluginManager.CreateAudioSource(input.Path);
                            result = input with { Duration = aud.Duration, SecondPerFrame = 1 };
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Auto fix asset info for {input}");
                        }
                    }
                }
            }
            return false;
        }

        public async void FilterAssets()
        {
            Assets.Clear();
            var query = _searchText?.Trim() ?? string.Empty;

            List<AssetItem> filtered = new();
            if (string.IsNullOrEmpty(query))
            {
                filtered = _allAssets.ToList();
            }
            else
            {
                var inputPron = (await TextServices.GetHowToPronuce(SearchText, default)).ToLower();
                var inputPronInLocate = ((await TextServices.GetHowToPronuce(SearchText, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
                var searchLower = SearchText.ToLower();


                foreach (var asset in _allAssets)
                {
                    var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                    var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                    if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                    {
                        filtered.Add(asset);
                    }
                }
            }

            // 应用排序
            if (OrderOption == 0)
            {
                // By add date - 按创建时间降序排序
                filtered = filtered.OrderByDescending(a => a.CreatedAt).ToList();
            }
            else if (OrderOption == 1)
            {
                filtered = (await filtered.OrderByPronounceAsync(c => c.Name))
                                 .GroupBy(c => TextHelper.DetectTextLanguage(c.Name))
                                 .OrderByDescending(g => g.Count())
                                 .SelectMany(c => c)
                                 .ToList();
            }

            foreach (var asset in filtered)
            {
                Assets.Add(asset);
            }
        }



        public void DeleteAsset(AssetItem asset)
        {
            if (asset == null || string.IsNullOrEmpty(asset.AssetId)) return;
            if (AssetDatabase.Remove(asset.AssetId))
            {
                LoadAssets();
            }
        }

        public void RenameAsset(AssetItem asset, string newName)
        {
            if (asset == null || string.IsNullOrEmpty(asset.AssetId) || string.IsNullOrWhiteSpace(newName)) return;
            if (AssetDatabase.Rename(asset.AssetId, newName))
            {
                LoadAssets();
            }
        }



        private void OnSelectAsset(AssetItem asset)
        {
            SelectedAsset = asset;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
