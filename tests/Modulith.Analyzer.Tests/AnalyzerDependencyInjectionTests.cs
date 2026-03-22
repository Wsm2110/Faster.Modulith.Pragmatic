using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Xunit;

namespace Modulith.Analyzer.Tests;

/// <summary>
/// Contains unit tests validating MOD006 — Unregistered Dependency Detection.
/// Every type injected via a constructor must have a corresponding DI registration
/// somewhere in the solution. If no registration is found, the build fails.
/// </summary>
public class AnalyzerDependencyInjectionTests
{

    /// <summary>
    /// Runs the analyzer against source that does NOT use Microsoft.Extensions.DependencyInjection.
    /// </summary>
    private static Task VerifyAsync(string sourceCode, params DiagnosticResult[] expected)
        => AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expected);

    /// <summary>
    /// Runs the analyzer against source that DOES use Microsoft.Extensions.DependencyInjection.
    /// Adds the required NuGet package reference to the test workspace so the compiler
    /// can resolve IServiceCollection, AddScoped, etc.
    /// </summary>
    private static async Task VerifyWithDiAsync(string sourceCode, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<ModuleBoundaryAnalyzer, DefaultVerifier>
        {
            TestCode = sourceCode,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100
                .AddPackages(ImmutableArray.Create(
                    new PackageIdentity("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.5"),
                    new PackageIdentity("Microsoft.Extensions.Logging.Abstractions", "10.0.5")))

        };

        const string EditorConfig = @"
root = true
[*.cs]
modulith.architectural_layers = Application, Domain, Infrastructure, Contracts
modulith.exempt_keywords = BuildingBlocks, Shared, Common
";
        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", SourceText.From(EditorConfig)));
        test.ExpectedDiagnostics.AddRange(expected);
        await test.RunAsync();
    }

    // -------------------------------------------------------------------------
    // Failure paths — should trigger MOD006
    // -------------------------------------------------------------------------

    /// <summary>
    /// Validates MOD006: Injecting an interface with no registration anywhere fires an error.
    /// </summary>
    [Fact]
    public async Task Mod006_Fails_WhenInterfaceIsInjectedWithNoRegistration()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public interface IMyRepository { }

    public class StorageService
    {
        public StorageService({|#0:IMyRepository|} repository) { }
    }
}
";
        await VerifyAsync(sourceCode,
            DiagnosticResult.CompilerError("MOD006")
                .WithLocation(0)
                .WithArguments("IMyRepository", "StorageService", "MyRepository"));
    }

    /// <summary>
    /// Validates MOD006: Injecting a concrete type with no registration fires an error.
    /// </summary>
    [Fact]
    public async Task Mod006_Fails_WhenConcreteTypeIsInjectedWithNoRegistration()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public class TrackRepository { }

    public class StorageService
    {
        public StorageService({|#0:TrackRepository|} repository) { }
    }
}
";
        await VerifyAsync(sourceCode,
            DiagnosticResult.CompilerError("MOD006")
                .WithLocation(0)
                .WithArguments("TrackRepository", "StorageService", "TrackRepository"));
    }

    /// <summary>
    /// Validates MOD006: Multiple unregistered parameters in the same constructor each fire independently.
    /// </summary>
    [Fact]
    public async Task Mod006_Fails_ForEachUnregisteredParameterInConstructor()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public interface ITrackRepository { }
    public interface IEventDispatcher { }

    public class StorageService
    {
        public StorageService(
            {|#0:ITrackRepository|} repository,
            {|#1:IEventDispatcher|} dispatcher)
        { }
    }
}
";
        await VerifyAsync(sourceCode,
            DiagnosticResult.CompilerError("MOD006").WithLocation(0).WithArguments("ITrackRepository", "StorageService", "TrackRepository"),
            DiagnosticResult.CompilerError("MOD006").WithLocation(1).WithArguments("IEventDispatcher", "StorageService", "EventDispatcher"));
    }

    /// <summary>
    /// Validates MOD006: Injecting an interface when only the concrete type is registered
    /// does not satisfy the interface — both sides of the mapping must be explicit.
    /// </summary>
    [Fact]
    public async Task Mod006_Fails_WhenInterfaceInjectedButOnlyConcreteCouldBeRegistered()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IStorageService { }
    public class StorageService : IStorageService { }

    public class StorageConsumer
    {
        // No IStorageService registration exists — only a concrete StorageService could be
        public StorageConsumer({|#0:IStorageService|} service) { }
    }
}
";
        await VerifyAsync(sourceCode,
            DiagnosticResult.CompilerError("MOD006")
                .WithLocation(0)
                .WithArguments("IStorageService", "StorageConsumer", "StorageService"));
    }

    /// <summary>
    /// Validates MOD006: Interface registered only via concrete AddScoped (no interface mapping)
    /// does not satisfy interface injection.
    /// </summary>
    [Fact]
    public async Task Mod006_Fails_WhenInterfaceInjectedButOnlyConcreteRegisteredViaDi()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IMyRepository { }
    public class MyRepository : IMyRepository { }

    public static class StorageModuleExtensions
    {
        public static IServiceCollection AddStorageModule(this IServiceCollection services)
        {
            services.AddScoped<MyRepository>(); // concrete only — IMyRepository not mapped
            return services;
        }
    }

    public class StorageService
    {
        public StorageService({|#0:IMyRepository|} repository) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode,
            DiagnosticResult.CompilerError("MOD006")
                .WithLocation(0)
                .WithArguments("IMyRepository", "StorageService", "MyRepository"));
    }

    // -------------------------------------------------------------------------
    // Success paths — should NOT trigger MOD006
    // -------------------------------------------------------------------------

    /// <summary>
    /// Success Path: Interface registered with concrete mapping — no error.
    /// services.AddScoped&lt;IFoo, Foo&gt;() satisfies IFoo injection.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenInterfaceRegisteredWithConcreteMapping()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IMyRepository { }
    public class MyRepository : IMyRepository { }

    public static class StorageModuleExtensions
    {
        public static IServiceCollection AddStorageModule(this IServiceCollection services)
        {
            services.AddScoped<IMyRepository, MyRepository>();
            return services;
        }
    }

    public class StorageService
    {
        public StorageService(IMyRepository repository) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: Concrete type registered directly and injected directly — no error.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenConcreteTypeRegisteredAndInjected()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public class TrackRepository { }

    public static class StorageModuleExtensions
    {
        public static IServiceCollection AddStorageModule(this IServiceCollection services)
        {
            services.AddScoped<TrackRepository>();
            return services;
        }
    }

    public class StorageService
    {
        public StorageService(TrackRepository repository) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: AddSingleton satisfies injection just as AddScoped would.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenRegisteredViaSingleton()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface ICache { }
    public class InMemoryCache : ICache { }

    public static class Extensions
    {
        public static IServiceCollection Register(this IServiceCollection services)
        {
            services.AddSingleton<ICache, InMemoryCache>();
            return services;
        }
    }

    public class StorageService
    {
        public StorageService(ICache cache) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: AddTransient satisfies injection.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenRegisteredViaTransient()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IValidator { }
    public class TrackValidator : IValidator { }

    public static class Extensions
    {
        public static IServiceCollection Register(this IServiceCollection services)
        {
            services.AddTransient<IValidator, TrackValidator>();
            return services;
        }
    }

    public class StorageService
    {
        public StorageService(IValidator validator) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: System types (ILogger, etc.) are never flagged —
    /// they are framework-provided and never manually registered.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenInjectingFrameworkTypes()
    {
        var sourceCode = @"
using Microsoft.Extensions.Logging;

namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public class StorageService
    {
        public StorageService(ILogger<StorageService> logger) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: Extension method classes (registration contexts) are never analyzed —
    /// they are the registration, not the consumer.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenConstructorIsInExtensionsClass()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IMyRepository { }

    public static class StorageModuleExtensions
    {
        // IServiceCollection parameter here must never fire MOD006
        public static IServiceCollection AddStorageModule(this IServiceCollection services)
        {
            return services;
        }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: typeof() registration syntax satisfies injection.
    /// services.AddScoped(typeof(IFoo), typeof(Foo)) is a valid registration form.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenRegisteredViaTypeofSyntax()
    {
        var sourceCode = @"
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Modulith.Template.Pragmatic.Modules.Storage
{
    public interface IMyRepository { }
    public class MyRepository : IMyRepository { }

    public static class Extensions
    {
        public static IServiceCollection Register(this IServiceCollection services)
        {
            services.AddScoped(typeof(IMyRepository), typeof(MyRepository));
            return services;
        }
    }

    public class StorageService
    {
        public StorageService(IMyRepository repository) { }
    }
}
";
        await VerifyWithDiAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Value types (int, bool) are never flagged — they are never DI-registered.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenInjectingValueTypes()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public class StorageService
    {
        public StorageService(int timeout, bool enableCache) { }
    }
}
";
        await VerifyAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Program class is a registration context and must never fire MOD006.
    /// </summary>
    [Fact]
    public async Task Mod006_Passes_WhenConstructorIsInProgramClass()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic
{
    public interface IUnregisteredService { }

    public class Program
    {
        // Program is a registration context — never flagged
        public Program(IUnregisteredService service) { }
    }
}
";
        await VerifyAsync(sourceCode);
    }


}