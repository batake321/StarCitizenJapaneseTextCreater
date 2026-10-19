using System.Text.RegularExpressions;
using System.Windows.Threading;
using Windows.Gaming.Input;

namespace StarCitizenJapaneseTextCreater;

// ジョイスティック / ゲームパッドの「押された瞬間」を読む部品 (Windows.Gaming.Input)。
//   ジョイスティック = RawGameController。ただし Gamepad.FromGameController が非 null のものはゲームパッドとして扱う
//   UI スレッドの DispatcherTimer で約 16ms ごとに読み、離→押 に変わった入力だけを InputPressed で通知する
//   通知する入力コードは js 番号 / gp1_ を付けない部分コード ("button3", "hat1_up", "a" など)。
//   js 番号と物理デバイスの対応はこの部品では決めない (actionmaps.xml と利用者の手動修正で決まる)
//   軸の動きは通知しない
public enum GameControllerKind
{
    Joystick,
    Gamepad,
}

// 接続中のデバイス 1 つ
public sealed record GameControllerDeviceInfo(GameControllerKind Kind, ushort VendorId, ushort ProductId, string DisplayName);

// 押された入力 1 つ。InputCode は部分コード ("button3", "hat1_up", "a" など)
public sealed class GameControllerInputEventArgs : EventArgs
{
    public GameControllerKind Kind { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string DisplayName { get; }
    public string InputCode { get; }
    // 機器を見分ける ID (ジョイスティックは RawGameController.NonRoamableId。ゲームパッドと、取れなかったときは "")
    public string DeviceId { get; }

    public GameControllerInputEventArgs(GameControllerKind kind, ushort vendorId, ushort productId, string displayName, string inputCode, string deviceId = "")
    {
        Kind = kind;
        VendorId = vendorId;
        ProductId = productId;
        DisplayName = displayName;
        InputCode = inputCode;
        DeviceId = deviceId ?? "";
    }
}

public sealed class GameControllerInput : IDisposable
{
    private const double TriggerThreshold = 0.5;          // LT / RT はこの値を超えた瞬間を押下とする
    private const double TriggerReleaseThreshold = 0.3;   // 押下中の LT / RT は、この値を下回ったら離したとみなす (しきい値付近の揺れで続けて通知しないため)
    private const long RetryDelayMs = 1000;        // 新しいデバイスの最初の読み取りに失敗したとき、再構築で再挑戦するまでの間隔

    // ゲームパッドのボタンと部分コード (gp1_ を除いた表記。BindingEditDialog.GamepadKeys と同じ)
    private static readonly (GamepadButtons Button, string Code)[] GamepadButtonCodes =
    {
        (GamepadButtons.A, "a"), (GamepadButtons.B, "b"), (GamepadButtons.X, "x"), (GamepadButtons.Y, "y"),
        (GamepadButtons.LeftShoulder, "shoulderl"), (GamepadButtons.RightShoulder, "shoulderr"),
        (GamepadButtons.LeftThumbstick, "thumbl"), (GamepadButtons.RightThumbstick, "thumbr"),
        (GamepadButtons.DPadUp, "dpad_up"), (GamepadButtons.DPadDown, "dpad_down"),
        (GamepadButtons.DPadLeft, "dpad_left"), (GamepadButtons.DPadRight, "dpad_right"),
        (GamepadButtons.Menu, "start"), (GamepadButtons.View, "back"),
    };

    // actionmaps.xml の Product 属性の末尾の GUID。先頭 8 桁 = PID(上位 4 桁) + VID(下位 4 桁)、末尾 12 桁 = "PIDVID" の ASCII
    private static readonly Regex ProductGuidRegex = new(
        @"\{([0-9A-Fa-f]{4})([0-9A-Fa-f]{4})-0000-0000-0000-504944564944\}",
        RegexOptions.Compiled);

    private sealed class JoystickState
    {
        public required RawGameController Controller;
        public required ushort VendorId;
        public required ushort ProductId;
        public required string DisplayName;
        public required string DeviceId;            // RawGameController.NonRoamableId (取れなければ "")
        public required bool[] Buttons;
        public required GameControllerSwitchPosition[] Switches;
        public required double[] Axes;              // GetCurrentReading に渡すための入れ物 (軸は通知しない)
        public bool[] PrevButtons = Array.Empty<bool>();
        // ハットごとの「最後に読んだ斜めでない値」(中央か上下左右)。斜めの読み値では変えない
        public GameControllerSwitchPosition[] LastStraightSwitches = Array.Empty<GameControllerSwitchPosition>();
    }

    private sealed class GamepadState
    {
        public required Gamepad Gamepad;
        public required ushort VendorId;
        public required ushort ProductId;
        public required string DisplayName;
        public GamepadButtons PrevButtons;
        public bool PrevLeftTrigger;
        public bool PrevRightTrigger;
    }

    private readonly DispatcherTimer _timer;
    private List<JoystickState> _joysticks = new();
    private List<GamepadState> _gamepads = new();
    private int _devicesChanged = 1;   // 接続・切断があった (イベントは別スレッドで来るので、次の Tick で一覧を作り直す)
    private long _retryAtMs;           // 0 以外 = 読み取りに失敗したデバイスがあり、Environment.TickCount64 がこの値以上になったら再構築する
    private int _stopGeneration;       // Stop() のたびに増やす。通知の途中で Stop されたか (その後 Start されても) を見分ける
    private bool _inTick;              // OnTick の実行中 (受け手が入れ子のメッセージループを回したときの再入を防ぐ)
    private bool _subscribed;
    private bool _disposed;

    // 押された瞬間 (離→押) の通知。UI スレッドで呼ばれる
    public event EventHandler<GameControllerInputEventArgs>? InputPressed;

    public GameControllerInput()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    // ポーリングを始める。UI スレッドから呼ぶ
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer.IsEnabled) return;   // 動作中は何もしない (前回状態を取り直すと押下を取りこぼすため)
        if (!_subscribed)
        {
            RawGameController.RawGameControllerAdded += OnDeviceChanged;
            RawGameController.RawGameControllerRemoved += OnDeviceChanged;
            Gamepad.GamepadAdded += OnGamepadChanged;
            Gamepad.GamepadRemoved += OnGamepadChanged;
            _subscribed = true;
        }
        // 止めている間に押した入力を通知しないよう、続けて接続しているデバイスの前回状態を今の読み値で取り直す
        ResyncPrevious();
        Interlocked.Exchange(ref _devicesChanged, 1);
        _timer.Start();
    }

    // ポーリングを止める。止めている間の押下は通知しない
    public void Stop()
    {
        _stopGeneration++;
        _timer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        if (_subscribed)
        {
            RawGameController.RawGameControllerAdded -= OnDeviceChanged;
            RawGameController.RawGameControllerRemoved -= OnDeviceChanged;
            Gamepad.GamepadAdded -= OnGamepadChanged;
            Gamepad.GamepadRemoved -= OnGamepadChanged;
            _subscribed = false;
        }
        _joysticks = new();
        _gamepads = new();
    }

    // 接続中のデバイスの一覧 (種別、VID、PID、表示名)。その場で Windows.Gaming.Input から読み直す。
    //   アプリ起動直後は Windows.Gaming.Input の一覧がまだ空のことがある (接続済みのデバイスも Added イベントの後で一覧に入る)
    public static IReadOnlyList<GameControllerDeviceInfo> GetConnectedDevices()
    {
        var list = new List<GameControllerDeviceInfo>();
        var (joysticks, gamepads) = EnumerateControllers();
        foreach (var raw in joysticks)
            list.Add(new GameControllerDeviceInfo(GameControllerKind.Joystick, raw.HardwareVendorId, raw.HardwareProductId, raw.DisplayName ?? ""));
        foreach (var (_, vid, pid, name) in gamepads)
            list.Add(new GameControllerDeviceInfo(GameControllerKind.Gamepad, vid, pid, name));
        return list;
    }

    // actionmaps.xml の <options Product="..."> から VID / PID を取り出す。
    //   例: " VKBsim Gladiator EVO L    {0201231D-0000-0000-0000-504944564944}" → VID=0x231D, PID=0x0201
    //   GUID が無い、または末尾が "PIDVID" (504944564944) の形でない場合は false
    public static bool TryParseProductVidPid(string? product, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (string.IsNullOrEmpty(product)) return false;
        var m = ProductGuidRegex.Match(product);
        if (!m.Success) return false;
        productId = Convert.ToUInt16(m.Groups[1].Value, 16);
        vendorId = Convert.ToUInt16(m.Groups[2].Value, 16);
        return true;
    }

    // --- 内部 ---

    private void OnDeviceChanged(object? sender, RawGameController e) => Interlocked.Exchange(ref _devicesChanged, 1);
    private void OnGamepadChanged(object? sender, Gamepad e) => Interlocked.Exchange(ref _devicesChanged, 1);

    // RawGameController の一覧を、ジョイスティックとゲームパッドに分ける。
    //   Gamepad.FromGameController が非 null のものはゲームパッド側に入れる。
    //   Gamepad.Gamepads にだけあるもの (RawGameController 側に見つからないもの) もゲームパッドとして足す
    private static (List<RawGameController> Joysticks, List<(Gamepad Pad, ushort Vid, ushort Pid, string Name)> Gamepads) EnumerateControllers()
    {
        var joysticks = new List<RawGameController>();
        var gamepads = new List<(Gamepad Pad, ushort Vid, ushort Pid, string Name)>();
        foreach (var raw in RawGameController.RawGameControllers)
        {
            var pad = Gamepad.FromGameController(raw);
            if (pad != null)
                gamepads.Add((pad, raw.HardwareVendorId, raw.HardwareProductId, raw.DisplayName ?? ""));
            else
                joysticks.Add(raw);
        }
        foreach (var pad in Gamepad.Gamepads)
        {
            if (gamepads.Any(g => ReferenceEquals(g.Pad, pad) || g.Pad.Equals(pad))) continue;
            var raw = RawGameController.FromGameController(pad);
            gamepads.Add((pad, raw?.HardwareVendorId ?? 0, raw?.HardwareProductId ?? 0, raw?.DisplayName ?? ""));
        }
        return (joysticks, gamepads);
    }

    // 一覧にあるデバイスの前回状態 (ボタン・ハット・トリガー) を今の読み値で取り直す (Start 用)。
    //   読めなかったデバイスは一覧から外す (次の再構築で新しいデバイスとして読み直す)
    private void ResyncPrevious()
    {
        var joysticks = new List<JoystickState>();
        foreach (var j in _joysticks)
        {
            try
            {
                j.Controller.GetCurrentReading(j.Buttons, j.Switches, j.Axes);
            }
            catch (Exception)
            {
                continue;
            }
            Array.Copy(j.Buttons, j.PrevButtons, j.Buttons.Length);
            // 止めている間の動きは分からないので、斜めを読んだハットは新しくつないだときと同じく中央から数え直す
            for (int i = 0; i < j.Switches.Length; i++)
                j.LastStraightSwitches[i] = GameControllerSwitchPosition.Center;
            UpdateLastStraight(j.Switches, j.LastStraightSwitches);
            joysticks.Add(j);
        }

        var gamepads = new List<GamepadState>();
        foreach (var g in _gamepads)
        {
            GamepadReading r;
            try
            {
                r = g.Gamepad.GetCurrentReading();
            }
            catch (Exception)
            {
                continue;
            }
            g.PrevButtons = r.Buttons;
            g.PrevLeftTrigger = IsTriggerPressed(r.LeftTrigger, g.PrevLeftTrigger);
            g.PrevRightTrigger = IsTriggerPressed(r.RightTrigger, g.PrevRightTrigger);
            gamepads.Add(g);
        }

        _joysticks = joysticks;
        _gamepads = gamepads;
    }

    // 一覧を作り直す。続けて接続しているデバイスは前回の状態を引き継ぐ。
    //   新しく見つかったデバイスは、最初に読んだ状態を「前回」とする (接続時に押していた入力は通知しない)
    //   新しいデバイスの最初の読み取りで例外が出たら、その 1 台だけ今回の一覧から外し、1 秒以上たってから再構築して再挑戦する
    private void RebuildDevices()
    {
        var (rawJoysticks, rawGamepads) = EnumerateControllers();
        bool retry = false;

        var joysticks = new List<JoystickState>();
        foreach (var raw in rawJoysticks)
        {
            var existing = _joysticks.FirstOrDefault(j => ReferenceEquals(j.Controller, raw) || j.Controller.Equals(raw));
            if (existing != null)
            {
                joysticks.Add(existing);
                continue;
            }
            JoystickState s;
            try
            {
                s = new JoystickState
                {
                    Controller = raw,
                    VendorId = raw.HardwareVendorId,
                    ProductId = raw.HardwareProductId,
                    DisplayName = raw.DisplayName ?? "",
                    DeviceId = raw.NonRoamableId ?? "",
                    Buttons = new bool[raw.ButtonCount],
                    Switches = new GameControllerSwitchPosition[raw.SwitchCount],
                    Axes = new double[raw.AxisCount],
                };
                raw.GetCurrentReading(s.Buttons, s.Switches, s.Axes);
            }
            catch (Exception)
            {
                retry = true;
                continue;
            }
            s.PrevButtons = (bool[])s.Buttons.Clone();
            // 斜めでない値をまだ読んでいないので中央とし、最初の読み値で更新する (つないだときに押していた方向は通知しない)
            s.LastStraightSwitches = new GameControllerSwitchPosition[s.Switches.Length];
            Array.Fill(s.LastStraightSwitches, GameControllerSwitchPosition.Center);
            UpdateLastStraight(s.Switches, s.LastStraightSwitches);
            joysticks.Add(s);
        }

        var gamepads = new List<GamepadState>();
        foreach (var (pad, vid, pid, name) in rawGamepads)
        {
            var existing = _gamepads.FirstOrDefault(g => ReferenceEquals(g.Gamepad, pad) || g.Gamepad.Equals(pad));
            if (existing != null)
            {
                gamepads.Add(existing);
                continue;
            }
            GamepadReading r;
            try
            {
                r = pad.GetCurrentReading();
            }
            catch (Exception)
            {
                retry = true;
                continue;
            }
            gamepads.Add(new GamepadState
            {
                Gamepad = pad,
                VendorId = vid,
                ProductId = pid,
                DisplayName = name,
                PrevButtons = r.Buttons,
                PrevLeftTrigger = r.LeftTrigger > TriggerThreshold,
                PrevRightTrigger = r.RightTrigger > TriggerThreshold,
            });
        }

        _joysticks = joysticks;
        _gamepads = gamepads;
        _retryAtMs = retry ? Environment.TickCount64 + RetryDelayMs : 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 受け手が ShowDialog などで入れ子のメッセージループを回している間の Tick は何もしない
        //   (その間は読み取りもしないので、ダイアログ表示中に押して離した入力は通知されない。
        //    閉じた時点でまだ押している入力は、外側の OnTick が終わった後の次の Tick で拾う)
        if (_disposed || _inTick) return;
        _inTick = true;
        try
        {
            TickCore();
        }
        finally
        {
            _inTick = false;
        }
    }

    private void TickCore()
    {

        var pressed = new List<GameControllerInputEventArgs>();

        // 接続・切断の印があればすぐ、読み取りに失敗したデバイスがあれば 1 秒以上たってから作り直す
        bool changed = Interlocked.Exchange(ref _devicesChanged, 0) == 1;
        bool retryDue = _retryAtMs != 0 && Environment.TickCount64 >= _retryAtMs;
        if (changed || retryDue)
        {
            try
            {
                RebuildDevices();
            }
            catch (Exception)
            {
                // 一覧の取得などで予期しない失敗をしたら、1 秒以上たってから作り直す (今の一覧のまま読み続ける)
                _retryAtMs = Environment.TickCount64 + RetryDelayMs;
            }
        }

        // 読み取りはデバイス単位。失敗した 1 台だけ一覧から外し、他のデバイスの処理と通知は続ける。
        //   外した 1 台は 1 秒以上たってからの再構築で、新しいデバイスとして読み直す
        bool failed = false;
        var devicePressed = new List<GameControllerInputEventArgs>();
        var joysticks = new List<JoystickState>(_joysticks.Count);
        foreach (var j in _joysticks)
        {
            devicePressed.Clear();
            try
            {
                PollJoystick(j, devicePressed);
            }
            catch (Exception)
            {
                failed = true;
                continue;
            }
            pressed.AddRange(devicePressed);
            joysticks.Add(j);
        }
        var gamepads = new List<GamepadState>(_gamepads.Count);
        foreach (var g in _gamepads)
        {
            devicePressed.Clear();
            try
            {
                PollGamepad(g, devicePressed);
            }
            catch (Exception)
            {
                failed = true;
                continue;
            }
            pressed.AddRange(devicePressed);
            gamepads.Add(g);
        }
        if (failed)
        {
            _joysticks = joysticks;
            _gamepads = gamepads;
            _retryAtMs = Environment.TickCount64 + RetryDelayMs;
        }

        // 受け手の中で Stop (その後 Start されても) / Dispose されたら、この Tick の残りは配らない (受け手の例外はそのまま上へ伝える)
        int generation = _stopGeneration;
        foreach (var args in pressed)
        {
            if (_disposed || _stopGeneration != generation) break;
            InputPressed?.Invoke(this, args);
        }
    }

    private static void PollJoystick(JoystickState j, List<GameControllerInputEventArgs> pressed)
    {
        j.Controller.GetCurrentReading(j.Buttons, j.Switches, j.Axes);

        // ボタン: 0 始まりのインデックス + 1 を番号とする (index 0 → "button1")。
        //   ※ この番号が Star Citizen の js?_button? の番号と一致するかは未確認
        for (int i = 0; i < j.Buttons.Length; i++)
        {
            if (j.Buttons[i] && !j.PrevButtons[i])
                pressed.Add(new GameControllerInputEventArgs(GameControllerKind.Joystick, j.VendorId, j.ProductId, j.DisplayName, $"button{i + 1}", j.DeviceId));
        }

        // ハット: switch のインデックス + 1 を hat 番号とする。読み値が上下左右のどれかで、
        //   「最後に読んだ斜めでない値」(中央か上下左右) と違うときに通知する
        //   (上 → 右上 → 上 では再通知しない、中央 → 左上 → 上 では「上」、上 → 右上 → 右 では「右」を通知)。
        //   斜めは通知しない: ゲームのハットの入力名は hat?_up/down/left/right だけ
        for (int i = 0; i < j.Switches.Length; i++)
        {
            if (j.Switches[i] == j.LastStraightSwitches[i]) continue;
            var dir = j.Switches[i] switch
            {
                GameControllerSwitchPosition.Up => "up",
                GameControllerSwitchPosition.Down => "down",
                GameControllerSwitchPosition.Left => "left",
                GameControllerSwitchPosition.Right => "right",
                _ => null,
            };
            if (dir != null)
                pressed.Add(new GameControllerInputEventArgs(GameControllerKind.Joystick, j.VendorId, j.ProductId, j.DisplayName, $"hat{i + 1}_{dir}", j.DeviceId));
        }

        Array.Copy(j.Buttons, j.PrevButtons, j.Buttons.Length);
        UpdateLastStraight(j.Switches, j.LastStraightSwitches);
    }

    // 斜めでない読み値 (中央か上下左右) だけを「最後に読んだ斜めでない値」に入れる
    private static void UpdateLastStraight(GameControllerSwitchPosition[] current, GameControllerSwitchPosition[] lastStraight)
    {
        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] is GameControllerSwitchPosition.Center or GameControllerSwitchPosition.Up or GameControllerSwitchPosition.Down
                or GameControllerSwitchPosition.Left or GameControllerSwitchPosition.Right)
                lastStraight[i] = current[i];
        }
    }

    private static void PollGamepad(GamepadState g, List<GameControllerInputEventArgs> pressed)
    {
        var r = g.Gamepad.GetCurrentReading();

        foreach (var (button, code) in GamepadButtonCodes)
        {
            if ((r.Buttons & button) != 0 && (g.PrevButtons & button) == 0)
                pressed.Add(new GameControllerInputEventArgs(GameControllerKind.Gamepad, g.VendorId, g.ProductId, g.DisplayName, code));
        }

        bool lt = IsTriggerPressed(r.LeftTrigger, g.PrevLeftTrigger);
        bool rt = IsTriggerPressed(r.RightTrigger, g.PrevRightTrigger);
        if (lt && !g.PrevLeftTrigger)
            pressed.Add(new GameControllerInputEventArgs(GameControllerKind.Gamepad, g.VendorId, g.ProductId, g.DisplayName, "triggerl_btn"));
        if (rt && !g.PrevRightTrigger)
            pressed.Add(new GameControllerInputEventArgs(GameControllerKind.Gamepad, g.VendorId, g.ProductId, g.DisplayName, "triggerr_btn"));

        g.PrevButtons = r.Buttons;
        g.PrevLeftTrigger = lt;
        g.PrevRightTrigger = rt;
    }

    // LT / RT の押下状態 (戻り幅付き): 離している間は TriggerThreshold を超えたら押下、押下中は TriggerReleaseThreshold を下回ったら離した
    private static bool IsTriggerPressed(double value, bool wasPressed) =>
        wasPressed ? value >= TriggerReleaseThreshold : value > TriggerThreshold;
}
