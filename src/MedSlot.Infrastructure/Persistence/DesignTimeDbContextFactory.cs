using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MedSlot.Infrastructure.Persistence;

/// <summary>
/// Для <c>dotnet ef</c>: миграции создаются без запуска API.
/// Подключение нужно только для <c>dotnet ef database update</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MedSlotDbContext>
{
    public MedSlotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=medslot;Username=medslot;Password=medslot";

        var options = new DbContextOptionsBuilder<MedSlotDbContext>();
        DependencyInjection.ConfigureDbContext(options, connectionString);
        return new MedSlotDbContext(options.Options);
    }
}
