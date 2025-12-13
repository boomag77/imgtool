using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ImgViewer.Helpers
{
    /// <summary>
    /// Renders documentation paragraphs while gently highlighting parameter names.
    /// The text format is still a plain string, but lines that look like "- Parameter: details"
    /// will show the parameter part with a warm accent color and semi-bold weight.
    /// </summary>
    public sealed class HighlightedTextBlock : TextBlock
    {
        private static readonly System.Windows.Media.Brush HighlightBrush;

        static HighlightedTextBlock()
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x5E));
            brush.Freeze();
            HighlightBrush = brush;
        }

        public string? FormattedText
        {
            get => (string?)GetValue(FormattedTextProperty);
            set => SetValue(FormattedTextProperty, value);
        }

        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.Register(
                nameof(FormattedText),
                typeof(string),
                typeof(HighlightedTextBlock),
                new PropertyMetadata(string.Empty, OnFormattedTextChanged));

        private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HighlightedTextBlock block)
            {
                block.UpdateInlines(e.NewValue as string ?? string.Empty);
            }
        }

        private void UpdateInlines(string text)
        {
            Inlines.Clear();
            if (string.IsNullOrEmpty(text))
                return;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i > 0)
                    Inlines.Add(new LineBreak());

                if (line.Length == 0)
                    continue;

                AppendLine(line);
            }
        }

        private void AppendLine(string line)
        {
            string trimmedStart = line.TrimStart();
            int indentLength = line.Length - trimmedStart.Length;
            string indent = indentLength > 0 ? line.Substring(0, indentLength) : string.Empty;

            if (trimmedStart.StartsWith("- "))
            {
                string content = trimmedStart.Substring(2);
                AppendBullet(indent, content);
                return;
            }

            if (trimmedStart.StartsWith("Parameter:", StringComparison.OrdinalIgnoreCase))
            {
                AppendParameterLine(indent, trimmedStart);
                return;
            }

            Inlines.Add(new Run(line));
        }

        private void AppendBullet(string indent, string content)
        {
            if (!string.IsNullOrEmpty(indent))
                Inlines.Add(new Run(indent));

            Inlines.Add(new Run("- "));

            var (parameter, rest) = SplitParameter(content);
            if (string.IsNullOrEmpty(parameter))
            {
                Inlines.Add(new Run(content));
                return;
            }

            Inlines.Add(CreateHighlightRun(parameter));
            Inlines.Add(new Run(": "));

            if (!string.IsNullOrWhiteSpace(rest))
                Inlines.Add(new Run(rest.TrimStart()));
        }

        private void AppendParameterLine(string indent, string trimmedLine)
        {
            if (!string.IsNullOrEmpty(indent))
                Inlines.Add(new Run(indent));

            const string prefix = "Parameter:";
            Inlines.Add(new Run(prefix + " "));

            string remainder = trimmedLine.Length > prefix.Length
                ? trimmedLine.Substring(prefix.Length).TrimStart()
                : string.Empty;

            if (string.IsNullOrEmpty(remainder))
                return;

            var (parameter, tail) = ExtractInlineParameter(remainder);
            if (string.IsNullOrEmpty(parameter))
            {
                Inlines.Add(new Run(remainder));
                return;
            }

            Inlines.Add(CreateHighlightRun(parameter));

            if (!string.IsNullOrEmpty(tail))
                Inlines.Add(new Run(tail));
        }

        private static (string parameter, string rest) SplitParameter(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return (string.Empty, string.Empty);

            int colonIndex = content.IndexOf(':');
            if (colonIndex <= 0)
                return (string.Empty, string.Empty);

            string parameter = content.Substring(0, colonIndex).Trim();
            if (parameter.StartsWith("`", StringComparison.Ordinal) && parameter.EndsWith("`", StringComparison.Ordinal) && parameter.Length > 1)
            {
                parameter = parameter.Substring(1, parameter.Length - 2);
            }

            string rest = colonIndex + 1 < content.Length ? content.Substring(colonIndex + 1) : string.Empty;
            return (parameter, rest);
        }

        private static (string parameter, string tail) ExtractInlineParameter(string remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder))
                return (string.Empty, string.Empty);

            if (remainder.StartsWith("`", StringComparison.Ordinal))
            {
                int closing = remainder.IndexOf('`', 1);
                if (closing > 1)
                {
                    string name = remainder.Substring(1, closing - 1).Trim();
                    string tail = closing + 1 < remainder.Length ? remainder.Substring(closing + 1) : string.Empty;
                    return (name, tail);
                }
            }

            int terminator = remainder.IndexOf('.');
            if (terminator < 0)
                terminator = remainder.IndexOf('(');
            if (terminator < 0)
                terminator = remainder.IndexOfAny(new[] { ' ', '\t' });

            string parameter = terminator > 0 ? remainder.Substring(0, terminator) : remainder;
            string tailPart = terminator > 0 && terminator < remainder.Length
                ? remainder.Substring(terminator)
                : string.Empty;

            parameter = parameter.Trim();
            return (parameter, tailPart);
        }

        private static Run CreateHighlightRun(string text) =>
            new(text)
            {
                Foreground = HighlightBrush,
                FontWeight = FontWeights.SemiBold
            };
    }
}
