namespace OhMyLibrary.Tests.Infrastructure;

/// <summary>
/// A throwaway directory under the system temp folder, deleted when the test disposes it.
/// </summary>
/// <remarks>
/// Deletion is best effort: a virus scanner or an indexer can hold a handle open for a moment after
/// the test has finished, and failing a green test over a leftover temp folder helps nobody.
/// </remarks>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    /// <param name="prefix">Short label that appears in the folder name, to make a leftover identifiable.</param>
    public TempDirectory(string prefix = "oml")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string Path { get; }

    /// <summary>Creates a subdirectory and returns its absolute path.</summary>
    /// <param name="segments">Path segments below <see cref="Path"/>.</param>
    public string CreateSubdirectory(params string[] segments)
    {
        var full = Combine(segments);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Combines path segments against <see cref="Path"/> without creating anything.</summary>
    /// <param name="segments">Path segments below <see cref="Path"/>.</param>
    public string Combine(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    /// <summary>
    /// Writes a file, creating the directories above it.
    /// </summary>
    /// <param name="relativePath">Path of the file below <see cref="Path"/>.</param>
    /// <param name="content">File content, written as UTF-8 without a byte order mark.</param>
    /// <returns>The absolute path of the file.</returns>
    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// Writes a binary file, creating the directories above it.
    /// </summary>
    /// <param name="relativePath">Path of the file below <see cref="Path"/>.</param>
    /// <param name="content">File content.</param>
    /// <returns>The absolute path of the file.</returns>
    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle is still open somewhere; the OS cleans the temp folder up eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
