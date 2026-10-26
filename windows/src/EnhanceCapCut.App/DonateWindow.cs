using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        Title = "Support EnhanceCapCut";

        loadingSection = form.Section();
        var loadingText = Text.Body("Loading the ways to donate…");
        loadingText.Foreground = Theme.Secondary;
        loadingSection.Add(ExportWindow.Row(new ProgressRing { IsActive = true, Width = 16, Height = 16 }, loadingText));

        failedSection = form.Section();
        failedSection.Add(Text.Body("Couldn't load the ways to donate. Check your internet connection."));
        var retry = new Button { Content = "Try Again" };
        retry.Click += (_, _) => model.Load();
        failedSection.Add(retry);

        messageSection = form.Section();
        messageSection.Add(messageText);
        methodsSection = form.Section("Ways to donate");

        Content = new Grid { Background = Theme.Resource("SolidBackgroundFillColorBaseBrush"), Children = { form } };
        Platform.SetUpFrame(this, "window.donate", 440, 340, 400, 280);
        model.Changed += Update;
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
            var donate = new Button { Content = "Donate…" };
            ToolTipService.SetToolTip(donate, method.Url.AbsoluteUri);
            donate.Click += (_, _) => DonateModel.Open(method);
            methodsSection.Add(ExportWindow.Spread(text, donate));
        }
        form.Refresh();
    }
}
