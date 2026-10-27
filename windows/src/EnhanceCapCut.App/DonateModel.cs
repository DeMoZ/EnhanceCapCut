using EnhanceCapCut.Draft;
using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.App;

abstract record DonateState
{
    public sealed record Loading : DonateState;
    public sealed record Loaded(DonationInfo Info) : DonateState;
    public sealed record Failed : DonateState;
}

/// <summary>
/// The "Support EnhanceCapCut" window (the About window on the Mac): app info, then ways to donate, downloaded from the
/// <c>DeMoZ/donations</c> repository each time the window opens, so links and texts change without a release. The last
/// copy shows while (or if) the download fails. The counterpart of the macOS version's <c>DonateModel</c>;
/// <see cref="Changed"/> fires on the UI thread.
/// </summary>
sealed class DonateModel
{
    public DonateState State { get; private set; } = new DonateState.Loading();
    public event Action? Changed;
    bool loading;

    /// <summary>The languages for the texts of the file: the interface language first, then the user's order in Windows
    /// settings.</summary>
    public IReadOnlyList<string> Languages =>
        [Localization.Current.Code(), .. global::Windows.System.UserProfile.GlobalizationPreferences.Languages];

    public async void Load()
    {
        if (loading) return;
        loading = true;
        if (State is not DonateState.Loaded)
        {
            State = Donations.Cached() is { } cached ? new DonateState.Loaded(cached) : new DonateState.Loading();
            Changed?.Invoke();
        }
        var fresh = await Donations.DownloadAsync();
        loading = false;
        if (fresh is not null) State = new DonateState.Loaded(fresh);
        else if (State is DonateState.Loading) State = new DonateState.Failed();
        Changed?.Invoke();
    }

    public const string ProjectPage = "https://github.com/DeMoZ/EnhanceCapCut";

    public static void Open(DonationMethod method) => Platform.OpenUri(method.Url.AbsoluteUri);
}
