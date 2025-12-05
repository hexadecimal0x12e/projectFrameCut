using Android.Opengl;
using System;

public static class GLInfoHelper
{
    public static string GetGLESInfo()
    {
        try
        {
            var display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
            int[] ver = new int[2];
            EGL14.EglInitialize(display, ver, 0, ver, 1);

            int[] attrib = {
                EGL14.EglRedSize, 8,
                EGL14.EglGreenSize, 8,
                EGL14.EglBlueSize, 8,
                EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
                EGL14.EglNone
            };
            EGLConfig[] configs = new EGLConfig[1];
            int[] num = new int[1];
            EGL14.EglChooseConfig(display, attrib, 0, configs, 0, 1, num, 0);

            int[] ctxAttrib = { EGL14.EglContextClientVersion, 2, EGL14.EglNone };
            var context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, ctxAttrib, 0);

            int[] surfAttrib = { EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone };
            var surface = EGL14.EglCreatePbufferSurface(display, configs[0], surfAttrib, 0);

            EGL14.EglMakeCurrent(display, surface, surface, context);

            var renderer = GLES20.GlGetString(GLES20.GlRenderer) ?? "Unknown";
            var vendor = GLES20.GlGetString(GLES20.GlVendor) ?? "Unknown";
            var version = GLES20.GlGetString(GLES20.GlVersion) ?? "Unknown";

            EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
            EGL14.EglDestroySurface(display, surface);
            EGL14.EglDestroyContext(display, context);
            EGL14.EglTerminate(display);

            return $"Renderer: {renderer}\nVendor: {vendor}\nVersion: {version}";
        }
        catch (Exception ex)
        {
            return $"Error getting GL info: {ex.Message}";
        }
    }
}