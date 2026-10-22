namespace ExportViewer.App;

/// <summary>Shown on Windows when the Microsoft Edge WebView2 runtime is not installed.</summary>
public sealed class WebViewMissingPage : ContentPage
{
    public WebViewMissingPage()
    {
        Content = new VerticalStackLayout
        {
            Padding = 48,
            Spacing = 16,
            MaximumWidthRequest = 640,
            Children =
            {
                new Label { Text = "One more thing is needed", FontSize = 26, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "AI Export Viewer needs a free Windows component called \"Microsoft Edge WebView2 Runtime\". " +
                           "It is already installed on most computers, but not on this one.",
                    FontSize = 16,
                },
                new Label
                {
                    Text = "Ask your IT team to install the \"WebView2 Runtime (Evergreen)\" from Microsoft, then open this app again. " +
                           "This app does not download anything itself.",
                    FontSize = 16,
                },
            },
        };
    }
}
