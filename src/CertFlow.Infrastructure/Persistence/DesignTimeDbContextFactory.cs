using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CertFlow.Infrastructure.Persistence;

/// <summary>
/// Used only by the EF Core CLI tools (dotnet ef migrations / database update).
/// Bypasses the application host so design-time commands don't need Service Bus,
/// Graph, or Azure OpenAI configuration to be present.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CertFlowDbContext>
{
    public CertFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__CertFlow")
            ?? "Server=(localdb)\\mssqllocaldb;Database=certflow;Trusted_Connection=True;";

        var options = new DbContextOptionsBuilder<CertFlowDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new CertFlowDbContext(options);
    }
}
