using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace StarCitizenJapaneseTextCreater;

public class ChatTemplateSelector : DataTemplateSelector
{
    public static readonly ChatTemplateSelector Instance = new();

    private static DataTemplate? _userTemplate;
    private static DataTemplate? _aiTemplate;

    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        if (item is ChatBubble bubble)
        {
            if (bubble.IsUser)
                return _userTemplate ??= CreateUserTemplate();
            else
                return _aiTemplate ??= CreateAiTemplate();
        }
        return base.SelectTemplate(item, container);
    }

    private static DataTemplate CreateUserTemplate()
    {
        var template = new DataTemplate(typeof(ChatBubble));
        var tbFactory = new FrameworkElementFactory(typeof(TextBox));
        tbFactory.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("Text") { Mode = System.Windows.Data.BindingMode.OneWay });
        tbFactory.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        tbFactory.SetBinding(TextBox.ForegroundProperty, new System.Windows.Data.Binding("Foreground"));
        tbFactory.SetValue(TextBox.FontSizeProperty, 13.0);
        tbFactory.SetValue(TextBox.IsReadOnlyProperty, true);
        tbFactory.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
        tbFactory.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
        tbFactory.SetValue(TextBox.IsTabStopProperty, false);
        template.VisualTree = tbFactory;
        return template;
    }

    private static DataTemplate CreateAiTemplate()
    {
        var template = new DataTemplate(typeof(ChatBubble));
        var rtbFactory = new FrameworkElementFactory(typeof(RichTextBox));
        rtbFactory.SetValue(RichTextBox.IsReadOnlyProperty, true);
        rtbFactory.SetValue(RichTextBox.BorderThicknessProperty, new Thickness(0));
        rtbFactory.SetValue(RichTextBox.BackgroundProperty, Brushes.Transparent);
        rtbFactory.SetValue(RichTextBox.IsTabStopProperty, false);
        rtbFactory.SetValue(RichTextBox.FontSizeProperty, 13.0);
        rtbFactory.SetValue(RichTextBox.FontFamilyProperty, new FontFamily("Segoe UI, Meiryo, sans-serif"));
        rtbFactory.SetValue(RichTextBox.PaddingProperty, new Thickness(0));
        rtbFactory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnRichTextBoxLoaded));
        template.VisualTree = rtbFactory;
        return template;
    }

    private static void OnRichTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox rtb || rtb.DataContext is not ChatBubble bubble) return;
        var doc = MarkdownToFlowDocument(bubble.Text, bubble.IsError);
        rtb.Document = doc;
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static FlowDocument MarkdownToFlowDocument(string markdown, bool isError)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Meiryo, sans-serif"),
            FontSize = 13,
            Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))
                : new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21)),
            PagePadding = new Thickness(0)
        };

        var parsed = Markdown.Parse(markdown, Pipeline);

        foreach (var block in parsed)
        {
            var wpfBlock = ConvertBlock(block);
            if (wpfBlock != null)
                doc.Blocks.Add(wpfBlock);
        }

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph(new Run(markdown)));

        return doc;
    }

    private static System.Windows.Documents.Block? ConvertBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var para = new Paragraph { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) };
                para.FontSize = heading.Level switch
                {
                    1 => 16,
                    2 => 15,
                    3 => 14,
                    _ => 13
                };
                AddInlines(para.Inlines, heading.Inline);
                return para;
            }

            case ParagraphBlock paragraphBlock:
            {
                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlines(para.Inlines, paragraphBlock.Inline);
                return para;
            }

            case ListBlock listBlock:
            {
                var list = new List
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(20, 0, 0, 0)
                };
                list.MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

                foreach (var item in listBlock)
                {
                    if (item is ListItemBlock listItem)
                    {
                        var li = new ListItem();
                        foreach (var child in listItem)
                        {
                            var childBlock = ConvertBlock(child);
                            if (childBlock != null)
                                li.Blocks.Add(childBlock);
                        }
                        if (li.Blocks.Count == 0)
                            li.Blocks.Add(new Paragraph(new Run("")));
                        list.ListItems.Add(li);
                    }
                }
                return list.ListItems.Count > 0 ? list : null;
            }

            case FencedCodeBlock codeBlock:
            {
                var text = codeBlock.Lines.ToString().TrimEnd();
                var para = new Paragraph(new Run(text)
                {
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
                })
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
                };
                return para;
            }

            case CodeBlock codeBlockGeneric:
            {
                var text = codeBlockGeneric.Lines.ToString().TrimEnd();
                var para = new Paragraph(new Run(text)
                {
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 12
                })
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                    Padding = new Thickness(8, 4, 8, 4)
                };
                return para;
            }

            case ThematicBreakBlock:
            {
                var para = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 4),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(0, 1, 0, 0)
                };
                return para;
            }

            case QuoteBlock quoteBlock:
            {
                var section = new Section
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(10, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                    BorderThickness = new Thickness(3, 0, 0, 0)
                };
                foreach (var child in quoteBlock)
                {
                    var childBlock = ConvertBlock(child);
                    if (childBlock != null)
                        section.Blocks.Add(childBlock);
                }
                return section;
            }

            default:
                return null;
        }
    }

    private static void AddInlines(InlineCollection inlines, ContainerInline? container)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    inlines.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterCount == 2 || emphasis.DelimiterChar == '*' && emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeights.Bold;
                    else
                        span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis);
                    inlines.Add(span);
                    break;
                }

                case CodeInline code:
                {
                    var run = new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Consolas, Courier New"),
                        FontSize = 12,
                        Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))
                    };
                    inlines.Add(run);
                    break;
                }

                case LinkInline link:
                {
                    var hyperlink = new Hyperlink { NavigateUri = null };
                    try { if (link.Url != null) hyperlink.NavigateUri = new Uri(link.Url); } catch { }
                    hyperlink.Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
                    AddInlines(hyperlink.Inlines, link);
                    if (hyperlink.Inlines.Count == 0 && link.Url != null)
                        hyperlink.Inlines.Add(new Run(link.Url));
                    hyperlink.RequestNavigate += (_, args) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch { }
                    };
                    inlines.Add(hyperlink);
                    break;
                }

                case LineBreakInline:
                    inlines.Add(new LineBreak());
                    break;

                case HtmlInline html:
                    // Skip raw HTML tags, just add text content if any
                    break;

                case ContainerInline container2:
                    AddInlines(inlines, container2);
                    break;

                default:
                    inlines.Add(new Run(inline.ToString() ?? ""));
                    break;
            }
        }
    }
}
