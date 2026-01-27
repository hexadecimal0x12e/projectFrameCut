using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
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
                foreach (var asset in AssetDatabase.Assets.Values)
                {
                    _allAssets.Add(asset);
                }
            }
            FilterAssets();
        }

        public async void FilterAssets()
        {
            Assets.Clear();
            var query = _searchText?.Trim() ?? string.Empty;

            List<AssetItem> filtered = new();
            if (string.IsNullOrEmpty(query))
            {
                filtered = _allAssets;
            }
            else
            {
                var inputPron = (await TextHelper.GetHowToPronuce(SearchText, default)).ToLower();
                var inputPronInLocate = ((await TextHelper.GetHowToPronuce(SearchText, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
                var searchLower = SearchText.ToLower();


                foreach (var asset in _allAssets)
                {
                    var assetPron = (await TextHelper.GetHowToPronuce(asset.Name, default)).ToLower();
                    var assetPronInLocate = (await TextHelper.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                    if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                    {
                        filtered.Add(asset);
                    }
                }


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
