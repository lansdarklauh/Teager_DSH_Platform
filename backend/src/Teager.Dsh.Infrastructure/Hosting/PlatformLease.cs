using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Hosting;

/// <summary>Runtime/cache/event state is single-instance. Enforce that boundary in the database.</summary>
public sealed class PlatformLease(IHostApplicationLifetime lifetime) : BackgroundService
{
    private SqlConnection? connection;
    public async Task AcquireAsync(DshDbContext db)
    {
        connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource='dsh:platform-instance', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0; SELECT @result;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) < 0) throw new InvalidOperationException("此数据库已有平台实例运行；当前部署只支持单实例，请勿启动第二个实例");
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (connection == null) continue;
                await using var command = connection.CreateCommand(); command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { lifetime.StopApplication(); break; }
        }
    }
    public override void Dispose() { connection?.Dispose(); base.Dispose(); }
}
