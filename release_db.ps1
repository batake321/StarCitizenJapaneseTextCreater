param(
    # 手で入れる用のバックアップ (sc_japanese_backup.zip など)。渡したときだけ、同じ名前の添付を載せ直す
    [string]$BackupZip,

    # 試しのモード: 何も載せず、作る物 (<SHA-256>.gz・差分) と新しい update-manifest.json を出力先に書くだけで終わる
    [switch]$DryRun,

    # 出力先 (既定は installer-output\db-update-v<版>)
    [string]$OutDir = ""
)

# 同梱の DB だけを、同じ版のリリースに出す (アプリの版は上げない)。
#   アプリ (v2.6.4 以降) は、最新のリリースの版が自分の版と同じとき、update-manifest.json の DB (gamedata_cache.db・translations.db・
#   equipment_cache.db・view3d_layout.db) だけをインストール先と比べ、違えば「データベースの更新があります」と知らせて差分で取る (AppUpdater.CheckDbOnlyAsync)。
#   v2.6.3 以前のアプリは版の比べで止まるので、同じ版の manifest が差し替わっても何も起きない。
# やること:
#   1. csproj の Version と GitHub の最新のリリースのタグが同じことを確かめる (違えば止める)
#   2. そのリリースの update-manifest.json を取る
#   3. release.ps1 と同じ取り方・同じ確かめ (-wal が空、view3d_layout.db の版の印、--prepare-bundled-db) で DB を出力先の db に用意する
#   4. manifest の DB と SHA-256 が違う DB について、<SHA-256>.gz と、manifest の今の DB からの差分 (--make-db-delta。丸ごとの .gz より小さいときだけ) を作る
#   5. 同じ版のリリースに .gz と差分を添付し、最後に update-manifest.json を差し替える (DB 以外の files の行は変えない。deltas は前の分を引き継いで足す)
#   6. -BackupZip を渡したときは、手で入れる用の zip (ファイル名そのまま) を添付し直す
# やらないこと: ビルド・publish・インストーラー・アプリの zip・commit / push・タグとリリースの作成。publish の DB も書き換えない
# (publish の exe は --prepare-bundled-db と --make-db-delta に使うだけ)。
# 例:
#   .\release_db.ps1 -DryRun                                  (試し。installer-output\db-update-v<版> に作るだけ)
#   .\release_db.ps1                                          (DB の .gz・差分を載せ、manifest を差し替える)
#   .\release_db.ps1 -BackupZip .\db_backup\sc_japanese_backup.zip   (手で入れる用の zip も載せ直す)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$ProjectName = "StarCitizenJapaneseTextCreater"
$PublishDir = "$ProjectDir\publish"
$Repo = "batake321/StarCitizenJapaneseTextCreater"
$ManifestName = "update-manifest.json"

# ===== 関数 (ここは定義だけ。下の「本体」から呼ぶ) =====

function New-GzFile([string]$src, [string]$dst) {
    $in = [System.IO.File]::OpenRead($src)
    try {
        $out = [System.IO.File]::Create($dst)
        try {
            $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}
function Expand-GzFile([string]$src, [string]$dst) {
    $in = [System.IO.File]::OpenRead($src)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $out = [System.IO.File]::Create($dst)
            try { $gz.CopyTo($out) } finally { $out.Dispose() }
        } finally { $gz.Dispose() }
    } finally { $in.Dispose() }
}

# 新しい manifest を組み立てる (ファイルにもネットワークにも触らない)。
#   PrevManifest: 今のリリースの manifest (ConvertFrom-Json したもの)
#   DbUpdates: 新しくする DB の一覧。1 つは @{ name; size; sha256; url; gzsize; delta }。delta は $null か
#              @{ fromVersion; toVersion; from; codec; url; size; sha256 } (manifest の今の DB からの差分)
# 決まり: version・runtimes はそのまま。files は並びも中身もそのままで、DbUpdates にある DB の行だけ size・sha256・url・gzsize を替え、
#         deltas は前の分を引き継いで、差分があれば最後に足す (同じ from → to の差分が前からあれば、作り直したもので置き換える)。
#         manifest に行の無い DB は最後に足す (deltas は空)。形は release.ps1 が作る物と同じ (path・size・sha256・url・gzsize・deltas)
function New-DbUpdateManifest {
    param($PrevManifest, [object[]]$DbUpdates)
    $files = @()
    $used = @{}
    foreach ($pf in @($PrevManifest.files)) {
        $u = $null
        foreach ($cand in @($DbUpdates)) { if ([string]$cand.name -ieq [string]$pf.path) { $u = $cand } }
        if (-not $u) { $files += $pf; continue }
        $used[([string]$u.name).ToLowerInvariant()] = $true
        $deltas = @()
        if ($pf.deltas) { $deltas += @($pf.deltas) }
        if ($u.delta) {
            $deltas = @($deltas | Where-Object { -not (([string]$_.from -ieq [string]$u.delta.from) -and ([string]$_.to -ieq [string]$u.sha256)) })
            $deltas += [ordered]@{ fromVersion = [string]$u.delta.fromVersion; toVersion = [string]$u.delta.toVersion; from = ([string]$u.delta.from).ToLowerInvariant(); to = ([string]$u.sha256).ToLowerInvariant();
                                   codec = [string]$u.delta.codec; url = [string]$u.delta.url; size = [long]$u.delta.size; sha256 = ([string]$u.delta.sha256).ToLowerInvariant() }
        }
        $files += [ordered]@{ path = [string]$pf.path; size = [long]$u.size; sha256 = ([string]$u.sha256).ToLowerInvariant(); url = [string]$u.url; gzsize = [long]$u.gzsize; deltas = @($deltas) }
    }
    foreach ($u in @($DbUpdates)) {
        if ($used[([string]$u.name).ToLowerInvariant()]) { continue }
        $files += [ordered]@{ path = [string]$u.name; size = [long]$u.size; sha256 = ([string]$u.sha256).ToLowerInvariant(); url = [string]$u.url; gzsize = [long]$u.gzsize; deltas = @() }
    }
    return [ordered]@{ version = [string]$PrevManifest.version; runtimes = @($PrevManifest.runtimes); files = @($files) }
}

# manifest を JSON の文字にする (release.ps1 と同じ ConvertTo-Json -Depth 8)
function ConvertTo-ManifestJson($Manifest) {
    return ($Manifest | ConvertTo-Json -Depth 8)
}

# ===== 本体 =====

Set-Location $ProjectDir

# 1. csproj の Version と、GitHub の最新のリリースのタグ
Write-Host "=== Version ===" -ForegroundColor Cyan
$csprojText = Get-Content (Join-Path $ProjectDir "$ProjectName.csproj") -Raw -Encoding UTF8
if ($csprojText -notmatch '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+)\s*</Version>') { throw "$ProjectName.csproj から Version を読めません" }
$Version = $Matches[1]
$latestRel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "release_db.ps1" }
if ([string]$latestRel.tag_name -ne "v$Version") {
    throw "csproj の Version (v$Version) が、GitHub の最新のリリース ($($latestRel.tag_name)) と違います。DB だけの更新は、最新のリリースと同じ版のときだけ出せます"
}
$releaseId = $latestRel.id
Write-Host "  v$Version (最新のリリースと同じ。添付 $(@($latestRel.assets).Count) 件)" -ForegroundColor Green

# 出力先: 既定のフォルダは前の物を消して作り直す。-OutDir で渡したフォルダは、中身があれば消さずに止める
if (-not $OutDir) {
    $OutDir = Join-Path $ProjectDir "installer-output\db-update-v$Version"
    if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
} elseif ((Test-Path $OutDir) -and @(Get-ChildItem $OutDir -Force).Count -gt 0) {
    throw "-OutDir のフォルダに中身があります (空のフォルダか、まだ無いフォルダを渡してください): $OutDir"
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$BlobDir = Join-Path $OutDir "assets"
$StageDir = Join-Path $OutDir "db"
New-Item -ItemType Directory -Force $BlobDir | Out-Null
New-Item -ItemType Directory -Force $StageDir | Out-Null
$DownloadBase = "https://github.com/$Repo/releases/download/v$Version"

# 2. そのリリースの update-manifest.json (取った物は出力先の manifest-before.json に残す)
Write-Host "=== Current manifest ===" -ForegroundColor Cyan
$mAsset = @($latestRel.assets | Where-Object { $_.name -eq $ManifestName }) | Select-Object -First 1
if (-not $mAsset) { throw "リリース v$Version に $ManifestName がありません (自動更新の manifest の無い版には、DB だけの更新を出せません)" }
$PrevManifestPath = Join-Path $OutDir "manifest-before.json"
$prevProgress = $ProgressPreference
$ProgressPreference = "SilentlyContinue"
try { Invoke-WebRequest -Uri ([string]$mAsset.browser_download_url) -OutFile $PrevManifestPath -UseBasicParsing -Headers @{ "User-Agent" = "release_db.ps1" } } finally { $ProgressPreference = $prevProgress }
$prevManifest = Get-Content $PrevManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$prevManifest.version -ne $Version) { throw "manifest の版 ($($prevManifest.version)) がリリースの版 (v$Version) と違います" }
$prevByPath = @{}
foreach ($pf in @($prevManifest.files)) { $prevByPath[([string]$pf.path).ToLowerInvariant()] = $pf }
Write-Host "  manifest: v$($prevManifest.version) ($(@($prevManifest.files).Count) ファイル)" -ForegroundColor Green

# --prepare-bundled-db と --make-db-delta に使う exe (publish のもの。ビルドはしない)。版が csproj と同じか確かめる
$exePath = Join-Path $PublishDir "$ProjectName.exe"
$dllPath = Join-Path $PublishDir "$ProjectName.dll"
if (-not (Test-Path $exePath -PathType Leaf) -or -not (Test-Path $dllPath -PathType Leaf)) { throw "publish に $ProjectName.exe / .dll がありません ($PublishDir)。v$Version を出したときの publish が要ります" }
$dllVer = [System.Reflection.AssemblyName]::GetAssemblyName($dllPath).Version
if ("$($dllVer.Major).$($dllVer.Minor).$($dllVer.Build)" -ne $Version) { throw "publish の $ProjectName.dll の版 ($dllVer) が csproj の Version (v$Version) と違います" }
$dllRow = $prevByPath["$ProjectName.dll".ToLowerInvariant()]
if ($dllRow -and ((Get-FileHash $dllPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$dllRow.sha256).ToLowerInvariant())) {
    Write-Host "  注意: publish の $ProjectName.dll は、公開した v$Version のもの (manifest の SHA-256) と違います (公開の後にビルドし直した物)" -ForegroundColor Yellow
}

# 3. DB を用意する (release.ps1 の「1.5. Bundle DB files from WorkDir」と同じ取り方・同じ確かめ。写す先だけ publish でなく出力先の db)
Write-Host "=== Bundle DB ===" -ForegroundColor Cyan
$workDir = Join-Path $ProjectDir "AppData"
# 同梱してよい DB はこの 4 つだけ (trade_cache.db は個人データなので絶対に出さない)
$allowedDbs = @("translations.db", "gamedata_cache.db", "equipment_cache.db", "view3d_layout.db")
# gamedata_cache.db と translations.db は WAL で使う。-wal が 0 バイトでなければ止める (アプリを閉じると、閉じるときのチェックポイントで -wal が空になる)
foreach ($db in @("gamedata_cache.db", "translations.db")) {
    $wal = Join-Path $workDir "$db-wal"
    if ((Test-Path $wal) -and ((Get-Item $wal).Length -gt 0)) {
        throw "アプリを一度起動して閉じ直してから実行してください ($wal に、まだ DB に書き戻していない中身があります)"
    }
}
# view3d_layout.db は作業ディレクトリでなく、3D の置き場所 (設定の Ship3DFolder、空なら %TEMP%\StarCitizenJapaneseTextCreater\ship3d) の stations にある
$ship3dFolder = ""
$settingsPath = Join-Path $env:LOCALAPPDATA "StarCitizenJapaneseTextCreater\appsettings.json"
if (Test-Path $settingsPath) { try { $ship3dFolder = [string]((Get-Content $settingsPath -Raw | ConvertFrom-Json).Ship3DFolder) } catch { } }
if ([string]::IsNullOrWhiteSpace($ship3dFolder)) { $ship3dFolder = Join-Path ([System.IO.Path]::GetTempPath()) "StarCitizenJapaneseTextCreater\ship3d" }
$layoutDbSrc = Join-Path $ship3dFolder "stations\view3d_layout.db"
foreach ($db in $allowedDbs) {
    $src = if ($db -eq "view3d_layout.db") { $layoutDbSrc } else { Join-Path $workDir $db }
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $StageDir $db) -Force
        Write-Host "  Bundled: $db ($("{0:N1} MB" -f ((Get-Item $src).Length / 1MB)))" -ForegroundColor Green
    } else {
        Write-Host "  Skip: $db (not found: $src)" -ForegroundColor Yellow
    }
}

# view3d_layout.db の版の印が「build|」(build_manifest.id の版) で始まるかを確かめる (release.ps1 と同じ)
$layoutDbStage = Join-Path $StageDir "view3d_layout.db"
if (Test-Path $layoutDbStage) {
    $layoutText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($layoutDbStage))
    if ($layoutText -notmatch 'scjt-view3d-layoutdb-') { throw "view3d_layout.db の形の印が見つかりません ($layoutDbStage)" }
    if ($layoutText -match 'p4k\|' -or $layoutText -notmatch 'build\|') { throw "view3d_layout.db の版の印が build_manifest.id の版 (build|...) ではありません。アプリの設定タブの［マップをまとめて取り込む］で作り直してから実行してください" }
    $layoutText = $null
    Write-Host "  view3d_layout.db: 版の印 build|… を確かめました" -ForegroundColor Green
}

# gamedata_cache.db と translations.db を DELETE 方式の 1 つのファイルにする (exe の --prepare-bundled-db。release.ps1 と同じ)
$prepareDbs = @(@("gamedata_cache.db", "translations.db") | ForEach-Object { Join-Path $StageDir $_ } | Where-Object { Test-Path $_ })
if ($prepareDbs.Count -gt 0) {
    $outFile = [System.IO.Path]::GetTempFileName()
    $proc = Start-Process -FilePath $exePath -ArgumentList (@("--prepare-bundled-db") + ($prepareDbs | ForEach-Object { "`"$_`"" })) -PassThru -NoNewWindow -Wait -RedirectStandardOutput $outFile
    Get-Content $outFile -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
    Remove-Item $outFile -ErrorAction SilentlyContinue
    if ($proc.ExitCode -ne 0) { throw "同梱する DB を DELETE 方式にできませんでした (終了コード $($proc.ExitCode))" }
    foreach ($p in $prepareDbs) {
        # 見出しの 18・19 バイト目が 1/1 (DELETE などの方式。2/2 は WAL) か、ここでも確かめる
        $fs = [System.IO.File]::OpenRead($p)
        try { $hdr = New-Object byte[] 100; [void]$fs.Read($hdr, 0, 100) } finally { $fs.Dispose() }
        if ($hdr[18] -ne 1 -or $hdr[19] -ne 1) { throw "同梱する DB の見出しが DELETE 方式ではありません: $p ($($hdr[18])/$($hdr[19]))" }
        foreach ($sfx in @("-wal", "-shm", "-journal")) {
            if (Test-Path "$p$sfx") { throw "同梱する DB の横のファイルが残っています: $p$sfx" }
        }
    }
    Write-Host "  Bundled DB journal mode: DELETE (header 1/1, quick_check ok)" -ForegroundColor Green
}
# 許可した DB 以外が出力先の db に紛れていたら止める (個人データを出さない)
$strayDbs = @(Get-ChildItem $StageDir -File | Where-Object { $allowedDbs -notcontains $_.Name })
if ($strayDbs.Count -gt 0) { throw "出してはいけないファイルが $StageDir にあります: $($strayDbs.Name -join ', ')" }

# 4. manifest の DB と SHA-256 が違う DB の <SHA-256>.gz と、manifest の今の DB からの差分を作る
Write-Host "=== DB update assets ===" -ForegroundColor Cyan
$deltaDbNames = @("gamedata_cache.db", "translations.db", "equipment_cache.db", "view3d_layout.db")
$newAssets = @{}    # 載せる添付の名前 → ファイル
$dbUpdates = @()
foreach ($db in $deltaDbNames) {
    $dbPath = Join-Path $StageDir $db
    if (-not (Test-Path $dbPath)) { continue }
    $sha = (Get-FileHash $dbPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $prevEntry = $prevByPath[$db.ToLowerInvariant()]
    if ($prevEntry -and ([string]$prevEntry.sha256).ToLowerInvariant() -eq $sha) {
        Write-Host "  同じ: $db (manifest と同じ SHA-256。出しません)" -ForegroundColor DarkGray
        continue
    }
    $gzName = "$sha.gz"
    $gzPath = Join-Path $BlobDir $gzName
    New-GzFile $dbPath $gzPath
    $newAssets[$gzName] = $gzPath
    $update = @{ name = $db; size = (Get-Item $dbPath).Length; sha256 = $sha; url = "$DownloadBase/$gzName"; gzsize = (Get-Item $gzPath).Length; delta = $null }
    Write-Host ("  新しい: {0} {1:N1} MB (丸ごとの .gz {2:N1} MB)" -f $db, ($update.size / 1MB), ($update.gzsize / 1MB)) -ForegroundColor Green
    if ($prevEntry -and $prevEntry.url) {
        $tmpGz = Join-Path $OutDir "prev-$db.gz"
        $tmpDb = Join-Path $OutDir "prev-$db"
        try {
            $prevProgress2 = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
            try { Invoke-WebRequest -Uri ([string]$prevEntry.url) -OutFile $tmpGz -UseBasicParsing } finally { $ProgressPreference = $prevProgress2 }
            Expand-GzFile $tmpGz $tmpDb
            if ((Get-FileHash $tmpDb -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$prevEntry.sha256).ToLowerInvariant()) { throw "manifest の今の $db の SHA-256 が manifest と違います" }
            $patchTmp = Join-Path $OutDir "delta-$db.tmp"
            $outFile = [System.IO.Path]::GetTempFileName()
            $proc = Start-Process -FilePath $exePath -ArgumentList @("--make-db-delta", "`"$tmpDb`"", "`"$dbPath`"", "`"$patchTmp`"") -PassThru -NoNewWindow -Wait -RedirectStandardOutput $outFile
            $deltaOut = (Get-Content $outFile -Encoding UTF8) -join " "
            Remove-Item $outFile -ErrorAction SilentlyContinue
            if ($proc.ExitCode -ne 0) { throw "差分を作れませんでした: $deltaOut" }
            $codec = ($deltaOut -split ' ')[1]
            $patchSha = (Get-FileHash $patchTmp -Algorithm SHA256).Hash.ToLowerInvariant()
            $patchSize = (Get-Item $patchTmp).Length
            if ($patchSize -lt $update.gzsize) {
                $prevSha = ([string]$prevEntry.sha256).ToLowerInvariant()
                $patchName = "delta-$($prevSha.Substring(0,16))-$($sha.Substring(0,16)).patch"
                Move-Item $patchTmp (Join-Path $BlobDir $patchName) -Force
                $newAssets[$patchName] = Join-Path $BlobDir $patchName
                # 同じ版の中の DB の入れ替えなので、fromVersion・toVersion はどちらもこの版
                $update.delta = @{ fromVersion = $Version; toVersion = $Version; from = $prevSha; codec = $codec; url = "$DownloadBase/$patchName"; size = $patchSize; sha256 = $patchSha }
                Write-Host ("  差分: {0} {1:N1} MB (丸ごとの .gz {2:N1} MB)" -f $db, ($patchSize / 1MB), ($update.gzsize / 1MB)) -ForegroundColor Green
            } else {
                Remove-Item $patchTmp -Force
                Write-Host "  差分: $db は差分が丸ごとより大きいので載せません" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "  差分: $db の差分を作れませんでした ($($_.Exception.Message))。丸ごとだけを載せます" -ForegroundColor Yellow
        } finally {
            Remove-Item $tmpGz, $tmpDb -ErrorAction SilentlyContinue
        }
    }
    $dbUpdates += $update
}

# 新しい manifest (出力先の update-manifest.json)
$ManifestPath = Join-Path $OutDir $ManifestName
if ($dbUpdates.Count -gt 0) {
    $manifest = New-DbUpdateManifest -PrevManifest $prevManifest -DbUpdates $dbUpdates
    [System.IO.File]::WriteAllText($ManifestPath, (ConvertTo-ManifestJson $manifest), (New-Object System.Text.UTF8Encoding($false)))
    $newBytes = ($newAssets.Values | ForEach-Object { (Get-Item $_).Length } | Measure-Object -Sum).Sum
    Write-Host ("  新しい manifest: {0} ファイル (うち DB {1} 個を新しくする)、載せる添付 {2} 件 ({3:N1} MB)" -f @($manifest.files).Count, $dbUpdates.Count, $newAssets.Count, ($newBytes / 1MB)) -ForegroundColor Green
    Write-Host "  $ManifestPath"
} else {
    Write-Host "  manifest と違う DB はありません (manifest は差し替えません)" -ForegroundColor Yellow
}
if ($BackupZip -and -not (Test-Path $BackupZip -PathType Leaf)) { throw "-BackupZip のファイルがありません: $BackupZip" }

# 試しのモード: ここで終わる (何も載せない)
if ($DryRun) {
    Write-Host ""
    Write-Host "=== 試しのモード (何も載せていません) ===" -ForegroundColor Yellow
    foreach ($name in ($newAssets.Keys | Sort-Object)) { Write-Host "  載せる添付: $name ($("{0:N1} MB" -f ((Get-Item $newAssets[$name]).Length / 1MB)))" }
    if ($dbUpdates.Count -gt 0) { Write-Host "  差し替える manifest: $ManifestPath (今の manifest の写し: $PrevManifestPath)" }
    if ($BackupZip) { Write-Host "  載せ直す zip: $(Split-Path $BackupZip -Leaf)" }
    Write-Host "  出力先: $OutDir"
    exit 0
}
if ($dbUpdates.Count -eq 0 -and -not $BackupZip) {
    Write-Host "載せる物がありません" -ForegroundColor Yellow
    exit 0
}

# 5. 載せる (GitHub の API。資格情報の取り方は release.ps1 と同じ)
Write-Host "=== GitHub Release (v$Version) ===" -ForegroundColor Cyan
$credFile = Join-Path $env:USERPROFILE ".git-credentials"
$token = ""
if (Test-Path $credFile) {
    $line = Get-Content $credFile | Where-Object { $_ -match "github.com" } | Select-Object -First 1
    if ($line -match "https://[^:]+:([^@]+)@github.com") {
        $token = $Matches[1]
    }
}
if (-not $token) { throw "GitHub token not found in .git-credentials. 何も載せていません (作った物は $OutDir にあります)" }

# 今の添付 (名前 → 添付)。同じ名前の添付は 1 つのリリースに 1 つしか置けないので、載せ直すときは先に消す
$existing = @{}
foreach ($a in @($latestRel.assets)) { $existing[[string]$a.name] = $a }
# GitHub のリリースの添付は 1 つのリリースに 1000 件まで
if (($existing.Count + $newAssets.Count + 2) -gt 1000) { throw "添付が多すぎます (今 $($existing.Count) 件 + 載せる $($newAssets.Count) 件)。1 つのリリースに 1000 件までです" }

# 添付を消す: DELETE /repos/{owner}/{repo}/releases/assets/{asset_id} (添付の url がこの形。成功は 204)
function Remove-ReleaseAsset($asset) {
    Invoke-RestMethod -Uri ([string]$asset.url) -Method Delete -Headers @{ "Authorization" = "token $token" } | Out-Null
}
# 添付を載せる: POST https://uploads.github.com/repos/{owner}/{repo}/releases/{release_id}/assets?name=… (release.ps1 と同じ呼び方)。載せた添付 (id・url など) を返す
function Send-ReleaseAsset([string]$name, [string]$path, [string]$contentType) {
    $upUrl = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=$name"
    return Invoke-RestMethod -Uri $upUrl -Method Post -Headers @{ "Authorization" = "token $token"; "Content-Type" = $contentType } -InFile $path
}
# 添付の名前を変える: PATCH /repos/{owner}/{repo}/releases/assets/{asset_id} に {"name":"…"}
function Rename-ReleaseAsset([string]$assetId, [string]$newName) {
    $bodyJson = @{ name = $newName } | ConvertTo-Json
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/assets/$assetId" -Method Patch -Headers @{ "Authorization" = "token $token"; "Content-Type" = "application/json" } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyJson)) -ContentType "application/json; charset=utf-8" | Out-Null
}

# 5.1. <SHA-256>.gz と DB の差分を先に載せる (manifest の URL の先が先にそろうように)。
# 同じ名前で同じ中身 (添付の digest が同じ) の物がもう載っていれば載せ直さない (途中で止まった後のやり直し)。中身が違う・分からないときは消してから載せる
$n = 0
foreach ($name in ($newAssets.Keys | Sort-Object)) {
    $n++
    $old = $existing[$name]
    if ($old) {
        $localSha = (Get-FileHash $newAssets[$name] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([string]$old.digest -eq "sha256:$localSha") {
            Write-Host "  [$n/$($newAssets.Count)] $name (もう載っています)" -ForegroundColor DarkGray
            continue
        }
        Remove-ReleaseAsset $old
    }
    Send-ReleaseAsset $name $newAssets[$name] "application/octet-stream" | Out-Null
    Write-Host "  [$n/$($newAssets.Count)] $name" -ForegroundColor Green
}

# 5.2. 手で入れる用の zip (渡したときだけ。ファイル名そのまま)
if ($BackupZip) {
    $zipName = Split-Path $BackupZip -Leaf
    if ($existing[$zipName]) { Remove-ReleaseAsset $existing[$zipName] }
    Send-ReleaseAsset $zipName $BackupZip "application/zip" | Out-Null
    Write-Host "Backup asset uploaded: $zipName" -ForegroundColor Green
}

# 5.3. 最後に manifest を差し替える。リリースに update-manifest.json が無い時間を短くするため、次の順で行う
# (manifest が無い間に v2.6.3 以前のアプリが確かめると、「差分の更新の情報が無いため、手で入れてください」の窓が出て、その版を見送りとして覚えてしまう):
#   (a) 新しい manifest を別の名前 (update-manifest.new.json) で載せる … ここまではリリースは前のまま
#   (b) 前の manifest を消す
#   (c) 新しい方の名前を update-manifest.json に変える … manifest が無いのは (b) と (c) の間だけ
# 途中で失敗したら、どの状態で止まったかと、手で直す方法を出して止まる
if ($dbUpdates.Count -gt 0) {
    $tmpManifestName = "update-manifest.new.json"
    $releasePage = "https://github.com/$Repo/releases/tag/v$Version"
    # 前のやり直しの残り (別の名前のまま残った manifest) があれば先に消す
    if ($existing[$tmpManifestName]) { Remove-ReleaseAsset $existing[$tmpManifestName] }

    # (a)
    $tmpAsset = $null
    try {
        $tmpAsset = Send-ReleaseAsset $tmpManifestName $ManifestPath "application/json"
    } catch {
        throw ("(a) 新しい manifest を載せられませんでした ($($_.Exception.Message))。リリース v$Version の $ManifestName は前のままです (.gz と差分は載っていますが、前の manifest からは使われません)。" +
               "もう一度このスクリプトを走らせればやり直せます")
    }
    if (-not $tmpAsset -or -not $tmpAsset.id) {
        throw ("(a) 新しい manifest を $tmpManifestName の名前で載せましたが、添付の番号 (id) が返りませんでした。リリース v$Version の $ManifestName は前のままです。" +
               "リリースのページ ($releasePage) で $tmpManifestName を消してから、もう一度このスクリプトを走らせてください")
    }

    # (b)
    try {
        Remove-ReleaseAsset $mAsset
    } catch {
        throw ("(b) 前の manifest を消せませんでした ($($_.Exception.Message))。新しい manifest は $tmpManifestName の名前で載っています。" +
               "リリースのページ ($releasePage) で $ManifestName が残っているかを見てください。残っていれば前のままなので、もう一度このスクリプトを走らせればやり直せます ($tmpManifestName は次の実行で消します)。" +
               "$ManifestName が消えていれば、$ManifestPath を $ManifestName の名前で添付し、$tmpManifestName を消してください")
    }

    # (c) 名前を変える (3 回まで)。だめなら、新しい manifest を update-manifest.json の名前で載せ直す (release.ps1 と同じ呼び方)
    $renamed = $false
    $lastError = ""
    for ($try = 1; $try -le 3 -and -not $renamed; $try++) {
        try {
            Rename-ReleaseAsset ([string]$tmpAsset.id) $ManifestName
            $renamed = $true
        } catch {
            $lastError = $_.Exception.Message
            Write-Host "  manifest の名前を変えられませんでした ($try 回目): $lastError" -ForegroundColor Yellow
            if ($try -lt 3) { Start-Sleep -Seconds 2 }
        }
    }
    if (-not $renamed) {
        try {
            Send-ReleaseAsset $ManifestName $ManifestPath "application/json" | Out-Null
            $renamed = $true
            Write-Host "  名前を変える代わりに、$ManifestName の名前で載せ直しました" -ForegroundColor Yellow
            try { Remove-ReleaseAsset $tmpAsset } catch { Write-Host "  $tmpManifestName を消せませんでした (残っていても害はありません。次の実行で消します): $($_.Exception.Message)" -ForegroundColor Yellow }
        } catch {
            throw ("(c) 前の manifest を消した後、新しい manifest の名前を $ManifestName に変えられず ($lastError)、載せ直しもできませんでした ($($_.Exception.Message))。" +
                   "今、リリース v$Version には $ManifestName がありません (新しい manifest は $tmpManifestName の名前で載っています)。" +
                   "すぐに、リリースのページ ($releasePage) で $ManifestPath を $ManifestName の名前で添付してください (その後 $tmpManifestName は消してください)。" +
                   "元に戻すなら、$PrevManifestPath を $ManifestName の名前に変えて添付してください")
        }
    }
    Write-Host "Update manifest replaced" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Release: $($latestRel.html_url)"
if ($dbUpdates.Count -gt 0) {
    Write-Host "新しくした DB: $(($dbUpdates | ForEach-Object { $_.name }) -join ', ')"
    Write-Host "v$Version のアプリは、次に更新を確かめるとき (起動の後・毎日の時刻・［更新を確認］) に「データベースの更新があります」と知らせます"
}
