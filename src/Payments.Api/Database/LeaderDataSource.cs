using Npgsql;

namespace Payments.Api.Database;

/// <summary>
/// The connections the leader locks are taken on (<see cref="AdvisoryLeaderLock"/>), when they are not those of the
/// rest of the service: ConnectionStrings:PaymentsLeader. For a pool such as PgBouncer in transaction mode, which gives
/// each transaction whichever server connection is free: a session advisory lock taken through it belongs to a server
/// connection the client does not keep, and outlives the client (docs/postgres-failover.md, «PgBouncer: опыт»). The
/// stand connects straight to the primary today and sets no such string: the jobs then use the main pool.
/// </summary>
public sealed record LeaderDataSource(NpgsqlDataSource Value);
