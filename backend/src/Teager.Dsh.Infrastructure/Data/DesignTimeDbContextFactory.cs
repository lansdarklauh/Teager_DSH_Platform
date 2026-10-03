using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Teager.Dsh.Infrastructure.Data;

/// <summary>设计时上下文工厂：供 dotnet ef 生成迁移使用（不会连接数据库）。连接串可用环境变量 DSH_DESIGN_CONN 覆盖。</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DshDbContext>
{
    /// <summary>创建设计时上下文。</summary>
    /// <param name="args">命令行参数。</param>
    public DshDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("DSH_DESIGN_CONN") ?? "Server=localhost,1433;Database=DshPlatform;User ID=sa;Password=ChangeMe_123;TrustServerCertificate=True";
        return new DshDbContext(new DbContextOptionsBuilder<DshDbContext>().UseSqlServer(conn).Options);
    }
}
