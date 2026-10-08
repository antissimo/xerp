using System.Reflection;

namespace Xerp.UnitTests;

/// <summary>The dependency rule of docs/architecture.md section 2.</summary>
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(Xerp.Domain.Common.ITenantOwned).Assembly;
    private static readonly Assembly Application = typeof(Xerp.Application.Ports.IXerpDb).Assembly;
    private static readonly Assembly Infrastructure = typeof(Xerp.Infrastructure.Persistence.XerpDbContext).Assembly;

    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();

    [Fact]
    public void AC03_Domain_references_no_framework_and_no_other_Xerp_project()
    {
        Assert.Equal("Xerp.Domain", Domain.GetName().Name);
        Assert.DoesNotContain(References(Domain), name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.StartsWith("Npgsql", StringComparison.Ordinal) ||
            name.StartsWith("Xerp.", StringComparison.Ordinal));
    }

    [Fact]
    public void AC04_Application_references_no_AspNetCore_Npgsql_Infrastructure_or_Api()
    {
        Assert.Equal("Xerp.Application", Application.GetName().Name);
        Assert.DoesNotContain(References(Application), name =>
            name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            name.StartsWith("Npgsql", StringComparison.Ordinal) ||
            name == "Xerp.Infrastructure" ||
            name == "Xerp.Api");
    }

    [Fact]
    public void AC05_Infrastructure_does_not_reference_Api()
    {
        Assert.Equal("Xerp.Infrastructure", Infrastructure.GetName().Name);
        Assert.DoesNotContain("Xerp.Api", References(Infrastructure));
    }
}
