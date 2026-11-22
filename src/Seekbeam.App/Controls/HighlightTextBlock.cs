using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Controls;

/// <summary>TextBlock rendering matched ranges in bold/accent colour.</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HighlightTextBlock),
        new PropertyMetadata("", (d, _) => ((HighlightTextBlock)d).Rebuild()));
    public static readonly DependencyProperty HighlightsProperty = DependencyProperty.Register(nameof(Highlights), typeof(IReadOnlyList<Highlight>), typeof(HighlightTextBlock),
        new PropertyMetadata(null, (d, _) => ((HighlightTextBlock)d).Rebuild()));
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(nameof(HighlightBrush), typeof(Brush), typeof(HighlightTextBlock),
        new PropertyMetadata(Brushes.DarkOrange, (d, _) => ((HighlightTextBlock)d).Rebuild()));

    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public IReadOnlyList<Highlight>? Highlights { get => (IReadOnlyList<Highlight>?)GetValue(HighlightsProperty); set => SetValue(HighlightsProperty, value); }
    public Brush HighlightBrush { get => (Brush)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    private void Rebuild()
    {
        Inlines.Clear();
        string text = SourceText ?? "";
        var hl = Highlights;
        if (hl == null || hl.Count == 0) { Inlines.Add(new Run(text)); return; }
        int pos = 0;
        foreach (var h in hl.OrderBy(h => h.Start))
        {
            if (h.Start < pos || h.Start >= text.Length) continue;
            int len = Math.Min(h.Length, text.Length - h.Start);
            if (h.Start > pos) Inlines.Add(new Run(text[pos..h.Start]));
            Inlines.Add(new Run(text.Substring(h.Start, len)) { FontWeight = FontWeights.SemiBold, Foreground = HighlightBrush });
            pos = h.Start + len;
        }
        if (pos < text.Length) Inlines.Add(new Run(text[pos..]));
    }
}
