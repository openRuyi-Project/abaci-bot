namespace abaci_bot.Services;

/// <summary>
/// Deep module abstracting spec discovery and retrieval from either remote GitHub Tree API or local filesystem.
/// Encapsulates caching, directory traversal, and fail-safe spec parsing.
/// </summary>
public interface ISpecCatalog
{
    /// <summary>
    /// Retrieves a single package's spec context.
    /// </summary>
    Task<SpecContext?> GetSpecAsync(string packageName, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all Rust crate specs (SPECS/rust-*).
    /// </summary>
    Task<IReadOnlyList<SpecContext>> GetRustSpecsAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists all package names in the SPECS repository.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllPackageNamesAsync(CancellationToken ct = default);
}

