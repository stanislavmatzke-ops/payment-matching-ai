using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentMatching.Data;
using Testcontainers.PostgreSql;

namespace PaymentMatching.Testing;

/// <summary>
/// One PostgreSQL 17 container per test assembly (Testcontainers; needs Docker). Each caller gets a
/// fresh database with the migrations applied, so tests never see each other's rows.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable
{
    private readonly SemaphoreSlim _start = new(1, 1);
    private PostgreSqlContainer? _container;

    public async Task<string> NewDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var admin = await StartAsync(cancellationToken);
        var name = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        await using var db = new PaymentsDbContext(PaymentsDb.Options(connectionString), new TenantContext());
        await db.Database.MigrateAsync(cancellationToken);
        return connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        _start.Dispose();
    }

    private async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        await _start.WaitAsync(cancellationToken);
        try
        {
            if (_container is null)
            {
                var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
                await container.StartAsync(cancellationToken);
                _container = container;
            }

            return _container.GetConnectionString();
        }
        finally
        {
            _start.Release();
        }
    }
}
