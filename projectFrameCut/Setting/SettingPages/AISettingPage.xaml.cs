using projectFrameCut.AIContracts;
using projectFrameCut.AIAssistance;
using projectFrameCut.Shared;
using static projectFrameCut.Setting.SettingManager.SettingsManager;
namespace projectFrameCut.Setting.SettingPages;

public sealed class AISettingPage : ContentPage
{
    private readonly AIProviderService service;
    private readonly Dictionary<(Guid ProfileId, string FieldId), string> pendingSecrets = [];
    private Guid? selectedProfileId;
    private bool rebuilding;

    public AISettingPage()
    {
        service = AIProviderService.Current ?? throw new InvalidOperationException("The AI provider service is not initialized.");
        selectedProfileId = service.Profiles.Document.Profiles.FirstOrDefault()?.Id;
        Title = SettingLocalizedResources.AISetting_Title;
        _ = RebuildAsync();
    }

    private async Task RebuildAsync()
    {
        if (rebuilding) return;
        rebuilding = true;
        try
        {
            var root = new VerticalStackLayout { Padding = 12, Spacing = 10 };
            var profiles = service.Profiles.Document.Profiles;
            root.Add(new Label { Text = SettingLocalizedResources.AISetting_Title, FontSize = 22, FontAttributes = FontAttributes.Bold });
            if (service.Profiles.LoadError is not null)
                root.Add(new Label { Text = "AI profile migration or legacy cleanup is incomplete. Existing configuration files were retained when possible.", TextColor = Colors.OrangeRed });

            var profilePicker = new Picker
            {
                Title = "Connection profile",
                ItemsSource = profiles.Select(x => x.Name).ToArray(),
                SelectedIndex = Math.Max(0, profiles.FindIndex(x => x.Id == selectedProfileId)),
            };
            profilePicker.SelectedIndexChanged += (_, _) =>
            {
                if (profilePicker.SelectedIndex >= 0 && profilePicker.SelectedIndex < profiles.Count)
                {
                    selectedProfileId = profiles[profilePicker.SelectedIndex].Id;
                    _ = RebuildAsync();
                }
            };
            root.Add(profilePicker);

            var profileButtons = new HorizontalStackLayout { Spacing = 8 };
            var add = new Button { Text = "Add profile" };
            add.Clicked += (_, _) =>
            {
                var provider = service.Registry.Providers.Values.FirstOrDefault();
                if (provider is null) return;
                var profile = new AIConnectionProfile
                {
                    Name = provider.Provider.Descriptor.DisplayName,
                    ProviderKey = provider.Key,
                    Configuration = provider.Provider.Descriptor.ConfigurationFields
                        .Where(x => x.Type != AIConfigurationFieldType.Secret && x.DefaultValue is not null)
                        .ToDictionary(x => x.Id, x => x.DefaultValue!, StringComparer.OrdinalIgnoreCase),
                    SecretFieldIds = provider.Provider.Descriptor.ConfigurationFields
                        .Where(x => x.Type == AIConfigurationFieldType.Secret).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                };
                profiles.Add(profile);
                selectedProfileId = profile.Id;
                _ = RebuildAsync();
            };
            profileButtons.Add(add);

            if (selectedProfileId is Guid profileId && profiles.FirstOrDefault(x => x.Id == profileId) is { } profile)
            {
                var delete = new Button { Text = "Delete profile" };
                delete.Clicked += async (_, _) => await DeleteProfileAsync(profile);
                profileButtons.Add(delete);
            }
            root.Add(profileButtons);

            if (selectedProfileId is Guid id && profiles.FirstOrDefault(x => x.Id == id) is { } selected)
                await AddProfileEditorAsync(root, selected);

            root.Add(new BoxView { HeightRequest = 1, Color = Colors.Gray, Margin = new(0, 8) });
            root.Add(new Label { Text = "Default models", FontSize = 18, FontAttributes = FontAttributes.Bold });
            await AddDefaultEditorAsync(root, AISelectionKeys.Chat, "Chat", AICapability.Chat, AIModality.Text);
            await AddDefaultEditorAsync(root, AISelectionKeys.Image, "Image generation", AICapability.ImageGeneration, AIModality.Text);
            await AddDefaultEditorAsync(root, AISelectionKeys.TextToVideo, "Text to video", AICapability.VideoGeneration, AIModality.Text);
            await AddDefaultEditorAsync(root, AISelectionKeys.FrameToVideo, "Frame to video", AICapability.VideoGeneration, AIModality.Text | AIModality.Image);

            var save = new Button { Text = Localized._Save };
            save.Clicked += async (_, _) =>
            {
                await FlushSecretsAsync();
                await service.Profiles.SaveAsync();
                save.Text = SettingLocalizedResources.Advanced_Success;
            };
            root.Add(save);
            Content = new ScrollView { Content = root };
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Build AI provider settings", typeof(AISettingPage));
            Content = new Label { Text = ex.Message, TextColor = Colors.Red, Margin = 12 };
        }
        finally
        {
            rebuilding = false;
        }
    }

    private async Task AddProfileEditorAsync(VerticalStackLayout root, AIConnectionProfile profile)
    {
        var name = new Entry { Text = profile.Name, Placeholder = "Profile name" };
        name.TextChanged += (_, e) => ReplaceProfile(profile with { Name = e.NewTextValue ?? string.Empty });
        root.Add(name);

        var registrations = service.Registry.Providers.Values.OrderBy(x => x.Provider.Descriptor.DisplayName).ToArray();
        var providerPicker = new Picker
        {
            Title = SettingLocalizedResources.AISetting_Provider,
            ItemsSource = registrations.Select(x => x.Provider.Descriptor.DisplayName).ToArray(),
            SelectedIndex = Array.FindIndex(registrations, x => string.Equals(x.Key, profile.ProviderKey, StringComparison.OrdinalIgnoreCase)),
        };
        providerPicker.SelectedIndexChanged += (_, _) =>
        {
            if (providerPicker.SelectedIndex < 0) return;
            var registration = registrations[providerPicker.SelectedIndex];
            ReplaceProfile(profile with
            {
                ProviderKey = registration.Key,
                Configuration = registration.Provider.Descriptor.ConfigurationFields
                    .Where(x => x.Type != AIConfigurationFieldType.Secret && x.DefaultValue is not null)
                    .ToDictionary(x => x.Id, x => x.DefaultValue!, StringComparer.OrdinalIgnoreCase),
                SecretFieldIds = registration.Provider.Descriptor.ConfigurationFields
                    .Where(x => x.Type == AIConfigurationFieldType.Secret).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            });
            _ = RebuildAsync();
        };
        root.Add(providerPicker);

        if (!service.Registry.TryGet(profile.ProviderKey, out var registration))
        {
            root.Add(new Label { Text = $"Provider '{profile.ProviderKey}' is not installed.", TextColor = Colors.OrangeRed });
            return;
        }

        foreach (var field in registration.Provider.Descriptor.ConfigurationFields)
        {
            if (field.Type == AIConfigurationFieldType.Boolean)
            {
                var toggle = new Switch { IsToggled = bool.TryParse(profile.Configuration.GetValueOrDefault(field.Id), out var value) && value };
                toggle.Toggled += (_, e) => profile.Configuration[field.Id] = e.Value.ToString();
                root.Add(new HorizontalStackLayout { Children = { new Label { Text = field.DisplayName, VerticalOptions = LayoutOptions.Center }, toggle } });
                continue;
            }

            if (field.Type == AIConfigurationFieldType.Selection)
            {
                var picker = new Picker { Title = field.DisplayName, ItemsSource = field.Choices.ToArray(), SelectedItem = profile.Configuration.GetValueOrDefault(field.Id) ?? field.DefaultValue };
                picker.SelectedIndexChanged += (_, _) => profile.Configuration[field.Id] = picker.SelectedItem?.ToString() ?? string.Empty;
                root.Add(picker);
                continue;
            }

            var entry = new Entry
            {
                Placeholder = field.Type == AIConfigurationFieldType.Secret ? $"{field.DisplayName} (leave blank to keep)" : field.Placeholder ?? field.DisplayName,
                Text = field.Type == AIConfigurationFieldType.Secret ? string.Empty : profile.Configuration.GetValueOrDefault(field.Id) ?? field.DefaultValue,
                IsPassword = field.Type == AIConfigurationFieldType.Secret,
                Keyboard = field.Type == AIConfigurationFieldType.Uri ? Keyboard.Url : field.Type == AIConfigurationFieldType.Number ? Keyboard.Numeric : Keyboard.Default,
            };
            entry.TextChanged += (_, e) =>
            {
                if (field.Type == AIConfigurationFieldType.Secret)
                    pendingSecrets[(profile.Id, field.Id)] = e.NewTextValue ?? string.Empty;
                else
                    profile.Configuration[field.Id] = e.NewTextValue ?? string.Empty;
            };
            root.Add(entry);
        }

        var test = new Button { Text = SettingLocalizedResources.AISetting_Test };
        test.Clicked += async (_, _) =>
        {
            await FlushSecretsAsync();
            var validation = await service.ValidateAsync(profile.Id);
            if (validation.IsValid)
            {
                var capability = registration.Provider.Descriptor.Capabilities.HasFlag(AICapability.Chat) ? AICapability.Chat
                    : registration.Provider.Descriptor.Capabilities.HasFlag(AICapability.ImageGeneration) ? AICapability.ImageGeneration
                    : AICapability.VideoGeneration;
                _ = await service.GetModelsAsync(profile.Id, new(capability));
            }
            await DisplayAlertAsync(validation.IsValid ? Localized._Info : Localized._Error,
                validation.IsValid ? "Provider connection succeeded." : validation.Error?.Message ?? string.Join(Environment.NewLine, validation.FieldErrors.Values),
                Localized._OK);
        };
        root.Add(test);
        await Task.CompletedTask;
    }

    private async Task AddDefaultEditorAsync(VerticalStackLayout root, string key, string title, AICapability capability, AIModality input)
    {
        var profiles = service.Profiles.Document.Profiles
            .Where(x => service.Registry.TryGet(x.ProviderKey, out var p) && p.Provider.Descriptor.Capabilities.HasFlag(capability))
            .ToArray();
        if (profiles.Length == 0)
        {
            root.Add(new Label { Text = $"{title}: no provider available", TextColor = Colors.Gray });
            return;
        }

        service.Profiles.Document.Defaults.TryGetValue(key, out var selection);
        int selectedIndex = Array.FindIndex(profiles, x => x.Id == selection?.ProfileId);
        if (selectedIndex < 0) selectedIndex = 0;
        var profilePicker = new Picker { Title = title + " provider", ItemsSource = profiles.Select(x => x.Name).ToArray(), SelectedIndex = selectedIndex };
        root.Add(profilePicker);

        IReadOnlyList<AIModelDescriptor> models = service.Registry.Get(profiles[selectedIndex].ProviderKey).Provider.Descriptor.RecommendedModels
            .Where(x => x.Capability.HasFlag(capability)).ToArray();
        var current = service.Profiles.Document.Defaults.GetValueOrDefault(key);
        var ids = models.Select(x => x.Id)
            .Concat(current?.ProfileId == profiles[selectedIndex].Id && !string.IsNullOrWhiteSpace(current.ModelId) ? [current.ModelId] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        View modelControl;
        if (ids.Length > 0)
        {
            var model = new Picker { Title = title + " model", ItemsSource = ids, SelectedItem = current?.ProfileId == profiles[selectedIndex].Id ? current.ModelId : ids.FirstOrDefault() };
            model.SelectedIndexChanged += (_, _) =>
            {
                if (model.SelectedItem is string modelId) service.Profiles.Document.Defaults[key] = new() { ProfileId = profiles[profilePicker.SelectedIndex].Id, ModelId = modelId };
            };
            modelControl = model;
            if (model.SelectedItem is string initialModel) service.Profiles.Document.Defaults[key] = new() { ProfileId = profiles[selectedIndex].Id, ModelId = initialModel };
        }
        else
        {
            var model = new Entry { Placeholder = title + " model", Text = current?.ProfileId == profiles[selectedIndex].Id ? current.ModelId : string.Empty };
            model.TextChanged += (_, e) => service.Profiles.Document.Defaults[key] = new() { ProfileId = profiles[profilePicker.SelectedIndex].Id, ModelId = e.NewTextValue ?? string.Empty };
            modelControl = model;
        }
        root.Add(modelControl);
        var refresh = new Button { Text = SettingLocalizedResources._Refresh };
        refresh.Clicked += async (_, _) =>
        {
            try
            {
                var discovered = await service.GetModelsAsync(profiles[profilePicker.SelectedIndex].Id, new(capability, input));
                if (modelControl is Picker picker) picker.ItemsSource = discovered.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                else if (discovered.FirstOrDefault() is { } first) ((Entry)modelControl).Text = first.Id;
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(Localized._Error, ex.Message, Localized._OK);
            }
        };
        root.Add(refresh);
        profilePicker.SelectedIndexChanged += (_, _) =>
        {
            if (profilePicker.SelectedIndex < 0) return;
            service.Profiles.Document.Defaults.Remove(key);
            _ = RebuildAsync();
        };
    }

    private void ReplaceProfile(AIConnectionProfile replacement)
    {
        int index = service.Profiles.Document.Profiles.FindIndex(x => x.Id == replacement.Id);
        if (index >= 0) service.Profiles.Document.Profiles[index] = replacement;
    }

    private async Task FlushSecretsAsync()
    {
        foreach (var item in pendingSecrets.ToArray())
        {
            if (!string.IsNullOrWhiteSpace(item.Value)) await service.Profiles.SetSecretAsync(item.Key.ProfileId, item.Key.FieldId, item.Value);
            pendingSecrets.Remove(item.Key);
        }
    }

    private async Task DeleteProfileAsync(AIConnectionProfile profile)
    {
        if (!await DisplayAlertAsync(Localized._Info, $"Delete AI profile '{profile.Name}'?", Localized._OK, Localized._Cancel)) return;
        foreach (string fieldId in profile.SecretFieldIds)
            _ = service.Profiles.RemoveSecret(profile.Id, fieldId);
        service.Profiles.Document.Profiles.RemoveAll(x => x.Id == profile.Id);
        foreach (var key in service.Profiles.Document.Defaults.Where(x => x.Value.ProfileId == profile.Id).Select(x => x.Key).ToArray())
            service.Profiles.Document.Defaults.Remove(key);
        await service.Profiles.SaveAsync();
        selectedProfileId = service.Profiles.Document.Profiles.FirstOrDefault()?.Id;
        await RebuildAsync();
    }
}
