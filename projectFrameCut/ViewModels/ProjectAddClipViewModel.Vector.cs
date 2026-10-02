using System.Collections.ObjectModel;
using System.Windows.Input;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel
{
    private readonly List<VectorComponentItemViewModel> vectorComponents = new();
    public ObservableCollection<VectorComponentItemViewModel> FilteredVectorComponents { get; } = new();

    public void LoadVectorComponents()
    {
        vectorComponents.Clear();
        foreach (var factory in VectorComponentHandlerServices.GetAvailableHandlers().Values)
        {
            try
            {
                vectorComponents.Add(new VectorComponentItemViewModel(this, factory()));
            }
            catch (Exception ex)
            {
                Log(ex, "Load vector component card", this);
            }
        }
        FilterVectorComponents();
        Log($"Loaded {vectorComponents.Count} vector component cards.");
    }

    private void FilterVectorComponents()
    {
        FilteredVectorComponents.Clear();
        foreach (var item in vectorComponents)
        {
            if (string.IsNullOrWhiteSpace(SearchText) ||
                item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                item.TypeName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                FilteredVectorComponents.Add(item);
        }
    }

    public void AddVectorComponent(IVectorComponent component)
    {
        uint duration = Math.Max(1u, SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300));
        BeginTimelineClipPlacement((track, start) => VectorClipServices.AddClip(_draftPage, component, track, start, duration), name: component.Name);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class VectorComponentItemViewModel
{
    public string Name { get; }
    public string TypeName { get; }
    public string Icon { get; }
    public string Description { get; }
    public ICommand AddCommand { get; }

    public VectorComponentItemViewModel(ProjectAddClipViewModel parent, IVectorComponentHandler handler)
    {
        var display = handler.GetDisplayItem(Localized._LocaleId_);
        Name = string.IsNullOrWhiteSpace(display.DisplayName) ? handler.DisplayName : display.DisplayName;
        TypeName = handler.TypeName;
        Icon = string.IsNullOrWhiteSpace(display.Icon) ? handler.Icon : display.Icon;
        Description = display.Description;
        AddCommand = new Command(() =>
        {
            try
            {
                var component = handler.Create();
                component.Name = Name;
                parent.AddVectorComponent(component);
                Log($"Selected vector component {handler.FromPlugin}/{TypeName} for placement.");
            }
            catch (Exception ex)
            {
                Log(ex, "Create vector component", parent);
                parent._draftPage.SetStateFail(ex.Message);
            }
        });
    }
}
