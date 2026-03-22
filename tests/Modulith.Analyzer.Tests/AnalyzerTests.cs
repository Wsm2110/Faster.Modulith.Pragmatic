using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Modulith.Analyzer;
using System.Threading.Tasks;
using Xunit;

namespace Modulith.Analyzer.Tests;

/// <summary>
/// Provides a base testing wrapper to automatically inject the required .editorconfig settings into the Roslyn testing workspace.
/// </summary>
public static class AnalyzerTestHelper
{
    private const string EditorConfig = @"
root = true
[*.cs]
modulith.architectural_layers = Application, Domain, Infrastructure, Contracts
modulith.exempt_keywords = BuildingBlocks, Shared, Common
";

    /// <summary>
    /// Runs the analyzer against the provided source code and asserts the expected diagnostics.
    /// </summary>
    public static async Task VerifyAnalyzerAsync(string sourceCode, params DiagnosticResult[] expectedDiagnostics)
    {
        var test = new CSharpAnalyzerTest<ModuleBoundaryAnalyzer, DefaultVerifier>
        {
            TestCode = sourceCode,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100
        };

        test.TestState.AnalyzerConfigFiles.Add(("/.editorconfig", SourceText.From(EditorConfig)));
        test.ExpectedDiagnostics.AddRange(expectedDiagnostics);

        await test.RunAsync();
    }
}

/// <summary>
/// Contains unit tests validating all rulesets, edge cases, and success paths enforced by the ModuleBoundaryAnalyzer.
/// </summary>
public class ModuleBoundaryAnalyzerTests
{
    /// <summary>
    /// Validates MOD001: Ensures that foreign DTOs cannot be leaked as public state via record primary constructors.
    /// </summary>
    [Fact]
    public async Task Mod001_Fails_WhenForeignDtoIsUsedInPrimaryConstructor()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public record ReplicationState({|#0:ReplicateTrackDto|} Dto);
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "ReplicateTrackDto", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Validates MOD002: Ensures entrypoint interfaces strictly define behavior and never expose properties.
    /// </summary>
    /// <summary>
    /// Validates MOD002: Ensures entrypoint interfaces strictly define behavior and never expose properties.
    /// </summary>
    [Fact]
    public async Task Mod002_Fails_WhenEntrypointContainsProperties()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public interface IStorageEntrypoint
    {
        // The diagnostic is reported specifically on the property identifier 'State'.
        string {|#0:State|} { get; set; }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD002")
            .WithLocation(0)
            .WithArguments("IStorageEntrypoint", "property", "State");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }
    /// <summary>
    /// Validates MOD003: Ensures that internal or non-entrypoint interfaces cannot be injected across modules.
    /// </summary>
    [Fact]
    public async Task Mod003_Fails_WhenInjectingForeignInternalInterface()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public interface IStorageRepository { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        public ReplicationService({|#0:IStorageRepository|} repository) { }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD003")
            .WithLocation(0)
            .WithArguments("IStorageRepository", "Storage", "Replication");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Validates MOD004: Ensures the service locator anti-pattern is blocked at compile time.
    /// </summary>
    /// <summary>
    /// Validates MOD004: Ensures the service locator anti-pattern is blocked at compile time.
    /// </summary>
    [Fact]
    public async Task Mod004_Fails_WhenInjectingServiceProvider()
    {
        var sourceCode = @"
using System;

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    public class ReplicationService
    {
        // The diagnostic is reported specifically on the parameter identifier 'provider'.
        public ReplicationService(IServiceProvider {|#0:provider|}) { }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD004")
            .WithLocation(0);

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }
    /// <summary>
    /// Validates MOD005: Ensures that a module is strictly prohibited from publishing an event it does not own.
    /// </summary>
    [Fact]
    public async Task Mod005_Fails_WhenPublishingForeignEvent()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.BuildingBlocks.Eventing
{
    public interface IEvent { }
    public interface IEventDispatcher { void Publish<T>(T eventToPublish) where T : IEvent; }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    public record TrackStoredEvent : IEvent;
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        private readonly IEventDispatcher _dispatcher;

        public ReplicationService(IEventDispatcher dispatcher) => _dispatcher = dispatcher;

        public void Execute()
        {
            _dispatcher.Publish({|#0:new TrackStoredEvent()|});
        }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD005")
            .WithLocation(0)
            .WithArguments("Replication", "TrackStoredEvent", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Validates Global Bypass: Ensures that references to types within exempt namespaces do not trigger boundary rules.
    /// </summary>
    [Fact]
    public async Task GlobalBypass_Passes_WhenReferencingBuildingBlocks()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.BuildingBlocks.Eventing
{
    public interface IEventDispatcher { }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;

    public class StorageService
    {
        private readonly IEventDispatcher _dispatcher;

        public StorageService(IEventDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }


    /// <summary>
    /// Edge Case: Ensures that DTOs can be used legally inside method bodies without triggering state leak rules.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Passes_WhenForeignDtoUsedInMethodBody()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        public void Process()
        {
            var dto = new ReplicateTrackDto(""123"");
        }
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Ensures generic types are properly unwrapped and evaluated for state leaks.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenForeignDtoWrappedInGenericType()
    {
        var sourceCode = @"
using System.Collections.Generic;

namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        // The diagnostic is reported specifically on the generic argument, not the collection interface.
        public IEnumerable<{|#0:ReplicateTrackDto|}> Dtos { get; set; }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "ReplicateTrackDto", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures external libraries and system types (which do not fit the module pattern) are safely ignored.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Passes_WhenUsingExternalLibraries()
    {
        var sourceCode = @"
using System;
using System.Threading.Tasks;

namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    public class StorageService
    {
        public string Data { get; set; }
        
        public Task ProcessAsync() => Task.CompletedTask;
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Ensures entrypoints can be legitimately stored as private fields after constructor injection.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Passes_WhenEntrypointUsedCorrectlyAsPrivateField()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public interface IStorageEntrypoint { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        private readonly IStorageEntrypoint _entrypoint;

        public ReplicationService(IStorageEntrypoint entrypoint)
        {
            _entrypoint = entrypoint;
        }
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Ensures that MOD005 correctly flags illegal publishing even when the type argument is explicitly declared on the invocation.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenPublishingForeignEventViaExplicitTypeArgument()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.BuildingBlocks.Eventing
{
    public interface IEvent { }
    public interface IEventDispatcher { void Publish<T>(T eventToPublish) where T : IEvent; }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    public record TrackStoredEvent : IEvent;
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        private readonly IEventDispatcher _dispatcher;

        public ReplicationService(IEventDispatcher dispatcher) => _dispatcher = dispatcher;

        public void Execute()
        {
            var evt = new TrackStoredEvent();
            // The diagnostic is reported on the specific argument 'evt' because it is checked first.
            _dispatcher.Publish<TrackStoredEvent>({|#0:evt|});
        }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD005")
            .WithLocation(0)
            .WithArguments("Replication", "TrackStoredEvent", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Success Path: Ensures that modules can freely subscribe to (reference) foreign events without triggering boundary rules.
    /// </summary>
    [Fact]
    public async Task SuccessPath_Passes_WhenSubscribingToForeignEvent()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.BuildingBlocks.Eventing
{
    public interface IEvent { }
    public interface IEventHandler<T> where T : IEvent { void Handle(T ev); }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    public record TrackStoredEvent : IEvent;
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Eventing;
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class TrackStoredEventHandler : IEventHandler<TrackStoredEvent>
    {
        public void Handle(TrackStoredEvent ev) { }
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Ensures that methods can legally return foreign DTOs or accept them as parameters without triggering state leaks.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Passes_WhenForeignDtoIsUsedInMethodSignatures()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        public ReplicateTrackDto Transform(ReplicateTrackDto input)
        {
            return input;
        }
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Ensures that standard private fields (not just properties or primary constructors) are caught when storing foreign DTOs.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenForeignDtoIsUsedAsPrivateField()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        private readonly {|#0:ReplicateTrackDto|} _cachedDto;
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "ReplicateTrackDto", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that fully qualified type names cannot be used to bypass the analyzer's using-statement checks.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenBypassingUsingStatementsWithFullyQualifiedNames()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    public class ReplicationService
    {
        public {|#0:Modulith.Template.Pragmatic.Modules.Storage.Contracts.ReplicateTrackDto|} Dto { get; set; }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "ReplicateTrackDto", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that complex tuple state declarations are recursively analyzed and blocked.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenForeignDtoIsHiddenInsideATupleField()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Contracts
{
    public record ReplicateTrackDto(string TrackId);
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Contracts;

    public class ReplicationService
    {
        private readonly (int Count, {|#0:ReplicateTrackDto|} Data) _tupleState;
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "ReplicateTrackDto", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that internal sub-namespaces (like a 'Security' folder) are still protected 
    /// by module boundaries and cannot be accessed by foreign modules.
    /// </summary>
    /// <summary>
    /// Edge Case: Ensures that internal sub-namespaces (like a 'Security' folder) are still protected 
    /// by module boundaries and cannot be accessed by foreign modules.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenReferencingInternalSecurityFolderOfForeignModule()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Security
{
    public class StorageEncryptionVault { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Security;

    public class ReplicationService
    {
        // Fails: Only wrap the field. We removed the constructor parameter to avoid duplicate diagnostics in this test.
        private readonly {|#0:StorageEncryptionVault|} _vault;

        public ReplicationService()
        {
            // Implementation detail...
        }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "StorageEncryptionVault", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that Infrastructure-specific types (like database entities) 
    /// are protected and cannot be used as structural state in foreign modules.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenReferencingInternalInfrastructureOfForeignModule()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Infrastructure.Persistence
{
    // A persistence-specific entity that should never leave the Storage module.
    public class TrackDbo { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Infrastructure.Persistence;

    public class ReplicationService
    {
        // Fails: Replication cannot have a Storage DBO as a private field.
        private readonly {|#0:TrackDbo|} _persistenceModel;
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "TrackDbo", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that Domain entities or Aggregates are encapsulated 
    /// and cannot be referenced by foreign modules.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenReferencingForeignDomainAggregate()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Domain.Aggregates
{
    public class StorageBucket { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Domain
{
    using Modulith.Template.Pragmatic.Modules.Storage.Domain.Aggregates;

    public class ReplicationAggregate
    {
        // Fails: A Domain entity from Replication cannot hold a reference to a Storage Aggregate.
        private readonly {|#0:StorageBucket|} _sourceBucket;
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "StorageBucket", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Success Path: Ensures that folders matching the 'exempt_keywords' (like Shared) 
    /// bypass all boundary checks.
    /// </summary>
    [Fact]
    public async Task SuccessPath_Passes_WhenReferencingSharedFolder()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Shared
{
    public class StorageConstants { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Shared;

    public class ReplicationService
    {
        // Passes: 'Shared' is in the exempt_keywords list in .editorconfig.
        private readonly StorageConstants _constants;

        public ReplicationService(StorageConstants constants)
        {
            _constants = constants;
        }
    }
}
";
        // Zero diagnostics expected.
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Success Path: Ensures that deep sub-namespaces within BuildingBlocks are correctly exempted.
    /// </summary>
    [Fact]
    public async Task SuccessPath_Passes_WhenReferencingDeepBuildingBlocks()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.BuildingBlocks.Security.Encryption
{
    public interface ICryptoService { }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    using Modulith.Template.Pragmatic.BuildingBlocks.Security.Encryption;

    public class StorageService
    {
        // Passes: BuildingBlocks is exempt.
        private readonly ICryptoService _crypto;

        public StorageService(ICryptoService crypto) => _crypto = crypto;
    }
}
";
        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode);
    }

    /// <summary>
    /// Edge Case: Prevents a class from inheriting from an internal class in a foreign module.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenInheritingFromForeignInternalClass()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Infrastructure
{
    public class BaseStorageService { }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Infrastructure
{
    using Modulith.Template.Pragmatic.Modules.Storage.Infrastructure;

    // Fails: Replication cannot inherit from Storage's internal base class.
    public class ReplicationStorageService : {|#0:BaseStorageService|}
    {
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "BaseStorageService", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Edge Case: Ensures that static members or factories in foreign modules 
    /// cannot be accessed if they reside outside of Contracts.
    /// </summary>
    /// <summary>
    /// Edge Case: Ensures that static members or factories in foreign modules 
    /// cannot be accessed if they reside outside of Contracts.
    /// </summary>
    /// <summary>
    /// Edge Case: Ensures that static members or factories in foreign modules 
    /// cannot be accessed if they reside outside of Contracts.
    /// </summary>
    [Fact]
    public async Task EdgeCase_Fails_WhenAccessingForeignStaticFactory()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Storage.Infrastructure
{
    public static class StorageFactory
    {
        public static string GetKey() => ""Secret"";
    }
}

namespace Modulith.Template.Pragmatic.Modules.Replication.Application
{
    using Modulith.Template.Pragmatic.Modules.Storage.Infrastructure;

    public class ReplicationService
    {
        public void Execute()
        {
            // Now correctly reports only 1 error on the class identifier.
            var key = {|#0:StorageFactory|}.GetKey();
        }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD001")
            .WithLocation(0)
            .WithArguments("Replication", "StorageFactory", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }

    /// <summary>
    /// Real World: Ensures that internal services (not Entrypoints) cannot be 
    /// injected via constructor across module boundaries.
    /// </summary>
    [Fact]
    public async Task RealWorld_Fails_WhenInjectingInternalInterfaceInsteadOfEntrypoint()
    {
        var sourceCode = @"
namespace Modulith.Template.Pragmatic.Modules.Identity.Infrastructure
{
    public interface IInternalUserCache { }
}

namespace Modulith.Template.Pragmatic.Modules.Storage.Application
{
    using Modulith.Template.Pragmatic.Modules.Identity.Infrastructure;

    public class StorageService
    {
        // Fails: MOD003 specifically targets foreign interface injection.
        public StorageService({|#0:IInternalUserCache|} userCache)
        {
        }
    }
}
";
        var expectedDiagnostic = DiagnosticResult.CompilerError("MOD003")
            .WithLocation(0)
            .WithArguments("IInternalUserCache", "Identity", "Storage");

        await AnalyzerTestHelper.VerifyAnalyzerAsync(sourceCode, expectedDiagnostic);
    }
}