using System.Text;

namespace Playnest.Services.Scanning;

/// <summary>Node of a Valve KeyValues (VDF/ACF) document.</summary>
public sealed class VdfNode
{
    public string? Value { get; set; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.TryGetValue(key, out var n) ? n : null;

    public string? Str(string key) => this[key]?.Value;

    public long Long(string key) => long.TryParse(Str(key), out var v) ? v : 0;

    /// <summary>Follows a path like "UserLocalConfigStore/Software/Valve/Steam/apps".</summary>
    public VdfNode? Path(string path)
    {
        var n = this;
        foreach (var part in path.Split('/'))
        {
            n = n?[part];
            if (n is null) return null;
        }
        return n;
    }
}

/// <summary>Parser for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, localconfig.vdf).</summary>
public static class Vdf
{
    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        int i = 0;
        string? pendingKey = null;

        while (true)
        {
            var tok = Next(text, ref i, out bool quoted);
            if (tok is null) break;

            if (!quoted && tok == "{")
            {
                var child = new VdfNode();
                if (pendingKey is not null) stack.Peek().Children[pendingKey] = child;
                stack.Push(child);
                pendingKey = null;
            }
            else if (!quoted && tok == "}")
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
            }
            else if (!quoted && tok.StartsWith('[') && tok.EndsWith(']'))
            {
                // Conditional like [$WIN32] - ignored.
            }
            else if (pendingKey is null)
            {
                pendingKey = tok;
            }
            else
            {
                stack.Peek().Children[pendingKey] = new VdfNode { Value = tok };
                pendingKey = null;
            }
        }
        return root;
    }

    private static string? Next(string s, ref int i, out bool quoted)
    {
        quoted = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= s.Length) return null;

        if (s[i] == '{' || s[i] == '}') return s[i++].ToString();

        if (s[i] == '"')
        {
            quoted = true;
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char e = s[i + 1];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => e });
                    if (e is not ('n' or 't' or '\\' or '"')) sb.Insert(sb.Length - 1, '\\');
                    i += 2;
                    continue;
                }
                sb.Append(s[i++]);
            }
            i++; // closing quote
            return sb.ToString();
        }

        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '"' && s[i] != '{' && s[i] != '}') i++;
        return s[start..i];
    }
}
