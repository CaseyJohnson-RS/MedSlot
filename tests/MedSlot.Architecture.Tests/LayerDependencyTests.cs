using NetArchTest.Rules;

namespace MedSlot.Architecture.Tests;

public class LayerDependencyTests
{
    [Fact]
    public void Domain_depends_only_on_BCL_and_NodaTime()
    {
        var result = Types.InAssembly(Assemblies.Domain)
            .Should()
            .OnlyHaveDependenciesOn("System", "NodaTime", Namespaces.Domain)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_frameworks()
    {
        var result = Types.InAssembly(Assemblies.Application)
            .ShouldNot()
            .HaveDependencyOnAny(
                Namespaces.Infrastructure,
                Namespaces.Api,
                Namespaces.Workers,
                Namespaces.AspNetCore,
                Namespaces.EntityFrameworkCore,
                Namespaces.Npgsql,
                Namespaces.Kafka)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_hosts()
    {
        var result = Types.InAssembly(Assemblies.Infrastructure)
            .ShouldNot()
            .HaveDependencyOnAny(Namespaces.Api, Namespaces.Workers)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Api_does_not_use_EF_Core_directly()
    {
        var result = Types.InAssembly(Assemblies.Api)
            .ShouldNot()
            .HaveDependencyOn(Namespaces.EntityFrameworkCore)
            .GetResult();

        result.ShouldBeSuccessful();
    }

    [Fact]
    public void Workers_do_not_depend_on_Api_or_each_other()
    {
        foreach (var worker in Assemblies.Workers)
        {
            var ownNamespace = worker.GetName().Name!;
            var otherWorkers = Assemblies.Workers
                .Where(w => w != worker)
                .Select(w => w.GetName().Name!);

            var result = Types.InAssembly(worker)
                .ShouldNot()
                .HaveDependencyOnAny([Namespaces.Api, .. otherWorkers])
                .GetResult();

            result.ShouldBeSuccessful(ownNamespace);
        }
    }
}
