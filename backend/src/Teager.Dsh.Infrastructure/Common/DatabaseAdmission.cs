using Microsoft.EntityFrameworkCore;
using Teager.Dsh.Infrastructure.Data;

namespace Teager.Dsh.Infrastructure.Common;

/// <summary>Serialize admission with resource lifecycle changes within a database transaction.</summary>
public static class DatabaseAdmission
{
    public static Task LockAsync(DshDbContext db) => db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sp_getapplock @Resource=N'dsh:lifecycle', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 51000, 'Resource admission timed out', 1;");
}
