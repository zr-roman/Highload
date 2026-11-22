using System.Text.RegularExpressions;

namespace SagaDrill;

/// <summary>The alerts of the saga: the group highload.saga of deploy/k8s/alerts.yaml.</summary>
public static partial class Alerts
{
    public const string File = "deploy/k8s/alerts.yaml";
    public const string Group = "highload.saga";

    /// <summary>
    /// The names of the alerts of the group as git has them: what Prometheus must have loaded. Read with a regular
    /// expression, not a YAML parser: the tool needs no package for two kinds of lines.
    /// </summary>
    public static IReadOnlyList<string> NamesInGit(string path)
    {
        var text = System.IO.File.ReadAllText(path).Replace("\r\n", "\n");
        var group = GroupBody().Match(text);
        if (!group.Success)
            throw new DrillException($"{path} has no group {Group}");
        return AlertName().Matches(group.Groups["body"].Value).Select(m => m.Groups["name"].Value).ToList();
    }

    /// <summary>
    /// An alert's query split into what it measures and its threshold: "max(age) > 300" is "max(age)" and "> 300".
    /// The whole query has no series until the alert fires, and that says nothing about its metrics. What it
    /// measures has a series as soon as its metrics exist, and its value says how far the alert is from firing.
    /// Null if the query does not end with a comparison to a number.
    /// </summary>
    public static (string Measure, string Threshold)? Split(string query)
    {
        var match = Comparison().Match(query);
        return match.Success
            ? (match.Groups["measure"].Value.Trim(), $"{match.Groups["op"].Value} {match.Groups["value"].Value}")
            : null;
    }

    // From "- name: highload.saga" to the next group or the end of the file.
    [GeneratedRegex(@"- name: highload\.saga\n(?<body>.*?)(?=\n\s*- name: |\z)", RegexOptions.Singleline)]
    private static partial Regex GroupBody();

    [GeneratedRegex(@"^\s*- alert:\s*(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex AlertName();

    // The last comparison of the query, to a number at its very end: "sum(a) / sum(b) > 0.05".
    [GeneratedRegex(@"^(?<measure>.*\S)\s*(?<op>>=|<=|==|!=|>|<)\s*(?<value>-?\d+(?:\.\d+)?(?:e[+-]?\d+)?)\s*$", RegexOptions.Singleline)]
    private static partial Regex Comparison();
}
