using Dapper;
using Npgsql;

namespace VNext.Memory.Infrastructure.Postgres;

public sealed class PostgresSchemaMigrator(NpgsqlDataSource dataSource)
{
    private const string MigrationSql = """
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        CREATE TABLE IF NOT EXISTS edge_devices (
            id uuid PRIMARY KEY,
            tenant_id text NOT NULL,
            principal_id text NOT NULL,
            device_name text NOT NULL,
            token_hash char(64) NOT NULL UNIQUE,
            is_active boolean NOT NULL DEFAULT true,
            created_at timestamptz NOT NULL,
            last_seen_at timestamptz NULL
        );

        CREATE INDEX IF NOT EXISTS ix_edge_devices_tenant
            ON edge_devices (tenant_id, principal_id);

        CREATE TABLE IF NOT EXISTS evidence_events (
            id uuid PRIMARY KEY,
            tenant_id text NOT NULL,
            principal_id text NOT NULL,
            device_id uuid NULL,
            actor_id text NOT NULL,
            session_id text NULL,
            source_type text NOT NULL,
            source_reference text NULL,
            kind smallint NOT NULL,
            trust smallint NOT NULL,
            content text NOT NULL,
            content_hash char(64) NOT NULL,
            scope jsonb NOT NULL,
            occurred_at timestamptz NOT NULL,
            created_at timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_evidence_tenant_time
            ON evidence_events (tenant_id, occurred_at DESC);

        CREATE INDEX IF NOT EXISTS ix_evidence_content_hash
            ON evidence_events (tenant_id, content_hash);

        CREATE TABLE IF NOT EXISTS memory_claims (
            id uuid PRIMARY KEY,
            tenant_id text NOT NULL,
            memory_key text NULL,
            kind smallint NOT NULL,
            resolution_policy smallint NOT NULL,
            status smallint NOT NULL,
            statement text NOT NULL,
            normalized_statement text NOT NULL,
            scope jsonb NOT NULL,
            scope_hash char(64) NOT NULL,
            confidence double precision NOT NULL,
            current_version integer NOT NULL,
            reinforcement_count integer NOT NULL DEFAULT 1,
            dedupe_key char(64) NOT NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL,
            expires_at timestamptz NULL,
            UNIQUE (tenant_id, dedupe_key)
        );

        CREATE INDEX IF NOT EXISTS ix_claims_tenant_status
            ON memory_claims (tenant_id, status, updated_at DESC);

        CREATE INDEX IF NOT EXISTS ix_claims_scope
            ON memory_claims USING gin (scope);

        CREATE INDEX IF NOT EXISTS ix_claims_statement_trgm
            ON memory_claims USING gin (statement gin_trgm_ops);

        CREATE TABLE IF NOT EXISTS memory_claim_versions (
            claim_id uuid NOT NULL REFERENCES memory_claims(id) ON DELETE CASCADE,
            version integer NOT NULL,
            status smallint NOT NULL,
            statement text NOT NULL,
            scope jsonb NOT NULL,
            confidence double precision NOT NULL,
            reason text NOT NULL,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (claim_id, version)
        );

        CREATE TABLE IF NOT EXISTS memory_evidence (
            claim_id uuid NOT NULL REFERENCES memory_claims(id) ON DELETE CASCADE,
            evidence_id uuid NOT NULL REFERENCES evidence_events(id) ON DELETE CASCADE,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (claim_id, evidence_id)
        );

        CREATE TABLE IF NOT EXISTS admission_decisions (
            id uuid PRIMARY KEY,
            evidence_id uuid NOT NULL REFERENCES evidence_events(id) ON DELETE CASCADE,
            candidate_id uuid NOT NULL,
            disposition smallint NOT NULL,
            score double precision NOT NULL,
            reason_codes jsonb NOT NULL,
            required_actions jsonb NOT NULL,
            review_after timestamptz NULL,
            created_at timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_admission_evidence
            ON admission_decisions (evidence_id, created_at DESC);
        """;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(824731902);",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                MigrationSql,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(824731902);",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }
}
