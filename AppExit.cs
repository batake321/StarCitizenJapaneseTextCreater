namespace StarCitizenJapaneseTextCreater;

// アプリを閉じるときの取り消しと、閉じる前に終わるのを待つ処理 (インデックス構築・装備データの再抽出・バックアップの取り込み・PC 間の同期の取り込み)。
// これらの処理は BeginWork で登録し、Token (呼び出し元の取り消しと合わせたもの) を見て途中で止まる。止まると、それぞれの控え
// (構築前の控え .prebuild.bak・再抽出の *_bak 表・取り込み前の控え .import.bak / .sync.bak) から元に戻してから終わる。
// MainWindow の閉じる処理 (RunExitAsync) が Cancel を呼び、WaitIdleAsync で戻し終わるまで待つ。
// Windows のサインアウト・シャットダウン (MainWindow.OnSessionEnding) では Cancel だけを呼び、待たない
internal static class AppExit
{
    private static readonly CancellationTokenSource s_cts = new();
    private static readonly object s_lock = new();
    private static readonly List<Work> s_running = new();

    public static CancellationToken Token => s_cts.Token;
    public static bool IsExiting => s_cts.IsCancellationRequested;

    public static void Cancel()
    {
        try { s_cts.Cancel(); } catch { }
    }

    // 処理の始めに呼び、終わったら (成功・失敗・取り消しのどれでも) Dispose する。label は待つ間の表題に出す名前 (例 "インデックス構築")
    public static IDisposable BeginWork(string label)
    {
        var w = new Work(label);
        lock (s_lock) s_running.Add(w);
        return w;
    }

    // 今走っている処理の名前 (重ならないように 1 つずつ)
    public static List<string> RunningLabels()
    {
        lock (s_lock) return s_running.Select(w => w.Label).Distinct().ToList();
    }

    // 登録した処理がすべて終わるまで、timeout まで待つ。終わったら true
    public static async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task[] pending;
            lock (s_lock) pending = s_running.Select(w => w.Done.Task).ToArray<Task>();
            if (pending.Length == 0) return true;
            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(left));
        }
    }

    private sealed class Work : IDisposable
    {
        public readonly string Label;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        public Work(string label) => Label = label;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (s_lock) s_running.Remove(this);
            Done.TrySetResult();
        }
    }
}
