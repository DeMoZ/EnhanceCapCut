using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using static EnhanceCapCut.Draft.Localization;

namespace EnhanceCapCut.App;

/// <summary>"Support EnhanceCapCut" (see <see cref="DonateModel"/>): the counterpart of the macOS version's <c>DonateView</c>.</summary>
sealed class DonateWindow : Window
{
    readonly DonateModel model;
    readonly FormView form = new();
    readonly FormSection loadingSection, failedSection, messageSection, methodsSection;
    readonly TextBlock messageText = Text.Body();

    public DonateWindow(DonateModel model)
    {
        this.model = model;
        Title = L("Support EnhanceCapCut");

        // Icon, name, version and the project page, as in the macOS window.
        var about = form.Section();
        var name = Text.Headline("EnhanceCapCut");
        name.FontSize = 20;
        var info = new StackPanel { Spacing = 2, Children = { name } };
        if (Updater.Current is { } version)
        {
            var versionText = Text.Body(L("Version {0}", version));
            versionText.Foreground = Theme.Secondary;
            info.Children.Add(versionText);
        }
        var page = new HyperlinkButton { Content = "github.com/DeMoZ/EnhanceCapCut", Padding = new Thickness(0) };
        page.Click += (_, _) => Platform.OpenUri(DonateModel.ProjectPage);
        info.Children.Add(page);
        var icon = new Image { Source = new BitmapImage(new Uri(Platform.IconPath)) { DecodePixelWidth = 112 }, Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Center };
        about.Add(Text.IconAndText(icon, info, 12));

        loadingSection = form.Section();
        var loadingText = Text.Body(L("Loading the ways to donate…"));
        loadingText.Foreground = Theme.Secondary;
        loadingSection.Add(ExportWindow.Row(new ProgressRing { IsActive = true, Width = 16, Height = 16 }, loadingText));

        failedSection = form.Section();
        failedSection.Add(Text.Body(L("Couldn't load the ways to donate. Check your internet connection.")));
        var retry = new Button { Content = L("Try Again") };
        retry.Click += (_, _) => model.Load();
        failedSection.Add(retry);

        messageSection = form.Section();
        messageSection.Add(messageText);
        methodsSection = form.Section(L("Ways to donate"));

        Content = new Grid { Background = Theme.Resource("SolidBackgroundFillColorBaseBrush"), Children = { form } };
        Platform.SetUpFrame(this, "window.donate", 440, 440, 400, 360);
        model.Changed += Update;
        Closed += (_, _) => model.Changed -= Update;
        Update();
    }

    void Update()
    {
        var info = (model.State as DonateState.Loaded)?.Info;
        loadingSection.IsShown = model.State is DonateState.Loading;
        failedSection.IsShown = model.State is DonateState.Failed;
        var message = info?.Message?.Resolved(model.Languages);
        messageSection.IsShown = message is not null;
        messageText.Text = message ?? "";
        methodsSection.IsShown = info is not null;
        methodsSection.Clear();
        foreach (var method in info?.Methods ?? [])
        {
            if (method.Title.Resolved(model.Languages) is not { } title) continue;
            var text = new StackPanel { Spacing = 2, Children = { Text.Body(title) } };
            if (method.Description?.Resolved(model.Languages) is { } description) text.Children.Add(Text.Caption(description));
            var donate = new Button { Content = L("Donate…") };
            ToolTipService.SetToolTip(donate, method.Url.AbsoluteUri);
            donate.Click += (_, _) => DonateModel.Open(method);
            methodsSection.Add(ExportWindow.Spread(text, donate));
        }
        form.Refresh();
    }
}
