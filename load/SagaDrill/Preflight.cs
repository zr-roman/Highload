namespace SagaDrill;

/// <summary>A check of the drill: its name and the evidence, in the words of the report.</summary>
public sealed record Check(string Name, bool Ok, string Detail);

/// <summary>A query of a panel or of an alert, and what Prometheus answered.</summary>
/// <param name="Threshold">For an alert, the threshold taken off its query: "> 300".</param>
/// <param name="Found">For a query without series, what Prometheus has of its metrics (Selectors.FoundAsync).</param>
public sealed record QueryCheck(string What, string Expression, QueryResult Result, string? Threshold = null, string? Found = null)
{
    public bool Ok => Result.Error is null && Result.Series.Count > 0;
}

/// <summary>What the preflight found: the stand, the warm-up load, the queries, and the checks made of them.</summary>
/// <param name="PaymentsApi">The replicas of Payments.Api at the end of the warm-up: HPA adds them under load.</param>
public sealed record PreflightResult(
    IReadOnlyList<Check> Checks, K6Result? Warmup, IReadOnlyList<QueryCheck> Queries, Replicas? PaymentsApi = null);

/// <summary>
/// Before the drill: the stand is as it needs it, and every query of the saga's panels and alerts finds its metrics
/// in Prometheus. That is the one thing the tests of the rules (deploy/k8s/alerts.test.yaml) cannot see: their series
/// are named by hand, so a metric renamed in the code leaves them green and the alert silent.
/// </summary>
public static class Preflight
{
    public static async Task<PreflightResult> RunAsync(DrillOptions options, Prometheus prometheus, string reports, CancellationToken ct)
    {
        var (rules, rulesCheck) = await RulesAsync(prometheus, ct);
        var checks = new List<Check> { await ArgoAsync(), await AntifraudAsync(), rulesCheck };

        // A load on a stand that is not ready would only make the report longer.
        if (checks.Any(c => !c.Ok))
            return new PreflightResult(checks, null, []);

        Console.WriteLine($"Warm-up: {options.Rate} payments a second for {options.WarmupSeconds} s");
        var warmup = await K6.RunAsync(options.Rate, options.WarmupSeconds, reports, "warmup", ct);
        var (paymentsApi, _) = await Stand.DeploymentAsync("payments-api");

        Console.WriteLine("Querying Prometheus");
        checks.Add(await AntifraudAnswersAsync(prometheus, ct));
        var queries = new List<QueryCheck>();
        foreach (var query in Dashboard.SagaQueries(Dashboard.File))
            queries.Add(new QueryCheck($"{query.Panel} — {query.Legend}", query.Expression, await prometheus.QueryAsync(query.Expression, ct)));
        foreach (var rule in rules)
        {
            queries.Add(Alerts.Split(rule.Query) is var (measure, threshold)
                ? new QueryCheck($"алерт {rule.Name}", measure, await prometheus.QueryAsync(measure, ct), threshold)
                : new QueryCheck($"алерт {rule.Name}", rule.Query,
                    new QueryResult([], "the query does not end with a comparison to a number: the tool cannot take its threshold off")));
        }

        // An empty query says only that something does not match. What Prometheus has of its metrics says what:
        // no such metric (renamed in the code), or the metric with other labels (server_address of another form).
        for (var i = 0; i < queries.Count; i++)
            if (queries[i] is { Ok: false, Result.Error: null } query)
                queries[i] = query with { Found = await Selectors.FoundAsync(prometheus, query.Expression, ct) };

        var empty = queries.Where(q => !q.Ok).ToList();
        checks.Add(new Check("запросы панелей и алертов саги нашли свои метрики", empty.Count == 0, empty.Count == 0
            ? $"все {queries.Count} вернули серии"
            : $"без серий {empty.Count} из {queries.Count}: {string.Join("; ", empty.Select(q => q.What))}"));
        return new PreflightResult(checks, warmup, queries, paymentsApi);
    }

    // The drill shows what the saga does when the antifraud fails, so before it the antifraud must answer. The
    // threshold is the 5 % of AntifraudFailingOpen, here of all the checks without a decision, of which fail open is
    // about half: a stand above it is near that alert before the drill has stopped anything. The first preflights
    // of 22.11.2026 found 45-92 % without a decision, and every other check passed.
    private static async Task<Check> AntifraudAnswersAsync(Prometheus prometheus, CancellationToken ct)
    {
        const string name = "антифрод отвечает: без решения меньше 5 % проверок в запросе";
        var result = await prometheus.QueryAsync(
            """
            sum(rate(payments_antifraud_decisions_total{path="request", outcome="unknown"}[1m]))
              / sum(rate(payments_antifraud_decisions_total{path="request"}[1m]))
            """, ct);
        if (result.Error is { } error)
            return new Check(name, false, error);
        if (result.Series is not [{ Value: var share }] || double.IsNaN(share))
            return new Check(name, false, "за последнюю минуту прогрева не было ни одной проверки");
        return new Check(name, share < 0.05, $"{share * 100:0.0} % за последнюю минуту прогрева".Replace('.', ','));
    }

    // The drill stops the antifraud by hand, so it must find Argo CD as it leaves it: synced, healthy, and putting
    // back what is changed by hand. A selfHeal found off is most likely a drill that did not finish.
    private static async Task<Check> ArgoAsync()
    {
        const string name = "Argo CD: приложение highload синхронизировано и само возвращает ручные правки";
        var (app, error) = await Stand.ArgoAppAsync();
        if (app is null)
            return new Check(name, false, $"не удалось прочитать приложение: {error}");

        var revision = app.Revision.Length > 12 ? app.Revision[..12] : app.Revision;
        var ok = app is { Sync: "Synced", Health: "Healthy", SelfHeal: true };
        return new Check(name, ok, $"ревизия `{revision}`, {app.Sync}, {app.Health}, selfHeal {(app.SelfHeal ? "включён" : "выключен")}" +
            (app.SelfHeal ? "" : ": если его сняли прошлые учения, вернуть — `kubectl apply -f deploy/argocd/highload.yaml`"));
    }

    private static async Task<Check> AntifraudAsync()
    {
        const string name = "антифрод: все реплики готовы";
        var (replicas, error) = await Stand.DeploymentAsync("antifraud");
        return replicas is null
            ? new Check(name, false, $"не удалось прочитать Deployment antifraud: {error}")
            : new Check(name, replicas.Desired > 0 && replicas.Ready == replicas.Desired, $"{replicas.Ready} из {replicas.Desired}");
    }

    // The alerts of git, loaded: the same names, and none of them failing to evaluate. "unknown" is a rule loaded
    // so recently that it has not been evaluated yet: it will be within 30 s.
    private static async Task<(IReadOnlyList<LoadedRule> Rules, Check Check)> RulesAsync(Prometheus prometheus, CancellationToken ct)
    {
        const string name = $"Prometheus загрузил правила {Alerts.Group} из git";
        var expected = Alerts.NamesInGit(Alerts.File);
        var loaded = await prometheus.AlertRulesAsync(Alerts.Group, ct);
        if (loaded is null)
            return ([], new Check(name, false, $"группы `{Alerts.Group}` в Prometheus нет: Argo CD ещё не применил `{Alerts.File}`?"));

        var names = loaded.Select(r => r.Name).ToList();
        var missing = expected.Except(names).ToList();
        var extra = names.Except(expected).ToList();
        var failing = loaded.Where(r => r.Health is not ("ok" or "unknown")).ToList();
        var problems = new List<string>();
        if (missing.Count > 0)
            problems.Add($"нет {string.Join(", ", missing)}");
        if (extra.Count > 0)
            problems.Add($"лишние {string.Join(", ", extra)}");
        problems.AddRange(failing.Select(r => $"{r.Name}: {r.Health}, {r.LastError}"));
        return (loaded, new Check(name, problems.Count == 0, problems.Count == 0
            ? $"{string.Join(", ", names)} — как в `{Alerts.File}`"
            : $"{string.Join("; ", problems)}: Argo CD ещё не применил последний коммит?"));
    }
}
