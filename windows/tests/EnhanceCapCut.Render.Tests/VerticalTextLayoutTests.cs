using EnhanceCapCut.Render;
using Kind = EnhanceCapCut.Render.VerticalTextLayout.Kind;

namespace EnhanceCapCut.Render.Tests;

/// <summary>The same cases as the macOS VerticalTextTests.</summary>
public class VerticalTextLayoutTests
{
    static List<Kind> Kinds(string text) =>
        text.Select(c => c == '\n' ? Kind.Newline : c == ' ' ? Kind.Space : Kind.Character).ToList();

    static List<float> Advances(List<Kind> kinds) =>
        kinds.Select(k => k == Kind.Newline ? 0f : k == Kind.Space ? 5f : 10f).ToList();

    static List<List<int>> Columns(string text, float limit)
    {
        var k = Kinds(text);
        return VerticalTextLayout.Columns(k, Advances(k), limit);
    }

    [Fact]
    public void NewlinesStartColumns() => Assert.Equal([[0, 1], [3]], Columns("AB\nC", 100));

    [Fact]
    public void OverflowBreaksAfterLastSpace() => Assert.Equal([[0, 1], [3, 4]], Columns("AB CD", 30));

    [Fact]
    public void OverflowWithoutSpaceBreaksBeforeCharacter() => Assert.Equal([[0, 1], [2, 3], [4]], Columns("ABCDE", 20));

    [Fact]
    public void ColumnsRunRightToLeftAndAlign()
    {
        var k = Kinds("AB\nC");
        var a = Advances(k);
        var (top, w, h) = VerticalTextLayout.Arrange(k, a, 100, 12, 10, 0);
        Assert.Equal((22f, 20f), (w, h));
        Assert.Equal([new(0, 17, 0), new(1, 17, 10), new(3, 5, 0)], top);
        Assert.Equal(new VerticalTextLayout.Placement(3, 5, 5), VerticalTextLayout.Arrange(k, a, 100, 12, 10, 1).Placements[^1]);
        Assert.Equal(new VerticalTextLayout.Placement(3, 5, 10), VerticalTextLayout.Arrange(k, a, 100, 12, 10, 2).Placements[^1]);
    }
}
