using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Feedarr.Api.Options;
using Dapper;
using System.Threading;

namespace Feedarr.Api.Data;

public sealed class Db
{
    private readonly AppOptions _opt;
    private string? _dbPath;
    private int _walConfigured;

    public Db(IOptions<AppOptions> opt) => _opt = opt.Value;

    public string DbPath => _dbPath ??= Path.Combine(_opt.DataDir, _opt.DbFileName);

    public SqliteConnection Open()
    {
        var conn = OpenRawConnection();
        try
        {
            conn.Execute("PRAGMA foreign_keys=ON;");
            conn.Execute("PRAGMA busy_timeout=5000;");
            conn.Execute("PRAGMA cache_size=-8192;");
            conn.Execute("PRAGMA temp_store=2;");
            conn.Execute("PRAGMA synchronous=NORMAL;");
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    internal SqliteConnection OpenNoFk()
    {
        var conn = OpenRawConnection();
        conn.Execute("PRAGMA busy_timeout=5000;");
        conn.Execute("PRAGMA cache_size=-8192;");
        conn.Execute("PRAGMA temp_store=2;");
        conn.Execute("PRAGMA synchronous=NORMAL;");
        return conn;
    }

    public void EnsureWalMode()
    {
        if (Interlocked.Exchange(ref _walConfigured, 1) == 1)
            return;

        using var conn = OpenRawConnection();
        conn.Execute("PRAGMA journal_mode=WAL;");
    }

    internal SqliteOptionsSnapshot GetOptionsSnapshot()
    {
        var builder = CreateConnectionStringBuilder();
        using var conn = OpenRawConnection();
        var journalMode = conn.ExecuteScalar<string>("PRAGMA journal_mode;");

        return new SqliteOptionsSnapshot(
            builder.Pooling,
            SqliteCacheMode.Default,
            builder.Mode,
            journalMode);
    }

    private SqliteConnection OpenRawConnection()
    {
        Directory.CreateDirectory(_opt.DataDir);

        var cs = CreateConnectionStringBuilder().ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private SqliteConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
    }
}

internal sealed record SqliteOptionsSnapshot(
    bool Pooling,
    SqliteCacheMode Cache,
    SqliteOpenMode Mode,
    string? JournalMode);
