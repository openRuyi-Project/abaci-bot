namespace abaci_bot.Services;

/// <summary>
/// Defines standard label constants used across the repository for workflow, contribution, compliance,
/// build system, and security tracking.
/// </summary>
public static class GitHubLabels
{
    // 1. Workflow lifecycle states
    public const string WorkflowInDev = "Workflow: In Dev";
    public const string WorkflowReadyForReview = "Workflow: Ready For Review";
    public const string WorkflowInReview = "Workflow: In Review";
    public const string WorkflowBlocked = "Workflow: Blocked";
    public const string WorkflowComplete = "Workflow: Complete";

    // 2. Notifications & compliance
    public const string CommitsUpdated = "Commits: Updated";
    public const string AiAssistance = "AI Assistance";
    public const string Ci = "CI";

    // 3. Contributor classification
    public const string CommunityStudent = "Community: Student contribution";
    public const string CommunityContribution = "Community: Contribution";

    // 4. Target release branch classification
    public const string TargetRolling = "Target: Rolling";
    public const string TargetLts = "Target: LTS";

    // 5. Build system classification
    public const string BuildSystemAutotools = "BuildSystem: autotools";
    public const string BuildSystemCmake = "BuildSystem: cmake";
    public const string BuildSystemGolangModule = "BuildSystem: golangmodule";
    public const string BuildSystemGolang = "BuildSystem: golang";
    public const string BuildSystemRustCrate = "BuildSystem: rustcrate";
    public const string BuildSystemRust = "BuildSystem: rust";
    public const string BuildSystemMeson = "BuildSystem: meson";
    public const string BuildSystemPyproject = "BuildSystem: pyproject";
    public const string BuildSystemMisc = "BuildSystem: misc";

    // 6. Security vulnerability severity mapping
    public const string Severity1 = "Severity: 1";
    public const string Severity2 = "Severity: 2";
    public const string Severity3 = "Severity: 3";
    public const string Severity4 = "Severity: 4";

    // 7. CVE tracking lifecycle state
    public const string VulnerabilityDetected = "Vulnerability: Detected";
    public const string VulnerabilityFixedPending = "Vulnerability: Fixed-Pending";
    public const string VulnerabilityResolved = "Vulnerability: Resolved";
    public const string VulnerabilityDismissed = "Vulnerability: Dismissed";
    public const string VulnerabilityPendingVerification = "Vulnerability: Pending Verification";

    // 8. Downstream rebuild package count tier
    public const string Rebuild0 = "Rebuild: 0";
    public const string Rebuild1To10 = "Rebuild: 1-10";
    public const string Rebuild11To100 = "Rebuild: 11-100";
    public const string Rebuild101To500 = "Rebuild: 101-500";
    public const string Rebuild501To1000 = "Rebuild: 501-1000";
    public const string Rebuild1001To2500 = "Rebuild: 1001-2500";
    public const string Rebuild2501To5000 = "Rebuild: 2501-5000";
    public const string Rebuild5001Plus = "Rebuild: 5001+";
}
