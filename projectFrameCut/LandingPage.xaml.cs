using System.Threading.Tasks;

namespace projectFrameCut;

public partial class LandingPage : ContentPage
{
    public Timer TimeoutTimer;


    public LandingPage()
    {
        InitializeComponent();
        string DraftToOpen = "";
#if WINDOWS
        DraftToOpen = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "";
#endif

        if (string.IsNullOrWhiteSpace(DraftToOpen))
            TimeoutTimer = new Timer((o) =>
            {
                Button b = new Button
                {
                    Text = Localized.LandingPage_BackToContent
                };
                b.Clicked += (s, e) =>
                {
                    Dispatcher.Dispatch(async () =>
                    {
                        await Navigation.PushAsync(new DebuggingMainPage());
                    });
                };
                Dispatcher.Dispatch(() =>
                {
                    Content = new Grid { Children = { b } };
                });
            }, new(), 10000, Timeout.Infinite);

        Content = new VerticalStackLayout
        {
            Children =
            {
                new ActivityIndicator
                {
                    IsRunning = true,
                    WidthRequest = 80,
                    HeightRequest = 80
                },
                new Label
                {
                    Text = Localized.LandingPage_Loading,
                    
                    FontSize = 72,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = Colors.White
                },
            },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center

        };
    }

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new DebuggingMainPage()); //todo: go to different page based on condition
    }
}