using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;
[TestClass, DoNotParallelize]
public sealed class HwAccelPictureTests
{
    private Func<IHwAccelPictureContext>? previousFactory;
    private FakeContext device = null !;
    [TestInitialize]
    public void Initialize()
    {
        previousFactory = HwAccelPictureScope.CreateContext;
        device = new();
        HwAccelPictureScope.CreateContext = () => device;
    }

    [TestCleanup]
    public void Cleanup()
    {
        HwAccelPictureScope.CreateContext = previousFactory;
        device.Dispose();
    }

    [TestMethod]
    public void HardwarePortsRemainDistinctAndArePictureCompatible()
    {
        Assert.AreEqual(EffectArgumentFieldType.HwAccelPicture, (EffectArgumentFieldType.HwAccelPicture | EffectArgumentFieldType.Mandatory).BaseType());
        Assert.IsTrue(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.HwAccelPicture, EffectArgumentFieldType.IPicture));
        Assert.IsTrue(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.HwAccelPicture));
        Assert.IsTrue(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.HwAccelPicture, EffectArgumentFieldType.FrameAsParameterFlow));
        Assert.IsFalse(EffectFieldTypes.AreCompatible(EffectArgumentFieldType.VectorPicture, EffectArgumentFieldType.HwAccelPicture));
    }

    [TestMethod]
    public void HardwareChainUploadsOnceAndDownloadsAtItsCpuConsumer()
    {
        using var source = Picture(10);
        using var scope = new HwAccelPictureScope();
        using var entry = scope.Enter();
        using var first = EffectPictureProcessing.Render(new HardwareEffect(), source, 1, 1);
        using var second = EffectPictureProcessing.Render(new HardwareEffect(), first, 1, 1);
        Assert.AreEqual(1, device.Uploads);
        Assert.AreEqual(0, device.Downloads);
        using var output = (IPicture)EffectPictureProcessing.Compute(new TestEffect(c =>
        {
            Assert.IsInstanceOfType<IPicture<byte>>(c.Input);
            return c.Input;
        }), new() { Input = second })!;
        Assert.AreEqual((byte)12, ((IPicture<byte>)output).r[0]);
        Assert.AreEqual(1, device.Downloads);
        Assert.AreEqual((byte)10, source.r[0]);
    }

    [TestMethod]
    public void HardwarePassthroughKeepsItsOutputHandleAlive()
    {
        using var source = Picture(10);
        using var scope = new HwAccelPictureScope();
        using var entry = scope.Enter();
        using var output = EffectPictureProcessing.Render(new HardwarePassthrough(), source, 1, 1);
        using var cpu = ((IHwAccelPicture)output).Download();
        Assert.AreEqual((byte)10, ((IPicture<byte>)cpu).r[0]);
    }

    [TestMethod]
    public void GraphBranchesShareTheirUploadAndMaterializeCpuParameters()
    {
        var left = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);
        var right = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.HwAccelPicture, c => c.Input);
        var join = new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input, new EffectArgumentFieldDescriptor { Id = "Other", FieldType = EffectArgumentFieldType.IPicture });
        left.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        right.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        join.SetMainInputSource(left.Id);
        join.AnchorsBindingState["Other"] = right.Id.ToString();
        join.SetFinalOutputSource(true);
        using var graph = new DynamicEffectGraph(Providers(left, right, join), new Dictionary<Guid, IEffect[]> { [left.Id] = [new HardwareEffect()], [right.Id] = [new HardwareEffect()], [join.Id] = [new TestEffect(c =>
        {
            Assert.IsInstanceOfType<IPicture<byte>>(c.Input);
            Assert.IsInstanceOfType<IPicture<byte>>(c.Parameters["Other"]);
            return c.Input;
        })], });
        using var source = Picture(20);
        using var output = graph.Render(source, 1, 1);
        Assert.AreEqual(1, device.Uploads);
        Assert.AreEqual(2, device.Downloads);
        Assert.AreEqual((byte)21, ((IPicture<byte>)output).r[0]);
        Assert.AreEqual(0, device.Images);
        Assert.IsTrue(device.Released);
    }

    [TestMethod]
    public void RetainedSnapshotOutlivesItsOriginalHandleAndContextOwner()
    {
        using var source = Picture(30);
        var original = device.Upload(source);
        var retained = original.Retain();
        original.Dispose();
        original.Dispose();
        device.Dispose();
        Assert.IsFalse(device.Released);
        using (var cpu = retained.Download())
            Assert.AreEqual((byte)30, ((IPicture<byte>)cpu).r[0]);
        retained.Dispose();
        Assert.IsTrue(device.Released);
        Assert.AreEqual(0, device.Images);
    }

    [TestMethod]
    public void CpuMutationDoesNotChangeTheDeviceSnapshot()
    {
        using var source = Picture(40);
        using var scope = new HwAccelPictureScope();
        using var entry = scope.Enter();
        using var gpu = EffectPictureProcessing.Render(new HardwareEffect(), source, 1, 1);
        using var changed = (IPicture)EffectPictureProcessing.Compute(new TestEffect(c =>
        {
            ((IPicture<byte>)c.Input!).r[0] = 99;
            return c.Input;
        }), new() { Input = gpu })!;
        using var unchanged = ((IHwAccelPicture)gpu).Download();
        Assert.AreEqual((byte)41, ((IPicture<byte>)unchanged).r[0]);
        Assert.AreEqual((byte)99, ((IPicture<byte>)changed).r[0]);
    }

    [TestMethod]
    public void GraphReleasesDeviceIntermediatesAfterTheirLastConsumer()
    {
        var nodes = Enumerable.Range(0, 6).Select(_ => new Provider(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input)).ToArray();
        for (int i = 0; i < nodes.Length; i++)
            nodes[i].SetMainInputSource(i == 0 ? IEffectProvider.InputAnchorGUID : nodes[i - 1].Id);
        nodes[^1].SetFinalOutputSource(true);
        using var graph = new DynamicEffectGraph(Providers(nodes), nodes.ToDictionary(p => p.Id, _ => new IEffect[] { new HardwareEffect() }));
        using var source = Picture(10);
        int peak = 0;
        using var output = graph.Evaluate(source, null, 0, 1, 1, 1, 1, new(0, 0, 1, 1, false), afterNode: (_, _) => peak = Math.Max(peak, device.Images)).Picture;
        Assert.IsTrue(peak <= 3, $"Retained {peak} device images in a linear chain.");
        Assert.AreEqual(1, device.Uploads);
        Assert.AreEqual(1, device.Downloads);
        Assert.AreEqual((byte)16, ((IPicture<byte>)output).r[0]);
        Assert.AreEqual(0, device.Images);
    }

    [TestMethod]
    public void ExplicitHardwareInputsAreUploadedAfterTheTransportBoundary()
    {
        using var source = Picture(50);
        var context = new EffectExecutionContext
        {
            Input = source,
            InputTypes = new Dictionary<string, EffectArgumentFieldType>
            {
                [EffectProviderAnchorExtensions.InputKey] = EffectArgumentFieldType.HwAccelPicture
            },
        };
        var boundary = new PictureBoundary(c =>
        {
            Assert.IsInstanceOfType<IPicture<byte>>(c.Input);
            Assert.AreEqual(EffectArgumentFieldType.HwAccelPicture, c.InputTypes[EffectProviderAnchorExtensions.InputKey]);
            return c.Input;
        });
        Assert.AreSame(source, EffectPictureProcessing.Compute(boundary, context));
        Assert.AreEqual(0, device.Uploads);
        using var native = (IPicture)EffectPictureProcessing.Compute(new TestEffect(c =>
        {
            Assert.IsInstanceOfType<IHwAccelPicture>(c.Input);
            return c.Input;
        }), context)!;
        Assert.AreEqual(1, device.Uploads);
        Assert.AreEqual(0, device.Downloads);
    }

    [TestMethod]
    public void SessionReusesUploadsAcrossFramesAndPreservesHandleMetadata()
    {
        using var source = Picture(50);
        source.Tag = "first";
        using var session = new HwAccelPictureSession();
        object identity;
        using (var scope = new HwAccelPictureScope(session))
        using (scope.Enter())
        using (var gpu = EffectPictureProcessing.Upload(source))
        {
            identity = gpu.ContentIdentity;
            Assert.AreEqual("first", gpu.Tag);
        }
        using var next = (IPicture)source.Snapshot();
        next.Tag = "second";
        using (var scope = new HwAccelPictureScope(session))
        using (scope.Enter())
        using (var gpu = EffectPictureProcessing.Upload(next))
        {
            Assert.AreSame(identity, gpu.ContentIdentity);
            Assert.AreEqual("second", gpu.Tag);
        }
        Assert.AreEqual(1, device.Uploads);
        using (var edit = source.Edit()) edit.Red[0] = 60;
        using (var scope = new HwAccelPictureScope(session))
        using (scope.Enter())
        using (var gpu = EffectPictureProcessing.Upload(source))
            Assert.AreNotSame(identity, gpu.ContentIdentity);
        Assert.AreEqual(2, device.Uploads);
        session.Dispose();
        Assert.AreEqual(0, device.Images);
    }

    [TestMethod]
    public void SessionDoesNotCacheEscapedArrays()
    {
        using var source = Picture(50);
        var red = source.r;
        using var session = new HwAccelPictureSession();
        using var scope = new HwAccelPictureScope(session);
        using var entry = scope.Enter();
        using var first = EffectPictureProcessing.Upload(source);
        red[0] = 90;
        using var second = EffectPictureProcessing.Upload(source);
        using var a = first.Download();
        using var b = second.Download();
        Assert.AreEqual((byte)50, ((IPicture<byte>)a).r[0]);
        Assert.AreEqual((byte)90, ((IPicture<byte>)b).r[0]);
        Assert.AreEqual(2, device.Uploads);
    }

    [TestMethod]
    public async Task SessionCoalescesConcurrentUploadsOfTheSameSnapshot()
    {
        using var source = Picture(50);
        using var session = new HwAccelPictureSession();
        var pictures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var scope = new HwAccelPictureScope(session);
            using var entry = scope.Enter();
            return EffectPictureProcessing.Upload(source);
        })));
        try
        {
            Assert.AreEqual(1, device.Uploads);
            foreach (var picture in pictures) Assert.AreSame(pictures[0].ContentIdentity, picture.ContentIdentity);
        }
        finally { foreach (var picture in pictures) picture.Dispose(); }
        session.Dispose();
        Assert.AreEqual(0, device.Images);
    }

    [TestMethod]
    public void ResourceBudgetNeverExceedsItsLimit()
    {
        var budget = new HwAccelResourceBudget();
        Assert.IsTrue(budget.TryReserve(HwAccelResourceBudget.DefaultBytes));
        Assert.IsFalse(budget.TryReserve(1));
        budget.Release(32);
        Assert.IsTrue(budget.TryReserve(32));
        Assert.AreEqual(HwAccelResourceBudget.DefaultBytes, budget.Bytes);
    }

    private sealed class PictureBoundary(Func<EffectExecutionContext, object?> compute) : TestEffect(compute), IEffect
    {
        public bool IsPictureTransportBoundary => true;
    }

    private sealed class HardwareEffect() : TestEffect(_ => throw new InvalidOperationException("CPU path used.")), IHwAccelPictureEffect
    {
        public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context)
        {
            Assert.AreSame(source, context.Input);
            context.CancellationToken.ThrowIfCancellationRequested();
            var input = (FakePicture)source;
            var copy = (IPicture<byte>)EffectPictureProcessing.Copy(input.Storage.Picture);
            copy.r[0]++;
            return new FakePicture(new Image(input.Storage.Device, copy));
        }
    }

    private sealed class HardwarePassthrough() : TestEffect(c => c.Input), IHwAccelPictureEffect
    {
        public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context) => source;
    }

    private sealed class FakeContext : IHwAccelPictureContext
    {
        private int references = 1;
        private bool disposed;
        internal int Uploads, Downloads, Images;
        internal bool Released;
        public IDisposable Retain()
        {
            Interlocked.Increment(ref references);
            return new Lease(this);
        }

        private sealed class Lease(FakeContext context) : IDisposable
        {
            private bool disposed;
            public void Dispose()
            {
                if (!disposed)
                {
                    disposed = true;
                    context.Release();
                }
            }
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref references) == 0)
                Released = true;
        }

        public IHwAccelPicture Upload(IPicture source)
        {
            if (source is FakePicture picture && ReferenceEquals(picture.Context, this))
                return picture.Retain();
            Uploads++;
            using var cpu = source is IHwAccelPicture gpu ? gpu.Download() : null;
            return new FakePicture(new Image(this, EffectPictureProcessing.Copy(cpu ?? source)));
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                Release();
            }
        }
    }

    private sealed class Image
    {
        internal readonly FakeContext Device;
        internal readonly IPicture Picture;
        internal int References = 1;
        private readonly IDisposable lease;
        internal Image(FakeContext device, IPicture picture)
        {
            Device = device;
            Picture = picture;
            lease = device.Retain();
            device.Images++;
        }

        internal void Release()
        {
            if (--References != 0)
                return;
            Picture.Dispose();
            Device.Images--;
            lease.Dispose();
        }
    }

    private sealed class FakePicture : IHwAccelPicture
    {
        internal readonly Image Storage;
        internal FakePicture(Image storage)
        {
            Storage = storage;
            HasAlphaChannel = storage.Picture.HasAlphaChannel;
        }

        public IHwAccelPictureContext Context => Storage.Device;
        public object ContentIdentity => Storage;
        public bool IsHdr => false;
        public Half MaximumBrightness => (Half)203;
        public int Width { get => Storage.Picture.Width; set => throw new NotSupportedException(); }
        public int Height { get => Storage.Picture.Height; set => throw new NotSupportedException(); }
        public int Pixels { get => Storage.Picture.Pixels; set => throw new NotSupportedException(); }
        public IPicture.PicturePixelMode BitPerPixel => Storage.Picture.BitPerPixel;
        public bool HasAlphaChannel { get; set; }
        public string Tag { get; set; } = "";
        public List<PictureProcessStack> ProcessStack { get; set; } = [];
        public bool CanBeDisposed { get; set; } = true;
        public bool Disposed { get; private set; }

        public IHwAccelPicture Retain()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Storage.References++;
            return new FakePicture(Storage);
        }

        public IPicture Download()
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Storage.Device.Downloads++;
            return EffectPictureProcessing.Copy(Storage.Picture);
        }

        public IPicture ToBitPerPixel(IPicture.PicturePixelMode bits) => bits == BitPerPixel ? Retain() : throw new NotSupportedException();
        public object? GetSpecificChannel(IPicture.ChannelId id) => throw new InvalidOperationException("Implicit GPU readback.");
        public ulong GetUniqueID() => throw new InvalidOperationException("Implicit GPU hash.");
        public string GetDiagnosticsInfo() => "Fake device picture";
        public void Dispose() => Dispose(false, false);
        public void Dispose(bool force = false, bool appendDisposedProcessStack = true)
        {
            if (Disposed || !force && !CanBeDisposed)
                return;
            Disposed = true;
            Storage.Release();
        }
    }
}
