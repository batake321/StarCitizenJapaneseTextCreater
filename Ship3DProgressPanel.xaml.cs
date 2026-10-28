using System.Windows;
using System.Windows.Controls;

namespace StarCitizenJapaneseTextCreater;

// 3D モデルの書き出しの進み具合の欄 (Ship3DExports.Instance を出す。［詳細］タブと設定タブに置く)
public partial class Ship3DProgressPanel : UserControl
{
    public Ship3DProgressPanel()
    {
        InitializeComponent();
        DataContext = Ship3DExports.Instance;
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Ship3DJob job) job.Cancel();
    }

    private void CancelBatch_Click(object sender, RoutedEventArgs e) => Ship3DExports.Instance.CancelBatch();
}
