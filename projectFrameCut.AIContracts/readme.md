# projectFrameCut AI provider contracts

This assembly contains the UI-free contracts used by built-in and plugin AI providers.

Global plugins opt in by implementing `IAIProviderPlugin` in addition to `IPluginBase` and returning provider factories keyed by the provider descriptor ID. A provider may implement any combination of `IAIChatProvider`, `IAIImageProvider`, `IAIVideoProvider`, and `IAIExtensionProvider`.

Provider configuration is described with `AIConfigurationFieldDescriptor`. Mark credentials as `Secret`; the host stores them in protected storage and supplies only the selected profile through `AIProviderContext.GetSecretAsync`. Providers must not persist or log returned secrets.

Chat tools are declarations only. Providers return tool-call events and the host invokes the tool. Media should be represented with `AIMediaReference`; isolated providers automatically transfer inline and file-backed media through the isolation payload channel.
