namespace EnhanceCapCut.Render;

/// <summary>
/// Places the characters of vertical text (CapCut's <c>typesetting</c> = 1) in columns (port of the macOS
/// <c>VerticalTextLayout</c>).
///
/// No vertical text was available to calibrate against a CapCut export, so this follows the usual vertical (CJK)
/// typesetting: every character stays upright and is stacked top to bottom, centered in its column; a newline or a
/// column longer than the wrap limit (the same <c>line_max_width</c> limit horizontal lines use) starts a new column to
/// the left of the previous one; columns are spaced like horizontal lines; alignment 0/1/2 puts the columns' text at
/// the top / middle / bottom. A column that overflows breaks after its last space when it has one, otherwise before
/// the character that doesn't fit.
/// </summary>
public static class VerticalTextLayout
{
    /// <summary>Vertical advance of a character, in its font size.</summary>
    public const float CharacterAdvance = 1;
    /// <summary>Vertical advance of a space, in its font size.</summary>
    public const float SpaceAdvance = 0.5f;

    public enum Kind { Character, Space, Newline }

    /// <summary>
    /// Where one character goes: its index in the input, the center of its column (from the left) and the top of its
    /// cell (from the top of the text box).
    /// </summary>
    public readonly record struct Placement(int Index, float CenterX, float Top);

    /// <summary>
    /// Lays out characters of the given kinds and vertical advances. <paramref name="limit"/> is the longest column,
    /// <paramref name="columnPitch"/> the distance between column centers and <paramref name="columnWidth"/> the width
    /// of one column. Spaces and newlines get no placement. Returns the placements and the size of the text box.
    /// </summary>
    public static (List<Placement> Placements, float Width, float Height) Arrange(IReadOnlyList<Kind> kinds,
        IReadOnlyList<float> advances, float limit, float columnPitch, float columnWidth, int alignment)
    {
        var columns = Columns(kinds, advances, limit);
        float Length(List<int> column)
        {
            // Trailing spaces don't count, so alignment sees the visible text.
            var end = column.Count;
            while (end > 0 && kinds[column[end - 1]] == Kind.Space) end--;
            return column.Take(end).Sum(i => advances[i]);
        }
        var lengths = columns.Select(Length).ToList();
        var height = lengths.Count == 0 ? 0 : lengths.Max();
        var width = columns.Count == 0 ? 0 : (columns.Count - 1) * columnPitch + columnWidth;
        var placements = new List<Placement>();
        for (var c = 0; c < columns.Count; c++)
        {
            var centerX = width - columnWidth / 2 - c * columnPitch;
            var y = alignment == 0 ? 0 : alignment == 2 ? height - lengths[c] : (height - lengths[c]) / 2;
            foreach (var i in columns[c])
            {
                if (kinds[i] == Kind.Character) placements.Add(new(i, centerX, y));
                y += advances[i];
            }
        }
        return (placements, width, height);
    }

    /// <summary>Splits the characters into columns (indices; newlines dropped, spaces at a wrap dropped).</summary>
    public static List<List<int>> Columns(IReadOnlyList<Kind> kinds, IReadOnlyList<float> advances, float limit)
    {
        var columns = new List<List<int>>();
        var current = new List<int>();
        float length = 0;
        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == Kind.Newline)
            {
                columns.Add(current);
                current = [];
                length = 0;
                continue;
            }
            // A wrapped column doesn't start with a space.
            if (kinds[i] == Kind.Space && current.Count == 0 && columns.Count > 0 && i > 0 && kinds[i - 1] != Kind.Newline) continue;
            if (length + advances[i] > limit && current.Count > 0)
            {
                if (kinds[i] == Kind.Space)
                {
                    columns.Add(current);
                    current = [];
                    length = 0;
                    continue;
                }
                var space = current.FindLastIndex(j => kinds[j] == Kind.Space);
                if (space > 0)
                {
                    columns.Add(current[..space]);
                    current = current[(space + 1)..];
                }
                else
                {
                    columns.Add(current);
                    current = [];
                }
                length = current.Sum(j => advances[j]);
            }
            current.Add(i);
            length += advances[i];
        }
        columns.Add(current);
        return columns;
    }
}
