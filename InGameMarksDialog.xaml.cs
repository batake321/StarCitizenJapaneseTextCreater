using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// 翻訳タブの [日本語化] を押したときに、ゲーム内の名前に付ける印を選ぶダイアログ。
// 例の文字列は実際の翻訳データから 1 つずつ選んだもの
public partial class InGameMarksDialog : Window
{
    public bool EquipmentClassMarks => chkEquipClassMarks.IsChecked == true;
    public bool MissionReputationMarks => chkMissionRepMarks.IsChecked == true;
    public bool BlueprintMissionMarks => chkBlueprintMarks.IsChecked == true;

    public InGameMarksDialog(bool equipmentClassMarks, bool missionReputationMarks, bool blueprintMissionMarks)
    {
        InitializeComponent();
        chkEquipClassMarks.IsChecked = equipmentClassMarks;
        chkMissionRepMarks.IsChecked = missionReputationMarks;
        chkBlueprintMarks.IsChecked = blueprintMissionMarks;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
