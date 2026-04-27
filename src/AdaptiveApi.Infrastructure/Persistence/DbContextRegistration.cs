using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Infrastructure.Persistence;

public static class DbContextRegistration
{
    public static IServiceCollection AddAdaptiveApiDb(this IServiceCollection services)
    {
        services.AddDbContext<AdaptiveApiDbContext>((sp, o) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var provider = (cfg.GetValue<string>("Database:Provider") ?? "Sqlite").Trim();
            var connectionString = cfg.GetValue<string>("Database:ConnectionString");

            switch (provider.ToLowerInvariant())
            {
                case "postgres":
                case "postgresql":
                case "npgsql":
                    if (string.IsNullOrWhiteSpace(connectionString))
                        throw new InvalidOperationException(
                            "Database:ConnectionString is required when Database:Provider=Postgres.");
                    o.UseNpgsql(connectionString);
                    break;

                case "sqlserver":
                case "mssql":
                    if (string.IsNullOrWhiteSpace(connectionString))
                        throw new InvalidOperationException(
                            "Database:ConnectionString is required when Database:Provider=SqlServer.");
                    o.UseSqlServer(connectionString);
                    break;

                case "sqlite":
                default:
                    var path = cfg.GetValue<string>("Database:Path") ?? "adaptiveapi.db";
                    var sqliteCs = !string.IsNullOrWhiteSpace(connectionString)
                        ? connectionString
                        : $"Data Source={path}";
                    o.UseSqlite(sqliteCs);
                    break;
            }
        });

        return services;
    }
}
