namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// Locates the files under <c>Fixtures/</c>, which the project copies next to the test assembly.
/// </summary>
public static class Fixture
{
    /// <summary>Absolute path of the fixture root.</summary>
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>
    /// Resolves a fixture by its path relative to <see cref="Root"/>.
    /// </summary>
    /// <param name="relativePath">Path segments below <c>Fixtures/</c>, for example <c>Acf</c> and <c>appmanifest_570.acf</c>.</param>
    /// <exception cref="FileNotFoundException">The fixture was not copied to the output directory.</exception>
    public static string Resolve(params string[] relativePath)
    {
        var full = System.IO.Path.Combine([Root, .. relativePath]);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"Fixture '{string.Join('/', relativePath)}' is missing. Fixtures are copied from the project's Fixtures folder.",
                full);
        }

        return full;
    }

    /// <summary>Reads a text fixture.</summary>
    /// <param name="relativePath">Path segments below <c>Fixtures/</c>.</param>
    public static string ReadText(params string[] relativePath) => File.ReadAllText(Resolve(relativePath));

    /// <summary>Reads a binary fixture.</summary>
    /// <param name="relativePath">Path segments below <c>Fixtures/</c>.</param>
    public static byte[] ReadBytes(params string[] relativePath) => File.ReadAllBytes(Resolve(relativePath));
}
