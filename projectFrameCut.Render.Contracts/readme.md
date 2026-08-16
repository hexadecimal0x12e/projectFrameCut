# projectFrameCut Render RPC contracts

This assembly is the serialization boundary between the editor and the render backend.
It contains protobuf-net DTOs, the request/response envelope, client/service interfaces,
and the in-process direct transport. It intentionally has no dependency on FFmpeg,
MAUI, `projectFrameCut.Render`, `IClip`, or `IPicture`.

Preview artifacts are represented by project-relative paths. Timeline previews remain
under `thumbs/`; clip-local DynamicPreview artifacts remain under
`thumbs/perClip/<clip-id>/timeline/` contains timeline frame thumbnails;
`thumbs/perClip/<clip-id>/dynamic/` contains DynamicPreview artifacts. The UI owns
DynamicPreview layout and composition.

Protocol evolution rules:

- Never reuse an existing `ProtoMember` number.
- Add fields as optional/defaultable values.
- Increment `RenderProtocol.CurrentVersion` for incompatible envelope semantics.
- Keep direct transport serialization enabled so it behaves like a future network transport.
