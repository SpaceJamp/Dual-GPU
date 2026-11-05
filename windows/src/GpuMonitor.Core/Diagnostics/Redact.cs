using System.Text.RegularExpressions;

namespace GpuMonitor.Core.Diagnostics;

/// <summary>Strips the home path, user name and machine name from report text.</summary>
public sealed class Redactor
{
    private const string Word = @"[\p{L}\p{N}_]";
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private readonly List<(Regex Rx, string With)> _rules = new();

    public Redactor(string userName, string machineName, string homePath)
    {
        if (!string.IsNullOrWhiteSpace(homePath))
        {
            var trimmed = homePath.TrimEnd('\\', '/');
            if (trimmed.Length > 0)
            {
                var forms = new[] { trimmed, trimmed.Replace("\\", "\\\\") }
                    .Distinct().OrderByDescending(s => s.Length)
                    .Select(Regex.Escape);
                _rules.Add((new Regex($"(?:{string.Join("|", forms)})(?!{Word})", Opts), "<home>"));
            }
        }
        AddToken(userName, "<user>");
        AddToken(machineName, "<host>");
    }

    public static Redactor ForCurrentUser() => new(Environment.UserName, Environment.MachineName,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void AddToken(string token, string with)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var t = Regex.Escape(token);
        // Short tokens are only replaced when they sit right after a path separator.
        var pattern = token.Length < 3
            ? $@"(?<=[\\/]){t}(?!{Word})"
            : $"(?<!{Word}){t}(?!{Word})";
        _rules.Add((new Regex(pattern, Opts), with));
    }

    public string Apply(string text)
    {
        foreach (var (rx, with) in _rules) text = rx.Replace(text, with);
        return text;
    }
}
