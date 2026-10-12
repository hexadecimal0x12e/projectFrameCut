# projectFrameCut.ExternalRpcSourceClient

Connects to a persistent external RPC authorization pipe and registers synthetic video and stereo audio sources.
The client must stay running while the project reads frames or audio samples. Audio uses 48 kHz float32 PCM and a 440 Hz tone.

```powershell
dotnet run --project tools/projectFrameCut.ExternalRpcSourceClient -- `
  --pipe projectFrameCut-rpc-... `
  --token ... `
  --client-id 00000000-0000-0000-0000-000000000000 `
  --width 640 --height 360 --fps 30 --alpha --hdr
```

The `PipeName`, `Token`, and `ClientId` are the decrypted result of a persistent
authorization request. The source appears in the project add-clip UI after registration.
