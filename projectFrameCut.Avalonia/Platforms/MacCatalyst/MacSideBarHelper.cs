using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Maui.Platform;
#if MACCATALYST
using UIKit;
using Foundation;
using CoreGraphics;

namespace projectFrameCut.Platforms.MacCatalyst
{
    public class MacSideBarHelper
    {
        public static void MakeWindow(Microsoft.Maui.Controls.Window mauiWindow)
        {
            var window = mauiWindow.Handler?.PlatformView as UIWindow;
            if (window == null) return;

            // Configure TitleBar to be transparent/hidden so sidebar extends to top
            if (window.WindowScene?.Titlebar != null)
            {
                window.WindowScene.Titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
                window.WindowScene.Titlebar.Toolbar = null;
            }

            // Check if we already replaced the root
            if (window.RootViewController is UISplitViewController) return;

            var originalRoot = window.RootViewController;
            
            var splitVC = new UISplitViewController(UISplitViewControllerStyle.DoubleColumn);
            splitVC.PrimaryBackgroundStyle = UISplitViewControllerBackgroundStyle.Sidebar;
            splitVC.PreferredDisplayMode = UISplitViewControllerDisplayMode.OneBesideSecondary;
            splitVC.PreferredSplitBehavior = UISplitViewControllerSplitBehavior.Tile;
            splitVC.PreferredPrimaryColumnWidthFraction = 0.2f;
            splitVC.MinimumPrimaryColumnWidth = 200;
            splitVC.MaximumPrimaryColumnWidth = 300;

            var sidebarVC = new SidebarViewController();
            sidebarVC.SelectionChanged += (s, route) =>
            {
                Shell.Current.GoToAsync(route);
            };

            splitVC.SetViewController(sidebarVC, UISplitViewControllerColumn.Primary);
            splitVC.SetViewController(originalRoot, UISplitViewControllerColumn.Secondary);

            window.RootViewController = splitVC;
        }

        public static void SetSidebarHidden(bool hidden)
        {
            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as UIWindow;
            if (window?.RootViewController is UISplitViewController splitVC)
            {
                UIView.Animate(0.3, () =>
                {
                    splitVC.PreferredDisplayMode = hidden
                        ? UISplitViewControllerDisplayMode.SecondaryOnly
                        : UISplitViewControllerDisplayMode.OneBesideSecondary;
                });
            }
        }
    }

    public class SidebarViewController : UIViewController, IUICollectionViewDelegate
    {
        UICollectionView collectionView;
        UICollectionViewDiffableDataSource<NSString, SidebarItem> dataSource;

        public event EventHandler<string> SelectionChanged;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            ConfigureHierarchy();
            ConfigureDataSource();
            ApplyInitialSnapshot();
            
            // Select first item by default
            var firstIndexPath = NSIndexPath.FromItemSection(0, 0);
            collectionView.SelectItem(firstIndexPath, false, UICollectionViewScrollPosition.None);
        }

        private void ConfigureHierarchy()
        {
            var config = new UICollectionLayoutListConfiguration(UICollectionLayoutListAppearance.Sidebar);
            config.ShowsSeparators = false;
            config.HeaderMode = UICollectionLayoutListHeaderMode.None;
            var layout = UICollectionViewCompositionalLayout.GetLayout(config);
            
            collectionView = new UICollectionView(View.Bounds, layout);
            collectionView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
            collectionView.Delegate = this;
            
            // Important for "Liquid Glass" effect: make backgrounds transparent
            View.BackgroundColor = UIColor.Clear;
            collectionView.BackgroundColor = UIColor.Clear;
            
            View.AddSubview(collectionView);
        }

        private void ConfigureDataSource()
        {
            var cellRegistration = UICollectionViewCellRegistration.GetRegistration(typeof(UICollectionViewListCell), (cell, indexPath, item) =>
            {
                var sidebarItem = item as SidebarItem;
                var listCell = cell as UICollectionViewListCell;
                
                var contentConfig = listCell.DefaultContentConfiguration;
                contentConfig.Text = sidebarItem.Title;
                
                var imageConfig = UIImageSymbolConfiguration.Create(UIImageSymbolScale.Large);
                contentConfig.Image = UIImage.GetSystemImage(sidebarItem.Icon, imageConfig);
                
                listCell.ContentConfiguration = contentConfig;
            });

            dataSource = new UICollectionViewDiffableDataSource<NSString, SidebarItem>(collectionView, (collectionView, indexPath, item) =>
            {
                return collectionView.DequeueConfiguredReusableCell(cellRegistration, indexPath, item);
            });
        }

        private void ApplyInitialSnapshot()
        {
            var snapshot = new NSDiffableDataSourceSnapshot<NSString, SidebarItem>();
            snapshot.AppendSections(new NSString[] { new NSString("Main") });
            
            var items = new SidebarItem[]
            {
                new SidebarItem(Localized.AppShell_ProjectsTab, "folder", "//home"),
                new SidebarItem(Localized.AppShell_AssetsTab, "photo.on.rectangle", "//assets"),
                new SidebarItem(Localized.AppShell_DebugTab, "wrench.and.screwdriver", "//debug"),
                new SidebarItem(Localized._Options, "gearshape", "//options")
            };
            
            snapshot.AppendItems(items);
            dataSource.ApplySnapshot(snapshot, false);
        }

        [Export("collectionView:didSelectItemAtIndexPath:")]
        public void ItemSelected(UICollectionView collectionView, NSIndexPath indexPath)
        {
            var item = dataSource.GetItemIdentifier(indexPath);
            if (item != null)
            {
                SelectionChanged?.Invoke(this, item.Route);
            }
        }
    }

    public class SidebarItem : NSObject
    {
        public string Title { get; }
        public string Icon { get; }
        public string Route { get; }

        public SidebarItem(string title, string icon, string route)
        {
            Title = title;
            Icon = icon;
            Route = route;
        }
    }
}
#endif
