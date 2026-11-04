using System.Text;

namespace StarCitizenJapaneseTextCreater;

// glob の一致 (crate glob-match 0.2.1 の glob_match の移植。StarBreaker の --filter が使うもの)。
// 「*」は区切りをまたがず、「**」はまたぐ。「?」「[...]」「{a,b}」「!」(否定)・「\」(逃がし) も同じ決まり。区切りは「/」と「\」(Windows の is_separator)
internal static class GlobMatch
{
    private struct Wildcard { public int GlobIndex, PathIndex; }
    private struct State { public int PathIndex, GlobIndex; public Wildcard Wildcard, Globstar; }

    private static bool IsSep(byte c) => c == (byte)'/' || c == (byte)'\\';

    public static bool IsMatch(string glob, string path) => IsMatch(Encoding.UTF8.GetBytes(glob), Encoding.UTF8.GetBytes(path));

    public static bool IsMatch(byte[] glob, byte[] path)
    {
        var state = new State();
        var braces = new State[10];
        int braceLen = 0, longest = 0;
        bool negated = false;
        while (state.GlobIndex < glob.Length && glob[state.GlobIndex] == (byte)'!') { negated = !negated; state.GlobIndex++; }

        State Push(State s)
        {
            braces[braceLen++] = s;
            return new State { PathIndex = s.PathIndex, GlobIndex = s.GlobIndex + 1 };
        }
        State Pop(State s)
        {
            braceLen--;
            var r = new State { PathIndex = longest, GlobIndex = s.GlobIndex, Wildcard = braces[braceLen].Wildcard, Globstar = braces[braceLen].Globstar };
            if (braceLen == 0) longest = 0;
            return r;
        }
        void Backtrack(ref State s) { s.GlobIndex = s.Wildcard.GlobIndex; s.PathIndex = s.Wildcard.PathIndex; }

        while (state.GlobIndex < glob.Length || state.PathIndex < path.Length)
        {
            if (state.GlobIndex < glob.Length)
            {
                byte g = glob[state.GlobIndex];
                if (g == (byte)'*')
                {
                    bool isGlobstar = state.GlobIndex + 1 < glob.Length && glob[state.GlobIndex + 1] == (byte)'*';
                    if (isGlobstar) state.GlobIndex = SkipGlobstars(glob, state.GlobIndex + 2) - 2;
                    state.Wildcard.GlobIndex = state.GlobIndex;
                    state.Wildcard.PathIndex = state.PathIndex + 1;
                    if (isGlobstar)
                    {
                        state.GlobIndex += 2;
                        if (glob.Length == state.GlobIndex) state.Globstar = state.Wildcard;
                        else if ((state.GlobIndex < 3 || glob[state.GlobIndex - 3] == (byte)'/') && glob[state.GlobIndex] == (byte)'/')
                        {
                            if (state.PathIndex == 0 || (state.PathIndex < path.Length && IsSep(path[state.PathIndex - 1])))
                                state.GlobIndex += 1;
                            state.Globstar = state.Wildcard;
                        }
                    }
                    else state.GlobIndex += 1;

                    if (state.Globstar.PathIndex != state.Wildcard.PathIndex && state.PathIndex < path.Length && IsSep(path[state.PathIndex]))
                    {
                        if (state.Globstar.PathIndex > 0 && state.PathIndex + 1 < path.Length)
                        {
                            state.GlobIndex = state.Globstar.GlobIndex;
                            state.Wildcard.GlobIndex = state.Globstar.GlobIndex;
                        }
                        else state.Wildcard.PathIndex = 0;
                    }
                    if (braceLen > 0 && state.GlobIndex < glob.Length && (glob[state.GlobIndex] == (byte)',' || glob[state.GlobIndex] == (byte)'}'))
                        if (SkipBraces(ref state, glob, false) == 0) return false;
                    continue;
                }
                if (g == (byte)'?' && state.PathIndex < path.Length)
                {
                    if (!IsSep(path[state.PathIndex])) { state.GlobIndex++; state.PathIndex++; continue; }
                }
                else if (g == (byte)'[' && state.PathIndex < path.Length)
                {
                    state.GlobIndex++;
                    byte c = path[state.PathIndex];
                    bool neg = false;
                    if (state.GlobIndex < glob.Length && (glob[state.GlobIndex] == (byte)'^' || glob[state.GlobIndex] == (byte)'!')) { neg = true; state.GlobIndex++; }
                    bool first = true, isMatch = false;
                    while (state.GlobIndex < glob.Length && (first || glob[state.GlobIndex] != (byte)']'))
                    {
                        byte low = glob[state.GlobIndex];
                        if (!Unescape(ref low, glob, ref state.GlobIndex)) return false;
                        state.GlobIndex++;
                        byte high;
                        if (state.GlobIndex + 1 < glob.Length && glob[state.GlobIndex] == (byte)'-' && glob[state.GlobIndex + 1] != (byte)']')
                        {
                            state.GlobIndex++;
                            high = glob[state.GlobIndex];
                            if (!Unescape(ref high, glob, ref state.GlobIndex)) return false;
                            state.GlobIndex++;
                        }
                        else high = low;
                        if (low <= c && c <= high) isMatch = true;
                        first = false;
                    }
                    if (state.GlobIndex >= glob.Length) return false;
                    state.GlobIndex++;
                    if (isMatch != neg) { state.PathIndex++; continue; }
                }
                else if (g == (byte)'{' && state.PathIndex < path.Length)
                {
                    if (braceLen >= braces.Length) return false;
                    state = Push(state);
                    continue;
                }
                else if (g == (byte)'}' && braceLen > 0)
                {
                    longest = Math.Max(longest, state.PathIndex);
                    state.GlobIndex++;
                    state = Pop(state);
                    continue;
                }
                else if (g == (byte)',' && braceLen > 0)
                {
                    longest = Math.Max(longest, state.PathIndex);
                    state.PathIndex = braces[braceLen - 1].PathIndex;
                    state.GlobIndex++;
                    state.Wildcard = default; state.Globstar = default;
                    continue;
                }
                else if (state.PathIndex < path.Length && !(g == (byte)'?' || g == (byte)'[' || g == (byte)'{' || (g == (byte)'}' && braceLen > 0) || (g == (byte)',' && braceLen > 0)))
                {
                    byte c = g;
                    if (!Unescape(ref c, glob, ref state.GlobIndex)) return false;
                    bool isMatch = c == (byte)'/' ? IsSep(path[state.PathIndex]) : path[state.PathIndex] == c;
                    if (isMatch)
                    {
                        if (braceLen > 0 && state.GlobIndex > 0 && glob[state.GlobIndex - 1] == (byte)'}')
                        {
                            longest = state.PathIndex;
                            state = Pop(state);
                        }
                        state.GlobIndex++;
                        state.PathIndex++;
                        if (c != (byte)'/') state.Globstar.PathIndex = 0;
                        continue;
                    }
                }
                else if (state.PathIndex < path.Length && (g == (byte)'}' || g == (byte)','))
                {
                    // braceLen == 0 の「}」「,」は普通の文字 (上の分かれで braceLen > 0 は扱った)
                    byte c = g;
                    if (path[state.PathIndex] == c)
                    {
                        state.GlobIndex++; state.PathIndex++; state.Globstar.PathIndex = 0;
                        continue;
                    }
                }
            }

            if (state.Wildcard.PathIndex > 0 && state.Wildcard.PathIndex <= path.Length) { Backtrack(ref state); continue; }

            if (braceLen > 0)
            {
                int bs = SkipBraces(ref state, glob, true);
                if (bs == 0) return false;
                if (bs == 1) { state.PathIndex = braces[braceLen - 1].PathIndex; continue; }
                if (longest > 0) { state = Pop(state); continue; }
                state = braces[braceLen - 1];
                braceLen--;
                if (state.Wildcard.PathIndex > 0 && state.Wildcard.PathIndex <= path.Length) { Backtrack(ref state); continue; }
            }
            return negated;
        }
        if (braceLen > 0 && state.GlobIndex > 0 && glob[state.GlobIndex - 1] == (byte)'}')
        {
            longest = state.PathIndex;
            Pop(state);
        }
        return !negated;
    }

    private static bool Unescape(ref byte c, byte[] glob, ref int gi)
    {
        if (c == (byte)'\\')
        {
            gi++;
            if (gi >= glob.Length) return false;
            c = glob[gi] switch { (byte)'a' => 0x61, (byte)'b' => 0x08, (byte)'n' => (byte)'\n', (byte)'r' => (byte)'\r', (byte)'t' => (byte)'\t', var x => x };
        }
        return true;
    }

    // 0: Invalid、1: Comma、2: EndBrace
    private static int SkipBraces(ref State s, byte[] glob, bool stopOnComma)
    {
        int braces = 1;
        bool inBrackets = false;
        while (s.GlobIndex < glob.Length && braces > 0)
        {
            byte c = glob[s.GlobIndex];
            if (c == (byte)'{' && !inBrackets) braces++;
            else if (c == (byte)'}' && !inBrackets) braces--;
            else if (c == (byte)',' && stopOnComma && braces == 1 && !inBrackets) { s.GlobIndex++; return 1; }
            else if ((c == (byte)'*' || c == (byte)'?' || c == (byte)'[') && !inBrackets)
            {
                if (c == (byte)'[') inBrackets = true;
                if (c == (byte)'*' && s.GlobIndex + 1 < glob.Length && glob[s.GlobIndex + 1] == (byte)'*')
                {
                    s.GlobIndex = SkipGlobstars(glob, s.GlobIndex + 2) - 2;
                    s.GlobIndex++;
                }
            }
            else if (c == (byte)']') inBrackets = false;
            else if (c == (byte)'\\') s.GlobIndex++;
            s.GlobIndex++;
        }
        return braces != 0 ? 0 : 2;
    }

    private static int SkipGlobstars(byte[] glob, int gi)
    {
        while (gi + 3 <= glob.Length && glob[gi] == (byte)'/' && glob[gi + 1] == (byte)'*' && glob[gi + 2] == (byte)'*') gi += 3;
        return gi;
    }

    // StarBreaker の matches_filter (glob のとき): 名前とパターンの「\」を「/」にして比べる
    public static bool MatchesFilter(string name, string? filter) => filter == null || IsMatch(filter.Replace('\\', '/'), name.Replace('\\', '/'));
}
