# projectFrameCut.ExternalRpcSourceClient

Connects to a persistent external RPC authorization pipe and registers synthetic video, stereo audio and an invert mask source.
The client must stay running while the project reads frames, audio samples or processes masks. Audio uses 48 kHz float32 PCM and a 440 Hz tone.
The mask appears in the mask list, accepts an `Enabled` boolean parameter and preserves Alpha/HDR planes.

```powershell
dotnet run --project tools/projectFrameCut.ExternalRpcSourceClient -- `
  --pipe projectFrameCut-rpc-... `
  --token ... `
  --client-id 00000000-0000-0000-0000-000000000000 `
  --width 640 --height 360 --fps 30 --alpha --hdr
```

The `PipeName`, `Token`, and `ClientId` are the decrypted result of a persistent
authorization request. The source appears in the project add-clip UI after registration.
