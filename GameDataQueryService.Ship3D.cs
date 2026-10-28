using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D 表示の別ウィンドウ (Ship3DWindow) の船の切り替えで使う: 船のメーカー名
public partial class GameDataQueryService
{
    // 船・車両 (ships と item_index にある record) のメーカー名 (record → 名前)。
    // ［詳細］タブの【基本】のメーカーと同じ決め方: item_index.manufacturer (例 .../scitemmanufacturer.aegs.json) の最後の語を
    // 翻訳 DB の manufacturer_Name<コード> で英語の名前にする (ManufacturerName。引けなければコードを大文字で)。
    // 船は items に行が無い (実データで確認) ので item_index の値を使う。メーカーが空の船は入れない
    public Dictionary<string, string> GetShipManufacturerNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var loc = new LocReader(_translationsDbPath);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT x.record_name, COALESCE(x.manufacturer, '')
                FROM ships s JOIN item_index x ON x.record_name = s.record_name
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = ManufacturerName(r.GetString(1), loc);
                if (name.Length > 0) result[r.GetString(0)] = name;
            }
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        return result;
    }
}
