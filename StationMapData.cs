using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using SocChild = StarCitizenJapaneseTextCreater.GameDataExtractor.SocChild;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップのデータ層 (取り出し)。座標はメートル、z が上。
//   StationIndex: Data.p4k の Data/ObjectContainers の socpak の位置表 (中央ディレクトリを Ship3DP4kIndex と同じ読み方で読む。既存の索引は変えない)。
//     socpak は p4k の中でそのまま開く (無圧縮のものは p4k の該当の範囲を ZipArchive に渡す。一時ファイルは作らない)。
//     星系の socpak の Child の木を恒星から辿り、starMapRecord が 地図を作る種類 (StationPlace.IsMapPlace) の場所の uuid と一致する Child をステーションのルートにする (FindRoots)。
//   StationTree: ルートから Child を辿ったインスタンスの木 (ルートの枠での変換・minBounds/maxBounds) と、
//     インスタンスごとの metadata (部屋・部屋の接続・トラムとエレベーター・案内図の区画と名札)・.soc の店 (SCShop)・.rmxml (RoomMapping)。
// 変換の決まり (計画の照合で Checkmate の実データと合わせたもの):
//   子の位置 = 親の位置 + 親の回転·pos、子の回転 = 親の回転 × rot (四元数 w 先)。入れ子の Child (Child の中の Child) も親の Child の枠で同じに掛ける
//   (恒星の Child の中の入れ子の Child pyro2_l4 は恒星の rot で回して starmap_locations の RR_P2_L4 と一致する)。entdata の Pos は使わない。
//   恒星の回転は、xml の文字を float32 に丸めてから正規化したもの (StarRotRule.Float32) を既定にする。
//   Checkmate の店 3 つがこの規則で利用者の点と合った (float64 のままだとルートが (4.4, 48.3) ずれる)。ほかのステーションで合うかは未確認
// 自転: ルートの上に planetRotationSpeed を持つ socpak (惑星・衛星) があれば、その天体の Child の枠で
//   全体 = 天体の親の枠 · T(天体の pos) · Rz(θ) · R(天体の rot) · 天体から下 とする (GameDataExtractor の BuildStarmap の式
//   「R(q_恒星)·( P_惑星 + Rz(θ)·( R(q_惑星)·p_local ) )」と同じ置き方)。θ は呼ぶ側が渡す (データの式は StationRoot.DataTheta。補正は MapSpinCorrections)。
// 公転: 星系の xml の entdata の EntityComponentOrbit (SOrbitComponentParams) には OrbitalRadius・OrbitalAngle だけがあり、速さ・周期の欄は無い
//   (pyrosystem.socpak で確認)。データで動かせないので、Child の pos のまま (動かさない) とする
internal enum StarRotRule { Float64, Float32 }

// 位置と回転 (四元数 w 先、長さ 1)。Apply(v) = 位置 + 回転·v
internal readonly record struct StationXf(double X, double Y, double Z, double Qw, double Qx, double Qy, double Qz)
{
    public static readonly StationXf Identity = new(0, 0, 0, 1, 0, 0, 0);

    public static StationXf Of(double x, double y, double z, (double W, double X, double Y, double Z)? rot) =>
        rot is { } q ? new(x, y, z, q.W, q.X, q.Y, q.Z) : new(x, y, z, 1, 0, 0, 0);

    // この枠の中に置いた子の枠 (子の位置 = 位置 + 回転·子の位置、子の回転 = 回転 × 子の回転)
    public StationXf Then(StationXf c)
    {
        var (x, y, z) = Rotate(c.X, c.Y, c.Z);
        var (w, qx, qy, qz) = Mul((Qw, Qx, Qy, Qz), (c.Qw, c.Qx, c.Qy, c.Qz));
        return new(X + x, Y + y, Z + z, w, qx, qy, qz);
    }

    public (double X, double Y, double Z) Apply(double x, double y, double z)
    {
        var (rx, ry, rz) = Rotate(x, y, z);
        return (X + rx, Y + ry, Z + rz);
    }

    public (double X, double Y, double Z) Apply((double X, double Y, double Z) v) => Apply(v.X, v.Y, v.Z);

    public StationXf Inverse()
    {
        var inv = new StationXf(0, 0, 0, Qw, -Qx, -Qy, -Qz);
        var (x, y, z) = inv.Rotate(X, Y, Z);
        return inv with { X = -x, Y = -y, Z = -z };
    }

    // 回転·v (v' = q v q⁻¹)
    public (double X, double Y, double Z) Rotate(double x, double y, double z)
    {
        // t = 2 (q.xyz × v)、v' = v + w t + q.xyz × t
        double tx = 2 * (Qy * z - Qz * y), ty = 2 * (Qz * x - Qx * z), tz = 2 * (Qx * y - Qy * x);
        return (x + Qw * tx + (Qy * tz - Qz * ty), y + Qw * ty + (Qz * tx - Qx * tz), z + Qw * tz + (Qx * ty - Qy * tx));
    }

    internal static (double W, double X, double Y, double Z) Mul((double W, double X, double Y, double Z) a, (double W, double X, double Y, double Z) b) =>
        (a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
         a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
         a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
         a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    // +z の回りに θ (ラジアン) 回す
    public static StationXf RotZ(double theta) => new(0, 0, 0, Math.Cos(theta / 2), 0, 0, Math.Sin(theta / 2));
}

// 星図の場所 1 つ (starmap_locations の record_name・uuid・type・system。座標は使わない)
internal sealed record StationPlace(string Record, string Uuid, string Type, string System)
{
    // ステーション・都市として地図を作る種類 (Manmade 系・LandingZone)
    public bool IsStation => Type.StartsWith("Manmade", StringComparison.Ordinal) || Type == "LandingZone" || IsHangar;
    // (2026-11-03、利用者の指示「ハンガーのエレベーターは、マイハンガー (S M L XL 等) で、ハンガーのマップを出してほしい」) プレイヤーのハンガーの中 (instanced の区域。
    // 星図の場所ではない)。record は HangarRecordPrefix + ハンガーの socpak のキー (StationHangars.RecordOf)。ステーションと同じ扱い (IsStation) で木を辿る
    public const string TypeHangar = "Hangar";
    public bool IsHangar => Type == TypeHangar;
    // 地図を作れる種類 (2026-11-01、利用者の決定): ステーション・都市 (IsStation) に、前哨基地 (Outpost・Outpost_InvalidQT)・小惑星 (Asteroid・Asteroid_ValidQT)・下の 4 種類を足したもの。
    // FindRoots はこの種類のルートを探す。まとめての取り込みの対象は IsStation のものだけ (呼ぶ側で IsStation で絞る)
    // (2026-11-01、利用者の指示「ミッションで使われるマップは全部出すべき」) PointOfInterest・Anomaly・JumpPoint・NavPoint も足した。
    // 星図の場所の表 (starmap_locations) でルートが見つかったもの: PointOfInterest 74 か所のうち 55 (RSI Event Platform 48・GlaciemRing_MissionLocation・KeegerBelt_MissionLocation・
    // BennyHenge・Vision Center・分析用 2)、Anomaly 16 のうち 6 (ジャンプポイント 5・Stanton-Pyro Jump Point Wreck Site)、JumpPoint 2 のうち 2、NavPoint 2 のうち 2 (レースのコース)
    public bool IsMapPlace => IsStation || Type is "Outpost" or "Outpost_InvalidQT" or "Asteroid" or "Asteroid_ValidQT" or "PointOfInterest" or "Anomaly" or "JumpPoint" or "NavPoint"
        || Type is TypeCave or TypeWreck or TypeCrashSite;

    // 星図に載っていない場所 (2026-11-01、利用者の決定「1 A」。StationIndex.FindRoots の UnlistedKind) の record の頭と、種類。
    // 種類は、星図の種類 (Outpost・Asteroid) に当たるものはそれを使い、無いものは下の 3 つ。種類の言葉は ExtraTypeLabels (ゲームの訳)
    public const string UnlistedPrefix = "Unlisted.";
    public const string TypeCave = "Unlisted_Cave", TypeWreck = "Unlisted_Wreck", TypeCrashSite = "Unlisted_CrashSite";
    // 星図の種類に無い種類の言葉。ゲームの訳 (translations.db の日本語) と同じ: 洞窟 = GarethWIP_CaveName「Cave／洞窟」、
    // 残骸 = hud_wreckage「Wreckage／残骸」、墜落現場 = mission_location_stanton_0136「Crash Site／墜落現場」
    public static readonly IReadOnlyDictionary<string, string> ExtraTypeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TypeCave] = "洞窟", [TypeWreck] = "残骸", [TypeCrashSite] = "墜落現場",
    };
    public bool IsUnlisted => Record.StartsWith(UnlistedPrefix, StringComparison.Ordinal);
}

// ステーションのルート (星系の木の中で、starMapRecord がその場所の uuid の Child)
internal sealed class StationRoot
{
    public required StationPlace Place { get; init; }
    // ルートの socpak (ObjectContainers の下の相対パスの小文字) と Child の entityName
    public required string Socpak { get; init; }
    public required string EntityName { get; init; }
    // 恒星から辿った socpak の並び (恒星の socpak から、ルートの親の socpak まで。入れ子の Child は親と同じ socpak の xml の中なので並びに入らない)
    public required IReadOnlyList<string> Path { get; init; }
    // 恒星の Child の深さを 0 としたルートの深さ
    public required int Depth { get; init; }
    // 恒星の Child の位置と回転 (xml の文字そのままの成分。正規化していない)
    public required (double X, double Y, double Z) StarPos { get; init; }
    public required (double W, double X, double Y, double Z) StarRotRaw { get; init; }
    // 恒星の枠 (恒星の回転の後) から見た変換。自転する天体の下なら PreSpin · Rz(θ) · PostSpin、そうでなければ PreSpin だけ (PostSpin は単位)
    public required StationXf PreSpin { get; init; }
    public required StationXf PostSpin { get; init; }
    // 自転する天体 (無ければ SpinSocpak が "")。SpinUuid はその Child の starMapRecord、SpinRotHours は planetRotationSpeed (1 回転の時間 (時間))
    public string SpinSocpak { get; init; } = "";
    public string SpinUuid { get; init; } = "";
    public double? SpinRotHours { get; init; }
    // ルートの Child の入れ子の Child (xml の Child 要素の中の ChildObjectContainers。無ければ空)。socpak の無い Child (Onyx Facility の s1a_asd_facility_001) は、
    // 中身 (asd_layout_facility_001.socpak) がこの入れ子の Child にある (2026-11-01)
    public IReadOnlyList<SocChild> Kids { get; init; } = Array.Empty<SocChild>();
    // 星図に載っていない場所 (Place.IsUnlisted) のルートの Child の guid と tags (xml の Child 要素の属性。無ければ "")。契約の場所の tag から地図を引くのに使える
    public string ChildGuid { get; init; } = "";
    public string ChildTags { get; init; } = "";
    // 星図に載っていない場所で、同じ socpak の 2 つ目からの置き場 (QV Logistics Station の環の部品など) のとき、元の星図の場所の record ("" ならそうでない)
    public string SharedRecord { get; init; } = "";
    // ルートの Child (FindRoots が辿った SocChild。同じ socpak の Child は覚えた同じものなので、規則 2 の置き場どうしは同じものを指す)。
    // 契約の行き先の地図 (GameDataExtractor の mission_map_places) で、ルートの中にある Child を辿るのに使う
    public SocChild? Node { get; init; }

    public bool Spins => SpinSocpak.Length > 0;

    // 恒星の回転 (規則ごと)。Float32: 成分を float32 に丸めてから倍精度で正規化。Float64: 倍精度のまま正規化
    public (double W, double X, double Y, double Z) StarRot(StarRotRule rule)
    {
        var (w, x, y, z) = StarRotRaw;
        if (rule == StarRotRule.Float32) { w = (float)w; x = (float)x; y = (float)y; z = (float)z; }
        var len = Math.Sqrt(w * w + x * x + y * y + z * z);
        return len > 0 ? (w / len, x / len, y / len, z / len) : (1, 0, 0, 0);
    }

    // ルートの全体の変換 (ルートの枠 → 恒星を原点とする全体の座標)。theta は自転の角度 (ラジアン。自転しないルートでは使わない)
    public StationXf GlobalXf(StarRotRule rule = StationIndex.DefaultRule, double theta = 0)
    {
        var star = StationXf.Of(StarPos.X, StarPos.Y, StarPos.Z, StarRot(rule));
        var mid = Spins ? PreSpin.Then(StationXf.RotZ(theta)).Then(PostSpin) : PreSpin;
        return star.Then(mid);
    }

    // 全体の座標 (/showlocation の座標) → ルートの枠の局所の座標
    public (double X, double Y, double Z) ToLocal((double X, double Y, double Z) global, StarRotRule rule = StationIndex.DefaultRule, double theta = 0) =>
        GlobalXf(rule, theta).Inverse().Apply(global);

    // 局所の座標 → 全体の座標
    public (double X, double Y, double Z) ToGlobal((double X, double Y, double Z) local, StarRotRule rule = StationIndex.DefaultRule, double theta = 0) =>
        GlobalXf(rule, theta).Apply(local);

    // 自転の角度のデータの式 (ラジアン。0〜2π。CurrentLocation.DataThetaAt と同じ): 2π × (unix ÷ (時間 × 3600)) mod 2π。自転しない・時間が無ければ null。
    // 時間は、アプリの星系の木 (GameDataQueryService.Detail の node.RotHours) と同じく、float の精度の最短の 10 進にしたもの (例 5.0999999 → 5.1)。
    // 補正値 (MapSpinCorrections) はその時間で求めてあるので、同じ時間で計算しないと補正が合わない
    // (Orison の実測で、5.0999999 のままだと補正後の θ が 0.69° ずれ、局所で約 63 km 離れた。5.1 なら補正値 -54.114° をそのまま再現する)
    public double? DataTheta(double unix)
    {
        if (SpinRotHours is not { } raw || !(raw > 0) || !double.IsFinite(raw)) return null;
        var h = double.Parse(((float)raw).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        var turns = unix / (h * 3600);
        return (turns - Math.Floor(turns)) * 2 * Math.PI;
    }

    // 補正を足した自転の角度 (ラジアン。0〜2π) = データの式 (DataTheta) + その天体の補正値。補正値が無ければデータの式のまま。自転しない・時間が無ければ null。
    // bodyRecord は自転する天体の星図の record 名 (例 StarMapObject.Stanton2。SpinUuid の場所の record。MapSpinCorrection.Body と同じ形)。
    // 補正値は corrections (App.Config.MapSpinCorrections) のうち Body が bodyRecord と同じで OffsetDeg が有限の値の最後の 1 つ
    // (アプリの CurrentLocation.CorrectionOf と同じ引き方。補正値は天体ごとに最新の 1 つだけを入れ替えて覚える)。角度の丸めも CurrentLocation.ThetaAt と同じ
    public double? CorrectedTheta(double unix, string bodyRecord, IReadOnlyList<MapSpinCorrection>? corrections)
    {
        if (DataTheta(unix) is not { } th) return null;
        if (corrections?.LastOrDefault(c => c.Body == bodyRecord && double.IsFinite(c.OffsetDeg)) is { } c) th += c.OffsetDeg * Math.PI / 180;
        th %= 2 * Math.PI;
        return th < 0 ? th + 2 * Math.PI : th;
    }
}

// FindRoots の結果
internal sealed class StationRootSearch
{
    public Dictionary<string, StationRoot> Roots { get; } = new(StringComparer.Ordinal);
    // 恒星の Child のあった星系の socpak
    public List<string> SystemSocpaks { get; } = new();
    // 開いた socpak の数・開けなかった socpak (p4k に無い・読めない)
    public int Opened { get; set; }
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);
    // 深さの上限で辿るのをやめた Child の数
    public int DepthCut { get; set; }
}

// p4k の中の socpak の位置表と、socpak の直下の xml の読み取り (読んだものは覚える)
internal sealed class StationIndex : IDisposable
{
    // 区画の中身 (SocpakContents.Read) を、場所の木どうしで使い回す置き場 (2026-11-04、区画の置き方の一覧を一括で作るとき (StationView3D.BuildLayoutDb) だけ入れる。
    // null なら今までどおり木ごとに読む)。同じ socpak の中身は 1 回だけ読む
    public System.Collections.Concurrent.ConcurrentDictionary<string, SocpakContents?>? SharedContents { get; set; }

    // 恒星の回転の規則の既定 (計画の照合の結果)
    public const StarRotRule DefaultRule = StarRotRule.Float32;
    // ルートを探すときの深さの上限 (恒星の Child を 0。Checkmate のルートは 2)
    public const int DefaultRootDepth = 12;

    private const string OcPrefix = "data/objectcontainers/";

    public string P4kPath { get; }
    public long P4kSize { get; }
    public long P4kWriteUtcTicks { get; }
    // socpak (ObjectContainers の下の相対パスの小文字) → ローカルヘッダの位置・圧縮後の大きさ・元の大きさ・圧縮の方式
    public Dictionary<string, (long HeaderOffset, long CompressedSize, long Size, int Method)> Entries { get; }
    // 中央ディレクトリを読んだ時間 (秒)
    public double BuildSeconds { get; }

    private readonly SafeFileHandle _handle;
    private readonly ConcurrentDictionary<string, SocpakHead?> _heads = new(StringComparer.Ordinal);

    private StationIndex(string path, SafeFileHandle handle, Dictionary<string, (long, long, long, int)> entries, double seconds)
    {
        P4kPath = path;
        _handle = handle;
        Entries = entries;
        BuildSeconds = seconds;
        var fi = new FileInfo(path);
        P4kSize = fi.Length;
        P4kWriteUtcTicks = fi.LastWriteTimeUtc.Ticks;
    }

    public void Dispose() => _handle.Dispose();

    // Data.p4k の中央ディレクトリから Data/ObjectContainers の .socpak の項目を集める (読み方は Ship3DP4kIndex.Read と同じ。ZIP64 の追加の欄も読む)
    public static StationIndex Build(string p4kPath, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dict = new Dictionary<string, (long, long, long, int)>(StringComparer.Ordinal);
        using (var fs = new FileStream(p4kPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            long len = fs.Length;
            int tailLen = (int)Math.Min(len, 65536 + 22);
            var tail = new byte[tailLen];
            fs.Position = len - tailLen;
            fs.ReadExactly(tail);
            int eocd = -1;
            for (int i = tailLen - 22; i >= 0; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new InvalidDataException("Data.p4k の中央ディレクトリの終わりが見つかりません");
            long entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
            long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
            long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
            if (entries == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
            {
                int loc = eocd - 20;
                if (loc < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(loc)) != 0x07064b50)
                    throw new InvalidDataException("Data.p4k の ZIP64 の記録が見つかりません");
                long z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8));
                var rec = new byte[56];
                fs.Position = z64;
                fs.ReadExactly(rec);
                if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x06064b50) throw new InvalidDataException("Data.p4k の ZIP64 の中央ディレクトリの終わりが読めません");
                entries = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
            }
            fs.Position = cdOffset;
            using var br = new BufferedStream(fs, 1 << 20);
            var hdr = new byte[46];
            var buf = new byte[65536 * 3];
            for (long k = 0; k < entries; k++)
            {
                if ((k & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                br.ReadExactly(hdr);
                if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x02014b50) throw new InvalidDataException($"Data.p4k の中央ディレクトリの {k + 1} 件目が読めません");
                int method = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(10));
                long csize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(20));
                long usize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(24));
                int nl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(28));
                int xl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(30));
                int cl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(32));
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(42));
                br.ReadExactly(buf.AsSpan(0, nl + xl + cl));
                // 名前が Data/ObjectContainers/…socpak のものだけ (大文字小文字・区切りは問わない)
                if (nl < OcPrefix.Length + 7) continue;
                var name = Encoding.UTF8.GetString(buf, 0, nl).Replace('\\', '/');
                if (!name.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) || !name.StartsWith(OcPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (usize == 0xFFFFFFFF || csize == 0xFFFFFFFF || offset == 0xFFFFFFFF)
                {
                    // ZIP64 の追加の欄 (id 0x0001): 0xFFFFFFFF の欄だけが、元の大きさ・圧縮後の大きさ・ローカルヘッダの位置の順に 8 バイトずつ入る
                    int p = nl, end = nl + xl;
                    while (p + 4 <= end)
                    {
                        int id = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p));
                        int sz = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 2));
                        if (id == 0x0001)
                        {
                            int q = p + 4, qe = Math.Min(end, p + 4 + sz);
                            if (usize == 0xFFFFFFFF && q + 8 <= qe) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(q)); q += 8; }
                            if (csize == 0xFFFFFFFF && q + 8 <= qe) { csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(q)); q += 8; }
                            if (offset == 0xFFFFFFFF && q + 8 <= qe) { offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(q)); }
                            break;
                        }
                        p += 4 + sz;
                    }
                }
                dict[name[OcPrefix.Length..].ToLowerInvariant()] = (offset, csize, usize, method);
            }
        }
        var handle = File.OpenHandle(p4kPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        return new StationIndex(p4kPath, handle, dict, sw.Elapsed.TotalSeconds);
    }

    // socpak を開く。無圧縮 (方式 0) は p4k の該当の範囲をそのまま ZipArchive に渡す (中の必要な項目だけを読む)。
    // deflate (方式 8) はメモリに展開する。それ以外の方式・p4k に無いものは null
    public ZipArchive? OpenSocpak(string rel)
    {
        if (!Entries.TryGetValue(Resolve(rel), out var e)) return null;
        var lh = new byte[30];
        if (RandomAccess.Read(_handle, lh, e.HeaderOffset) != 30 || BinaryPrimitives.ReadUInt32LittleEndian(lh) != 0x04034b50)
            throw new InvalidDataException($"Data.p4k の {rel} のローカルヘッダが読めません");
        long start = e.HeaderOffset + 30 + BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(28));
        switch (e.Method)
        {
            case 0:
                return new ZipArchive(new P4kWindowStream(_handle, start, e.Size), ZipArchiveMode.Read);
            case 8:
                var comp = new byte[e.CompressedSize];
                if (RandomAccess.Read(_handle, comp, start) != comp.Length) throw new InvalidDataException($"Data.p4k の {rel} が読めません");
                var ms = new MemoryStream((int)Math.Min(int.MaxValue, Math.Max(0, e.Size)));
                using (var ds = new DeflateStream(new MemoryStream(comp), CompressionMode.Decompress)) ds.CopyTo(ms);
                ms.Position = 0;
                return new ZipArchive(ms, ZipArchiveMode.Read);
            default:
                return null;
        }
    }

    private Dictionary<string, List<string>>? _byFileName;

    // Child の name (ReadContainerChildren が data/objectcontainers/ を外したもの) を、位置表のキーに直す。
    // 実データには data/ の無い "objectcontainers/pu/…" の形 (stand_platform_a_final_iasi_a.socpak の Child) と、
    // パスの無いファイル名だけの形 (stanton2b.socpak の Child "apexsandvalakkarevent.socpak") がある。
    // 前者は objectcontainers/ を外す。後者は同じファイル名の socpak が位置表に 1 つだけあればそれにする。直せなければそのまま返す
    // 規則は GameDataExtractor.DeliverableSocpaks.ResolveChildSocpak (星図の座標 BuildStarmap と共通)。ファイル名の表は要るときに 1 度だけ作る
    public string Resolve(string rel) => GameDataExtractor.DeliverableSocpaks.ResolveChildSocpak(rel, Entries.ContainsKey, () =>
    {
        if (_byFileName == null) Interlocked.CompareExchange(ref _byFileName, GameDataExtractor.DeliverableSocpaks.GroupByFileName(Entries.Keys), null);
        return _byFileName;
    });

    // socpak の中の項目の全部のバイト
    internal static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream((int)Math.Max(0, Math.Min(e.Length, int.MaxValue)));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // socpak の直下の <名前>.xml (普通の XML) から: Child の木 (GameDataExtractor.DeliverableSocpaks.ReadContainerChildren)、
    // ObjectContainer の minBounds・maxBounds、ExposedEntities の Entity の planetRotationSpeed。p4k に無い・開けない・xml が無いものは null。読んだものは覚える
    internal SocpakHead? Head(string rel) => _heads.GetOrAdd(Resolve(rel), r =>
    {
        try
        {
            using var zip = OpenSocpak(r);
            if (zip == null) return null;
            var own = r[(r.LastIndexOf('/') + 1)..];
            own = (own.EndsWith(".socpak", StringComparison.Ordinal) ? own[..^7] : own) + ".xml";
            var entry = zip.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/').ToLowerInvariant() == own);
            if (entry == null) return new SocpakHead(new List<SocChild>(), null, null, null, null);
            var bytes = ReadAll(entry);
            if (bytes.Length >= 7 && Encoding.ASCII.GetString(bytes, 0, 7) == "CryXmlB") return new SocpakHead(new List<SocChild>(), null, null, null, null);
            var root = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root;
            double? hours = null;
            var ent = root?.Descendants("Entity").FirstOrDefault(x => x.Parent?.Name == "ExposedEntities" && x.Attribute("planetRotationSpeed") != null);
            if (ent != null && double.TryParse((string?)ent.Attribute("planetRotationSpeed"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var h) && double.IsFinite(h))
                hours = h;
            return new SocpakHead(GameDataExtractor.DeliverableSocpaks.ReadContainerChildren(root), root,
                ParseVec((string?)root?.Attribute("minBounds")), ParseVec((string?)root?.Attribute("maxBounds")), hours);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException) { return null; }
    });

    internal static (double X, double Y, double Z)? ParseVec(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(',');
        if (p.Length != 3) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var st = System.Globalization.NumberStyles.Float;
        return double.TryParse(p[0], st, inv, out var x) && double.TryParse(p[1], st, inv, out var y) && double.TryParse(p[2], st, inv, out var z)
            ? (x, y, z) : null;
    }

    // 四元数の文字 "w,x,y,z" (正規化しない)。読めなければ null
    internal static (double W, double X, double Y, double Z)? ParseQuat(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(',');
        if (p.Length != 4) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var st = System.Globalization.NumberStyles.Float;
        var q = new double[4];
        for (int i = 0; i < 4; i++) if (!double.TryParse(p[i], st, inv, out q[i])) return null;
        return (q[0], q[1], q[2], q[3]);
    }

    // 正規化した四元数 (長さ 0 なら null。ReadContainerChildren と同じ扱い)
    internal static (double W, double X, double Y, double Z)? Normalize((double W, double X, double Y, double Z)? q)
    {
        if (q is not { } r) return null;
        var len = Math.Sqrt(r.W * r.W + r.X * r.X + r.Y * r.Y + r.Z * r.Z);
        return len > 0 ? (r.W / len, r.X / len, r.Y / len, r.Z / len) : null;
    }

    // socpak の .soc に、書き出しで読む部品 (床・階段・灯り。名前の語の決まり StationFloorBuilder.IsExportMesh(mesh)) があるか (読んだものは覚える)。
    // 部品の表 (StationMeshKinds) は取り込みのたびに増えるので使わない (木の形が表の中身で変わらないように。調べの担当が数えたのも同じ決まり)
    private readonly ConcurrentDictionary<string, bool> _floorParts = new(StringComparer.Ordinal);
    public bool HasFloorParts(string socpak) => _floorParts.GetOrAdd(Resolve(socpak), r => StationFloorBuilder.ScanSocMeshes(this, r).Any(StationFloorBuilder.IsExportMesh));

    // Child の部分木 (Child の socpak、入れ子の Child、socpak の直下の Child を、深さ StationTree.DefaultMaxDepth まで。同じ socpak は 1 度だけ) に、
    // 床・階段の部品のある socpak (HasFloorParts) があるか (2026-11-02、外す決まりの Child を外さない判定。StationTree.Load)
    public bool SubtreeHasFloorParts(SocChild child)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(SocChild c, int depth)
        {
            if (depth > StationTree.DefaultMaxDepth) return false;
            if (c.Socpak.Length > 0)
            {
                var r = Resolve(c.Socpak);
                if (seen.Add(r))
                {
                    if (HasFloorParts(r)) return true;
                    if (Head(r) is { } h) foreach (var k in h.Children) if (Walk(k, depth + 1)) return true;
                }
            }
            foreach (var k in c.Kids) if (Walk(k, depth + 1)) return true;
            return false;
        }
        return Walk(child, 0);
    }

    // ステーションのルートを探す。places は星図の場所 (Star と、地図を作る種類 (StationPlace.IsMapPlace) を含める)。
    // 星系の socpak (pu/system/<星系>/<名前>.socpak) の xml の中から starMapRecord が Star の Child を見つけ、そこから入れ子の Child と子の socpak の xml の Child を辿る。
    // 同じ socpak の xml の Child は 1 度だけ辿る (GameDataExtractor の BuildStarmap と同じ)。同じ場所が 2 つ以上の Child にあれば最初に辿ったもの
    public StationRootSearch FindRoots(IReadOnlyCollection<StationPlace> places, int maxDepth = DefaultRootDepth, CancellationToken ct = default)
    {
        var res = new StationRootSearch();
        var stars = places.Where(p => p.Type == "Star" && p.Uuid.Length > 0).GroupBy(p => p.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // 星図に載っていない場所のルートにする Child (同じ SocChild のもの) → (場所、Child の guid・tags、共有元の record)
        var pending = new Dictionary<SocChild, (StationPlace Place, string Guid, string Tags, string Shared)>(ReferenceEqualityComparer.Instance);
        var holders = new HashSet<string>(StringComparer.Ordinal);
        var sharedCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentSystem = "";
        var targets = places.Where(p => p.IsMapPlace && p.Uuid.Length > 0).GroupBy(p => p.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var opened = new HashSet<string>(StringComparer.Ordinal);
        SocpakHead? HeadOf(string rel)
        {
            rel = Resolve(rel);
            var h = Head(rel);
            if (opened.Add(rel)) { if (h == null) res.Missing.Add(rel); else res.Opened++; }
            return h;
        }

        // 恒星の Child の下を辿る。frame は恒星の枠 (恒星の回転の後) から見たこの Child の枠、path は辿ってきた socpak
        void Visit(SocChild node, StationXf frame, int depth, (string Socpak, string Uuid, double Hours, StationXf Pre, StationXf Post)? spin,
                   (double, double, double) starPos, (double, double, double, double) starRaw, List<string> path)
        {
            ct.ThrowIfCancellationRequested();
            var head = node.Socpak.Length > 0 ? HeadOf(node.Socpak) : null;
            if (targets.TryGetValue(node.StarMapRecord, out var place) && !res.Roots.ContainsKey(place.Record))
                res.Roots[place.Record] = new StationRoot
                {
                    Place = place, Socpak = Resolve(node.Socpak), EntityName = node.EntityName, Path = path.ToList(), Depth = depth,
                    StarPos = starPos, StarRotRaw = starRaw,
                    PreSpin = spin?.Pre ?? frame, PostSpin = spin?.Post ?? StationXf.Identity,
                    SpinSocpak = spin?.Socpak ?? "", SpinUuid = spin?.Uuid ?? "", SpinRotHours = spin?.Hours, Kids = node.Kids, Node = node,
                };
            // 星図に載っていない場所 (下の pending で決めたもの) のルート
            if (pending.TryGetValue(node, out var up) && !res.Roots.ContainsKey(up.Place.Record))
                res.Roots[up.Place.Record] = new StationRoot
                {
                    Place = up.Place, Socpak = Resolve(node.Socpak), EntityName = node.EntityName, Path = path.ToList(), Depth = depth,
                    StarPos = starPos, StarRotRaw = starRaw,
                    PreSpin = spin?.Pre ?? frame, PostSpin = spin?.Post ?? StationXf.Identity,
                    SpinSocpak = spin?.Socpak ?? "", SpinUuid = spin?.Uuid ?? "", SpinRotHours = spin?.Hours, Kids = node.Kids,
                    ChildGuid = up.Guid, ChildTags = up.Tags, SharedRecord = up.Shared, Node = node,
                };
            if (depth >= maxDepth) { res.DepthCut += node.Kids.Count + (head?.Children.Count ?? 0); return; }
            void Kid(SocChild k)
            {
                var local = StationXf.Of(k.X, k.Y, k.Z, k.Rot);
                var kf = frame.Then(local);
                var ks = spin is { } s ? s with { Post = s.Post.Then(local) } : spin;
                // 子の socpak が自転する天体 (直下の xml に planetRotationSpeed) なら、その Child の枠で自転を掛ける。下にまた天体があればそれに替える
                if (k.Socpak.Length > 0 && HeadOf(k.Socpak) is { RotationHours: { } h } && h > 0)
                {
                    var pre = (spin is { } s2 ? s2.Pre.Then(s2.Post) : frame).Then(new StationXf(k.X, k.Y, k.Z, 1, 0, 0, 0));
                    ks = (Resolve(k.Socpak), k.StarMapRecord, h, pre, StationXf.Of(0, 0, 0, k.Rot));
                }
                Visit(k, kf, depth + 1, ks, starPos, starRaw, path);
            }
            foreach (var k in node.Kids) Kid(k);
            if (head != null && expanded.Add(Resolve(node.Socpak)))
            {
                path.Add(Resolve(node.Socpak));
                // 自転する天体 (惑星・衛星) の socpak の直下の Child で、starMapRecord の無いものは、UnlistedKind で種類が決まれば 1 か所のルートにする
                var els = head.RotationHours is > 0 ? head.Root?.Elements("ChildObjectContainers").Elements("Child").ToList() : null;
                if (els != null && els.Count != head.Children.Count) els = null;
                if (head.Children.Any(c => targets.ContainsKey(c.StarMapRecord))) holders.Add(Resolve(node.Socpak));
                for (int i = 0; i < head.Children.Count; i++)
                {
                    var k = head.Children[i];
                    if (els != null && k.StarMapRecord.Length == 0 && UnlistedKind(k) is { } kind)
                    {
                        var guid = ((string?)els[i].Attribute("guid") ?? "").Trim().ToLowerInvariant();
                        var rec = StationPlace.UnlistedPrefix + (guid.Length > 0 ? guid : $"{Path.GetFileNameWithoutExtension(Resolve(node.Socpak))}.{i}");
                        pending[k] = (new StationPlace(rec, "", kind, currentSystem), guid, ((string?)els[i].Attribute("tags") ?? "").Trim(), "");
                    }
                    Kid(k);
                }
                path.RemoveAt(path.Count - 1);
            }
            else if (head != null && holders.Contains(Resolve(node.Socpak)))
            {
                // 星図の場所の Child を持つ socpak の 2 つ目からの置き場 (同じ socpak の中身は 1 度しか辿らないので、ここで、その Child だけを辿る)。
                // QV Logistics Station (Nyx_Kaboos) の環の部品 glaciemring_segment_wtn_001 は 20 か所に置かれ、どれも中の Child が同じ星図の場所を持つ (2026-11-01 の調べ)
                path.Add(Resolve(node.Socpak));
                foreach (var k in head.Children.Where(c => targets.ContainsKey(c.StarMapRecord)))
                {
                    var src = targets[k.StarMapRecord];
                    var n = sharedCount[src.Record] = sharedCount.GetValueOrDefault(src.Record) + 1;
                    var rec = StationPlace.UnlistedPrefix + src.Record.Replace("StarMapObject.", "") + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    pending[k] = (new StationPlace(rec, src.Uuid, src.Type, src.System), "", "", src.Record);
                    Kid(k);
                }
                path.RemoveAt(path.Count - 1);
            }
        }

        foreach (var rel in Entries.Keys.Where(r => r.StartsWith("pu/system/", StringComparison.Ordinal) && r.Count(c => c == '/') == 3).OrderBy(r => r, StringComparer.Ordinal))
        {
            var head = HeadOf(rel);
            if (head?.Root == null) continue;
            // 恒星の Child (xml の要素から rot の文字そのままを取る)
            foreach (var el in head.Root.Descendants("Child"))
            {
                var uuid = ((string?)el.Attribute("starMapRecord") ?? "").Trim().ToLowerInvariant();
                if (!stars.ContainsKey(uuid)) continue;
                var star = FindChild(head.Children, el);
                if (star == null) continue;
                if (!res.SystemSocpaks.Contains(rel)) res.SystemSocpaks.Add(rel);
                var raw = ParseQuat((string?)el.Attribute("rot")) ?? (1, 0, 0, 0);
                currentSystem = stars[uuid].System;
                Visit(star, StationXf.Identity, 0, null, (star.X, star.Y, star.Z), raw, new List<string> { rel });
            }
        }
        return res;
    }

    // 星図に載っていない場所の種類 (2026-11-01)。Child の socpak と、入れ子の Child の socpak と、Child の socpak の直下の Child の socpak のパスで決める。
    // 上から順に当てる: ①「_crash_」(地上の墜落船 drlct_sfce_*_crash_*) → 墜落現場、②「/cave/」→ 洞窟、
    // ③「/ug_facility/」「/ugf/」「/outpost/」「/fob/」「/prison/」→ 前哨基地 (Outpost)、④「drlct」「/derelict/」「shipgraveyard」「/hijackedships/」→ 残骸、
    // ⑤「asteroidcluster/」「asteroidfield」→ 小惑星 (Asteroid)。どれにも当たらないもの (街の背景・明かり・レースの印・空の飾りなど) は null (ルートにしない)
    private string? UnlistedKind(SocChild k)
    {
        var paths = new List<string>();
        void Add(SocChild c) { if (c.Socpak.Length > 0) paths.Add(Resolve(c.Socpak)); foreach (var x in c.Kids) Add(x); }
        Add(k);
        if (k.Socpak.Length > 0 && Head(k.Socpak) is { } h) foreach (var c in h.Children) if (c.Socpak.Length > 0) paths.Add(Resolve(c.Socpak));
        bool Any(params string[] words) => paths.Any(p => words.Any(w => p.Contains(w, StringComparison.Ordinal)));
        return Any("_crash_") ? StationPlace.TypeCrashSite
            : Any("/cave/") ? StationPlace.TypeCave
            : Any("/ug_facility/", "/ugf/", "/outpost/", "/fob/", "/prison/") ? "Outpost"
            : Any("drlct", "/derelict/", "shipgraveyard", "/hijackedships/") ? StationPlace.TypeWreck
            : Any("asteroidcluster/", "asteroidfield") ? "Asteroid"
            : null;
    }

    // xml の Child 要素に当たる SocChild (ReadContainerChildren は要素の順に作るので、同じ順に辿って探す)
    private static SocChild? FindChild(List<SocChild> list, XElement target)
    {
        SocChild? found = null;
        void Walk(XElement el, List<SocChild> kids)
        {
            int i = 0;
            foreach (var cc in el.Elements("ChildObjectContainers"))
                foreach (var c in cc.Elements("Child"))
                {
                    if (i >= kids.Count) return;
                    var sc = kids[i++];
                    if (c == target) { found = sc; return; }
                    Walk(c, sc.Kids);
                    if (found != null) return;
                }
        }
        var root = target.Ancestors().LastOrDefault();
        if (root != null) Walk(root, list);
        return found;
    }
}

// socpak の直下の xml から読んだもの
internal sealed record SocpakHead(List<SocChild> Children, XElement? Root, (double X, double Y, double Z)? MinBounds, (double X, double Y, double Z)? MaxBounds, double? RotationHours);

// p4k の中の 1 つの範囲を読むだけのストリーム (ZipArchive に渡す。位置を持たない読み方 (RandomAccess) なので、同じ p4k を並行に読んでよい)
internal sealed class P4kWindowStream : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly long _start, _length;
    private long _pos;

    public P4kWindowStream(SafeFileHandle handle, long start, long length) { _handle = handle; _start = start; _length = length; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = (int)Math.Min(buffer.Length, Math.Max(0, _length - _pos));
        if (n <= 0) return 0;
        var r = RandomAccess.Read(_handle, buffer[..n], _start + _pos);
        _pos += r;
        return r;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
        return _pos;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// インスタンス 1 つ (ルートを 0。Parent は親のインスタンスの番号、ルートは -1)。ToStation はこのインスタンスの枠 → ルートの枠 (局所)
internal sealed record StationInstance(int Index, int Parent, string Socpak, string EntityName, int Depth, StationXf ToStation,
    (double X, double Y, double Z)? MinBounds, (double X, double Y, double Z)? MaxBounds, bool Found)
{
    // 親の xml の Child 要素の guid 属性 (小文字。ルートは "")。.rmxml の SuperGUID の「Child の guid,…,Entity の CryGUID」の解決に使う (StationRouteGraph)
    public string Guid { get; init; } = "";
    // 親の xml の Child 要素の starMapRecord 属性 (星図の場所の uuid、小文字。無ければ ""。ルートはルートの場所の uuid)。区画の名前に使う (StationRouteGraph.NameSections)
    public string StarMapRecord { get; init; } = "";
    // 親の xml の Child 要素の tags 属性 (タグの GUID、小文字。無ければ空)。新しい乗り物の仕組み (sctransport*) の乗り場と行き先を結ぶのに使う (StationTree.LinkTransportSystem)
    public IReadOnlyList<string> ChildTags { get; init; } = Array.Empty<string>();
}

// 木に入れない Child の class (xml の Child の class 属性)。HarvestableObjectContainer (タグ 1875c48f Engine/Components/HarvestableComponent) は
// 出し入れされる入れ物で、Orison では holidayboxes 5 個 (orison_sp_int_holidayboxes など)、ほかに fleetweek2022 の lootables・siege の platform_*_all。
// FW22_* (FW22_SC_Ammo・FW22_SC_JustBoxes) は Fleet Week 2022 の入れ物。どちらも「常に置かれる」ものではないと見て除く (推測: class の名前と中身から)
// (2026-11-01、利用者の決定) ステーション・都市でない場所 (前哨基地・小惑星。StationPlace.IsStation が false) のルートでは HarvestableObjectContainer を外さない。
// QV Breaker Station (AsteroidClusterBase_Nyx_Rockcracker) は、ステーションの中身 (rockcrack_int_002・rockcrack_ext_001・rockcrack_asteroid_001、
// rockcrack_op_001 の下の rockcrack_op_int_001) が HarvestableObjectContainer の Child にある。ステーション・都市の木は前のまま
internal static class StationChildFilter
{
    public static bool Skip(string cls) => Skip(cls, true);
    public static bool Skip(string cls, bool isStation) =>
        isStation && cls.Equals("HarvestableObjectContainer", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("FW22_", StringComparison.OrdinalIgnoreCase);
}

// metadata の Entity 1 つ (局所の座標に直したもの)。Kind は metadata の xml のファイル名 (小文字、.xml を除く。例 entitycomponentroom)。
// Name は nameIdentifier (無ければ name)。Pos・Rot は PivotRelativePos・PivotRelativeRot (無ければ transform_ls の T・Q) を局所に直したもの (無ければ null)。
// Attrs は Entity の属性の全部 (元の文字のまま)。
// sctransitmanager は Tags に PositiveTags の Tag、NegativeTags に NegativeTags の Tag を入れる (ほかの種類の NegativeTags は空)。
// sctransitnavsplinedata は Path に ControlPoints の ControlPoint の position (Entity の PivotRelativePos・Rot を掛けてから局所に直したもの) を xml の順に入れる (ほかの種類は空)
internal sealed record StationPoint(string Kind, int Instance, string Guid, string Name, (double X, double Y, double Z)? Pos,
    (double W, double X, double Y, double Z)? Rot, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string> Attrs)
{
    public IReadOnlyList<string> NegativeTags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(double X, double Y, double Z)> Path { get; init; } = Array.Empty<(double, double, double)>();
}

// .soc の店 1 つ (Entity EntityClass="SCShop" の Name・EntityCryGUID・Pos。Pos は局所に直したもの、RawPos は socpak の枠の値)
internal sealed record StationShop(int Instance, string EntityName, string Guid, (double X, double Y, double Z) Pos, (double X, double Y, double Z) RawPos, bool FromBytes);

// .rmxml の RoomMapping の 1 行 (3 段目の要素)。A・B・C は 1〜3 段目の SuperGUID、Next は NextRoomSuperGUID (無ければ "")。意味は未確認 (StationTree の説明)
internal sealed record StationRoomRoute(int Instance, string A, string B, string C, double Distance, string Features, string Next);

// 端末 1 つ (2026-11-02、利用者の指示「端末の場所がわかる？ … マークは端末のところがいい」)。.soc の Entity の Pos・Rotate を局所に直したもの。
// Kind は StationTerminalKinds の鍵 (fleet・clinic・pharmacy・clone・shop・commodity・cargo・refinery・freight)。Basis は判定の根拠 (例 "metadata:entitycomponentshipinsuranceprovider"、"class:CloneLocationAdmin")。
// ShopGuid・ShopEntity は店の端末のとき、同じ socpak のいちばん近い SCShop (EntityCryGUID・Name)。無ければ ""
internal sealed record StationTerminal(int Instance, string Kind, string Guid, string EntityClass, string EntityName,
    (double X, double Y, double Z) Pos, (double W, double X, double Y, double Z)? Rot, string Basis)
{
    public string ShopGuid { get; init; } = "";
    public string ShopEntity { get; init; } = "";
}

// 外の設備 1 つ (2026-11-02、利用者の指示「外部の着陸パッドの場所と番号、外部の貨物エレベーター、割り当てられるハンガーの場所が立体で見たい」。3D の表示は別に作る)。
// Kind: pad (EntityClass LandingArea。NameKey は HUDDisplayName、Size は dimensions)、deck (EntityClass LoadingArea。NameKey は HUDDisplayName (@cargodeck_NN)、Size は dimensions)、
// hangar (metadata entitycomponentinstancedinteriorgateway の Entity。GatewaySize は metadata の値、Link は EntityLinks の先 (HangarDoor))、
// freight (LoadingPlatformManager の台 loadingPlatform の位置。Link は kiosk の CryGUID。Deck は台を箱の中に含む deck の NameKey (直接の参照は無いので推測。DeckGuess))
internal sealed record StationExterior(int Instance, string Kind, string Guid, string EntityClass, string EntityName,
    (double X, double Y, double Z) Pos, (double W, double X, double Y, double Z)? Rot)
{
    public string NameKey { get; init; } = "";
    public (double X, double Y, double Z)? Size { get; init; }
    public int GatewaySize { get; init; }
    public string Link { get; init; } = "";
    public string Deck { get; init; } = "";
    public bool DeckGuess { get; init; }
    public double DeckDistance { get; init; }
}

// ステーションの木 (ルートから Child を辿ったインスタンス) と、その中身
internal sealed class StationTree
{
    // 木を辿る深さの上限 (ルートを 0)
    public const int DefaultMaxDepth = 24;
    // 集める metadata の種類 (metadata の xml のファイル名)
    public static readonly string[] MetaKinds =
    [
        "entitycomponentroom", "entitycomponentroomconnector", "sctransitgateway", "sctransitperipheral", "sctransitdestination",
        "entitycomponentinteriormapsection", "entitycomponentinteriormaplabel",
        // 段階 4 (道順) で足したもの: トラム・シャトルの路線の管理 (PositiveTags・NegativeTags) と、走る道筋 (ControlPoints)
        "sctransitmanager", "sctransitnavsplinedata",
        // 路線・階が実装されているかの判定 (2026-11-01) に使うもの: 使える物の印 (医療の場所・リスポン・任務・出入りの小部屋)
        "actionareacomponent", "entitycomponentspawnpoint", "entitycomponentslotspawnpoint", "subsumptionmissionlinkcomponent", "entitycomponentspawncloset",
        // (2026-11-01) 新しい乗り物の仕組み (Area18 の電車・エレベーター)。読んだら上の sctransit* の種類に直す (SocpakContents.TransportKinds)
        "sctransportmanager", "sctransportgateway", "sctransportdestination", "sctransportnavspline", "sctransportgatewaycontrolpanel", "sctransportgatewaytimerpanel",
    ];

    // ルート。ステーション・都市でない場所では、AlignToPlaceStructure が枠を回したものに置き換える
    public required StationRoot Root { get; internal set; }
    public List<StationInstance> Instances { get; } = new();
    public List<StationPoint> Points { get; } = new();
    public List<StationShop> Shops { get; } = new();
    public List<StationRoomRoute> RoomRoutes { get; } = new();
    // 端末と外の設備 (2026-11-02。SocpakContents.ReadTerminals)
    public List<StationTerminal> Terminals { get; } = new();
    public List<StationExterior> Exteriors { get; } = new();
    // 深さの上限で辿るのをやめた Child の数・親と同じ socpak が祖先にあって辿らなかった Child の数
    public int DepthCut { get; private set; }
    public int CycleCut { get; private set; }
    // StationChildFilter で木に入れなかった Child の数と socpak
    public int ClassCut { get; private set; }
    public List<string> ClassCutNames { get; } = new();
    // (2026-11-02) 外す決まり (StationChildFilter) に当たった Child の (socpak, entityName, class): 外したもの (CutChildren) と、部分木に床・階段の部品があるので外さなかったもの (KeptChildren)
    public List<(string Socpak, string Entity, string Class)> CutChildren { get; } = new();
    public List<(string Socpak, string Entity, string Class)> KeptChildren { get; } = new();

    // インスタンス i の枠 → 全体の座標 の変換と、その逆 (全体 → インスタンスの枠)
    public StationXf InstanceToGlobal(int i, StarRotRule rule = StationIndex.DefaultRule, double theta = 0) => Root.GlobalXf(rule, theta).Then(Instances[i].ToStation);
    public StationXf GlobalToInstance(int i, StarRotRule rule = StationIndex.DefaultRule, double theta = 0) => InstanceToGlobal(i, rule, theta).Inverse();

    // ルートから辿る。contents が true なら metadata・.soc の店・.rmxml も読む (同じ socpak は 1 回だけ読み、インスタンスごとに変換を掛ける)
    public static StationTree Load(StationIndex index, StationRoot root, int maxDepth = DefaultMaxDepth, bool contents = true, CancellationToken ct = default)
    {
        var tree = new StationTree { Root = root };
        var chain = new List<string>();
        // nestedEls は nested と同じ並びの xml の Child 要素 (guid を読むため。ReadContainerChildren は Child 要素の順に SocChild を作るので、同じ順に並べる)
        void Add(int parent, string socpak, string entityName, int depth, StationXf toStation, List<SocChild> nested, List<XElement> nestedEls, string guid, string starMapRecord, string childTags = "")
        {
            ct.ThrowIfCancellationRequested();
            socpak = index.Resolve(socpak);
            var head = socpak.Length > 0 ? index.Head(socpak) : null;
            var idx = tree.Instances.Count;
            tree.Instances.Add(new StationInstance(idx, parent, socpak, entityName, depth, toStation, head?.MinBounds, head?.MaxBounds, head != null)
            {
                Guid = guid, StarMapRecord = starMapRecord,
                ChildTags = childTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant()).ToList(),
            });
            var kids = nested.Concat(head?.Children ?? Enumerable.Empty<SocChild>()).ToList();
            var kidEls = nestedEls.Concat(ChildElements(head?.Root)).ToList();
            if (kids.Count == 0) return;
            if (depth >= maxDepth) { tree.DepthCut += kids.Count; return; }
            chain.Add(socpak);
            for (int n = 0; n < kids.Count; n++)
            {
                var k = kids[n];
                var el = n < kidEls.Count && kidEls.Count == kids.Count ? kidEls[n] : null;
                if (k.Socpak.Length > 0 && chain.Contains(index.Resolve(k.Socpak), StringComparer.Ordinal)) { tree.CycleCut++; continue; }
                if (el != null && StationChildFilter.Skip((string?)el.Attribute("class") ?? "", root.Place.IsStation))
                {
                    // (2026-11-02、利用者の決定「1 OK」) 外す決まりの Child でも、部分木に床・階段の部品のある socpak があれば外さない (StationIndex.SubtreeHasFloorParts)
                    var cls = (string?)el.Attribute("class") ?? "";
                    if (index.SubtreeHasFloorParts(k))
                        tree.KeptChildren.Add((index.Resolve(k.Socpak), k.EntityName, cls));
                    else { tree.ClassCut++; tree.ClassCutNames.Add(index.Resolve(k.Socpak)); tree.CutChildren.Add((index.Resolve(k.Socpak), k.EntityName, cls)); continue; }
                }
                Add(idx, k.Socpak, k.EntityName, depth + 1, toStation.Then(StationXf.Of(k.X, k.Y, k.Z, k.Rot)), k.Kids,
                    el != null ? ChildElements(el) : new List<XElement>(), ((string?)el?.Attribute("guid") ?? "").Trim().ToLowerInvariant(), k.StarMapRecord,
                    (string?)el?.Attribute("tags") ?? "");
            }
            chain.RemoveAt(chain.Count - 1);
        }
        // socpak の無いルートは、ルートの Child の入れ子の Child を辿る (xml の要素は持っていないので、guid と class の絞り込みは無し)。
        // socpak のあるルートは前のまま (入れ子の Child を足すと、socpak の Child と xml の要素の並びが合わなくなり、class の絞り込みが効かなくなるため)
        Add(-1, root.Socpak, root.EntityName, 0, StationXf.Identity, root.Socpak.Length == 0 ? root.Kids.ToList() : new List<SocChild>(), new List<XElement>(), "", root.Place.Uuid.ToLowerInvariant());
        if (!root.Place.IsStation) { tree.KeepPlaceCluster(); tree.AlignToPlaceStructure(); }
        if (contents) { tree.LoadContents(index, ct); tree.LinkTransportSystem(); }
        return tree;
    }

    // ステーション・都市でない場所 (前哨基地・小惑星) で、星図の場所の座標 (ルートの枠の原点) から、ひとつながりのインスタンスのまとまりだけを残す (2026-11-01、利用者の決定)。
    // まとまり: ルートを除くインスタンスの原点 (ステーションの枠) を、間が PlaceClusterLink m 以内のものどうしでつなぐ。始めは原点がルートの原点にいちばん近いインスタンス。
    // 残すのは、まとまりのインスタンスと、その祖先 (ルートを含む)。番号は前の順のまま詰める。
    // QV Logistics Station (Nyx_Kaboos) は、星図の場所の Child が環の setup (glaciem_ring_wtn_setup) で、ステーション (rs_ext_ptn_st_a の下 137 個と rsi_polaris_kaboos の下 9 個) のほかに、
    // 小惑星の基地 ab_nyx_asmbl_01_dung_ptn_001 (59 個) と残骸 drlct_* (3 か所) が入っていた。
    // 値の根拠 (Nyx_Kaboos の原点の間): ステーションの中で、いちばん近いほかの原点までの間の最大は 1824 m (rs_ext_ptn_st_a の下。外の貨物エレベーター station_ext_cargo_elevator_wtn)、
    // rs_ext_ptn_st_a と rsi_polaris_kaboos の間は 344 m、ステーションからいちばん近いほかのまとまり (ab_nyx_asmbl_01_dung_ptn_001) までは 7993 m、残骸までは 8062〜9588 m。
    // 1824 m の 2 倍より大きく、7993 m の半分より小さい 4000 m にした
    public const double PlaceClusterLink = 4000;
    // 直前の KeepPlaceCluster で外したインスタンスの数 (確かめ用)
    public int PlaceClusterCut { get; private set; }

    private void KeepPlaceCluster()
    {
        if (Instances.Count <= 2) return;
        var pts = Instances.Where(i => i.Index > 0).ToDictionary(i => i.Index, i => i.ToStation.Apply((0, 0, 0)));
        static double D((double X, double Y, double Z) a, (double X, double Y, double Z) c) => Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y) + (a.Z - c.Z) * (a.Z - c.Z));
        var seed = pts.OrderBy(kv => D(kv.Value, (0, 0, 0))).ThenBy(kv => kv.Key).First().Key;
        var keep = new HashSet<int> { 0, seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        var rest = pts.Keys.Where(k => k != seed).ToList();
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            for (int n = rest.Count - 1; n >= 0; n--)
                if (D(pts[p], pts[rest[n]]) <= PlaceClusterLink) { keep.Add(rest[n]); queue.Enqueue(rest[n]); rest.RemoveAt(n); }
        }
        foreach (var k in keep.ToList()) for (int i = Instances[k].Parent; i >= 0 && keep.Add(i); i = Instances[i].Parent) { }
        _placeSeed = seed;
        if (keep.Count == Instances.Count) return;
        PlaceClusterCut = Instances.Count - keep.Count;
        var map = new Dictionary<int, int>();
        var list = new List<StationInstance>();
        foreach (var inst in Instances)
        {
            if (!keep.Contains(inst.Index)) continue;
            map[inst.Index] = list.Count;
            list.Add(inst with { Index = list.Count, Parent = inst.Parent >= 0 ? map[inst.Parent] : -1 });
        }
        Instances.Clear();
        Instances.AddRange(list);
        _placeSeed = map[seed];
    }

    // KeepPlaceCluster のまとまりの始めのインスタンス (無ければ -1)
    private int _placeSeed = -1;
    // 直前の AlignToPlaceStructure で枠を回した角度 (度。回さなければ 0。確かめ用)
    public double PlaceAlignDeg { get; private set; }

    // ステーション・都市でない場所で、地図の枠 (上下と向き) を、まとまりの始めのインスタンスの深さ 1 の祖先 (構造物の入れ物) の枠に合わせる (2026-11-01)。
    // QV Logistics Station (Nyx_Kaboos) は、ルート (環の setup) の枠に対して、ステーション rs_ext_ptn_st_a が上の向きで 10.5° 傾いて置かれていて
    // (上の向き (0.180, -0.021, 0.983))、床の板が坂になって階が作れなかった。枠を回すだけで、全体の座標は変わらない
    // (インスタンスの ToStation に回転の逆を掛け、ルートの PreSpin (自転するなら PostSpin) に回転を掛ける)。上の向きの違いが PlaceAlignMinDeg 度以下なら回さない
    public const double PlaceAlignMinDeg = 0.5;
    private void AlignToPlaceStructure()
    {
        if (_placeSeed <= 0) return;
        int top = _placeSeed;
        while (Instances[top].Parent > 0) top = Instances[top].Parent;
        var t = Instances[top].ToStation;
        var r = new StationXf(0, 0, 0, t.Qw, t.Qx, t.Qy, t.Qz);
        var up = r.Apply((0, 0, 1));
        var deg = Math.Acos(Math.Clamp(up.Z, -1, 1)) * 180 / Math.PI;
        if (deg <= PlaceAlignMinDeg) return;
        PlaceAlignDeg = deg;
        var inv = r.Inverse();
        for (int i = 0; i < Instances.Count; i++) Instances[i] = Instances[i] with { ToStation = inv.Then(Instances[i].ToStation) };
        var o = Root;
        Root = new StationRoot
        {
            Place = o.Place, Socpak = o.Socpak, EntityName = o.EntityName, Path = o.Path, Depth = o.Depth, StarPos = o.StarPos, StarRotRaw = o.StarRotRaw,
            PreSpin = o.Spins ? o.PreSpin : o.PreSpin.Then(r), PostSpin = o.Spins ? o.PostSpin.Then(r) : o.PostSpin,
            SpinSocpak = o.SpinSocpak, SpinUuid = o.SpinUuid, SpinRotHours = o.SpinRotHours, Kids = o.Kids,
        };
    }

    // 要素の ChildObjectContainers の Child 要素 (ReadContainerChildren と同じ並び)
    private static List<XElement> ChildElements(XElement? el) =>
        el == null ? new List<XElement>() : el.Elements("ChildObjectContainers").Elements("Child").ToList();

    private void LoadContents(StationIndex index, CancellationToken ct)
    {
        var cache = new Dictionary<string, SocpakContents?>(StringComparer.Ordinal);
        foreach (var inst in Instances)
        {
            ct.ThrowIfCancellationRequested();
            if (inst.Socpak.Length == 0 || !inst.Found) continue;
            if (!cache.TryGetValue(inst.Socpak, out var c))
                cache[inst.Socpak] = c = index.SharedContents is { } sc ? sc.GetOrAdd(inst.Socpak, k => SocpakContents.Read(index, k)) : SocpakContents.Read(index, inst.Socpak);
            if (c == null) continue;
            var xf = inst.ToStation;
            foreach (var m in c.Meta)
            {
                (double, double, double)? pos = m.Pos is { } p ? xf.Apply(p) : null;
                (double, double, double, double)? rot = m.Rot is { } r ? StationXf.Mul((xf.Qw, xf.Qx, xf.Qy, xf.Qz), r) : null;
                Points.Add(new StationPoint(m.Kind, inst.Index, m.Guid, m.Name, pos, rot, m.Tags, m.Attrs)
                {
                    NegativeTags = m.NegativeTags,
                    Path = m.Path.Count == 0 ? Array.Empty<(double, double, double)>() : m.Path.Select(q => xf.Apply(q)).ToList(),
                });
            }
            foreach (var s in c.Shops) Shops.Add(new StationShop(inst.Index, s.Name, s.Guid, xf.Apply(s.Pos), s.Pos, s.FromBytes));
            foreach (var r in c.Routes) RoomRoutes.Add(r with { Instance = inst.Index });
            (double, double, double, double)? Rq((double W, double X, double Y, double Z)? q) => q is { } r2 ? StationXf.Mul((xf.Qw, xf.Qx, xf.Qy, xf.Qz), r2) : (xf.Qw, xf.Qx, xf.Qy, xf.Qz);
            foreach (var t in c.Terminals) Terminals.Add(t with { Instance = inst.Index, Pos = xf.Apply(t.Pos), Rot = Rq(t.Rot) });
            foreach (var x in c.Exteriors) Exteriors.Add(x with { Instance = inst.Index, Pos = xf.Apply(x.Pos), Rot = Rq(x.Rot) });
        }
        LinkFreightDecks();
        if (Root.Place.IsHangar) SplitShipElevators();
    }

    // (2026-11-03、親の決定「船のエレベーターの台 (LoadingPlatform_ShipElevator_*) は、貨物エレベーターに入れず、船のエレベーターとして出します」) ハンガーの地図だけ:
    // 外の設備の freight (LoadingPlatformManager の台) のうち、船の着陸の場所 (pad。EntityClass LandingArea) と同じ所 (ShipElevatorJoin m 以内) にあるものを、
    // 外の設備から外し、端末の種類 shipelev (船のエレベーター) にする (地図の印・端末の一覧に出す)。
    // 実データ (2026-11-03、ハーネス): hangar_smlfrnt_001_rest_occu は LoadingPlatform_ShipElevator_HangarSmallFront と LandingArea_ShipElevator_HangarSmallFront が同じ (0, -43, 4.5)、
    // hangar_xltop_001_orison は 0.8 m (高さだけ)、hangar_xltop_001_lorville は同じ、hangar_medfrnt_002_rest_rund は 0.5 m (高さだけ)。
    // 貨物エレベーターの台 (LoadingPlatform_FreightElevator_*) は、いちばん近い pad から 20 m 以上離れていた
    public const double ShipElevatorJoin = 2;
    private void SplitShipElevators()
    {
        static double D((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        var pads = Exteriors.Where(e => e.Kind == "pad").ToList();
        for (int i = Exteriors.Count - 1; i >= 0; i--)
        {
            var f = Exteriors[i];
            if (f.Kind != "freight" || !pads.Any(p => D(p.Pos, f.Pos) <= ShipElevatorJoin)) continue;
            Exteriors.RemoveAt(i);
            Terminals.Add(new StationTerminal(f.Instance, "shipelev", f.Guid, f.EntityClass, f.EntityName, f.Pos, f.Rot, "pad+LoadingPlatform"));
        }
    }

    // 外の貨物エレベーターの台 (freight) と外部貨物の区画 (deck。LoadingArea、HUDDisplayName @cargodeck_NN) の対応 (2026-11-02)。
    // データに直接の参照は見つかっていない (調べの報告) ので、近さで当てた推測 (DeckGuess を付ける)。台の位置から中心がいちばん近い deck で、
    // 間が deck の大きさ (dimensions の大きい辺) の 2 倍以内のもの。無ければ Deck は ""。
    // 確かめた値 (2026-11-02): RR_HUR_LEO は台 12 と deck 12 が 1 つずつ、どれも約 425 m (水平 404〜407 m、2 番目に近い deck は 590 m 以上)。
    // Nyx_Levski は台 16 と deck 8 で、deck 1 つに台 2 つ、325〜340 m (2 番目は 340〜532 m で差の小さい台がある)。deck の大きさは 300 m
    private void LinkFreightDecks()
    {
        var decks = Exteriors.Where(e => e.Kind == "deck" && e.NameKey.Length > 0).ToList();
        for (int i = 0; i < Exteriors.Count; i++)
        {
            var f = Exteriors[i];
            if (f.Kind != "freight") continue;
            StationExterior? best = null; double bd = double.MaxValue;
            foreach (var d in decks)
            {
                var dist = Math.Sqrt((d.Pos.X - f.Pos.X) * (d.Pos.X - f.Pos.X) + (d.Pos.Y - f.Pos.Y) * (d.Pos.Y - f.Pos.Y) + (d.Pos.Z - f.Pos.Z) * (d.Pos.Z - f.Pos.Z));
                var lim = d.Size is { } s ? 2 * Math.Max(s.X, Math.Max(s.Y, s.Z)) : 0;
                if (dist <= lim && dist < bd) { bd = dist; best = d; }
            }
            if (best != null) Exteriors[i] = f with { Deck = best.NameKey, DeckGuess = true, DeckDistance = bd };
        }
    }

    // 新しい乗り物の仕組み (metadata の sctransport*。Area18 で使われ、New Babbage・Orison・Lorville・Checkmate・Seraphim・Kaboos には無い) の乗り場を、
    // 今の路線の作り方 (StationRouteGraph.TransitLines) で使えるようにする (2026-11-01、利用者の決まり「データで乗れる路線は乗れるようにする」)。データで確かめた結び付き:
    //   ・路線の管理 (SCTransportManager) の DNF の正のタグ = 路線のタグ (電車 A 9dec21ba・B a25cc25e、Habs のエレベーター 0e52edb7 など)。行き先 (SCTransportDestination) の Tags にこのタグがある。
    //   ・行き先の DNF の正のタグ = 乗り場のタグ。電車は乗り場の Entity の Tags (A 793660ad・B 78cafa1c)、エレベーターの乗り場 (transport_accesspoint_*) は
    //     Entity の Tags が空で、親の xml の Child の tags 属性 (例 a18_habtower_lobby の Child の 37ade105…) にある。
    //   ・乗り場の DNF の正のタグ = 操作盤 (SCTransportGatewayControlPanel・TimerPanel) の Tags のタグ。操作盤は同じインスタンスにある。
    // 結び方: 乗り場のタグ (Entity の Tags と、インスタンスの ChildTags) に DNF の正のタグが当たる行き先のうち、いちばん近い行き先の Tags を乗り場の Tags に足す
    // (行き先 1 つに乗り場が 6 つの階 (Habs のロビー) もあるので、乗り場ごとにいちばん近い行き先を選ぶ)。
    // 操作盤: 同じインスタンスに、乗り場の DNF の正のタグを Tags に持つ操作盤があれば、乗り場の Attrs に panelLinked=1 を入れる (TransitLines が装置ありとみなす)
    public void LinkTransportSystem()
    {
        static bool New(StationPoint p) => p.Attrs.TryGetValue(SocpakContents.TransportAttr, out var v) && v == "1";
        static List<string> Dnf(StationPoint p) => p.Attrs.TryGetValue(SocpakContents.DnfAttr, out var v) ? v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
        var gws = Enumerable.Range(0, Points.Count).Where(i => Points[i].Kind == "sctransitgateway" && New(Points[i]) && Points[i].Pos != null).ToList();
        if (gws.Count == 0) return;
        var dests = Points.Where(p => p.Kind == "sctransitdestination" && New(p) && p.Pos != null).ToList();
        var panels = Points.Where(p => p.Kind == "sctransitperipheral" && New(p)).ToList();
        static double D((double X, double Y, double Z) a, (double X, double Y, double Z) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
        foreach (var i in gws)
        {
            var g = Points[i];
            var own = g.Tags.Select(t => t.ToLowerInvariant()).Concat(Instances[g.Instance].ChildTags).ToHashSet(StringComparer.Ordinal);
            var near = dests.Where(d => Dnf(d).Any(own.Contains)).OrderBy(d => D(d.Pos!.Value, g.Pos!.Value)).FirstOrDefault();
            var tags = g.Tags.Concat(Instances[g.Instance].ChildTags).Concat(near?.Tags ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var attrs = new Dictionary<string, string>(g.Attrs, StringComparer.Ordinal);
            var gd = Dnf(g);
            if (panels.Any(p => p.Instance == g.Instance && p.Tags.Any(t => gd.Contains(t.ToLowerInvariant())))) attrs["panelLinked"] = "1";
            Points[i] = g with { Tags = tags, Attrs = attrs };
        }
    }
}

// socpak 1 つの中身 (socpak の枠の値のまま)
internal sealed class SocpakContents
{
    public List<(string Kind, string Guid, string Name, (double X, double Y, double Z)? Pos, (double W, double X, double Y, double Z)? Rot,
        IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string> Attrs, IReadOnlyList<string> NegativeTags,
        IReadOnlyList<(double X, double Y, double Z)> Path)> Meta { get; } = new();
    public List<(string Name, string Guid, (double X, double Y, double Z) Pos, bool FromBytes)> Shops { get; } = new();
    public List<StationRoomRoute> Routes { get; } = new();
    // 端末と外の設備 (socpak の枠の値のまま。Instance は -1)
    public List<StationTerminal> Terminals { get; } = new();
    public List<StationExterior> Exteriors { get; } = new();
    // 端末・外の設備の判定に使う metadata (2026-11-02): ファイル名 (.xml を除く) → Entity の CryGUID (小文字)。
    // entitycomponentinstancedinteriorgateway は CryGUID → (GatewaySize, OcRelativeTransform の位置・回転)
    public static readonly string[] TerminalMetaKinds = ["entitycomponentshipinsuranceprovider", "entitycomponentcommodityuiprovider", "entitycomponentshoppingprovider"];
    public const string GatewayMetaKind = "entitycomponentinstancedinteriorgateway";
    private readonly Dictionary<string, HashSet<string>> _termMeta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Size, (double X, double Y, double Z)? Pos, (double W, double X, double Y, double Z)? Rot)> _gateways = new(StringComparer.OrdinalIgnoreCase);

    // .soc のバイト列の店 (CryXmlB の Entity に Pos が無かったときだけ使う。assemble.py と同じ並び「SCShop_… | x,y,z | SCShop | GUID | GUID」)
    private static readonly Regex ShopBytesRegex = new(
        @"(SCShop_[A-Za-z0-9_\-]+)[^\x20-\x7e]+([-0-9.e]+,[-0-9.e]+,[-0-9.e]+)[^\x20-\x7e]+SCShop[^\x20-\x7e]+[0-9a-f-]{36}[^\x20-\x7e]+([0-9a-f-]{36})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // transform_ls="Q : w,x,y,z | T : x,y,z"
    private static readonly Regex TransformLsRegex = new(@"Q\s*:\s*([^|]*)\|\s*T\s*:\s*([^|""]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SocpakContents? Read(StationIndex index, string rel)
    {
        try
        {
            using var zip = index.OpenSocpak(rel);
            if (zip == null) return null;
            var c = new SocpakContents();
            var socs = new List<byte[]>();
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/').ToLowerInvariant();
                if (name.EndsWith('/')) continue;
                var file = name[(name.LastIndexOf('/') + 1)..];
                if (name.Contains("/metadata/", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)
                    && StationTree.MetaKinds.Contains(file[..^4]))
                    c.ReadMeta(file[..^4], StationIndex.ReadAll(e));
                else if (name.Contains("/metadata/", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal)
                    && (TerminalMetaKinds.Contains(file[..^4]) || file[..^4] == GatewayMetaKind))
                    c.ReadTerminalMeta(file[..^4], StationIndex.ReadAll(e));
                else if (file.EndsWith(".soc", StringComparison.Ordinal))
                {
                    var bytes = StationIndex.ReadAll(e);
                    c.ReadShops(bytes);
                    c.ReadLandingAreas(bytes);
                    socs.Add(bytes);
                }
                else if (file.EndsWith(".rmxml", StringComparison.Ordinal))
                    c.ReadRoomMapping(StationIndex.ReadAll(e));
            }
            // 端末・外の設備は、metadata (CryGUID の一覧) を全部読んでから .soc の Entity に当てる (zip の中の並びは metadata が後のことがある)
            c.ReadTerminals(socs);
            return c;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return null; }
    }

    private static XDocument? ParseAny(byte[] bytes)
    {
        try
        {
            return bytes.Length >= 7 && Encoding.ASCII.GetString(bytes, 0, 7) == "CryXmlB"
                ? CryXmlParser.ParseBinaryBytes(bytes)
                : XDocument.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    // 新しい乗り物の仕組みの種類 → 今の種類 (StationTree.LinkTransportSystem の説明)
    public static readonly IReadOnlyDictionary<string, string> TransportKinds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sctransportmanager"] = "sctransitmanager", ["sctransportgateway"] = "sctransitgateway", ["sctransportdestination"] = "sctransitdestination",
        ["sctransportnavspline"] = "sctransitnavsplinedata", ["sctransportgatewaycontrolpanel"] = "sctransitperipheral", ["sctransportgatewaytimerpanel"] = "sctransitperipheral",
    };
    // 新しい仕組みから直した点の Attrs の印 (値 "1") と、DNF の正のタグ (小文字、カンマ区切り)
    public const string TransportAttr = "transportSystem";
    public const string DnfAttr = "dnfTags";

    private void ReadMeta(string kind, byte[] bytes)
    {
        var doc = ParseAny(bytes);
        if (doc?.Root == null) return;
        if (TransportKinds.TryGetValue(kind, out var oldKind)) { ReadTransportMeta(kind, oldKind, doc); return; }
        foreach (var ent in doc.Root.Elements("Entity"))
        {
            var attrs = ent.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
            var pos = StationIndex.ParseVec((string?)ent.Attribute("PivotRelativePos"));
            var rot = StationIndex.Normalize(StationIndex.ParseQuat((string?)ent.Attribute("PivotRelativeRot")));
            if ((pos == null || rot == null) && (string?)ent.Attribute("transform_ls") is { } tl && TransformLsRegex.Match(tl) is { Success: true } m)
            {
                pos ??= StationIndex.ParseVec(m.Groups[2].Value.Trim());
                rot ??= StationIndex.Normalize(StationIndex.ParseQuat(m.Groups[1].Value.Trim()));
            }
            var tags = ent.Elements("Tags").Elements("Tag").Select(t => (string?)t.Attribute("TagId") ?? "").Where(t => t.Length > 0).ToList();
            var neg = new List<string>();
            if (kind == "sctransitmanager")
            {
                tags = ent.Elements("PositiveTags").Elements("Tags").Elements("Tag").Select(t => (string?)t.Attribute("TagId") ?? "").Where(t => t.Length > 0).ToList();
                neg = ent.Elements("NegativeTags").Elements("Tags").Elements("Tag").Select(t => (string?)t.Attribute("TagId") ?? "").Where(t => t.Length > 0).ToList();
            }
            var path = new List<(double X, double Y, double Z)>();
            if (kind == "sctransitnavsplinedata")
            {
                // ControlPoint の position は Entity の枠 (PivotRelativePos・PivotRelativeRot) の中の値として socpak の枠に直す
                // (Orison のシャトルの道筋の両端が乗り場 (sctransitgateway) に重なることで確かめた。StationRouteGraph の説明)
                var ef = StationXf.Of(pos?.X ?? 0, pos?.Y ?? 0, pos?.Z ?? 0, rot);
                foreach (var cp in ent.Elements("ControlPoints").Elements("ControlPoint"))
                    if (StationIndex.ParseVec((string?)cp.Attribute("position")) is { } p) path.Add(ef.Apply(p));
            }
            var name = (string?)ent.Attribute("nameIdentifier") ?? (string?)ent.Attribute("name") ?? "";
            Meta.Add((kind, (string?)ent.Attribute("CryGUID") ?? "", name, pos, rot, tags, attrs, neg, path));
        }
    }

    // 新しい乗り物の仕組みの metadata (位置は PivotRelativeTM="Q : w,x,y,z | T : x,y,z")。管理は DNF の正のタグを路線のタグ (Tags) に、行き先は locNameId を名前にする。
    // 道筋の ControlPoint は SplineData の下にある (Area18 の TransportNavSpline_Area18_Train_A_Central-to-Bevic は Q 0,0,0,1 (z まわり 180°) で、
    // 最後の点 (4623.4, 6533.7, -490.3) が Bevic の乗り場 (中央の乗り場から (-4623, -6534, -490)) に重なることで、w,x,y,z の順と確かめた)
    private void ReadTransportMeta(string kind, string oldKind, XDocument doc)
    {
        static List<string> TagIds(IEnumerable<XElement> els) => els.Select(t => ((string?)t.Attribute("TagId") ?? "").ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        foreach (var ent in doc.Root!.Elements("Entity"))
        {
            var attrs = ent.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
            (double X, double Y, double Z)? pos = null; (double W, double X, double Y, double Z)? rot = null;
            if ((string?)ent.Attribute("PivotRelativeTM") is { } tm && TransformLsRegex.Match(tm) is { Success: true } m)
            {
                pos = StationIndex.ParseVec(m.Groups[2].Value.Trim());
                rot = StationIndex.Normalize(StationIndex.ParseQuat(m.Groups[1].Value.Trim()));
            }
            var tags = TagIds(ent.Elements("Tags").Elements("Tag"));
            var dnfPos = TagIds(ent.Elements("DNFTags").Elements("DNFTerm").Elements("DNFPositiveTags").Elements("Tag"));
            var dnfNeg = TagIds(ent.Elements("DNFTags").Elements("DNFTerm").Elements("DNFNegativeTags").Elements("Tag"));
            attrs[TransportAttr] = "1";
            attrs[DnfAttr] = string.Join(",", dnfPos);
            var neg = new List<string>();
            if (kind == "sctransportmanager") { tags = dnfPos; neg = dnfNeg; }
            var path = new List<(double X, double Y, double Z)>();
            if (kind == "sctransportnavspline")
            {
                var ef = StationXf.Of(pos?.X ?? 0, pos?.Y ?? 0, pos?.Z ?? 0, rot);
                foreach (var cp in ent.Descendants("ControlPoint"))
                    if (StationIndex.ParseVec((string?)cp.Attribute("position")) is { } p) path.Add(ef.Apply(p));
            }
            // 番号を付ける行き先 (autoNumberedName="1"。名前の訳が「Floor %d」) は、ゲームが付ける番号の決め方をデータで確かめられていないので、名前を使わない ("")
            var name = kind == "sctransportdestination"
                ? ((string?)ent.Attribute("autoNumberedName") == "1" ? "" : (string?)ent.Attribute("locNameId") ?? "")
                : (string?)ent.Attribute("name") ?? "";
            Meta.Add((oldKind, (string?)ent.Attribute("CryGUID") ?? "", name, pos, rot, tags, attrs, neg, path));
        }
    }

    // .soc の中の CryXmlB を "CryXmlB" の位置で切り分け、"SCShop" を含むものの Entity (EntityClass="SCShop") の Name・EntityCryGUID・Pos を取る
    // (GameDataExtractor.DeliverableSocpaks.ReadSocShops と同じ切り分け方)。Pos の無い店は、バイト列の並びから取れれば使う
    private void ReadShops(byte[] bytes)
    {
        var latin1 = Encoding.Latin1.GetString(bytes);
        if (!latin1.Contains("SCShop", StringComparison.Ordinal)) return;
        var starts = new List<int>();
        for (int at = latin1.IndexOf("CryXmlB", StringComparison.Ordinal); at >= 0; at = latin1.IndexOf("CryXmlB", at + 7, StringComparison.Ordinal))
            starts.Add(at);
        var noPos = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < starts.Count; i++)
        {
            var a = starts[i];
            var z = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
            if (latin1.IndexOf("SCShop", a, z - a, StringComparison.Ordinal) < 0) continue;
            XDocument doc;
            try { doc = CryXmlParser.ParseBinaryBytes(bytes[a..z]); }
            catch { continue; }
            foreach (var ent in doc.Descendants("Entity"))
            {
                if ((string?)ent.Attribute("EntityClass") != "SCShop") continue;
                var name = (string?)ent.Attribute("Name") ?? "";
                var guid = (string?)ent.Attribute("EntityCryGUID") ?? "";
                if (StationIndex.ParseVec((string?)ent.Attribute("Pos")) is { } p) Shops.Add((name, guid, p, false));
                else noPos.Add(guid);
            }
        }
        if (noPos.Count == 0) return;
        foreach (Match m in ShopBytesRegex.Matches(latin1))
            if (noPos.Remove(m.Groups[3].Value) && StationIndex.ParseVec(m.Groups[2].Value) is { } p)
                Shops.Add((m.Groups[1].Value, m.Groups[3].Value, p, true));
    }

    // 船の発着の場所 (2026-11-01、親の決定 (b)): .soc の CryXmlB の Entity のうち EntityClass="LandingArea" (着陸の場所) を、metadata の点と同じ形で
    // Meta に足す (Kind は LandingAreaKind。Pos は Entity の Pos)。路線・階が実装されているかの判定 (StationRouteGraph.UsableKinds) に使う。
    // 実データ: Checkmate の rs_ext_pyro2_l4 に [PROC]LandingArea_Pad_SmlB など 3 つ。Seraphim・New Babbage にもあり、Orison (Vision Center を含む) には無い
    public const string LandingAreaKind = "landingarea";
    // (2026-11-01、親の決定) 貨物の積み下ろしの場所 LoadingArea・LoadingPlatform も同じ扱いにする (Kind は LandingAreaKind、attrs の EntityClass で見分ける)
    public static readonly string[] ShipAreaClasses = ["LandingArea", "LoadingArea", "LoadingPlatform"];

    private void ReadLandingAreas(byte[] bytes)
    {
        var latin1 = Encoding.Latin1.GetString(bytes);
        if (!ShipAreaClasses.Any(k => latin1.Contains(k, StringComparison.Ordinal))) return;
        var starts = new List<int>();
        for (int at = latin1.IndexOf("CryXmlB", StringComparison.Ordinal); at >= 0; at = latin1.IndexOf("CryXmlB", at + 7, StringComparison.Ordinal))
            starts.Add(at);
        for (int i = 0; i < starts.Count; i++)
        {
            var a = starts[i];
            var z = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
            if (!ShipAreaClasses.Any(k => latin1.IndexOf(k, a, z - a, StringComparison.Ordinal) >= 0)) continue;
            XDocument doc;
            try { doc = CryXmlParser.ParseBinaryBytes(bytes[a..z]); }
            catch { continue; }
            foreach (var ent in doc.Descendants("Entity"))
            {
                var cls = (string?)ent.Attribute("EntityClass") ?? "";
                if (!ShipAreaClasses.Contains(cls)) continue;
                if (StationIndex.ParseVec((string?)ent.Attribute("Pos")) is not { } p) continue;
                var attrs = new Dictionary<string, string>(StringComparer.Ordinal) { ["EntityClass"] = cls };
                Meta.Add((LandingAreaKind, (string?)ent.Attribute("EntityCryGUID") ?? "", (string?)ent.Attribute("Name") ?? "", p, null,
                    Array.Empty<string>(), attrs, Array.Empty<string>(), Array.Empty<(double, double, double)>()));
            }
        }
    }

    // 端末の判定に使う metadata (TerminalMetaKinds) の Entity の CryGUID と、ハンガーの入口 (GatewayMetaKind) の GatewaySize・OcRelativeTransform
    private void ReadTerminalMeta(string kind, byte[] bytes)
    {
        var doc = ParseAny(bytes);
        if (doc?.Root == null) return;
        foreach (var ent in doc.Root.Elements("Entity"))
        {
            var guid = ((string?)ent.Attribute("CryGUID") ?? "").Trim().ToLowerInvariant();
            if (guid.Length == 0) continue;
            if (kind == GatewayMetaKind)
            {
                int.TryParse((string?)ent.Attribute("GatewaySize"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size);
                (double, double, double)? pos = null; (double, double, double, double)? rot = null;
                if ((string?)ent.Attribute("OcRelativeTransform") is { } tm && TransformLsRegex.Match(tm) is { Success: true } m)
                {
                    pos = StationIndex.ParseVec(m.Groups[2].Value.Trim());
                    rot = StationIndex.Normalize(StationIndex.ParseQuat(m.Groups[1].Value.Trim()));
                }
                _gateways[guid] = (size, pos, rot);
                continue;
            }
            if (!_termMeta.TryGetValue(kind, out var set)) _termMeta[kind] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(guid);
        }
    }

    // 端末の種類の判定 (2026-11-02。上から順に当てる。場所の名前では決めない):
    //   ① metadata entitycomponentshipinsuranceprovider の CryGUID → fleet (艦隊マネージャー。ShipSelectorKiosk_*)
    //   ② metadata entitycomponentcommodityuiprovider の CryGUID → commodity (コモディティ。*CommodityKiosk*)
    //   ③ LoadingPlatformManager の kiosk の参照先 → freight (貨物エレベーターの端末)
    //   ④ EntityLinks の先が EntityClass「Refinery」の Entity → refinery (精製所の端末。SoftLock_Terminal_Standard_Refinery*)
    //   ⑤ EntityClass に「CloneLocationAdmin」→ clone (再生登録)、「HospitalCheckIn」→ clinic (クリニックの受付の端末)
    //   ⑥ 店の端末: EntityClass に「ShoppingKiosk」「Shop_Terminal」があるもの、または metadata entitycomponentshoppingprovider の CryGUID
    //      (EntityClass に「screen」のあるもの (精製所の画面 MASTER_screen_16x9_MiningKiosk) は除く。理由: 精製所の受付の壁の高い所 (床から約 5 m) にある画面で、
    //      人が前に立って使う端末ではない (RR_HUR_L1 の util_a_refinery_admin_lge_001 で、Pos の高さ 5.27 m、端末 SoftLock_Terminal_Standard_Refinery は 4.27 m の床の上。推測を含む))。
    //      同じ socpak のいちばん近い SCShop を店にし、その SCShop の Name に「Pharmacy」があれば pharmacy (薬局)、「Cargo_Office」があり「Rental」が無ければ cargo (貨物センター)、ほかは shop
    //      (「Rental」を除く理由: Cargo Center の socpak (cargo_office_001) には、貨物の店 SCShop_Cargo_Office と、乗り物の貸し出しの店 SCShop_Cargo_Office_Rentals
    //      (ShopKiosk_Rentals の端末) の 2 つがあり、貸し出しの端末は Cargo Center の端末ではないため。RR_HUR_LEO で確かめた)
    // Pos の無い Entity は使わない
    private static readonly string[] TerminalNeedles = ["Kiosk", "CloneLocationAdmin", "HospitalCheckIn", "Refinery", "Shop_Terminal", "LoadingPlatform", "LandingArea", "LoadingArea", "SCShop", "InstancedHangarGateway",
        // (2026-11-02) 外部のドッキングポート (EntityClass「Station_DockingTube」の Entity。SCItemDockingTube の HUDDisplayName が @dockingport_01・@ui_interactor_dockingport_01 など)
        "DockingTube"];

    private sealed record SocEnt(string Cls, string Name, string Guid, (double X, double Y, double Z)? Pos, (double W, double X, double Y, double Z)? Rot, List<string> Links, XElement E);

    private void ReadTerminals(List<byte[]> socs)
    {
        var needles = TerminalNeedles.Concat(_termMeta.Values.SelectMany(s => s)).Concat(_gateways.Keys).ToList();
        var ents = new List<SocEnt>();
        foreach (var bytes in socs)
        {
            var latin1 = Encoding.Latin1.GetString(bytes);
            if (!needles.Any(k => latin1.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
            var starts = new List<int>();
            for (int at = latin1.IndexOf("CryXmlB", StringComparison.Ordinal); at >= 0; at = latin1.IndexOf("CryXmlB", at + 7, StringComparison.Ordinal)) starts.Add(at);
            for (int i = 0; i < starts.Count; i++)
            {
                var a = starts[i];
                var z = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
                if (!needles.Any(k => latin1.IndexOf(k, a, z - a, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                XDocument doc;
                try { doc = CryXmlParser.ParseBinaryBytes(bytes[a..z]); }
                catch { continue; }
                foreach (var en in doc.Descendants("Entity"))
                    ents.Add(new SocEnt((string?)en.Attribute("EntityClass") ?? "", (string?)en.Attribute("Name") ?? "",
                        ((string?)en.Attribute("EntityCryGUID") ?? "").Trim().ToLowerInvariant(),
                        StationIndex.ParseVec((string?)en.Attribute("Pos")), StationIndex.Normalize(StationIndex.ParseQuat((string?)en.Attribute("Rotate"))),
                        en.Elements("EntityLinks").Elements("Link").Select(l => ((string?)l.Attribute("TargetCryGUID") ?? "").Trim().ToLowerInvariant()).Where(g => g.Length > 0).ToList(), en));
            }
        }
        if (ents.Count == 0) return;
        var byGuid = new Dictionary<string, SocEnt>(StringComparer.Ordinal);
        foreach (var e in ents) if (e.Guid.Length > 0) byGuid.TryAdd(e.Guid, e);
        HashSet<string> Meta(string k) => _termMeta.TryGetValue(k, out var s) ? s : new HashSet<string>();
        var fleet = Meta("entitycomponentshipinsuranceprovider");
        var commodity = Meta("entitycomponentcommodityuiprovider");
        var shopping = Meta("entitycomponentshoppingprovider");
        var refineries = ents.Where(e => e.Cls == "Refinery" && e.Guid.Length > 0).Select(e => e.Guid).ToHashSet(StringComparer.Ordinal);
        // LoadingPlatformManager の参照 (属性 loadingPlatformEntityReferences の loadingPlatform・kiosk。無ければ SCLoadingPlatformManager の子の entitySuperGUID の最後の GUID)
        var managers = new List<(SocEnt M, string Platform, string Kiosk)>();
        static string Last(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()?.ToLowerInvariant() ?? "";
        foreach (var e in ents.Where(e => e.Cls == "LoadingPlatformManager"))
        {
            var refs = e.E.Descendants("loadingPlatformEntityReferences").FirstOrDefault();
            var mgr = e.E.Elements("SCLoadingPlatformManager").FirstOrDefault();
            var platform = ((string?)refs?.Attribute("loadingPlatform") ?? "").Trim().ToLowerInvariant();
            var kiosk = ((string?)refs?.Attribute("kiosk") ?? "").Trim().ToLowerInvariant();
            if (platform.Length == 0) platform = Last((string?)mgr?.Element("LoadingPlatform")?.Attribute("entitySuperGUID"));
            if (kiosk.Length == 0) kiosk = Last((string?)mgr?.Element("Kiosk")?.Attribute("entitySuperGUID"));
            managers.Add((e, platform, kiosk));
        }
        var freightKiosks = managers.Select(m => m.Kiosk).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var shops = ents.Where(e => e.Cls == "SCShop" && e.Pos != null).ToList();
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in ents)
        {
            if (e.Pos is not { } pos || e.Guid.Length == 0 || done.Contains(e.Guid)) continue;
            string? kind = null, basis = null;
            if (fleet.Contains(e.Guid)) (kind, basis) = ("fleet", "metadata:entitycomponentshipinsuranceprovider");
            else if (commodity.Contains(e.Guid)) (kind, basis) = ("commodity", "metadata:entitycomponentcommodityuiprovider");
            else if (freightKiosks.Contains(e.Guid)) (kind, basis) = ("freight", "LoadingPlatformManager:kiosk");
            else if (e.Links.Any(refineries.Contains)) (kind, basis) = ("refinery", "EntityLinks:Refinery");
            else if (e.Cls.Contains("CloneLocationAdmin", StringComparison.OrdinalIgnoreCase)) (kind, basis) = ("clone", "class:CloneLocationAdmin");
            else if (e.Cls.Contains("HospitalCheckIn", StringComparison.OrdinalIgnoreCase)) (kind, basis) = ("clinic", "class:HospitalCheckIn");
            else if (!e.Cls.Contains("screen", StringComparison.OrdinalIgnoreCase)
                     && (e.Cls.Contains("ShoppingKiosk", StringComparison.OrdinalIgnoreCase) || e.Cls.Contains("Shop_Terminal", StringComparison.OrdinalIgnoreCase) || shopping.Contains(e.Guid)))
                (kind, basis) = ("shop", shopping.Contains(e.Guid) ? "metadata:entitycomponentshoppingprovider" : "class:ShoppingKiosk");
            if (kind == null) continue;
            done.Add(e.Guid);
            var t = new StationTerminal(-1, kind, e.Guid, e.Cls, e.Name, pos, e.Rot, basis!);
            if (kind == "shop" && shops.Count > 0)
            {
                var sh = shops.MinBy(s => Dist(s.Pos!.Value, pos))!;
                var nm = sh.Name;
                var sub = nm.Contains("Pharmacy", StringComparison.OrdinalIgnoreCase) ? "pharmacy"
                    : nm.Contains("Cargo_Office", StringComparison.OrdinalIgnoreCase) && !nm.Contains("Rental", StringComparison.OrdinalIgnoreCase) ? "cargo" : "shop";
                t = t with { Kind = sub, Basis = sub == "shop" ? basis! : basis + "+SCShop:" + nm, ShopGuid = sh.Guid, ShopEntity = nm };
            }
            Terminals.Add(t);
        }
        // 外の設備
        static (double X, double Y, double Z)? Dims(XElement en)
        {
            var d = en.Descendants("dimensions").FirstOrDefault();
            if (d == null) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture; var st = System.Globalization.NumberStyles.Float;
            return double.TryParse((string?)d.Attribute("x"), st, inv, out var x) && double.TryParse((string?)d.Attribute("y"), st, inv, out var y) && double.TryParse((string?)d.Attribute("z"), st, inv, out var z) ? (x, y, z) : null;
        }
        static string Hud(XElement en, string comp) => (string?)en.Descendants(comp).FirstOrDefault()?.Attribute("HUDDisplayName") ?? "";
        // AI 専用の印の付いた着陸パッド・ドッキングポートは外す (2026-11-02、利用者の指示「プレイヤーは使えない場所」):
        //   LandingArea は canBeUsedBy="AI"、SCItemDockingTube は CanBeUsedBy="AI" (属性の名前の大文字・小文字が部品で違うので、どちらも見る)。
        //   写しで確かめた: Stanton Gateway の保安ドック (stanton/station/sec/secdock_vert_complete.socpak) の Dock1〜3_LandingArea (@landing_pad_01〜10 が 3 組) と
        //   Station_DockingTube_vert_01〜03 (@ui_interactor_secdock_a〜c_dockingport) に付いている
        static bool AiOnly(XElement en, string comp)
        {
            var c = en.Descendants(comp).FirstOrDefault();
            var v = (string?)c?.Attribute("canBeUsedBy") ?? (string?)c?.Attribute("CanBeUsedBy");
            return string.Equals(v, "AI", StringComparison.OrdinalIgnoreCase);
        }
        var exDone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in ents)
        {
            if (e.Guid.Length == 0 || exDone.Contains(e.Guid)) continue;
            if (e.Cls == "LandingArea" && e.Pos is { } pp)
            {
                exDone.Add(e.Guid);
                if (AiOnly(e.E, "LandingArea")) continue;
                Exteriors.Add(new StationExterior(-1, "pad", e.Guid, e.Cls, e.Name, pp, e.Rot) { NameKey = Hud(e.E, "LandingArea"), Size = Dims(e.E) });
            }
            // (2026-11-02、利用者の指示「外部のドッキングポートは番号が大事」) 外部のドッキングポート: EntityClass「Station_DockingTube」の Entity。
            //   名前の鍵は SCItemDockingTube の HUDDisplayName (写しで確かめた: Stanton Gateway @dockingport_01・02 と保安ドックの @ui_interactor_secdock_a〜c_dockingport、
            //   Checkmate @ui_interactor_dockingport_01・@dockingport_02、Levski @ui_interactor_dockingport_01〜04、HUR-L1 @ui_interactor_dockingport_01・02)
            else if (e.Cls == "Station_DockingTube" && e.Pos is { } dp)
            {
                exDone.Add(e.Guid);
                if (AiOnly(e.E, "SCItemDockingTube")) continue;
                Exteriors.Add(new StationExterior(-1, "dock", e.Guid, e.Cls, e.Name, dp, e.Rot) { NameKey = Hud(e.E, "SCItemDockingTube") });
            }
            else if (e.Cls == "LoadingArea" && e.Pos is { } lp)
            {
                exDone.Add(e.Guid);
                Exteriors.Add(new StationExterior(-1, "deck", e.Guid, e.Cls, e.Name, lp, e.Rot) { NameKey = Hud(e.E, "LoadingArea"), Size = Dims(e.E) });
            }
            else if (_gateways.TryGetValue(e.Guid, out var gw) && (e.Pos ?? gw.Pos) is { } gp)
            {
                exDone.Add(e.Guid);
                Exteriors.Add(new StationExterior(-1, "hangar", e.Guid, e.Cls, e.Name, gp, e.Pos != null ? e.Rot : gw.Rot) { GatewaySize = gw.Size, Link = e.Links.FirstOrDefault() ?? "" });
            }
        }
        // metadata にだけあって .soc に Entity の無いハンガーの入口 (OcRelativeTransform の位置)
        foreach (var (guid, gw) in _gateways)
            if (!exDone.Contains(guid) && gw.Pos is { } gp2)
            {
                exDone.Add(guid);
                Exteriors.Add(new StationExterior(-1, "hangar", guid, "", "", gp2, gw.Rot) { GatewaySize = gw.Size });
            }
        foreach (var (m, platform, kiosk) in managers)
        {
            var pl = platform.Length > 0 && byGuid.TryGetValue(platform, out var p) && p.Pos != null ? p : null;
            var pos = pl?.Pos ?? m.Pos;
            if (pos is not { } fp) continue;
            Exteriors.Add(new StationExterior(-1, "freight", pl?.Guid ?? m.Guid, pl?.Cls ?? m.Cls, pl?.Name ?? m.Name, fp, pl != null ? pl.Rot : m.Rot) { Link = kiosk });
        }
    }

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    // RoomMapping: 1 段目・2 段目・3 段目の要素の SuperGUID と、3 段目の Distance・Features・NextRoomSuperGUID (要素の名前は空なので位置で読む)
    private void ReadRoomMapping(byte[] bytes)
    {
        var doc = ParseAny(bytes);
        if (doc?.Root == null) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var a in doc.Root.Elements())
            foreach (var b in a.Elements())
                foreach (var c in b.Elements())
                {
                    double.TryParse((string?)c.Attribute("Distance"), System.Globalization.NumberStyles.Float, inv, out var d);
                    Routes.Add(new StationRoomRoute(-1, (string?)a.Attribute("SuperGUID") ?? "", (string?)b.Attribute("SuperGUID") ?? "",
                        (string?)c.Attribute("SuperGUID") ?? "", d, (string?)c.Attribute("Features") ?? "", (string?)c.Attribute("NextRoomSuperGUID") ?? ""));
                }
    }
}

// 同じ地図を使いまわしている場所の判定 (2026-11-01 追加。別ウィンドウの一覧のまとまりに使う)。
// ルートの中身の鍵: ルートの socpak (小文字) と、ルートの Child の入れ子の Child (Kids) の socpak・ルートからの位置 (0.01 m に丸め)・回転 (1e-4 に丸め) を
// 入れ子の順にたどって並べたもの、と木の Child を外す決まり (StationChildFilter は IsStation で変わる) の区別。
// ルートの位置・向き (StarPos・PreSpin・PostSpin)・自転する天体・Child の entityName・starMapRecord (場所ごとに違う) は入れない。
// socpak の中身はファイルで決まるので、鍵が同じ場所は、ルートより下の木 (StationTree) が同じ socpak の並びになる (2 か所ずつの StationTree での確かめは呼ぶ側で)
internal static class StationContentKey
{
    public static string Of(StationRoot root)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(root.Place.IsStation ? "S|" : "M|").Append(root.Socpak.ToLowerInvariant());
            // place: この Child の位置・回転を鍵に入れるか。ルートに socpak が無く (中身が入れ子の Child にある。Onyx Facility の s1a_asd_facility_001 など)、
        // 入れ子の Child が 1 つだけなら、その Child の位置・回転はルートの置き方と同じ扱いにして入れない
        // (Onyx Facility 120 か所は、どれも asd_layout_facility_001.socpak 1 つで、ルートからの位置だけが数 m ずつ違った。ハーネスで確かめた)
        void Kids(IReadOnlyList<SocChild> kids, bool place)
        {
            foreach (var k in kids)
            {
                sb.Append("|<").Append(k.Socpak.ToLowerInvariant());
                if (place)
                {
                    sb.Append(System.FormattableString.Invariant($"@{k.X:0.00},{k.Y:0.00},{k.Z:0.00}"));
                    if (k.Rot is { } r) sb.Append(System.FormattableString.Invariant($"r{r.W:0.0000},{r.X:0.0000},{r.Y:0.0000},{r.Z:0.0000}"));
                }
                Kids(k.Kids, true);
                sb.Append('>');
            }
        }
        Kids(root.Kids, !(root.Socpak.Length == 0 && root.Kids.Count == 1));
        return sb.ToString();
    }
}
