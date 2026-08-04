using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OnlineExamPlatform.Web.Data;

namespace OnlineExamPlatform.Tests.Helpers;

public static class DbContextFactory
{
    /// <summary>
    /// Creates an isolated in-memory DbContext. Each call gets its own database so
    /// tests never share state.
    /// </summary>
    public static ApplicationDbContext Create() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Creates an isolated in-memory DbContext with the given interceptors attached,
    /// mirroring how Program.cs wires them onto the pooled context.
    /// </summary>
    public static ApplicationDbContext Create(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptors)
            .Options);
}
