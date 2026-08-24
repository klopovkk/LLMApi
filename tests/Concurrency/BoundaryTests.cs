using Xunit;

namespace Concurrency;

/// <summary>
/// Guards the Principle IV boundary.
///
/// The plan originally proposed enforcing this with assembly references alone — src/Middleware
/// referencing LLM.Abstraction and never LLM.FakeProvider. That does not survive contact with a
/// composition root: an executable has to name a concrete implementation in order to register it.
///
/// So the rule is narrower than "no reference", and correspondingly it needs a test the compiler
/// cannot provide: exactly one file, Program.cs, may mention a concrete provider. Everything else
/// in the middleware sees ILlmClient only.
/// </summary>
public sealed class BoundaryTests
{
    [Fact]
    public void MiddlewareSources_FileOtherThanCompositionRoot_DoesNotNameConcreteProvider()
    {
        // Arrange
        var middleware = Path.Combine(RepositoryRoot(), "src", "Middleware");
        var offenders = new List<string>();

        // Act
        foreach (var file in Directory.EnumerateFiles(middleware, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            if (Path.GetFileName(file) == "Program.cs")
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("LLM.FakeProvider", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(middleware, file));
            }
        }

        // Assert
        Assert.True(
            offenders.Count == 0,
            "Only Program.cs may name a concrete provider. Offending files: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void AbstractionAssembly_Always_HasNoNonFrameworkDependencies()
    {
        // If the boundary assembly ever grows a dependency, both sides of the boundary can start
        // sharing types through it, and the separation stops meaning anything.
        // Act
        var references = typeof(LLM.Abstraction.ILlmClient).Assembly.GetReferencedAssemblies();

        // Assert
        var nonFramework = references
            .Select(r => r.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                        && !name.Equals("netstandard", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(nonFramework);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LLMApi.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
