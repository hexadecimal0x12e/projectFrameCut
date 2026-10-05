#if IOS || MACCATALYST
using Metal;
using Foundation;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.HwAccelEngine.Platforms.iOS;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class ColorAdjustmentEffect_HwAccel
    {
        private IMTLComputePipelineState? _pipelineState;
        private void InitializePipeline()
        {
            if (_pipelineState != null)
                return;
            var d = MetalExecutionHelper.Device;
            using var options = new MTLCompileOptions();
            using var l = d.CreateLibrary(ShaderSrc, options, out NSError e);
            if (l == null)
                throw new Exception(e?.LocalizedDescription);
            using var pipelineStateFunction = l.CreateFunction("coloradjust_compute");
            _pipelineState = d.CreateComputePipelineState(pipelineStateFunction, out e);
            if (_pipelineState == null)
                throw new Exception(e?.LocalizedDescription);
        }

        private FourChannelResult ComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int width, int height, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal)
        {
            InitializePipeline();
            int count = r.Length;
            float invertF = invert ? 1f : 0f;
            using var rB = MetalExecutionHelper.CreateBuffer(r);
            using var gB = MetalExecutionHelper.CreateBuffer(g);
            using var bB = MetalExecutionHelper.CreateBuffer(b);
            using var aB = MetalExecutionHelper.CreateBuffer(a);
            using var rOB = MetalExecutionHelper.AllocBuffer(count);
            using var gOB = MetalExecutionHelper.AllocBuffer(count);
            using var bOB = MetalExecutionHelper.AllocBuffer(count);
            using var aOB = MetalExecutionHelper.AllocBuffer(count);
            var(cb, enc) = MetalExecutionHelper.CreateCommandEncoder();
            using var commandBuffer = cb;
            using var commandEncoder = enc;
            enc.SetComputePipelineState(_pipelineState!);
            enc.SetBuffer(rB, 0, 0);
            enc.SetBuffer(gB, 0, 1);
            enc.SetBuffer(bB, 0, 2);
            enc.SetBuffer(aB, 0, 3);
            unsafe
            {
                enc.SetBytes((IntPtr)(&brightness), (nuint)sizeof(float), 4);
                enc.SetBytes((IntPtr)(&contrast), (nuint)sizeof(float), 5);
                enc.SetBytes((IntPtr)(&saturation), (nuint)sizeof(float), 6);
                enc.SetBytes((IntPtr)(&hue), (nuint)sizeof(float), 7);
                enc.SetBytes((IntPtr)(&gamma), (nuint)sizeof(float), 8);
                enc.SetBytes((IntPtr)(&vibrance), (nuint)sizeof(float), 9);
                enc.SetBytes((IntPtr)(&temperature), (nuint)sizeof(float), 10);
                enc.SetBytes((IntPtr)(&invertF), (nuint)sizeof(float), 11);
                enc.SetBytes((IntPtr)(&grayscale), (nuint)sizeof(float), 12);
                enc.SetBytes((IntPtr)(&opacity), (nuint)sizeof(float), 13);
                enc.SetBytes((IntPtr)(&maxVal), (nuint)sizeof(float), 14);
            }

            enc.SetBuffer(rOB, 0, 15);
            enc.SetBuffer(gOB, 0, 16);
            enc.SetBuffer(bOB, 0, 17);
            enc.SetBuffer(aOB, 0, 18);
            var(tgs, tg) = MetalExecutionHelper.ComputeDispatchSizes(count, (int)_pipelineState!.MaxTotalThreadsPerThreadgroup);
            enc.DispatchThreads(tg, tgs);
            enc.EndEncoding();
            cb.Commit();
            cb.WaitUntilCompleted();
            MetalExecutionHelper.CheckCommand(cb);
            var rO = new float[count];
            Marshal.Copy(rOB.Contents, rO, 0, count);
            var gO = new float[count];
            Marshal.Copy(gOB.Contents, gO, 0, count);
            var bO = new float[count];
            Marshal.Copy(bOB.Contents, bO, 0, count);
            var aO = new float[count];
            Marshal.Copy(aOB.Contents, aO, 0, count);
            return new FourChannelResult(rO, gO, bO, aO);
        }

        private const string ShaderSrc = @"
#include <metal_stdlib>
using namespace metal;
kernel void coloradjust_compute(
    device const float* r [[buffer(0)]], device const float* g [[buffer(1)]],
    device const float* b [[buffer(2)]], device const float* a [[buffer(3)]],
    constant float& brightness [[buffer(4)]], constant float& contrast [[buffer(5)]],
    constant float& saturation [[buffer(6)]], constant float& hue [[buffer(7)]],
    constant float& gamma [[buffer(8)]], constant float& vibrance [[buffer(9)]],
    constant float& temperature [[buffer(10)]], constant float& invertF [[buffer(11)]],
    constant float& grayscale [[buffer(12)]], constant float& opacity [[buffer(13)]],
    constant float& maxV [[buffer(14)]],
    device float* rO [[buffer(15)]], device float* gO [[buffer(16)]],
    device float* bO [[buffer(17)]], device float* aO [[buffer(18)]],
    uint id [[thread_position_in_grid]])
{
    float cr = r[id], cg = g[id], cb = b[id], ca = a[id];
    float bf = brightness - 1.0;
    cr = bf>=0.0 ? cr+(maxV-cr)*bf : cr*(1.0+bf);
    cg = bf>=0.0 ? cg+(maxV-cg)*bf : cg*(1.0+bf);
    cb = bf>=0.0 ? cb+(maxV-cb)*bf : cb*(1.0+bf);
    cr = ((cr/maxV-0.5)*contrast+0.5)*maxV;
    cg = ((cg/maxV-0.5)*contrast+0.5)*maxV;
    cb = ((cb/maxV-0.5)*contrast+0.5)*maxV;
    float gray = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    cr = gray + saturation*(cr-gray);
    cg = gray + saturation*(cg-gray);
    cb = gray + saturation*(cb-gray);
    // Hue
    { float nr=cr/maxV, ng=cg/maxV, nb=cb/maxV;
      float cmx=fmax(fmax(nr,ng),nb), cmn=fmin(fmin(nr,ng),nb), delta=cmx-cmn;
      float hh=0.0, ss, ll;
      if (delta>0.0) { if(cmx==nr) hh=60.0*fmod((ng-nb)/delta,6.0); else if(cmx==ng) hh=60.0*((nb-nr)/delta+2.0); else hh=60.0*((nr-ng)/delta+4.0); if(hh<0.0) hh+=360.0; }
      ll=(cmx+cmn)*0.5; ss=(ll>0.0&&ll<1.0)?delta/(1.0-fabs(2.0*ll-1.0)):0.0;
      hh+=hue; if(hh<0.0) hh+=360.0; if(hh>=360.0) hh-=360.0;
      if(ss<0.000001) { cr=ll*maxV; cg=ll*maxV; cb=ll*maxV; }
      else { float qq=ll<0.5?ll*(1.0+ss):ll+ss-ll*ss; float p=2.0*ll-qq; float hN=hh/360.0;
        float Tr=hN+0.333333; if(Tr<0.0)Tr+=1.0; if(Tr>1.0)Tr-=1.0;
        float Tg=hN; if(Tg<0.0)Tg+=1.0; if(Tg>1.0)Tg-=1.0;
        float Tb=hN-0.333333; if(Tb<0.0)Tb+=1.0; if(Tb>1.0)Tb-=1.0;
        float h2r(float t) { return t<0.166667?p+(qq-p)*6.0*t:t<0.5?qq:t<0.666667?p+(qq-p)*(0.666667-t)*6.0:p; }
        cr=h2r(Tr)*maxV; cg=h2r(Tg)*maxV; cb=h2r(Tb)*maxV; }
    }
    float invG = 1.0 / fmax(gamma, 0.001);
    cr = maxV * pow(cr/maxV, invG);
    cg = maxV * pow(cg/maxV, invG);
    cb = maxV * pow(cb/maxV, invG);
    float vSat = 1.0 + vibrance * 0.5;
    float vGray = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    cr = vGray + vSat*(cr-vGray); cg = vGray + vSat*(cg-vGray); cb = vGray + vSat*(cb-vGray);
    cr *= 1.0 + temperature * 0.01; cb *= 1.0 - temperature * 0.01;
    if (invertF > 0.5) { cr = maxV - cr; cg = maxV - cg; cb = maxV - cb; }
    float lum = 0.2126*cr + 0.7152*cg + 0.0722*cb;
    float gs = grayscale >= 1.0 ? 1.0 : 1.0 - grayscale;
    rO[id] = lum + gs*(cr - lum);
    gO[id] = lum + gs*(cg - lum);
    bO[id] = lum + gs*(cb - lum);
    aO[id] = ca * opacity;
}
";
        partial void ReleaseNativeResources()
        {
            _pipelineState?.Dispose();
            _pipelineState = null;
        }
    }
}
#endif
