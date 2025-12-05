namespace projectFrameCut
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Use sidebar shell on Mac Catalyst, original tabbed shell on iOS
            if (OperatingSystem.IsMacCatalyst())
            {
                return new Window(new AppShell_MacCatalyst());
            }
            return new Window(new AppShell());
        }
    }
}