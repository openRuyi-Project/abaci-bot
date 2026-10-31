namespace abaci_bot.Services;

/// <summary>
/// Deep module providing an offline, pure-function CVSS v3.1 / v3.0 Base Score calculator.
/// </summary>
public static class CvssCalculator
{
    /// <summary>
    /// Parses a CVSS v3.1 or v3.0 vector string (e.g., "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H")
    /// and calculates the authoritative Base Score according to FIRST CVSS specification.
    /// </summary>
    /// <param name="vector">The raw CVSS vector string.</param>
    /// <returns>The calculated base score between 0.0 and 10.0, or null if the vector is invalid.</returns>
    public static double? CalculateBaseScore(string? vector)
    {
        if (string.IsNullOrWhiteSpace(vector))
            return null;

        var parts = vector.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var metrics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
            var kv = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2)
            {
                metrics[kv[0]] = kv[1].ToUpperInvariant();
            }
        }

        if (!metrics.TryGetValue("AV", out var av) ||
            !metrics.TryGetValue("AC", out var ac) ||
            !metrics.TryGetValue("PR", out var pr) ||
            !metrics.TryGetValue("UI", out var ui) ||
            !metrics.TryGetValue("S", out var s) ||
            !metrics.TryGetValue("C", out var c) ||
            !metrics.TryGetValue("I", out var i) ||
            !metrics.TryGetValue("A", out var a))
        {
            return null;
        }

        // Attack Vector
        var avScore = av switch
        {
            "N" => 0.85,
            "A" => 0.62,
            "L" => 0.55,
            "P" => 0.20,
            _ => (double?)null
        };

        // Attack Complexity
        var acScore = ac switch
        {
            "L" => 0.77,
            "H" => 0.44,
            _ => (double?)null
        };

        // Scope
        var scopeChanged = s switch
        {
            "U" => false,
            "C" => true,
            _ => (bool?)null
        };

        if (avScore == null || acScore == null || scopeChanged == null)
            return null;

        // Privileges Required
        var prScore = pr switch
        {
            "N" => 0.85,
            "L" => scopeChanged.Value ? 0.68 : 0.62,
            "H" => scopeChanged.Value ? 0.50 : 0.27,
            _ => (double?)null
        };

        // User Interaction
        var uiScore = ui switch
        {
            "N" => 0.85,
            "R" => 0.62,
            _ => (double?)null
        };

        // CIA Impacts
        var cScore = c switch { "N" => 0.0, "L" => 0.22, "H" => 0.56, _ => (double?)null };
        var iScore = i switch { "N" => 0.0, "L" => 0.22, "H" => 0.56, _ => (double?)null };
        var aScore = a switch { "N" => 0.0, "L" => 0.22, "H" => 0.56, _ => (double?)null };

        if (prScore == null || uiScore == null || cScore == null || iScore == null || aScore == null)
            return null;

        var iss = 1.0 - ((1.0 - cScore.Value) * (1.0 - iScore.Value) * (1.0 - aScore.Value));

        double impact;
        if (!scopeChanged.Value)
        {
            impact = 6.42 * iss;
        }
        else
        {
            impact = 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15.0);
        }

        if (impact <= 0.0)
            return 0.0;

        var exploitability = 8.22 * avScore.Value * acScore.Value * prScore.Value * uiScore.Value;

        double baseScore;
        if (!scopeChanged.Value)
        {
            baseScore = Math.Min(impact + exploitability, 10.0);
        }
        else
        {
            baseScore = Math.Min(1.08 * (impact + exploitability), 10.0);
        }

        return CvssRoundup(baseScore);
    }

    private static double CvssRoundup(double input)
    {
        var intInput = (int)Math.Round(input * 100000.0);
        if (intInput % 10000 == 0)
        {
            return intInput / 100000.0;
        }

        return Math.Ceiling(intInput / 10000.0) / 10.0;
    }
}
