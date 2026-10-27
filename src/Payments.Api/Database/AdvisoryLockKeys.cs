namespace Payments.Api.Database;

/// <summary>
/// Keys of PostgreSQL advisory locks in the payments database; each background job needs its own
/// </summary>
internal static class AdvisoryLockKeys
{
    internal const long OutboxRelay = 7_301_001;
    internal const long PaymentRecovery = 7_301_002;
}
