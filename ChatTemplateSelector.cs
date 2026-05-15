using System.Windows;
using System.Windows.Controls;

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
        tbFactory.SetValue(TextBox.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        tbFactory.SetValue(TextBox.IsTabStopProperty, false);
        template.VisualTree = tbFactory;
        return template;
    }

    private static DataTemplate CreateAiTemplate()
    {
        var template = new DataTemplate(typeof(ChatBubble));
        var wbFactory = new FrameworkElementFactory(typeof(WebBrowser));
        wbFactory.SetValue(FrameworkElement.MinHeightProperty, 40.0);
        wbFactory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWebBrowserLoaded));
        template.VisualTree = wbFactory;
        return template;
    }

    private static void OnWebBrowserLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebBrowser wb || wb.DataContext is not ChatBubble bubble) return;
        wb.NavigateToString(bubble.HtmlContent);
        wb.LoadCompleted += (_, _) =>
        {
            try
            {
                dynamic? doc = wb.Document;
                if (doc?.body != null)
                {
                    int h = (int)doc.body.scrollHeight;
                    wb.Height = Math.Max(40, h + 12);
                }
            }
            catch { wb.Height = 200; }
        };
    }
}
