using EnhanceCapCut.Subtitles;

namespace EnhanceCapCut.Draft.Tests;

// Ported from the macOS version's DonationsTests.swift; same cases, same expectations.

public class DonationsTests
{
    const string File = """
    {
      "schema": 1,
      "future": {"anything": true},
      "message": {"en": "Support it", "ru": "Поддержите", "pt-BR": "Apoie"},
      "methods": [
        {"id": "cloudtips", "title": {"en": "Card", "ru": "Карта"}, "url": "https://pay.cloudtips.ru/p/1"},
        {"id": "plain", "title": "Plain title", "description": {"ru": "Только по-русски"}, "url": "https://example.com/d"},
        {"id": "http", "title": {"en": "Insecure"}, "url": "http://example.com"},
        {"id": "script", "title": {"en": "Script"}, "url": "javascript:alert(1)"},
        {"id": "untitled", "url": "https://example.com"},
        42
      ]
    }
    """;

    [Fact]
    public void ReadsMethodsLenientlyAndOnlyHttps()
    {
        var info = DonationInfo.Parse(File)!;
        Assert.Equal(1, info.Schema);
        Assert.Equal(["cloudtips", "plain"], info.Methods.Select(m => m.Id));
        Assert.Equal("https://pay.cloudtips.ru/p/1", info.Methods[0].Url.AbsoluteUri);
        Assert.Equal("Plain title", info.Methods[1].Title.Resolved(["ru"]));
        // A description without English shows only for its language.
        Assert.Equal("Только по-русски", info.Methods[1].Description?.Resolved(["ru-RU"]));
        Assert.Null(info.Methods[1].Description?.Resolved(["de"]));
    }

    [Fact]
    public void PicksTheLanguage()
    {
        var message = DonationInfo.Parse(File)!.Message!;
        Assert.Equal("Apoie", message.Resolved(["pt-BR"]));
        Assert.Equal("Apoie", message.Resolved(["pt_br"]));
        Assert.Equal("Поддержите", message.Resolved(["ru-RU", "en-US"]));
        // The next preferred language before English.
        Assert.Equal("Поддержите", message.Resolved(["de-DE", "ru"]));
        Assert.Equal("Support it", message.Resolved(["de-DE"]));
        Assert.Equal("Support it", message.Resolved([]));
    }

    [Fact]
    public void RejectsFilesWithoutMethods()
    {
        Assert.Null(DonationInfo.Parse("not json"));
        Assert.Null(DonationInfo.Parse("""{"schema": 2, "methods": []}"""));
        Assert.Null(DonationInfo.Parse("""{"methods": "none"}"""));
    }

    [Fact]
    public void ReadsTheCachedCopy()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"ecc-donations-{Guid.NewGuid()}.json");
        try
        {
            Assert.Null(Donations.Cached(cache));
            System.IO.File.WriteAllText(cache, File);
            Assert.Equal(2, Donations.Cached(cache)?.Methods.Count);
        }
        finally
        {
            System.IO.File.Delete(cache);
        }
    }
}
