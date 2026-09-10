# projectFrameCut.ExternalRpcSourceClient

Connects to a persistent external RPC authorization pipe and registers a synthetic video source.
The client must stay running while the project reads frames from the source.

```powershell
dotnet run --project tools/projectFrameCut.ExternalRpcSourceClient -- `
  --pipe projectFrameCut-rpc-... `
  --token ... `
  --client-id 00000000-0000-0000-0000-000000000000 `
  --width 640 --height 360 --fps 30 --alpha --hdr
```

The `PipeName`, `Token`, and `ClientId` are the decrypted result of a persistent
authorization request. The source appears in the project add-clip UI after registration.
