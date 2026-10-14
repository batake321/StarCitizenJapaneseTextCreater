#define MyAppName "Star Citizen Japanese Text Creator"
#define MyAppExeName "StarCitizenJapaneseTextCreater.exe"
#define MyAppPublisher "batake321"
#define MyAppURL "https://github.com/batake321/StarCitizenJapaneseTextCreater"

; Version is passed via /DMyAppVersion=x.y.z from build script
#ifndef MyAppVersion
  #define MyAppVersion "1.16.0"
#endif

; .NET 10 Desktop Runtime (x64)。無ければインストーラーがダウンロードして入れる。
; URL は Microsoft 公式のリリース情報 https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
; (10.0.12 の windowsdesktop / win-x64 / windowsdesktop-runtime-win-x64.exe) のもの。
; そこに載っている SHA-512 と一致することを確かめたファイルから求めた SHA-256 を、ダウンロード時の確認に使う。
; release.ps1 はリリースのたびに最新の版を確かめて /DDotNetVersion= /DDotNetUrl= /DDotNetSha256= で渡す。下は /D が無いときの値
#ifndef DotNetVersion
  #define DotNetVersion "10.0.12"
#endif
#define DotNetFileName "windowsdesktop-runtime-" + DotNetVersion + "-win-x64.exe"
#ifndef DotNetUrl
  #define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/" + DotNetVersion + "/" + DotNetFileName
#endif
#ifndef DotNetSha256
  #define DotNetSha256 "55a67d8476cde95a9cc43a95803b4f54446e7c9251ed22aa6421d4908174ae84"
#endif
#define DotNetPageUrl "https://dotnet.microsoft.com/download/dotnet/10.0/runtime"

[Setup]
AppId={{B8E3F2A1-5C4D-4E6F-9A7B-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer-output
OutputBaseFilename=SCJPTextCreator-v{#MyAppVersion}-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; インストール先のファイルを使っているアプリを、準備のページで閉じる (既定値 yes を明記)
CloseApplications=yes

; Signing (uncomment when you have a code signing certificate)
; SignTool=signtool sign /f "$path_to_cert.pfx" /p $password /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; インストール先 ({app}) にだけ置く。作業ディレクトリや %LOCALAPPDATA% にはファイルを置かない。
; 同梱 DB (translations.db / gamedata_cache.db / equipment_cache.db) は {app} に置き、
; 作業ディレクトリへの反映はアプリ起動時の処理 (同梱 DB が替わった・作業ディレクトリに無い・使えないときに、
; 同梱 DB を土台に作り直し、今までの DB から利用者の入力 (手動訳・用語集・チャットの記憶など) を引き継ぐ) に任せる。
; trade_cache.db (と作業用の -wal / -shm / -journal) は個人データなので、publish に紛れていても入れない
Source: "publish\*"; DestDir: "{app}"; Excludes: "trade_cache.db,trade_cache.db-wal,trade_cache.db-shm,trade_cache.db-journal"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // アプリの既定値 (App.OnStartup: WorkingDirectory が空なら C:\temp)
  DefaultWorkDir = 'C:\temp';
  WorkDirKey = 'WorkingDirectory';

var
  WorkDirPage: TInputDirWizardPage;
  UserConfigExists: Boolean;
  UserCfgPath: String;
  UserCfgKnown: Boolean;
  JsonDecodeOk: Boolean;
  JsonBadComment: Boolean;
  CaseTablesReady: Boolean;
  CaseLower, CaseUpper, CaseSupLower, CaseSupUpper: String;
  InitialWorkDir: String;
  DownloadPage: TDownloadWizardPage;
  NeedDotNet: Boolean;
  DotNetDownloaded: Boolean;
  DotNetNeedsRestart: Boolean;

// ---------------------------------------------------------------
// .NET 10 Desktop Runtime (x64) の判定と、無いときの自動インストール
// ---------------------------------------------------------------

// FileExists はワイルドカードを扱わないので、FindFirst で 10.0.* のフォルダを探す
function IsDotNet10DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// 自動で入れられなかったとき: ダウンロードページを開くかを尋ね、インストールは続ける。
// SuppressibleMsgBox なので /SUPPRESSMSGBOXES では「いいえ」として進み、無人インストールは止まらない
procedure AskOpenDotNetDownloadPage(const Reason: String);
var
  ResultCode: Integer;
begin
  Log('.NET 10 Desktop Runtime を自動で入れられませんでした: ' + Reason);
  if SuppressibleMsgBox('.NET 10 Desktop Runtime を自動で入れられませんでした (' + Reason + ')。' + #13#10 +
    'ダウンロードページを開きますか？', mbConfirmation, MB_YESNO, IDNO) = IDYES then
    ShellExec('open', '{#DotNetPageUrl}', '', '', SW_SHOW, ewNoWait, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  NeedDotNet := not IsDotNet10DesktopInstalled();
  if NeedDotNet then
    Log('.NET 10 Desktop Runtime が見つかりません。インストールの前にダウンロードして入れます');
end;

// [準備完了] の次へで、.NET 10 Desktop Runtime をダウンロードする ({tmp} へ。SHA-256 を確かめる)
procedure DownloadDotNetIfNeeded();
begin
  DotNetDownloaded := False;
  if not NeedDotNet then Exit;
  try
    if WizardSilent then
      DownloadTemporaryFile('{#DotNetUrl}', '{#DotNetFileName}', '{#DotNetSha256}', nil)
    else
    begin
      DownloadPage.Clear;
      DownloadPage.Add('{#DotNetUrl}', '{#DotNetFileName}', '{#DotNetSha256}');
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
    end;
    DotNetDownloaded := True;
  except
    // 利用者が [中止] を押したときは、公式サンプル (Examples\CodeDownloadFiles.iss) と同じくログに残すだけにする
    // (失敗の知らせは出さない)。インストールはそのまま続ける
    if (not WizardSilent) and DownloadPage.AbortedByUser then
      Log('.NET 10 Desktop Runtime のダウンロードは利用者が中止しました')
    else
      AskOpenDotNetDownloadPage('ダウンロードに失敗: ' + GetExceptionMessage);
  end;
end;

// インストールの直前に、ダウンロードしたランタイムのインストーラーを静かに動かす。
// 引数と終了コードは Microsoft の文書 (Install .NET on Windows の Command-line options) のとおり:
// /install /quiet /norestart、0 = 成功、3010 = 成功 (再起動が必要)、それ以外 = 失敗
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not (NeedDotNet and DotNetDownloaded) then Exit;
  Log('.NET 10 Desktop Runtime をインストールします');
  if not Exec(ExpandConstant('{tmp}\{#DotNetFileName}'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    AskOpenDotNetDownloadPage('インストーラーを起動できません: ' + SysErrorMessage(ResultCode));
    Exit;
  end;
  Log('.NET 10 Desktop Runtime のインストーラーの終了コード: ' + IntToStr(ResultCode));
  if ResultCode = 3010 then
    DotNetNeedsRestart := True
  else if ResultCode <> 0 then
    AskOpenDotNetDownloadPage('インストールに失敗: 終了コード ' + IntToStr(ResultCode));
end;

// ランタイムのインストーラーが再起動を求めた (3010) ときは、完了時に再起動を尋ねる
function NeedRestart(): Boolean;
begin
  Result := DotNetNeedsRestart;
end;

// ---------------------------------------------------------------
// appsettings.json の読み書き
// 最上位のオブジェクトの "WorkingDirectory" の値だけを差し替え、ほかの部分は 1 文字も変えない
// ---------------------------------------------------------------
// 作業ディレクトリを書くユーザーの設定ファイル。InitializeWizard で決める (UserCfgKnown が False なら分からない)
function UserConfigPath(): String;
begin
  Result := UserCfgPath;
end;

// PowerShell の単一引用符の文字列に入れるため、' を '' にする
function PsSingleQuoted(const S: String): String;
var
  T: String;
begin
  T := S;
  StringChangeEx(T, '''', '''''', True);
  Result := T;
end;

// Setup を最初に起動したユーザー (元のユーザー) の %LOCALAPPDATA% を調べる。
// ExecAsOriginalUser で Windows 標準の PowerShell (powershell.exe) を元のユーザーとして動かし、
// [Environment]::GetFolderPath('LocalApplicationData') を HandOffDir のファイルに UTF-8 (BOM なし) で書かせて読む。
// パスは .NET から直接ファイルに書くので、cmd の解釈 (& や ^ など) を通らない。
// HandOffDir には、元のユーザーが書けて Setup が読める場所 ({commonappdata} = ProgramData) を渡す。
// ProgramData の権限は、Users はファイルを作れるが他人のファイルは読むだけ (書き換え・削除はできない)、作った本人と Administrators は全権。
// ほかのユーザーの書いたファイルを読まないように:
//   - 名前は {tmp} (Setup ごとに作られる名前) ・日時・乱数から作った SHA-256 で、推測しにくくする
//   - Setup は実行前にそのファイルが無いことを確かめ、PowerShell は FileMode.CreateNew (既にあれば失敗) で作る。
//     失敗したら終了コードが 0 にならないので読まない。こうして読むファイルは、元のユーザーが今作ったものに限られる
// 制約: ヘルプ (runasoriginaluser) のとおり、Setup を「管理者として実行」や昇格済みのプロセスから起動した場合は
// 元のユーザーに戻れず、昇格したアカウントの %LOCALAPPDATA% になる
function GetOriginalUserLocalAppData(const HandOffDir: String; var Dir: String): Boolean;
var
  F, Cmd: String;
  Raw: AnsiString;
  ResultCode: Integer;
begin
  Result := False;
  Dir := '';
  F := AddBackslash(HandOffDir) + 'scjtc_' + GetSHA256OfUnicodeString(ExpandConstant('{tmp}') + '|' +
    GetDateTimeString('yyyymmddhhnnss', #0, #0) + '|' + IntToStr(Random(2147483647)) + '|' +
    IntToStr(Random(2147483647)) + '|' + IntToStr(Random(2147483647))) + '.txt';
  if FileExists(F) then Exit;
  // PowerShell の単一引用符の文字列の中では ' を '' と書く
  Cmd := 'try { $p = [Environment]::GetFolderPath(''LocalApplicationData''); ' +
    '$b = (New-Object System.Text.UTF8Encoding($false)).GetBytes($p); ' +
    '$fs = [System.IO.File]::Open(''' + PsSingleQuoted(F) + ''', [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None); ' +
    'try { $fs.Write($b, 0, $b.Length) } finally { $fs.Dispose() } } catch { exit 1 }';
  try
    try
      if not ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Cmd + '"', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        Log('元のユーザーとして PowerShell を起動できませんでした: ' + SysErrorMessage(ResultCode));
        Exit;
      end;
      if ResultCode <> 0 then
      begin
        Log('元のユーザーの %LOCALAPPDATA% を書き出せませんでした (終了コード ' + IntToStr(ResultCode) + ')');
        Exit;
      end;
      if not LoadStringFromFile(F, Raw) then
      begin
        Log('元のユーザーの %LOCALAPPDATA% を読めませんでした (ファイルが無い): ' + F);
        Exit;
      end;
      Dir := Trim(Utf8Decode(Raw));
      if (Dir = '') or not DirExists(Dir) then
      begin
        Log('元のユーザーの %LOCALAPPDATA% が正しく取れませんでした: [' + Dir + ']');
        Dir := '';
        Exit;
      end;
      Result := True;
    except
      Log('元のユーザーの %LOCALAPPDATA% を調べる途中で例外: ' + GetExceptionMessage);
      Dir := '';
      Result := False;
    end;
  finally
    DeleteFile(F);
  end;
end;

function LoadJsonText(const Path: String; var Text: String; var HasBom: Boolean): Boolean;
var
  Raw: AnsiString;
begin
  Result := LoadStringFromFile(Path, Raw);
  if not Result then Exit;
  // UTF-8 の BOM (EF BB BF) はデコードすると U+FEFF になる。アプリは BOM 付きで保存するので、付いていれば保存時にも付ける
  Text := Utf8Decode(Raw);
  HasBom := (Length(Text) >= 1) and (Text[1] = #$FEFF);
  if HasBom then
    Delete(Text, 1, 1);
end;

function SaveJsonText(const Path, Text: String; const HasBom: Boolean): Boolean;
var
  Raw: AnsiString;
begin
  if HasBom then
    Raw := Utf8Encode(#$FEFF + Text)
  else
    Raw := Utf8Encode(Text);
  Result := SaveStringToFile(Path, Raw, False);
end;

function IsJsonWs(const C: Char): Boolean;
begin
  Result := (C = ' ') or (C = #9) or (C = #13) or (C = #10);
end;

// S[I] から始まるコメント (// 〜 行末、/* 〜 */) を飛ばし、次の位置を返す。コメントでなければ I のまま。
// アプリ (Microsoft.Extensions.Configuration.Json) はコメントを読み飛ばすので、それに合わせる。
// 閉じていない /* は JsonBadComment を True にする
function SkipJsonComment(const S: String; I: Integer): Integer;
begin
  Result := I;
  if (I + 1 > Length(S)) or (S[I] <> '/') then Exit;
  if S[I + 1] = '/' then
  begin
    I := I + 2;
    while (I <= Length(S)) and (S[I] <> #10) and (S[I] <> #13) do
      I := I + 1;
    Result := I;
  end
  else if S[I + 1] = '*' then
  begin
    I := I + 2;
    while (I + 1 <= Length(S)) and not ((S[I] = '*') and (S[I + 1] = '/')) do
      I := I + 1;
    if I + 1 <= Length(S) then
      Result := I + 2
    else
    begin
      JsonBadComment := True;
      Result := Length(S) + 1;
    end;
  end;
end;

// 空白だけを飛ばす (コメントは飛ばさない)
function SkipWsOnly(const S: String; I: Integer): Integer;
begin
  while (I <= Length(S)) and IsJsonWs(S[I]) do
    I := I + 1;
  Result := I;
end;

// 空白とコメントを飛ばす
function SkipWs(const S: String; I: Integer): Integer;
var
  J: Integer;
begin
  while I <= Length(S) do
  begin
    if IsJsonWs(S[I]) then
      I := I + 1
    else
    begin
      J := SkipJsonComment(S, I);
      if J = I then Break;
      I := J;
    end;
  end;
  Result := I;
end;

function IsHexDigit(const C: Char): Boolean;
begin
  Result := ((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'f')) or ((C >= 'A') and (C <= 'F'));
end;

function IsDigit(const C: Char): Boolean;
begin
  Result := (C >= '0') and (C <= '9');
end;

// S[I] が '"' の文字列を確かめて飛ばし、閉じの '"' の次の位置を返す。正しくなければ 0。
// アプリ (System.Text.Json) と同じく、制御文字 (#0〜#31) をそのまま含むもの、知らないエスケープ、
// 対になっていないサロゲート (\uD800〜\uDFFF) は正しくないとする。
// Dec には中身を戻したもの (ASCII 以外の \u は戻せないので、そのときは DecOk が False で Dec は元の書き方のまま)
function ParseJsonString(const S: String; I: Integer; var Dec: String; var DecOk: Boolean): Integer;
var
  Start, N, N2: Integer;
  C: Char;
begin
  Result := 0;
  Dec := '';
  DecOk := True;
  Start := I;
  I := I + 1;
  while I <= Length(S) do
  begin
    C := S[I];
    if C = '"' then
    begin
      Result := I + 1;
      if not DecOk then
        Dec := Copy(S, Start, Result - Start);
      Exit;
    end;
    if C < #32 then Exit;
    if C = '\' then
    begin
      if I + 1 > Length(S) then Exit;
      C := S[I + 1];
      if C = '"' then Dec := Dec + '"'
      else if C = '\' then Dec := Dec + '\'
      else if C = '/' then Dec := Dec + '/'
      else if C = 'b' then Dec := Dec + #8
      else if C = 'f' then Dec := Dec + #12
      else if C = 'n' then Dec := Dec + #10
      else if C = 'r' then Dec := Dec + #13
      else if C = 't' then Dec := Dec + #9
      else if C = 'u' then
      begin
        if I + 5 > Length(S) then Exit;
        if not (IsHexDigit(S[I + 2]) and IsHexDigit(S[I + 3]) and IsHexDigit(S[I + 4]) and IsHexDigit(S[I + 5])) then Exit;
        N := StrToInt('$' + Copy(S, I + 2, 4));
        if (N >= $DC00) and (N <= $DFFF) then Exit;
        if (N >= $D800) and (N <= $DBFF) then
        begin
          // 上位サロゲートの次は \u の下位サロゲートでなければならない
          if I + 11 > Length(S) then Exit;
          if (S[I + 6] <> '\') or (S[I + 7] <> 'u') then Exit;
          if not (IsHexDigit(S[I + 8]) and IsHexDigit(S[I + 9]) and IsHexDigit(S[I + 10]) and IsHexDigit(S[I + 11])) then Exit;
          N2 := StrToInt('$' + Copy(S, I + 8, 4));
          if (N2 < $DC00) or (N2 > $DFFF) then Exit;
          DecOk := False;
          I := I + 6;
        end
        else if N < $80 then
          Dec := Dec + Chr(N)
        else
          // Chr は 0〜255 しか作れない (ヘルプ: function Chr(B: Byte)) ので、ASCII 以外は戻せない
          DecOk := False;
        I := I + 4;
      end
      else
        Exit;
      I := I + 2;
    end
    else
    begin
      Dec := Dec + C;
      I := I + 1;
    end;
  end;
end;

// S[I] から始まる数値・true・false・null を確かめて飛ばし、次の位置を返す。正しくなければ 0
function ParseJsonLiteral(const S: String; I: Integer): Integer;
begin
  Result := 0;
  if Copy(S, I, 4) = 'true' then Result := I + 4
  else if Copy(S, I, 5) = 'false' then Result := I + 5
  else if Copy(S, I, 4) = 'null' then Result := I + 4
  else
  begin
    // 数値: -? (0 | [1-9][0-9]*) (. [0-9]+)? ([eE] [+-]? [0-9]+)?
    if (I <= Length(S)) and (S[I] = '-') then I := I + 1;
    if I > Length(S) then Exit;
    if S[I] = '0' then
      I := I + 1
    else if (S[I] >= '1') and (S[I] <= '9') then
    begin
      while (I <= Length(S)) and IsDigit(S[I]) do I := I + 1;
    end
    else
      Exit;
    if (I <= Length(S)) and (S[I] = '.') then
    begin
      I := I + 1;
      if (I > Length(S)) or not IsDigit(S[I]) then Exit;
      while (I <= Length(S)) and IsDigit(S[I]) do I := I + 1;
    end;
    if (I <= Length(S)) and ((S[I] = 'e') or (S[I] = 'E')) then
    begin
      I := I + 1;
      if (I <= Length(S)) and ((S[I] = '+') or (S[I] = '-')) then I := I + 1;
      if (I > Length(S)) or not IsDigit(S[I]) then Exit;
      while (I <= Length(S)) and IsDigit(S[I]) do I := I + 1;
    end;
    Result := I;
  end;
end;

// アプリの重複キーの判定 (Dictionary の StringComparer.OrdinalIgnoreCase) に合わせて大文字にするための表。
// .NET 10.0.12 で、各文字の ToUpperInvariant のうち OrdinalIgnoreCase でも等しくなる組だけを集めて作った (PowerShell で全文字を調べて生成)。
// Inno の AnsiUppercase は ANSI のコードページを通るので、ASCII 以外の文字が ? などにまとまってしまい使えない
procedure InitCaseTables();
begin
  if CaseTablesReady then Exit;
  CaseLower :=
    'abcdefghijklmnopqrstuvwxyzµàáâãäåæçèéêëìíîïðñòóôõöøùúûüýþÿāăąćĉċ' +
    'čďđēĕėęěĝğġģĥħĩīĭįĳĵķĺļľŀłńņňŋōŏőœŕŗřśŝşšţťŧũūŭůűųŵŷźżžƀƃƅƈƌƒƕƙƚ' +
    'ƞơƣƥƨƭưƴƶƹƽƿǅǆǈǉǋǌǎǐǒǔǖǘǚǜǝǟǡǣǥǧǩǫǭǯǲǳǵǹǻǽǿȁȃȅȇȉȋȍȏȑȓȕȗșțȝȟȣȥȧȩȫ' +
    'ȭȯȱȳȼȿɀɂɇɉɋɍɏɐɑɒɓɔɖɗəɛɜɠɡɣɥɦɨɩɪɫɬɯɱɲɵɽʀʂʃʇʈʉʊʋʌʒʝʞͅͱͳͷͻͼͽάέήίαβγ' +
    'δεζηθικλμνξοπρςστυφχψωϊϋόύώϐϑϕϖϗϙϛϝϟϡϣϥϧϩϫϭϯϰϱϲϳϵϸϻабвгдежзийклм' +
    'нопрстуфхцчшщъыьэюяѐёђѓєѕіїјљњћќѝўџѡѣѥѧѩѫѭѯѱѳѵѷѹѻѽѿҁҋҍҏґғҕҗҙқҝҟҡ' +
    'ңҥҧҩҫҭүұҳҵҷҹһҽҿӂӄӆӈӊӌӎӏӑӓӕӗәӛӝӟӡӣӥӧөӫӭӯӱӳӵӷӹӻӽӿԁԃԅԇԉԋԍԏԑԓԕԗԙԛԝԟԡ' +
    'ԣԥԧԩԫԭԯաբգդեզէըթժիլխծկհձղճմյնշոչպջռսվտրցւփքօֆაბგდევზთიკლმნოპჟრსტ' +
    'უფქღყშჩცძწჭხჯჰჱჲჳჴჵჶჷჸჹჺჽჾჿᏸᏹᏺᏻᏼᏽᲀᲁᲂᲃᲄᲅᲆᲇᲈᵹᵽᶎḁḃḅḇḉḋḍḏḑḓḕḗḙḛḝḟḡḣḥ' +
    'ḧḩḫḭḯḱḳḵḷḹḻḽḿṁṃṅṇṉṋṍṏṑṓṕṗṙṛṝṟṡṣṥṧṩṫṭṯṱṳṵṷṹṻṽṿẁẃẅẇẉẋẍẏẑẓẕẛạảấầẩẫậ' +
    'ắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹỻỽỿἀἁἂἃἄἅἆἇἐἑἒἓἔἕἠἡἢἣἤἥἦἧἰ' +
    'ἱἲἳἴἵἶἷὀὁὂὃὄὅὑὓὕὗὠὡὢὣὤὥὦὧὰάὲέὴήὶίὸόὺύὼώᾀᾁᾂᾃᾄᾅᾆᾇᾐᾑᾒᾓᾔᾕᾖᾗᾠᾡᾢᾣᾤᾥᾦᾧᾰ' +
    'ᾱᾳιῃῐῑῠῡῥῳⅎⅰⅱⅲⅳⅴⅵⅶⅷⅸⅹⅺⅻⅼⅽⅾⅿↄⓐⓑⓒⓓⓔⓕⓖⓗⓘⓙⓚⓛⓜⓝⓞⓟⓠⓡⓢⓣⓤⓥⓦⓧⓨⓩⰰⰱⰲⰳⰴⰵⰶⰷⰸⰹ' +
    'ⰺⰻⰼⰽⰾⰿⱀⱁⱂⱃⱄⱅⱆⱇⱈⱉⱊⱋⱌⱍⱎⱏⱐⱑⱒⱓⱔⱕⱖⱗⱘⱙⱚⱛⱜⱝⱞⱟⱡⱥⱦⱨⱪⱬⱳⱶⲁⲃⲅⲇⲉⲋⲍⲏⲑⲓⲕⲗⲙⲛⲝⲟⲡⲣ' +
    'ⲥⲧⲩⲫⲭⲯⲱⲳⲵⲷⲹⲻⲽⲿⳁⳃⳅⳇⳉⳋⳍⳏⳑⳓⳕⳗⳙⳛⳝⳟⳡⳣⳬⳮⳳⴀⴁⴂⴃⴄⴅⴆⴇⴈⴉⴊⴋⴌⴍⴎⴏⴐⴑⴒⴓⴔⴕⴖⴗⴘⴙⴚⴛⴜ' +
    'ⴝⴞⴟⴠⴡⴢⴣⴤⴥⴧⴭꙁꙃꙅꙇꙉꙋꙍꙏꙑꙓꙕꙗꙙꙛꙝꙟꙡꙣꙥꙧꙩꙫꙭꚁꚃꚅꚇꚉꚋꚍꚏꚑꚓꚕꚗꚙꚛꜣꜥꜧꜩꜫꜭꜯꜳꜵꜷꜹꜻꜽꜿꝁꝃ' +
    'ꝅꝇꝉꝋꝍꝏꝑꝓꝕꝗꝙꝛꝝꝟꝡꝣꝥꝧꝩꝫꝭꝯꝺꝼꝿꞁꞃꞅꞇꞌꞑꞓꞔꞗꞙꞛꞝꞟꞡꞣꞥꞧꞩꞵꞷꞹꞻꞽꞿꟁꟃꟈꟊꟑꟗꟙꟶꭓꭰꭱꭲꭳꭴꭵ' +
    'ꭶꭷꭸꭹꭺꭻꭼꭽꭾꭿꮀꮁꮂꮃꮄꮅꮆꮇꮈꮉꮊꮋꮌꮍꮎꮏꮐꮑꮒꮓꮔꮕꮖꮗꮘꮙꮚꮛꮜꮝꮞꮟꮠꮡꮢꮣꮤꮥꮦꮧꮨꮩꮪꮫꮬꮭꮮꮯꮰꮱꮲꮳꮴꮵ' +
    'ꮶꮷꮸꮹꮺꮻꮼꮽꮾꮿａｂｃｄｅｆｇｈｉｊｋｌｍｎｏｐｑｒｓｔｕｖｗｘｙｚ';
  CaseUpper :=
    'ABCDEFGHIJKLMNOPQRSTUVWXYZΜÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖØÙÚÛÜÝÞŸĀĂĄĆĈĊ' +
    'ČĎĐĒĔĖĘĚĜĞĠĢĤĦĨĪĬĮĲĴĶĹĻĽĿŁŃŅŇŊŌŎŐŒŔŖŘŚŜŞŠŢŤŦŨŪŬŮŰŲŴŶŹŻŽɃƂƄƇƋƑǶƘȽ' +
    'ȠƠƢƤƧƬƯƳƵƸƼǷǄǄǇǇǊǊǍǏǑǓǕǗǙǛƎǞǠǢǤǦǨǪǬǮǱǱǴǸǺǼǾȀȂȄȆȈȊȌȎȐȒȔȖȘȚȜȞȢȤȦȨȪ' +
    'ȬȮȰȲȻⱾⱿɁɆɈɊɌɎⱯⱭⱰƁƆƉƊƏƐꞫƓꞬƔꞍꞪƗƖꞮⱢꞭƜⱮƝƟⱤƦꟅƩꞱƮɄƱƲɅƷꞲꞰΙͰͲͶϽϾϿΆΈΉΊΑΒΓ' +
    'ΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΣΤΥΦΧΨΩΪΫΌΎΏΒΘΦΠϏϘϚϜϞϠϢϤϦϨϪϬϮΚΡϹͿΕϷϺАБВГДЕЖЗИЙКЛМ' +
    'НОПРСТУФХЦЧШЩЪЫЬЭЮЯЀЁЂЃЄЅІЇЈЉЊЋЌЍЎЏѠѢѤѦѨѪѬѮѰѲѴѶѸѺѼѾҀҊҌҎҐҒҔҖҘҚҜҞҠ' +
    'ҢҤҦҨҪҬҮҰҲҴҶҸҺҼҾӁӃӅӇӉӋӍӀӐӒӔӖӘӚӜӞӠӢӤӦӨӪӬӮӰӲӴӶӸӺӼӾԀԂԄԆԈԊԌԎԐԒԔԖԘԚԜԞԠ' +
    'ԢԤԦԨԪԬԮԱԲԳԴԵԶԷԸԹԺԻԼԽԾԿՀՁՂՃՄՅՆՇՈՉՊՋՌՍՎՏՐՑՒՓՔՕՖᲐᲑᲒᲓᲔᲕᲖᲗᲘᲙᲚᲛᲜᲝᲞᲟᲠᲡᲢ' +
    'ᲣᲤᲥᲦᲧᲨᲩᲪᲫᲬᲭᲮᲯᲰᲱᲲᲳᲴᲵᲶᲷᲸᲹᲺᲽᲾᲿᏰᏱᏲᏳᏴᏵВДОСТТЪѢꙊꝽⱣꟆḀḂḄḆḈḊḌḎḐḒḔḖḘḚḜḞḠḢḤ' +
    'ḦḨḪḬḮḰḲḴḶḸḺḼḾṀṂṄṆṈṊṌṎṐṒṔṖṘṚṜṞṠṢṤṦṨṪṬṮṰṲṴṶṸṺṼṾẀẂẄẆẈẊẌẎẐẒẔṠẠẢẤẦẨẪẬ' +
    'ẮẰẲẴẶẸẺẼẾỀỂỄỆỈỊỌỎỐỒỔỖỘỚỜỞỠỢỤỦỨỪỬỮỰỲỴỶỸỺỼỾἈἉἊἋἌἍἎἏἘἙἚἛἜἝἨἩἪἫἬἭἮἯἸ' +
    'ἹἺἻἼἽἾἿὈὉὊὋὌὍὙὛὝὟὨὩὪὫὬὭὮὯᾺΆῈΈῊΉῚΊῸΌῪΎῺΏᾈᾉᾊᾋᾌᾍᾎᾏᾘᾙᾚᾛᾜᾝᾞᾟᾨᾩᾪᾫᾬᾭᾮᾯᾸ' +
    'ᾹᾼΙῌῘῙῨῩῬῼℲⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩⅪⅫⅬⅭⅮⅯↃⒶⒷⒸⒹⒺⒻⒼⒽⒾⒿⓀⓁⓂⓃⓄⓅⓆⓇⓈⓉⓊⓋⓌⓍⓎⓏⰀⰁⰂⰃⰄⰅⰆⰇⰈⰉ' +
    'ⰊⰋⰌⰍⰎⰏⰐⰑⰒⰓⰔⰕⰖⰗⰘⰙⰚⰛⰜⰝⰞⰟⰠⰡⰢⰣⰤⰥⰦⰧⰨⰩⰪⰫⰬⰭⰮⰯⱠȺȾⱧⱩⱫⱲⱵⲀⲂⲄⲆⲈⲊⲌⲎⲐⲒⲔⲖⲘⲚⲜⲞⲠⲢ' +
    'ⲤⲦⲨⲪⲬⲮⲰⲲⲴⲶⲸⲺⲼⲾⳀⳂⳄⳆⳈⳊⳌⳎⳐⳒⳔⳖⳘⳚⳜⳞⳠⳢⳫⳭⳲႠႡႢႣႤႥႦႧႨႩႪႫႬႭႮႯႰႱႲႳႴႵႶႷႸႹႺႻႼ' +
    'ႽႾႿჀჁჂჃჄჅჇჍꙀꙂꙄꙆꙈꙊꙌꙎꙐꙒꙔꙖꙘꙚꙜꙞꙠꙢꙤꙦꙨꙪꙬꚀꚂꚄꚆꚈꚊꚌꚎꚐꚒꚔꚖꚘꚚꜢꜤꜦꜨꜪꜬꜮꜲꜴꜶꜸꜺꜼꜾꝀꝂ' +
    'ꝄꝆꝈꝊꝌꝎꝐꝒꝔꝖꝘꝚꝜꝞꝠꝢꝤꝦꝨꝪꝬꝮꝹꝻꝾꞀꞂꞄꞆꞋꞐꞒꟄꞖꞘꞚꞜꞞꞠꞢꞤꞦꞨꞴꞶꞸꞺꞼꞾꟀꟂꟇꟉꟐꟖꟘꟵꞳᎠᎡᎢᎣᎤᎥ' +
    'ᎦᎧᎨᎩᎪᎫᎬᎭᎮᎯᎰᎱᎲᎳᎴᎵᎶᎷᎸᎹᎺᎻᎼᎽᎾᎿᏀᏁᏂᏃᏄᏅᏆᏇᏈᏉᏊᏋᏌᏍᏎᏏᏐᏑᏒᏓᏔᏕᏖᏗᏘᏙᏚᏛᏜᏝᏞᏟᏠᏡᏢᏣᏤᏥ' +
    'ᏦᏧᏨᏩᏪᏫᏬᏭᏮᏯＡＢＣＤＥＦＧＨＩＪＫＬＭＮＯＰＱＲＳＴＵＶＷＸＹＺ';
  CaseSupLower :=
    '𐐨𐐩𐐪𐐫𐐬𐐭𐐮𐐯𐐰𐐱𐐲𐐳𐐴𐐵𐐶𐐷𐐸𐐹𐐺𐐻𐐼𐐽𐐾𐐿𐑀𐑁𐑂𐑃𐑄𐑅𐑆𐑇' +
    '𐑈𐑉𐑊𐑋𐑌𐑍𐑎𐑏𐓘𐓙𐓚𐓛𐓜𐓝𐓞𐓟𐓠𐓡𐓢𐓣𐓤𐓥𐓦𐓧𐓨𐓩𐓪𐓫𐓬𐓭𐓮𐓯' +
    '𐓰𐓱𐓲𐓳𐓴𐓵𐓶𐓷𐓸𐓹𐓺𐓻𐖗𐖘𐖙𐖚𐖛𐖜𐖝𐖞𐖟𐖠𐖡𐖣𐖤𐖥𐖦𐖧𐖨𐖩𐖪𐖫' +
    '𐖬𐖭𐖮𐖯𐖰𐖱𐖳𐖴𐖵𐖶𐖷𐖸𐖹𐖻𐖼𐳀𐳁𐳂𐳃𐳄𐳅𐳆𐳇𐳈𐳉𐳊𐳋𐳌𐳍𐳎𐳏𐳐' +
    '𐳑𐳒𐳓𐳔𐳕𐳖𐳗𐳘𐳙𐳚𐳛𐳜𐳝𐳞𐳟𐳠𐳡𐳢𐳣𐳤𐳥𐳦𐳧𐳨𐳩𐳪𐳫𐳬𐳭𐳮𐳯𐳰' +
    '𐳱𐳲𑣀𑣁𑣂𑣃𑣄𑣅𑣆𑣇𑣈𑣉𑣊𑣋𑣌𑣍𑣎𑣏𑣐𑣑𑣒𑣓𑣔𑣕𑣖𑣗𑣘𑣙𑣚𑣛𑣜𑣝' +
    '𑣞𑣟𖹠𖹡𖹢𖹣𖹤𖹥𖹦𖹧𖹨𖹩𖹪𖹫𖹬𖹭𖹮𖹯𖹰𖹱𖹲𖹳𖹴𖹵𖹶𖹷𖹸𖹹𖹺𖹻𖹼𖹽' +
    '𖹾𖹿𞤢𞤣𞤤𞤥𞤦𞤧𞤨𞤩𞤪𞤫𞤬𞤭𞤮𞤯𞤰𞤱𞤲𞤳𞤴𞤵𞤶𞤷𞤸𞤹𞤺𞤻𞤼𞤽𞤾𞤿' +
    '𞥀𞥁𞥂𞥃';
  CaseSupUpper :=
    '𐐀𐐁𐐂𐐃𐐄𐐅𐐆𐐇𐐈𐐉𐐊𐐋𐐌𐐍𐐎𐐏𐐐𐐑𐐒𐐓𐐔𐐕𐐖𐐗𐐘𐐙𐐚𐐛𐐜𐐝𐐞𐐟' +
    '𐐠𐐡𐐢𐐣𐐤𐐥𐐦𐐧𐒰𐒱𐒲𐒳𐒴𐒵𐒶𐒷𐒸𐒹𐒺𐒻𐒼𐒽𐒾𐒿𐓀𐓁𐓂𐓃𐓄𐓅𐓆𐓇' +
    '𐓈𐓉𐓊𐓋𐓌𐓍𐓎𐓏𐓐𐓑𐓒𐓓𐕰𐕱𐕲𐕳𐕴𐕵𐕶𐕷𐕸𐕹𐕺𐕼𐕽𐕾𐕿𐖀𐖁𐖂𐖃𐖄' +
    '𐖅𐖆𐖇𐖈𐖉𐖊𐖌𐖍𐖎𐖏𐖐𐖑𐖒𐖔𐖕𐲀𐲁𐲂𐲃𐲄𐲅𐲆𐲇𐲈𐲉𐲊𐲋𐲌𐲍𐲎𐲏𐲐' +
    '𐲑𐲒𐲓𐲔𐲕𐲖𐲗𐲘𐲙𐲚𐲛𐲜𐲝𐲞𐲟𐲠𐲡𐲢𐲣𐲤𐲥𐲦𐲧𐲨𐲩𐲪𐲫𐲬𐲭𐲮𐲯𐲰' +
    '𐲱𐲲𑢠𑢡𑢢𑢣𑢤𑢥𑢦𑢧𑢨𑢩𑢪𑢫𑢬𑢭𑢮𑢯𑢰𑢱𑢲𑢳𑢴𑢵𑢶𑢷𑢸𑢹𑢺𑢻𑢼𑢽' +
    '𑢾𑢿𖹀𖹁𖹂𖹃𖹄𖹅𖹆𖹇𖹈𖹉𖹊𖹋𖹌𖹍𖹎𖹏𖹐𖹑𖹒𖹓𖹔𖹕𖹖𖹗𖹘𖹙𖹚𖹛𖹜𖹝' +
    '𖹞𖹟𞤀𞤁𞤂𞤃𞤄𞤅𞤆𞤇𞤈𞤉𞤊𞤋𞤌𞤍𞤎𞤏𞤐𞤑𞤒𞤓𞤔𞤕𞤖𞤗𞤘𞤙𞤚𞤛𞤜𞤝' +
    '𞤞𞤟𞤠𞤡';
  CaseTablesReady := True;
end;

// アプリの OrdinalIgnoreCase と同じ比べ方になるように、表を使って 1 文字ずつ大文字にする
function OrdinalUpper(const S: String): String;
var
  I, Lo, Hi, Mid: Integer;
  C, C2: Char;
  Done, IsPair: Boolean;
begin
  InitCaseTables();
  // Pos は ASCII 以外の文字で位置がずれる (Unicode の位置にならず、範囲外になることがある) ので使わず、文字どうしで比べる。
  // 速くするため、ASCII は a〜z だけを直接大文字にし (表の ASCII の部分と同じ結果)、それ以外は表を二分探索する
  // (表は文字の番号の小さい順に並んでいる。補助面の表は 2 文字 = 1 組ずつ、上位・下位サロゲートの順で並んでいる)
  Result := '';
  I := 1;
  while I <= Length(S) do
  begin
    C := S[I];
    Done := False;
    // 上位サロゲートの次が本当に下位サロゲートのときだけ組として扱う (単独の上位サロゲートは 1 文字だけ進める)
    IsPair := False;
    if (C >= #$D800) and (C <= #$DBFF) and (I < Length(S)) then
    begin
      C2 := S[I + 1];
      IsPair := (C2 >= #$DC00) and (C2 <= #$DFFF);
    end;
    if C <= #127 then
    begin
      if (C >= 'a') and (C <= 'z') then
        Result := Result + Chr(Ord(C) - 32)
      else
        Result := Result + C;
      I := I + 1;
    end
    else if IsPair then
    begin
      // 補助面の文字 (サロゲートの組) は組ごと表から探す
      Lo := 0;
      Hi := Length(CaseSupLower) div 2 - 1;
      while Lo <= Hi do
      begin
        Mid := (Lo + Hi) div 2;
        if (CaseSupLower[Mid * 2 + 1] = C) and (CaseSupLower[Mid * 2 + 2] = C2) then
        begin
          Result := Result + CaseSupUpper[Mid * 2 + 1] + CaseSupUpper[Mid * 2 + 2];
          Done := True;
          Break;
        end
        else if (CaseSupLower[Mid * 2 + 1] < C) or ((CaseSupLower[Mid * 2 + 1] = C) and (CaseSupLower[Mid * 2 + 2] < C2)) then
          Lo := Mid + 1
        else
          Hi := Mid - 1;
      end;
      if not Done then
        Result := Result + C + C2;
      I := I + 2;
    end
    else
    begin
      Lo := 1;
      Hi := Length(CaseLower);
      while Lo <= Hi do
      begin
        Mid := (Lo + Hi) div 2;
        if CaseLower[Mid] = C then
        begin
          Result := Result + CaseUpper[Mid];
          Done := True;
          Break;
        end
        else if CaseLower[Mid] < C then
          Lo := Mid + 1
        else
          Hi := Mid - 1;
      end;
      if not Done then
        Result := Result + C;
      I := I + 1;
    end;
  end;
end;

// アプリ (Microsoft.Extensions.Configuration.Json) は値を「親:子」の形のキーで覚え、同じキー (大文字小文字を区別しない) の値が
// 2 回出てくると壊れたとみなす。空のオブジェクト・配列は、そのキーを上書きで覚える (重複とはみなさない)。それに合わせる
function AddConfigKey(const Keys: TStringList; const Path: String; const IsLeaf: Boolean): Boolean;
var
  K: String;
  J: Integer;
  Found: Boolean;
begin
  K := OrdinalUpper(Path);
  Result := True;
  // TStringList.IndexOf は大文字小文字を区別しない比べ方 (Windows のロケール依存) をするので使わず、
  // 大文字にしたものを = (そのままの比べ方) で比べる
  Found := False;
  for J := 0 to Keys.Count - 1 do
    if Keys[J] = K then
    begin
      Found := True;
      Break;
    end;
  if Found then
  begin
    if IsLeaf then Result := False;
  end
  else
    Keys.Add(K);
end;

function JoinConfigPath(const Parent, Name: String): String;
begin
  if Parent = '' then
    Result := Name
  else
    Result := Parent + ':' + Name;
end;

// S[I] から始まる値を確かめて飛ばし、次の位置を返す。正しくなければ 0。
// オブジェクト・配列は中まで確かめる (コメントと末尾のカンマは許す)。Path はこの値のキー (重複の確認に使う)
function ParseJsonValue(const S: String; I: Integer; const Path: String; const Keys: TStringList): Integer;
var
  Name: String;
  NameOk: Boolean;
  Index, J: Integer;
  CloseCh: Char;
begin
  Result := 0;
  if I > Length(S) then Exit;
  if S[I] = '"' then
  begin
    J := ParseJsonString(S, I, Name, NameOk);
    if J = 0 then Exit;
    if not AddConfigKey(Keys, Path, True) then Exit;
    Result := J;
    Exit;
  end;
  if (S[I] = '{') or (S[I] = '[') then
  begin
    if S[I] = '{' then CloseCh := '}' else CloseCh := ']';
    I := SkipWs(S, I + 1);
    if I > Length(S) then Exit;
    if S[I] = CloseCh then
    begin
      AddConfigKey(Keys, Path, False);
      Result := I + 1;
      Exit;
    end;
    Index := 0;
    while True do
    begin
      if CloseCh = '}' then
      begin
        if (I > Length(S)) or (S[I] <> '"') then Exit;
        J := ParseJsonString(S, I, Name, NameOk);
        if J = 0 then Exit;
        // キーと : の間はコメントを許さない (アプリと同じ)。空白だけ飛ばす
        I := SkipWsOnly(S, J);
        if (I > Length(S)) or (S[I] <> ':') then Exit;
        I := SkipWs(S, I + 1);
        I := ParseJsonValue(S, I, JoinConfigPath(Path, Name), Keys);
      end
      else
      begin
        I := ParseJsonValue(S, I, JoinConfigPath(Path, IntToStr(Index)), Keys);
        Index := Index + 1;
      end;
      if I = 0 then Exit;
      I := SkipWs(S, I);
      if I > Length(S) then Exit;
      if S[I] = ',' then
      begin
        I := SkipWs(S, I + 1);
        // 末尾のカンマ
        if (I <= Length(S)) and (S[I] = CloseCh) then
        begin
          Result := I + 1;
          Exit;
        end;
      end
      else if S[I] = CloseCh then
      begin
        Result := I + 1;
        Exit;
      end
      else
        Exit;
    end;
  end;
  J := ParseJsonLiteral(S, I);
  if J = 0 then Exit;
  if not AddConfigKey(Keys, Path, True) then Exit;
  Result := J;
end;

// 最上位のオブジェクトから Key (大文字小文字を区別しない) を探す。ファイル全体をアプリと同じ基準で確かめる。
// 戻り値: 1 = 見つかった (ValStart..ValEnd-1 が値), 0 = 無い, -1 = JSON として読めない (アプリが壊れたとみなすもの)
// ObjOpen は最上位の '{' の位置、IsEmpty はオブジェクトが空かどうか。
// 確かめないもの: 型の変換 (例: 数値の設定に "abc") はアプリの設定クラス側の話なので、インストーラーでは判定できない。
// UTF-8 として正しくないバイト列や、入れ子の深さの上限も確かめない
function FindTopLevelKey(const S, Key: String; var ValStart, ValEnd, ObjOpen: Integer; var IsEmpty: Boolean): Integer;
var
  I, KEnd, VStart: Integer;
  Name: String;
  NameOk: Boolean;
  Keys: TStringList;
begin
  Result := -1;
  ValStart := 0;
  ValEnd := 0;
  ObjOpen := 0;
  IsEmpty := False;
  JsonBadComment := False;
  I := SkipWs(S, 1);
  if (I > Length(S)) or (S[I] <> '{') then Exit;
  ObjOpen := I;
  Keys := TStringList.Create;
  try
    I := SkipWs(S, I + 1);
    if (I <= Length(S)) and (S[I] = '}') then
    begin
      if SkipWs(S, I + 1) <= Length(S) then Exit;
      if JsonBadComment then Exit;
      IsEmpty := True;
      Result := 0;
      Exit;
    end;
    while True do
    begin
      if (I > Length(S)) or (S[I] <> '"') then Exit;
      KEnd := ParseJsonString(S, I, Name, NameOk);
      if KEnd = 0 then Exit;
      // キーと : の間はコメントを許さない (アプリと同じ)。空白だけ飛ばす
      I := SkipWsOnly(S, KEnd);
      if (I > Length(S)) or (S[I] <> ':') then Exit;
      I := SkipWs(S, I + 1);
      VStart := I;
      I := ParseJsonValue(S, I, Name, Keys);
      if I = 0 then Exit;
      // キー名の比べ方もアプリ (OrdinalIgnoreCase) に合わせる
      if NameOk and (OrdinalUpper(Name) = OrdinalUpper(Key)) then
      begin
        ValStart := VStart;
        ValEnd := I;
      end;
      I := SkipWs(S, I);
      if I > Length(S) then Exit;
      if S[I] = ',' then
      begin
        I := SkipWs(S, I + 1);
        // 末尾のカンマ: , の次が } なら終わり
        if (I <= Length(S)) and (S[I] = '}') then
          Break;
      end
      else if S[I] = '}' then
        Break
      else
        Exit;
    end;
    // 最上位の } の後はコメントと空白だけ
    if SkipWs(S, I + 1) <= Length(S) then Exit;
    if JsonBadComment then Exit;
    if ValStart > 0 then
      Result := 1
    else
      Result := 0;
  finally
    Keys.Free;
  end;
end;

// JSON の文字列リテラル ("..." を含む) を中身に戻す
function DecodeJsonString(const Lit: String): String;
var
  I, N: Integer;
  C: Char;
begin
  Result := '';
  I := 2;
  while I < Length(Lit) do
  begin
    C := Lit[I];
    if (C = '\') and (I + 1 < Length(Lit)) then
    begin
      I := I + 1;
      C := Lit[I];
      if C = 'n' then Result := Result + #10
      else if C = 'r' then Result := Result + #13
      else if C = 't' then Result := Result + #9
      else if C = 'b' then Result := Result + #8
      else if C = 'f' then Result := Result + #12
      else if C = 'u' then
      begin
        // Chr は 0〜255 しか作れないので、ASCII (0〜127) だけ戻す。それ以外は戻せないとして JsonDecodeOk を False にする
        N := StrToIntDef('$' + Copy(Lit, I + 1, 4), -1);
        if (N >= 0) and (N < $80) then
          Result := Result + Chr(N)
        else
          JsonDecodeOk := False;
        I := I + 4;
      end
      else Result := Result + C;
    end
    else
      Result := Result + C;
    I := I + 1;
  end;
end;

function EncodeJsonString(const Value: String): String;
var
  S: String;
begin
  S := Value;
  StringChangeEx(S, '\', '\\', True);
  StringChangeEx(S, '"', '\"', True);
  Result := '"' + S + '"';
end;

// 設定ファイルの作業ディレクトリを読む。
// 戻り値: 1 = 読めた (Value に値。文字列でなければ空), 0 = キーが無い, -1 = 読めない,
// 2 = 値はあるが ASCII 以外の \u エスケープを含んでいて戻せない (Value は空)
function ReadWorkDirFromJson(const Path: String; var Value: String): Integer;
var
  Text, Lit: String;
  HasBom, IsEmpty: Boolean;
  VS, VE, ObjOpen: Integer;
begin
  Value := '';
  if not LoadJsonText(Path, Text, HasBom) then
  begin
    Result := -1;
    Exit;
  end;
  Result := FindTopLevelKey(Text, WorkDirKey, VS, VE, ObjOpen, IsEmpty);
  if Result = 1 then
  begin
    Lit := Copy(Text, VS, VE - VS);
    if (Length(Lit) >= 2) and (Lit[1] = '"') then
    begin
      JsonDecodeOk := True;
      Value := DecodeJsonString(Lit);
      if not JsonDecodeOk then
      begin
        Value := '';
        Result := 2;
      end;
    end;
  end;
end;

// 設定ファイルの作業ディレクトリのキーだけを NewDir に書き換える (キーが無ければ先頭に足す)。
// 戻り値: 1 = 書き換えた, 0 = 同じ値なので書き換えていない, -1 = 読めない・書けない
function WriteWorkDirToJson(const Path, NewDir: String): Integer;
var
  Text, Lit, Indent, Head, Tail: String;
  HasBom, IsEmpty: Boolean;
  Found, VS, VE, ObjOpen: Integer;
begin
  Result := -1;
  if not LoadJsonText(Path, Text, HasBom) then Exit;
  Found := FindTopLevelKey(Text, WorkDirKey, VS, VE, ObjOpen, IsEmpty);
  if Found < 0 then Exit;
  if Found = 1 then
  begin
    Lit := Copy(Text, VS, VE - VS);
    if (Length(Lit) >= 2) and (Lit[1] = '"') and (DecodeJsonString(Lit) = NewDir) then
    begin
      Result := 0;
      Exit;
    end;
    // Copy を + の式の中で使うと ANSI の文字列として扱われ、ANSI に無い文字 (絵文字など) が ? に化ける (実物で確かめた)。
    // いったん String の変数に入れてからつなぐ
    Head := Copy(Text, 1, VS - 1);
    Tail := Copy(Text, VE, Length(Text));
    Text := Head + EncodeJsonString(NewDir) + Tail;
  end
  else
  begin
    Indent := #13#10 + '  ';
    if IsEmpty then
      Insert(Indent + '"' + WorkDirKey + '": ' + EncodeJsonString(NewDir) + #13#10, Text, ObjOpen + 1)
    else
      Insert(Indent + '"' + WorkDirKey + '": ' + EncodeJsonString(NewDir) + ',', Text, ObjOpen + 1);
  end;
  if SaveJsonText(Path, Text, HasBom) then
    Result := 1;
end;

// ---------------------------------------------------------------
// 作業ディレクトリのページ
// ---------------------------------------------------------------

// 作業ディレクトリを書くユーザーの設定ファイルの場所を決める。
// 非管理者モード: Setup を動かしているユーザー ({localappdata}) のもの。
// 管理者モード (すべてのユーザー向け): インストールした本人 (元のユーザー) のもの。取れなければ分からないままにし、
// {localappdata} (昇格したアカウントの可能性がある) には書かない
procedure DecideUserConfigPath(const AdminMode: Boolean; const HandOffDir: String);
var
  Lad: String;
begin
  UserCfgPath := '';
  UserCfgKnown := False;
  if not AdminMode then
  begin
    UserCfgPath := ExpandConstant('{localappdata}\StarCitizenJapaneseTextCreater\appsettings.json');
    UserCfgKnown := True;
  end
  else if GetOriginalUserLocalAppData(HandOffDir, Lad) then
  begin
    UserCfgPath := AddBackslash(Lad) + 'StarCitizenJapaneseTextCreater\appsettings.json';
    UserCfgKnown := True;
  end;
  UserConfigExists := UserCfgKnown and FileExists(UserCfgPath);
  if UserCfgKnown then
    Log('作業ディレクトリを書くユーザーの設定ファイル: ' + UserCfgPath)
  else
    Log('インストールした本人のユーザーの設定ファイルの場所が分かりません。作業ディレクトリは書き込みません');
end;

procedure InitializeWizard();
var
  Initial: String;
begin
  WorkDirPage := CreateInputDirPage(wpSelectDir,
    '作業ディレクトリ (一時ファイルの場所)',
    '翻訳データベース・ゲームデータ・抽出したファイルを置くフォルダを選んでください。',
    '次のフォルダを使います。無ければ作成します。後から設定タブの Working Directory で変えられます。',
    False, '');
  WorkDirPage.Add('');

  // .NET 10 Desktop Runtime のダウンロードの進み具合を見せるページ
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;

  // 既にユーザーの設定ファイルがあれば、その値を初期値として見せる (勝手に変えない)
  Initial := '';
  DecideUserConfigPath(IsAdminInstallMode, ExpandConstant('{commonappdata}'));
  if UserConfigExists then
  begin
    // ASCII 以外の \u を含む値は戻せない (Chr は 0〜255 しか作れない) ので、初期値には出せない。その場合は既定の値を見せ、
    // 値を変えなければ書き換えない
    if ReadWorkDirFromJson(UserConfigPath(), Initial) = 2 then
      Log('ユーザー設定の作業ディレクトリに ASCII 以外の \u エスケープがあり、初期値に今の値を出せません');
  end
  else
    Initial := GetPreviousData('WorkDir', '');
  if Trim(Initial) = '' then
    Initial := DefaultWorkDir;
  InitialWorkDir := Initial;
  WorkDirPage.Values[0] := Initial;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'WorkDir', Trim(WorkDirPage.Values[0]));
end;

// 同じフォルダか (大文字小文字と末尾の \ を無視)。下のフォルダは同じとみなさない
// (Lowercase などを式の中で直接比べると ANSI の文字列として比べられ、ANSI に無い文字が ? になって別のフォルダが同じに見えるので、
//  String の変数に入れてから、表を使った大文字 (OrdinalUpper) どうしで比べる)
function IsSameDir(const A, B: String): Boolean;
var
  X, Y: String;
begin
  X := Trim(A);
  Y := Trim(B);
  X := AddBackslash(OrdinalUpper(X));
  Y := AddBackslash(OrdinalUpper(Y));
  Result := X = Y;
end;

// 同じフォルダか、その下のフォルダか (大文字小文字と末尾の \ を無視。比べ方は IsSameDir と同じ)
function IsSameOrUnderDir(const A, Parent: String): Boolean;
var
  X, Y: String;
begin
  X := Trim(A);
  Y := Trim(Parent);
  Result := False;
  if (X = '') or (Y = '') then Exit;
  X := AddBackslash(OrdinalUpper(X));
  Y := AddBackslash(OrdinalUpper(Y));
  Result := (Length(X) >= Length(Y)) and (Copy(X, 1, Length(Y)) = Y);
end;

// Program Files ({commonpf} / {commonpf32} / {commonpf64}) そのものか、その下か
function IsUnderProgramFiles(const A: String): Boolean;
begin
  Result := IsSameOrUnderDir(A, ExpandConstant('{commonpf}'))
    or IsSameOrUnderDir(A, ExpandConstant('{commonpf32}'))
    or IsSameOrUnderDir(A, ExpandConstant('{commonpf64}'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // .NET 10 が無ければここでダウンロードする。失敗してもインストールは続ける
  if CurPageID = wpReady then
    DownloadDotNetIfNeeded();
  if CurPageID = WorkDirPage.ID then
  begin
    if Trim(WorkDirPage.Values[0]) = '' then
    begin
      SuppressibleMsgBox('作業ディレクトリを指定してください。', mbError, MB_OK, IDOK);
      Result := False;
    end
    // インストール先そのものとその下だと、更新時に同梱 DB で利用者の DB が上書きされ、アンインストールで消えるため不可
    else if IsSameOrUnderDir(WorkDirPage.Values[0], WizardDirValue) then
    begin
      SuppressibleMsgBox('作業ディレクトリにインストール先のフォルダとその下のフォルダは指定できません。' + #13#10 +
        '(更新やアンインストールでデータが消えるおそれがあります)', mbError, MB_OK, IDOK);
      Result := False;
    end
    // Program Files とその下は、普通の権限のアプリからは書き込めない (起動時に DB を写せない) ため不可
    else if IsUnderProgramFiles(WorkDirPage.Values[0]) then
    begin
      SuppressibleMsgBox('作業ディレクトリに Program Files とその下のフォルダは指定できません。' + #13#10 +
        '(アプリから書き込めません。例: C:\temp)', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

// アプリ (どこから起動したものでも) が動いているか。
// アプリは終了時に設定をまるごと保存するので、動いたままだと書き換えた作業ディレクトリが終了時に元へ戻る
function IsAppRunning(): Boolean;
var
  Locator, Service, Items: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''{#MyAppExeName}''');
    Result := Items.Count > 0;
  except
    Log('アプリが動いているかを確かめられませんでした: ' + GetExceptionMessage);
  end;
end;

// アプリが閉じられるまで待つ。閉じられたら True、利用者が取り消したら False
function WaitForAppClosed(): Boolean;
begin
  Result := True;
  while IsAppRunning() do
  begin
    if SuppressibleMsgBox('{#MyAppName} が起動しています。' + #13#10 +
      '作業ディレクトリの設定を書き込むため、アプリを閉じてから [再試行] を押してください。' + #13#10 +
      '([キャンセル] を押すと設定は書き込まず、起動後に設定タブで指定することになります)',
      mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// アプリが実際に使う作業ディレクトリを Dir に入れる。
// アプリは既定の設定ファイル ({app}) の上にユーザーの設定ファイルを重ねて読み (AppConfigLoader.Build)、
// 値が空なら C:\temp を使う (App.OnStartup)。
// ユーザーの設定ファイルが無い・壊れている (JSON として読めない) ときは、アプリは壊れたファイルを .broken-<日時> に名前を変え、
// 既定を写して読む (AppConfigLoader.Load) ので、既定の設定ファイルの値を使う。
// 既定の設定ファイル自体が読めないときは、アプリは例外で起動しない (AppConfigLoader.Load の catch の中の Build(baseDir, null)) ので False を返す。
// 値に戻せない \u エスケープがあるとき (ReadWorkDirFromJson が 2) も、値が分からないので False を返す
function EffectiveWorkDir(const UseUserConfig: Boolean; const UserPath, AppPath: String; var Dir: String): Boolean;
var
  V: String;
  R: Integer;
begin
  Result := False;
  Dir := '';
  if UseUserConfig then
  begin
    R := ReadWorkDirFromJson(UserPath, V);
    if R = 2 then Exit;
    if R = 1 then
    begin
      Dir := V;
      if Trim(Dir) = '' then
        Dir := DefaultWorkDir;
      Result := True;
      Exit;
    end;
    // R = 0 (キーが無い) / R = -1 (無い・壊れている) は既定の設定ファイルの値になる
  end;
  R := ReadWorkDirFromJson(AppPath, V);
  if (R < 0) or (R = 2) then Exit;
  if R = 1 then
    Dir := V;
  if Trim(Dir) = '' then
    Dir := DefaultWorkDir;
  Result := True;
end;

// 設定を書き換えずに終わるとき、アプリが今の設定で使う作業ディレクトリが無ければ警告する (フォルダは作らない)。警告したら True。
// SuppressibleMsgBox は /SILENT・/VERYSILENT では表示され、/SUPPRESSMSGBOXES を付けたときだけ出ない (ログには残す)
function WarnIfWorkDirMissing(): Boolean;
var
  CurrentDir: String;
begin
  Result := False;
  if not EffectiveWorkDir(UserConfigExists, UserConfigPath(), ExpandConstant('{app}\appsettings.json'), CurrentDir) then
  begin
    Log('既定の設定ファイルが読めない、または値を戻せないため、アプリが使う作業ディレクトリを求められませんでした');
    Exit;
  end;
  if DirExists(CurrentDir) then Exit;
  Log('作業ディレクトリがありません: ' + CurrentDir);
  SuppressibleMsgBox('作業ディレクトリ (' + CurrentDir + ') がありません。' + #13#10 +
    'アプリの設定タブで指定してください。', mbError, MB_OK, IDOK);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  WorkDir, ConfigPath, DefaultValue: String;
  R: Integer;
begin
  if CurStep <> ssPostInstall then Exit;

  WorkDir := Trim(WorkDirPage.Values[0]);
  if not DirExists(WorkDir) then
    if not ForceDirectories(WorkDir) then
      SuppressibleMsgBox('作業ディレクトリを作成できませんでした:' + #13#10 + WorkDir + #13#10 + #13#10 +
        'アプリの設定タブで別のフォルダを指定してください。', mbError, MB_OK, IDOK);

  // 管理者モードで本人のユーザー設定の場所が分からないときは書かずに知らせる ({localappdata} には書かない)
  if not UserCfgKnown then
  begin
    Log('インストールした本人のユーザー設定が分からないため、作業ディレクトリの設定を書き込みませんでした');
    SuppressibleMsgBox('作業ディレクトリの設定は書き込みませんでした。' + #13#10 +
      'アプリの設定タブの Working Directory で次のフォルダを指定してください:' + #13#10 + WorkDir, mbInformation, MB_OK, IDOK);
    Exit;
  end;

  // 管理者モードで本人のユーザー設定がまだ無いとき: {app} の既定の設定ファイルは書き換えず (ほかのユーザーに影響させない)、
  // アプリの初回起動時の処理 (AppConfigLoader.Load: ユーザー設定が無ければ exe と同じフォルダの appsettings.json を写す) と
  // 同じく既定の設定ファイルを本人の場所へ写してから、作業ディレクトリだけを書き換える。既定と同じ値なら何もしない
  if IsAdminInstallMode and not UserConfigExists then
  begin
    if (ReadWorkDirFromJson(ExpandConstant('{app}\appsettings.json'), DefaultValue) = 1) and (DefaultValue = WorkDir) then Exit;
    ForceDirectories(ExtractFileDir(UserConfigPath()));
    if not CopyFile(ExpandConstant('{app}\appsettings.json'), UserConfigPath(), True) then
    begin
      Log('既定の設定ファイルを本人のユーザー設定へ写せませんでした: ' + UserConfigPath());
      if not WarnIfWorkDirMissing() then
        SuppressibleMsgBox('設定ファイルに作業ディレクトリを書き込めませんでした:' + #13#10 + UserConfigPath() + #13#10 + #13#10 +
          'アプリの設定タブの Working Directory で次のフォルダを指定してください:' + #13#10 + WorkDir, mbError, MB_OK, IDOK);
      Exit;
    end;
    UserConfigExists := True;
    InitialWorkDir := '';
  end;

  // ユーザーの設定ファイルがあれば、そのキーだけを書き換える (API キー等はそのまま)。
  // 非管理者モードで無ければ、インストール先の既定の設定ファイルに書く (アプリが初回起動時にユーザーの設定ファイルへ写す)
  // 既にあったユーザーの設定ファイルは、画面で初期値から変えたときだけ書き換える
  if UserConfigExists then
  begin
    if WorkDir = InitialWorkDir then Exit;
    ConfigPath := UserConfigPath();

    // アプリは終了時にユーザーの設定ファイルへ保存するので、アプリが閉じるのを待ってから書く。
    // ({app} の既定の設定ファイルにはアプリは書き込まないので、そちらに書くときは待たない)
    if not WaitForAppClosed() then
    begin
      Log('アプリが起動したままなので、作業ディレクトリの設定を書き込みませんでした');
      if not WarnIfWorkDirMissing() then
        SuppressibleMsgBox('作業ディレクトリの設定は書き込みませんでした。' + #13#10 +
          'アプリの設定タブの Working Directory で次のフォルダを指定してください:' + #13#10 + WorkDir, mbInformation, MB_OK, IDOK);
      Exit;
    end;
  end
  else
    ConfigPath := ExpandConstant('{app}\appsettings.json');

  R := WriteWorkDirToJson(ConfigPath, WorkDir);
  if R < 0 then
  begin
    // 書き込めずに終わるときも、作業ディレクトリが無ければ同じ警告を出す
    Log('設定ファイルに作業ディレクトリを書き込めませんでした: ' + ConfigPath);
    if not WarnIfWorkDirMissing() then
      SuppressibleMsgBox('設定ファイルに作業ディレクトリを書き込めませんでした:' + #13#10 + ConfigPath + #13#10 + #13#10 +
        'アプリの設定タブの Working Directory で次のフォルダを指定してください:' + #13#10 + WorkDir, mbError, MB_OK, IDOK);
  end;
end;
