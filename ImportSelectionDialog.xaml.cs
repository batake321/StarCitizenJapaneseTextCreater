using System.Windows;

namespace StarCitizenJapaneseTextCreater;

public enum ImportMode { Add, Drop }

public partial class ImportSelectionDialog : Window
{
    public List<BackupCategory> SelectedCategories { get; private set; } = new();
    public ImportMode Mode { get; private set; } = ImportMode.Add;

    public ImportSelectionDialog()
    {
        InitializeComponent();
    }

    public void SetFileInfo(string fileName, long fileSize, Dictionary<BackupCategory, int> contents)
    {
        txtFileInfo.Text = $"ファイル: {fileName} ({fileSize / 1024.0:N0} KB)";

        if (contents.TryGetValue(BackupCategory.Translations, out var tc))
        {
            chkTranslations.IsEnabled = true;
            txtTransCount.Text = $"({tc:N0} 件)";
        }
        else
        {
            chkTranslations.IsEnabled = false;
            txtTransCount.Text = "(データなし)";
        }

        if (contents.TryGetValue(BackupCategory.Glossary, out var gc))
        {
            chkGlossary.IsEnabled = true;
            txtGlossaryCount.Text = $"({gc:N0} 件)";
        }
        else
        {
            chkGlossary.IsEnabled = false;
            txtGlossaryCount.Text = "(データなし)";
        }

        if (contents.TryGetValue(BackupCategory.Index, out var ic))
        {
            chkIndex.IsEnabled = true;
            txtIndexCount.Text = $"({ic:N0} 件)";
        }
        else
        {
            chkIndex.IsEnabled = false;
            txtIndexCount.Text = "(データなし)";
        }
    }

    private void ChkAll_Changed(object sender, RoutedEventArgs e)
    {
        var isChecked = chkAll.IsChecked == true;
        if (chkTranslations.IsEnabled) chkTranslations.IsChecked = isChecked;
        if (chkGlossary.IsEnabled) chkGlossary.IsChecked = isChecked;
        if (chkIndex.IsEnabled) chkIndex.IsChecked = isChecked;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        SelectedCategories.Clear();
        if (chkTranslations.IsChecked == true) SelectedCategories.Add(BackupCategory.Translations);
        if (chkGlossary.IsChecked == true) SelectedCategories.Add(BackupCategory.Glossary);
        if (chkIndex.IsChecked == true) SelectedCategories.Add(BackupCategory.Index);

        if (SelectedCategories.Count == 0)
        {
            MessageBox.Show("インポートするデータを1つ以上選択してください。", "選択なし", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Mode = rbDrop.IsChecked == true ? ImportMode.Drop : ImportMode.Add;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
