using System.Threading;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider
{
    internal static class DynamicPreviewRenderContext
    {
        private static readonly AsyncLocal<State?> s_state = new();

        public static State? Current => s_state.Value;

        public static void Set(State? state)
        {
            s_state.Value = state;
        }

        internal readonly record struct State(int ProjectRelativeWidth, int ProjectRelativeHeight, int TargetWidth, int TargetHeight);
    }
}
