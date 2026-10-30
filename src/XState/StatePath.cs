namespace XState;

/// <summary>
/// State-path parsing — a port of xstate v6 <c>toStatePath</c> (<c>utils.ts</c>). A state key
/// cannot contain an unescaped <c>.</c>; the escaped form <c>foo\.bar</c> is the key itself.
/// </summary>
internal static class StatePath
{
    /// <summary>
    /// Splits a dotted path into keys. An escaped <c>\.</c> stays escaped inside its segment;
    /// any other escape consumes the next character.
    /// </summary>
    public static List<string> Split(string path)
    {
        var result = new List<string>();
        var segment = new System.Text.StringBuilder();

        for (var i = 0; i < path.Length; i++)
        {
            switch (path[i])
            {
                case '\\':
                    if (i + 1 < path.Length)
                    {
                        if (path[i + 1] == '.') segment.Append('\\');
                        segment.Append(path[i + 1]);
                    }
                    i++;
                    continue;
                case '.':
                    result.Add(segment.ToString());
                    segment.Clear();
                    continue;
                default:
                    segment.Append(path[i]);
                    continue;
            }
        }

        result.Add(segment.ToString());
        return result;
    }

    /// <summary>Whether <paramref name="key"/> contains a <c>.</c> not preceded by <c>\</c>.</summary>
    public static bool HasUnescapedDot(string key)
    {
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '\\') i++;
            else if (key[i] == '.') return true;
        }

        return false;
    }

    /// <summary>xstate v6 <c>describeMachine</c>: <c>machine 'id'</c>, or <c>the machine</c> when anonymous.</summary>
    public static string DescribeMachine(string id) => id.Length > 0 ? $"machine '{id}'" : "the machine";

    /// <summary><see cref="DescribeMachine"/>, capitalized to start a sentence.</summary>
    public static string DescribeMachineStart(string id) => id.Length > 0 ? $"Machine '{id}'" : "The machine";
}
