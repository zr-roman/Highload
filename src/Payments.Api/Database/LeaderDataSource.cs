using Npgsql;

namespace Payments.Api.Database;

/// <summary>
/// The connections the leader locks are taken on (<see cref="AdvisoryLeaderLock"/>), when they are not those of the
/// rest of the service: ConnectionStrings:PaymentsLeader. In Kubernetes the queries go through PgBouncer in
/// transaction mode, which gives each transaction whichever server connection is free; a session advisory lock taken
/// there belongs to a server connection the client does not keep, so the lock goes straight to the primary instead
/// (deploy/k8s/postgres.yaml, Pooler). Not registered when there is no such string: the jobs then use the main pool.
/// </summary>
public sealed record LeaderDataSource(NpgsqlDataSource Value);
