namespace abaci_bot.Services;

/// <summary>
/// Result of evaluating an SPDX license expression.
/// </summary>
public record SpdxValidationResult(
    bool IsValid,
    IReadOnlyList<string> InvalidIdentifiers,
    string? NormalizedExpression = null,
    string? ErrorMessage = null);

/// <summary>
/// Service abstraction for validating SPDX license expressions and identifiers.
/// </summary>
public interface ISpdxLicenseService
{
    /// <summary>
    /// Checks if a single identifier is a known, valid SPDX license ID or recognized alias.
    /// </summary>
    bool IsValidSpdxIdentifier(string identifier);

    /// <summary>
    /// Checks if a single identifier is a known SPDX exception ID.
    /// </summary>
    bool IsValidSpdxException(string exceptionId);

    /// <summary>
    /// Validates an entire SPDX license expression, supporting boolean operators
    /// (AND, OR, WITH), parentheses, and trailing '+' operators.
    /// </summary>
    SpdxValidationResult ValidateExpression(string? licenseExpression);

    /// <summary>
    /// Updates in-memory license and exception definitions with latest data from configured remote indices.
    /// </summary>
    Task<bool> RefreshRemoteMetadataAsync(CancellationToken cancellationToken = default);
}
