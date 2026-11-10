namespace abaci_bot.Services;

/// <summary>
/// Deep module providing an offline, pure-function CVSS v4.0 / v3.1 / v3.0 Base Score calculator.
/// </summary>
public static class CvssCalculator
{
    /// <summary>
    /// Parses a CVSS v4.0, v3.1 or v3.0 vector string (e.g., "CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:H/I:H/A:H" or "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N")
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

        if (vector.Trim().StartsWith("CVSS:4.0", StringComparison.OrdinalIgnoreCase) ||
            (parts.Length > 0 && parts[0].Equals("CVSS:4.0", StringComparison.OrdinalIgnoreCase)))
        {
            return CalculateCvss4BaseScore(metrics);
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

    private static double? CalculateCvss4BaseScore(Dictionary<string, string> metrics)
    {
        if (!metrics.TryGetValue("AV", out var av) ||
            !metrics.TryGetValue("AC", out var ac) ||
            !metrics.TryGetValue("AT", out var at) ||
            !metrics.TryGetValue("PR", out var pr) ||
            !metrics.TryGetValue("UI", out var ui) ||
            !metrics.TryGetValue("VC", out var vc) ||
            !metrics.TryGetValue("VI", out var vi) ||
            !metrics.TryGetValue("VA", out var va) ||
            !metrics.TryGetValue("SC", out var sc) ||
            !metrics.TryGetValue("SI", out var si) ||
            !metrics.TryGetValue("SA", out var sa))
        {
            return null;
        }

        if (av is not ("N" or "A" or "L" or "P") ||
            ac is not ("L" or "H") ||
            at is not ("N" or "P") ||
            pr is not ("N" or "L" or "H") ||
            ui is not ("N" or "P" or "A") ||
            vc is not ("H" or "L" or "N") ||
            vi is not ("H" or "L" or "N") ||
            va is not ("H" or "L" or "N") ||
            sc is not ("H" or "L" or "N") ||
            si is not ("H" or "L" or "N") ||
            sa is not ("H" or "L" or "N"))
        {
            return null;
        }

        if (vc == "N" && vi == "N" && va == "N" && sc == "N" && si == "N" && sa == "N")
        {
            return 0.0;
        }

        // EQ1 (Exploitability)
        int eq1;
        if (av == "N" && pr == "N" && ui == "N") eq1 = 0;
        else if (av == "P" || pr == "H" || ui == "A") eq1 = 2;
        else eq1 = 1;

        // EQ2 (Complexity)
        var eq2 = (ac == "L" && at == "N") ? 0 : 1;

        // EQ3 (Vulnerable System Impact)
        int eq3;
        if (vc == "H" && vi == "H") eq3 = 0;
        else if (vc == "H" || vi == "H" || va == "H") eq3 = 1;
        else if (vc == "L" || vi == "L" || va == "L") eq3 = 2;
        else eq3 = 3;

        // EQ4 (Subsequent System Impact)
        int eq4;
        if (si == "H" && sa == "H") eq4 = 0;
        else if (sc == "H" || si == "H" || sa == "H") eq4 = 1;
        else if (sc == "L" || si == "L" || sa == "L") eq4 = 2;
        else eq4 = 3;

        return LookupCvss4BaseScore(eq1, eq2, eq3, eq4);
    }

    private static readonly double[,,,] Cvss4BaseScoreTable = new double[4, 4, 3, 3]
    {
        // eq3 = 0
        {
            // eq4 = 0
            { { 10.0, 9.9, 9.2 }, { 9.8, 9.5, 9.2 }, { 9.4, 9.2, 9.2 } },
            // eq4 = 1
            { { 9.7, 9.5, 8.5 }, { 9.3, 9.0, 8.5 }, { 8.8, 8.5, 8.5 } },
            // eq4 = 2
            { { 9.4, 9.2, 8.1 }, { 8.9, 8.6, 8.1 }, { 8.4, 8.1, 8.1 } },
            // eq4 = 3
            { { 9.3, 9.0, 7.9 }, { 8.7, 8.4, 7.9 }, { 8.2, 7.9, 7.9 } }
        },
        // eq3 = 1
        {
            // eq4 = 0
            { { 9.7, 9.4, 8.4 }, { 9.2, 8.9, 8.4 }, { 8.7, 8.4, 8.4 } },
            // eq4 = 1
            { { 9.1, 8.8, 7.7 }, { 8.6, 8.3, 7.7 }, { 8.0, 7.7, 7.7 } },
            // eq4 = 2
            { { 8.7, 8.4, 7.2 }, { 8.2, 7.8, 7.2 }, { 7.5, 7.2, 7.2 } },
            // eq4 = 3
            { { 8.6, 8.2, 7.0 }, { 8.0, 7.6, 7.0 }, { 7.3, 7.0, 7.0 } }
        },
        // eq3 = 2
        {
            // eq4 = 0
            { { 9.2, 8.9, 7.5 }, { 8.6, 8.2, 7.5 }, { 7.9, 7.5, 7.5 } },
            // eq4 = 1
            { { 8.3, 7.9, 6.4 }, { 7.6, 7.1, 6.4 }, { 6.8, 6.4, 6.4 } },
            // eq4 = 2
            { { 7.1, 6.6, 4.8 }, { 6.3, 5.8, 4.8 }, { 5.3, 4.8, 4.8 } },
            // eq4 = 3
            { { 6.9, 6.4, 4.5 }, { 6.0, 5.5, 4.5 }, { 5.0, 4.5, 4.5 } }
        },
        // eq3 = 3
        {
            // eq4 = 0
            { { 8.5, 8.1, 6.3 }, { 7.7, 7.2, 6.3 }, { 6.8, 6.3, 6.3 } },
            // eq4 = 1
            { { 7.4, 6.9, 4.9 }, { 6.5, 5.9, 4.9 }, { 5.4, 4.9, 4.9 } },
            // eq4 = 2
            { { 5.6, 5.1, 3.1 }, { 4.6, 4.1, 3.1 }, { 3.6, 3.1, 3.1 } },
            // eq4 = 3
            { { 0.0, 0.0, 0.0 }, { 0.0, 0.0, 0.0 }, { 0.0, 0.0, 0.0 } }
        }
    };

    private static double LookupCvss4BaseScore(int eq1, int eq2, int eq3, int eq4)
    {
        if (eq3 < 0 || eq3 > 3 || eq4 < 0 || eq4 > 3 || eq1 < 0 || eq1 > 2 || eq2 < 0 || eq2 > 2)
        {
            return 0.0;
        }

        return Cvss4BaseScoreTable[eq3, eq4, eq1, eq2];
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
