namespace Payments.Api.Recovery;

public sealed class RecoveryOptions
{
    /// <summary>Пауза между проходами, когда батч пришёл неполным (зависших платежей больше нет).
    /// Полный батч → следующий берётся сразу.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Сколько платёж должен пролежать в очереди, чтобы его взяло восстановление.
    /// Должен быть больше двух самых долгих саг запроса (авторизация, проверка антифрода и void, каждый вызов —
    /// до общего таймаута своего конвейера), иначе восстановление перехватит платёж, по которому эндпоинт ещё
    /// ждёт ответ или делает отмену.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько зависших платежей брать из базы за один запрос.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Сколько запросов к банку идут одновременно.
    /// Небольшое, потому что восстановление делит с платежами тот же rate limiter и circuit breaker.</summary>
    public int MaxParallelism { get; set; } = 8;
}
