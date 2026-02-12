using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Feedarr.Api.Options;
using Dapper;
using System.Threading;

namespace Feedarr.Api.Data;

public sealed class Db
{
    private readonly AppOptions _opt;
    private int _walConfigured;

    public Db(IOptions<AppOptions> opt) => _opt = opt.Value;

    public string DbPath => Path.Combine(_opt.DataDir, _opt.DbFileName);

    public SqliteConnection Open()
    {
        var conn = OpenRawConnection();
        conn.Execute("PRAGMA foreign_keys=ON;");
        conn.Execute("PRAGMA busy_timeout=5000;");

        return conn;
    }

    public void EnsureWalMode()
    {
        if (Interlocked.Exchange(ref _walConfigured, 1) == 1)
            return;

        using var conn = OpenRawConnection();
        conn.Execute("PRAGMA journal_mode=WAL;");
    }

    private SqliteConnection OpenRawConnection()
    {
        Directory.CreateDirectory(_opt.DataDir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }
}
