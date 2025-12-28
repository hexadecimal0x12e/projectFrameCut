using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using projectFrameCut.Asset;
using System.IO;
using System.Threading.Tasks;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.ViewModels
{
    public class AssetViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<AssetItem> Assets { get; } = new();

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
            Assets.Clear();
            if (AssetDatabase.Assets != null)
            {
                foreach (var asset in AssetDatabase.Assets.Values)
                {
                    Assets.Add(asset);
                }
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
