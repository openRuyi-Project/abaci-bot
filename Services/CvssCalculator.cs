namespace abaci_bot.Services;

/// <summary>
/// Authoritative offline CVSS v4.0 / v3.1 / v3.0 Base Score calculator compliant with FIRST specifications.
/// </summary>
public static class CvssCalculator
{
    /// <summary>
    /// Parses a CVSS v4.0, v3.1 or v3.0 vector string
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

        foreach (var (k, v) in metrics)
        {
            switch (k)
            {
                case "CVSS": break;
                case "AV" or "MAV": if (v is not ("X" or "N" or "A" or "L" or "P")) return null; break;
                case "AC" or "MAC": if (v is not ("X" or "L" or "H")) return null; break;
                case "AT" or "MAT": if (v is not ("X" or "N" or "P")) return null; break;
                case "PR" or "MPR": if (v is not ("X" or "N" or "L" or "H")) return null; break;
                case "UI" or "MUI": if (v is not ("X" or "N" or "P" or "A")) return null; break;
                case "VC" or "MVC": if (v is not ("X" or "H" or "L" or "N")) return null; break;
                case "VI" or "MVI": if (v is not ("X" or "H" or "L" or "N")) return null; break;
                case "VA" or "MVA": if (v is not ("X" or "H" or "L" or "N")) return null; break;
                case "SC" or "MSC": if (v is not ("X" or "H" or "L" or "N")) return null; break;
                case "SI": if (v is not ("H" or "L" or "N")) return null; break;
                case "SA": if (v is not ("H" or "L" or "N")) return null; break;
                case "MSI" or "MSA": if (v is not ("X" or "S" or "H" or "L" or "N")) return null; break;
                case "E": if (v is not ("X" or "A" or "P" or "U")) return null; break;
                case "CR" or "IR" or "AR": if (v is not ("X" or "H" or "M" or "L")) return null; break;
                default: return null;
            }
        }

        if (vc == "N" && vi == "N" && va == "N" && sc == "N" && si == "N" && sa == "N")
        {
            return 0.0;
        }

        return ScoreCvss4(metrics);
    }

    private static double ScoreCvss4(IReadOnlyDictionary<string, string> metrics)
    {
        var macroVector = ComputeCvss4MacroVector(metrics);
        if (!Cvss4MacroLookup.TryGetValue(macroVector, out var value))
        {
            return 0.0;
        }

        var eq1 = macroVector[0] - '0';
        var eq2 = macroVector[1] - '0';
        var eq3 = macroVector[2] - '0';
        var eq4 = macroVector[3] - '0';
        var eq5 = macroVector[4] - '0';
        var eq6 = macroVector[5] - '0';

        var eq1Lower = $"{eq1 + 1}{eq2}{eq3}{eq4}{eq5}{eq6}";
        var eq2Lower = $"{eq1}{eq2 + 1}{eq3}{eq4}{eq5}{eq6}";

        string? eq3eq6Lower = null;
        string? eq3eq6LowerLeft = null;
        string? eq3eq6LowerRight = null;

        if (eq3 == 1 && eq6 == 1)
        {
            eq3eq6Lower = $"{eq1}{eq2}{eq3 + 1}{eq4}{eq5}{eq6}";
        }
        else if (eq3 == 0 && eq6 == 1)
        {
            eq3eq6Lower = $"{eq1}{eq2}{eq3 + 1}{eq4}{eq5}{eq6}";
        }
        else if (eq3 == 1 && eq6 == 0)
        {
            eq3eq6Lower = $"{eq1}{eq2}{eq3}{eq4}{eq5}{eq6 + 1}";
        }
        else if (eq3 == 0 && eq6 == 0)
        {
            eq3eq6LowerLeft = $"{eq1}{eq2}{eq3}{eq4}{eq5}{eq6 + 1}";
            eq3eq6LowerRight = $"{eq1}{eq2}{eq3 + 1}{eq4}{eq5}{eq6}";
        }
        else
        {
            eq3eq6Lower = $"{eq1}{eq2}{eq3 + 1}{eq4}{eq5}{eq6 + 1}";
        }

        var eq4Lower = $"{eq1}{eq2}{eq3}{eq4 + 1}{eq5}{eq6}";
        var eq5Lower = $"{eq1}{eq2}{eq3}{eq4}{eq5 + 1}{eq6}";

        var scoreEq1Lower = Cvss4MacroLookup.TryGetValue(eq1Lower, out var s1) ? s1 : double.NaN;
        var scoreEq2Lower = Cvss4MacroLookup.TryGetValue(eq2Lower, out var s2) ? s2 : double.NaN;

        double scoreEq3eq6Lower = double.NaN;
        if (eq3 == 0 && eq6 == 0)
        {
            var left = Cvss4MacroLookup.TryGetValue(eq3eq6LowerLeft!, out var sl) ? sl : double.NaN;
            var right = Cvss4MacroLookup.TryGetValue(eq3eq6LowerRight!, out var sr) ? sr : double.NaN;
            if (!double.IsNaN(left) && !double.IsNaN(right))
            {
                scoreEq3eq6Lower = Math.Max(left, right);
            }
            else if (!double.IsNaN(left))
            {
                scoreEq3eq6Lower = left;
            }
            else
            {
                scoreEq3eq6Lower = right;
            }
        }
        else if (eq3eq6Lower != null && Cvss4MacroLookup.TryGetValue(eq3eq6Lower, out var s36))
        {
            scoreEq3eq6Lower = s36;
        }

        var scoreEq4Lower = Cvss4MacroLookup.TryGetValue(eq4Lower, out var s4) ? s4 : double.NaN;
        var scoreEq5Lower = Cvss4MacroLookup.TryGetValue(eq5Lower, out var s5) ? s5 : double.NaN;

        var eq1Maxes = MaxComposedEq1[eq1];
        var eq2Maxes = MaxComposedEq2[eq2];
        var eq3eq6Maxes = MaxComposedEq3Eq6[eq3, eq6];
        var eq4Maxes = MaxComposedEq4[eq4];
        var eq5Maxes = MaxComposedEq5[eq5];

        double curDistEq1 = 0, curDistEq2 = 0, curDistEq3eq6 = 0, curDistEq4 = 0;

        foreach (var m1 in eq1Maxes)
        {
            foreach (var m2 in eq2Maxes)
            {
                foreach (var m3 in eq3eq6Maxes)
                {
                    foreach (var m4 in eq4Maxes)
                    {
                        foreach (var m5 in eq5Maxes)
                        {
                            var maxVec = m1 + m2 + m3 + m4 + m5;

                            var dAV = GetMetricWeight("AV", GetMetric(metrics, "AV")) - GetMetricWeight("AV", ExtractMetric(maxVec, "AV"));
                            var dPR = GetMetricWeight("PR", GetMetric(metrics, "PR")) - GetMetricWeight("PR", ExtractMetric(maxVec, "PR"));
                            var dUI = GetMetricWeight("UI", GetMetric(metrics, "UI")) - GetMetricWeight("UI", ExtractMetric(maxVec, "UI"));

                            var dAC = GetMetricWeight("AC", GetMetric(metrics, "AC")) - GetMetricWeight("AC", ExtractMetric(maxVec, "AC"));
                            var dAT = GetMetricWeight("AT", GetMetric(metrics, "AT")) - GetMetricWeight("AT", ExtractMetric(maxVec, "AT"));

                            var dVC = GetMetricWeight("VC", GetMetric(metrics, "VC")) - GetMetricWeight("VC", ExtractMetric(maxVec, "VC"));
                            var dVI = GetMetricWeight("VI", GetMetric(metrics, "VI")) - GetMetricWeight("VI", ExtractMetric(maxVec, "VI"));
                            var dVA = GetMetricWeight("VA", GetMetric(metrics, "VA")) - GetMetricWeight("VA", ExtractMetric(maxVec, "VA"));

                            var dSC = GetMetricWeight("SC", GetMetric(metrics, "SC")) - GetMetricWeight("SC", ExtractMetric(maxVec, "SC"));
                            var dSI = GetMetricWeight("SI", GetMetric(metrics, "SI")) - GetMetricWeight("SI", ExtractMetric(maxVec, "SI"));
                            var dSA = GetMetricWeight("SA", GetMetric(metrics, "SA")) - GetMetricWeight("SA", ExtractMetric(maxVec, "SA"));

                            var dCR = GetMetricWeight("CR", GetMetric(metrics, "CR")) - GetMetricWeight("CR", ExtractMetric(maxVec, "CR"));
                            var dIR = GetMetricWeight("IR", GetMetric(metrics, "IR")) - GetMetricWeight("IR", ExtractMetric(maxVec, "IR"));
                            var dAR = GetMetricWeight("AR", GetMetric(metrics, "AR")) - GetMetricWeight("AR", ExtractMetric(maxVec, "AR"));

                            if (dAV < 0 || dPR < 0 || dUI < 0 || dAC < 0 || dAT < 0 ||
                                dVC < 0 || dVI < 0 || dVA < 0 || dSC < 0 || dSI < 0 || dSA < 0 ||
                                dCR < 0 || dIR < 0 || dAR < 0)
                            {
                                continue;
                            }

                            curDistEq1 = dAV + dPR + dUI;
                            curDistEq2 = dAC + dAT;
                            curDistEq3eq6 = dVC + dVI + dVA + dCR + dIR + dAR;
                            curDistEq4 = dSC + dSI + dSA;
                            goto MaxFound;
                        }
                    }
                }
            }
        }

    MaxFound:
        const double step = 0.1;
        var maxSevEq1 = MaxSeverityEq1[eq1] * step;
        var maxSevEq2 = MaxSeverityEq2[eq2] * step;
        var maxSevEq3eq6 = MaxSeverityEq3Eq6[eq3, eq6] * step;
        var maxSevEq4 = MaxSeverityEq4[eq4] * step;

        var availEq1 = value - scoreEq1Lower;
        var availEq2 = value - scoreEq2Lower;
        var availEq3eq6 = value - scoreEq3eq6Lower;
        var availEq4 = value - scoreEq4Lower;
        var availEq5 = value - scoreEq5Lower;

        var nExistingLower = 0;
        double normEq1 = 0, normEq2 = 0, normEq3eq6 = 0, normEq4 = 0;

        if (!double.IsNaN(availEq1))
        {
            nExistingLower++;
            normEq1 = availEq1 * (curDistEq1 / maxSevEq1);
        }
        if (!double.IsNaN(availEq2))
        {
            nExistingLower++;
            normEq2 = availEq2 * (curDistEq2 / maxSevEq2);
        }
        if (!double.IsNaN(availEq3eq6))
        {
            nExistingLower++;
            normEq3eq6 = availEq3eq6 * (curDistEq3eq6 / maxSevEq3eq6);
        }
        if (!double.IsNaN(availEq4))
        {
            nExistingLower++;
            normEq4 = availEq4 * (curDistEq4 / maxSevEq4);
        }
        if (!double.IsNaN(availEq5))
        {
            nExistingLower++;
        }

        var meanDist = nExistingLower == 0 ? 0.0 : (normEq1 + normEq2 + normEq3eq6 + normEq4) / nExistingLower;
        value -= meanDist;
        if (value < 0.0) value = 0.0;
        if (value > 10.0) value = 10.0;

        return Math.Round(value, 1, MidpointRounding.AwayFromZero);
    }

    private static string GetMetric(IReadOnlyDictionary<string, string> metrics, string metric)
    {
        if (metric == "E")
        {
            return (!metrics.TryGetValue("E", out var e) || e == "X") ? "A" : e;
        }
        if (metric is "CR" or "IR" or "AR")
        {
            return (!metrics.TryGetValue(metric, out var r) || r == "X") ? "H" : r;
        }
        if (metrics.TryGetValue("M" + metric, out var mod) && mod != "X")
        {
            return mod;
        }
        return metrics.TryGetValue(metric, out var v) ? v : string.Empty;
    }

    private static string ComputeCvss4MacroVector(IReadOnlyDictionary<string, string> metrics)
    {
        var av = GetMetric(metrics, "AV");
        var pr = GetMetric(metrics, "PR");
        var ui = GetMetric(metrics, "UI");
        var ac = GetMetric(metrics, "AC");
        var at = GetMetric(metrics, "AT");
        var vc = GetMetric(metrics, "VC");
        var vi = GetMetric(metrics, "VI");
        var va = GetMetric(metrics, "VA");
        var sc = GetMetric(metrics, "SC");
        var si = GetMetric(metrics, "SI");
        var sa = GetMetric(metrics, "SA");
        var cr = GetMetric(metrics, "CR");
        var ir = GetMetric(metrics, "IR");
        var ar = GetMetric(metrics, "AR");
        var e = GetMetric(metrics, "E");
        var msi = GetMetric(metrics, "MSI");
        var msa = GetMetric(metrics, "MSA");

        // EQ1 (Exploitability)
        int eq1;
        if (av == "N" && pr == "N" && ui == "N")
        {
            eq1 = 0;
        }
        else if ((av == "N" || pr == "N" || ui == "N") && !(av == "N" && pr == "N" && ui == "N") && av != "P")
        {
            eq1 = 1;
        }
        else
        {
            eq1 = 2;
        }

        // EQ2 (Complexity)
        var eq2 = (ac == "L" && at == "N") ? 0 : 1;

        // EQ3 (Vulnerable System Impact)
        int eq3;
        if (vc == "H" && vi == "H")
        {
            eq3 = 0;
        }
        else if (vc == "H" || vi == "H" || va == "H")
        {
            eq3 = 1;
        }
        else
        {
            eq3 = 2;
        }

        // EQ4 (Subsequent System Impact)
        int eq4;
        if (msi == "S" || msa == "S")
        {
            eq4 = 0;
        }
        else if (sc == "H" || si == "H" || sa == "H")
        {
            eq4 = 1;
        }
        else
        {
            eq4 = 2;
        }

        // EQ5 (Exploit Maturity)
        int eq5;
        if (e == "A") eq5 = 0;
        else if (e == "P") eq5 = 1;
        else eq5 = 2;

        // EQ6 (Impact Requirements)
        int eq6;
        if ((cr == "H" && vc == "H") || (ir == "H" && vi == "H") || (ar == "H" && va == "H"))
        {
            eq6 = 0;
        }
        else
        {
            eq6 = 1;
        }

        return $"{eq1}{eq2}{eq3}{eq4}{eq5}{eq6}";
    }

    private static double GetMetricWeight(string metric, string val) => (metric, val) switch
    {
        ("AV", "N") => 0.0,
        ("AV", "A") => 0.1,
        ("AV", "L") => 0.2,
        ("AV", "P") => 0.3,

        ("PR", "N") => 0.0,
        ("PR", "L") => 0.1,
        ("PR", "H") => 0.2,

        ("UI", "N") => 0.0,
        ("UI", "P") => 0.1,
        ("UI", "A") => 0.2,

        ("AC", "L") => 0.0,
        ("AC", "H") => 0.1,

        ("AT", "N") => 0.0,
        ("AT", "P") => 0.1,

        ("VC", "H") => 0.0,
        ("VC", "L") => 0.1,
        ("VC", "N") => 0.2,

        ("VI", "H") => 0.0,
        ("VI", "L") => 0.1,
        ("VI", "N") => 0.2,

        ("VA", "H") => 0.0,
        ("VA", "L") => 0.1,
        ("VA", "N") => 0.2,

        ("SC", "H") => 0.1,
        ("SC", "L") => 0.2,
        ("SC", "N") => 0.3,

        ("SI", "S") => 0.0,
        ("SI", "H") => 0.1,
        ("SI", "L") => 0.2,
        ("SI", "N") => 0.3,

        ("SA", "S") => 0.0,
        ("SA", "H") => 0.1,
        ("SA", "L") => 0.2,
        ("SA", "N") => 0.3,

        ("CR", "H") => 0.0,
        ("CR", "M") => 0.1,
        ("CR", "L") => 0.2,

        ("IR", "H") => 0.0,
        ("IR", "M") => 0.1,
        ("IR", "L") => 0.2,

        ("AR", "H") => 0.0,
        ("AR", "M") => 0.1,
        ("AR", "L") => 0.2,

        ("E", "U") => 0.2,
        ("E", "P") => 0.1,
        ("E", "A") => 0.0,

        _ => 0.0
    };

    private static string ExtractMetric(string maxVector, string metric)
    {
        var tag = metric + ":";
        var idx = maxVector.IndexOf(tag, StringComparison.Ordinal);
        if (idx < 0) return string.Empty;
        var start = idx + tag.Length;
        var end = maxVector.IndexOf('/', start);
        return end >= 0 ? maxVector[start..end] : maxVector[start..];
    }

    private static readonly string[][] MaxComposedEq1 =
    [
        ["AV:N/PR:N/UI:N/"],
        ["AV:A/PR:N/UI:N/", "AV:N/PR:L/UI:N/", "AV:N/PR:N/UI:P/"],
        ["AV:P/PR:N/UI:N/", "AV:A/PR:L/UI:P/"]
    ];

    private static readonly string[][] MaxComposedEq2 =
    [
        ["AC:L/AT:N/"],
        ["AC:H/AT:N/", "AC:L/AT:P/"]
    ];

    private static readonly string[,][] MaxComposedEq3Eq6 =
    {
        // eq3 = 0
        {
            ["VC:H/VI:H/VA:H/CR:H/IR:H/AR:H/"],
            ["VC:H/VI:H/VA:L/CR:M/IR:M/AR:H/", "VC:H/VI:H/VA:H/CR:M/IR:M/AR:M/"]
        },
        // eq3 = 1
        {
            ["VC:L/VI:H/VA:H/CR:H/IR:H/AR:H/", "VC:H/VI:L/VA:H/CR:H/IR:H/AR:H/"],
            ["VC:L/VI:H/VA:L/CR:H/IR:M/AR:H/", "VC:L/VI:H/VA:H/CR:H/IR:M/AR:M/", "VC:H/VI:L/VA:H/CR:M/IR:H/AR:M/", "VC:H/VI:L/VA:L/CR:M/IR:H/AR:H/", "VC:L/VI:L/VA:H/CR:H/IR:H/AR:M/"]
        },
        // eq3 = 2
        {
            [],
            ["VC:L/VI:L/VA:L/CR:H/IR:H/AR:H/"]
        }
    };

    private static readonly string[][] MaxComposedEq4 =
    [
        ["SC:H/SI:S/SA:S/"],
        ["SC:H/SI:H/SA:H/"],
        ["SC:L/SI:L/SA:L/"]
    ];

    private static readonly string[][] MaxComposedEq5 =
    [
        ["E:A/"],
        ["E:P/"],
        ["E:U/"]
    ];

    private static readonly double[] MaxSeverityEq1 = [1.0, 4.0, 5.0];
    private static readonly double[] MaxSeverityEq2 = [1.0, 2.0];
    private static readonly double[,] MaxSeverityEq3Eq6 =
    {
        { 7.0, 6.0 },
        { 8.0, 8.0 },
        { 0.0, 10.0 }
    };
    private static readonly double[] MaxSeverityEq4 = [6.0, 5.0, 4.0];

    private static readonly Dictionary<string, double> Cvss4MacroLookup = new(270)
    {
        ["000000"] = 10,
        ["000001"] = 9.9,
        ["000010"] = 9.8,
        ["000011"] = 9.5,
        ["000020"] = 9.5,
        ["000021"] = 9.2,
        ["000100"] = 10,
        ["000101"] = 9.6,
        ["000110"] = 9.3,
        ["000111"] = 8.7,
        ["000120"] = 9.1,
        ["000121"] = 8.1,
        ["000200"] = 9.3,
        ["000201"] = 9,
        ["000210"] = 8.9,
        ["000211"] = 8,
        ["000220"] = 8.1,
        ["000221"] = 6.8,
        ["001000"] = 9.8,
        ["001001"] = 9.5,
        ["001010"] = 9.5,
        ["001011"] = 9.2,
        ["001020"] = 9,
        ["001021"] = 8.4,
        ["001100"] = 9.3,
        ["001101"] = 9.2,
        ["001110"] = 8.9,
        ["001111"] = 8.1,
        ["001120"] = 8.1,
        ["001121"] = 6.5,
        ["001200"] = 8.8,
        ["001201"] = 8,
        ["001210"] = 7.8,
        ["001211"] = 7,
        ["001220"] = 6.9,
        ["001221"] = 4.8,
        ["002001"] = 9.2,
        ["002011"] = 8.2,
        ["002021"] = 7.2,
        ["002101"] = 7.9,
        ["002111"] = 6.9,
        ["002121"] = 5,
        ["002201"] = 6.9,
        ["002211"] = 5.5,
        ["002221"] = 2.7,
        ["010000"] = 9.9,
        ["010001"] = 9.7,
        ["010010"] = 9.5,
        ["010011"] = 9.2,
        ["010020"] = 9.2,
        ["010021"] = 8.5,
        ["010100"] = 9.5,
        ["010101"] = 9.1,
        ["010110"] = 9,
        ["010111"] = 8.3,
        ["010120"] = 8.4,
        ["010121"] = 7.1,
        ["010200"] = 9.2,
        ["010201"] = 8.1,
        ["010210"] = 8.2,
        ["010211"] = 7.1,
        ["010220"] = 7.2,
        ["010221"] = 5.3,
        ["011000"] = 9.5,
        ["011001"] = 9.3,
        ["011010"] = 9.2,
        ["011011"] = 8.5,
        ["011020"] = 8.5,
        ["011021"] = 7.3,
        ["011100"] = 9.2,
        ["011101"] = 8.2,
        ["011110"] = 8,
        ["011111"] = 7.2,
        ["011120"] = 7,
        ["011121"] = 5.9,
        ["011200"] = 8.4,
        ["011201"] = 7,
        ["011210"] = 7.1,
        ["011211"] = 5.2,
        ["011220"] = 5,
        ["011221"] = 3,
        ["012001"] = 8.6,
        ["012011"] = 7.5,
        ["012021"] = 5.2,
        ["012101"] = 7.1,
        ["012111"] = 5.2,
        ["012121"] = 2.9,
        ["012201"] = 6.3,
        ["012211"] = 2.9,
        ["012221"] = 1.7,
        ["100000"] = 9.8,
        ["100001"] = 9.5,
        ["100010"] = 9.4,
        ["100011"] = 8.7,
        ["100020"] = 9.1,
        ["100021"] = 8.1,
        ["100100"] = 9.4,
        ["100101"] = 8.9,
        ["100110"] = 8.6,
        ["100111"] = 7.4,
        ["100120"] = 7.7,
        ["100121"] = 6.4,
        ["100200"] = 8.7,
        ["100201"] = 7.5,
        ["100210"] = 7.4,
        ["100211"] = 6.3,
        ["100220"] = 6.3,
        ["100221"] = 4.9,
        ["101000"] = 9.4,
        ["101001"] = 8.9,
        ["101010"] = 8.8,
        ["101011"] = 7.7,
        ["101020"] = 7.6,
        ["101021"] = 6.7,
        ["101100"] = 8.6,
        ["101101"] = 7.6,
        ["101110"] = 7.4,
        ["101111"] = 5.8,
        ["101120"] = 5.9,
        ["101121"] = 5,
        ["101200"] = 7.2,
        ["101201"] = 5.7,
        ["101210"] = 5.7,
        ["101211"] = 5.2,
        ["101220"] = 5.2,
        ["101221"] = 2.5,
        ["102001"] = 8.3,
        ["102011"] = 7,
        ["102021"] = 5.4,
        ["102101"] = 6.5,
        ["102111"] = 5.8,
        ["102121"] = 2.6,
        ["102201"] = 5.3,
        ["102211"] = 2.1,
        ["102221"] = 1.3,
        ["110000"] = 9.5,
        ["110001"] = 9,
        ["110010"] = 8.8,
        ["110011"] = 7.6,
        ["110020"] = 7.6,
        ["110021"] = 7,
        ["110100"] = 9,
        ["110101"] = 7.7,
        ["110110"] = 7.5,
        ["110111"] = 6.2,
        ["110120"] = 6.1,
        ["110121"] = 5.3,
        ["110200"] = 7.7,
        ["110201"] = 6.6,
        ["110210"] = 6.8,
        ["110211"] = 5.9,
        ["110220"] = 5.2,
        ["110221"] = 3,
        ["111000"] = 8.9,
        ["111001"] = 7.8,
        ["111010"] = 7.6,
        ["111011"] = 6.7,
        ["111020"] = 6.2,
        ["111021"] = 5.8,
        ["111100"] = 7.4,
        ["111101"] = 5.9,
        ["111110"] = 5.7,
        ["111111"] = 5.7,
        ["111120"] = 4.7,
        ["111121"] = 2.3,
        ["111200"] = 6.1,
        ["111201"] = 5.2,
        ["111210"] = 5.7,
        ["111211"] = 2.9,
        ["111220"] = 2.4,
        ["111221"] = 1.6,
        ["112001"] = 7.1,
        ["112011"] = 5.9,
        ["112021"] = 3,
        ["112101"] = 5.8,
        ["112111"] = 2.6,
        ["112121"] = 1.5,
        ["112201"] = 2.3,
        ["112211"] = 1.3,
        ["112221"] = 0.6,
        ["200000"] = 9.3,
        ["200001"] = 8.7,
        ["200010"] = 8.6,
        ["200011"] = 7.2,
        ["200020"] = 7.5,
        ["200021"] = 5.8,
        ["200100"] = 8.6,
        ["200101"] = 7.4,
        ["200110"] = 7.4,
        ["200111"] = 6.1,
        ["200120"] = 5.6,
        ["200121"] = 3.4,
        ["200200"] = 7,
        ["200201"] = 5.4,
        ["200210"] = 5.2,
        ["200211"] = 4,
        ["200220"] = 4,
        ["200221"] = 2.2,
        ["201000"] = 8.5,
        ["201001"] = 7.5,
        ["201010"] = 7.4,
        ["201011"] = 5.5,
        ["201020"] = 6.2,
        ["201021"] = 5.1,
        ["201100"] = 7.2,
        ["201101"] = 5.7,
        ["201110"] = 5.5,
        ["201111"] = 4.1,
        ["201120"] = 4.6,
        ["201121"] = 1.9,
        ["201200"] = 5.3,
        ["201201"] = 3.6,
        ["201210"] = 3.4,
        ["201211"] = 1.9,
        ["201220"] = 1.9,
        ["201221"] = 0.8,
        ["202001"] = 6.4,
        ["202011"] = 5.1,
        ["202021"] = 2,
        ["202101"] = 4.7,
        ["202111"] = 2.1,
        ["202121"] = 1.1,
        ["202201"] = 2.4,
        ["202211"] = 0.9,
        ["202221"] = 0.4,
        ["210000"] = 8.8,
        ["210001"] = 7.5,
        ["210010"] = 7.3,
        ["210011"] = 5.3,
        ["210020"] = 6,
        ["210021"] = 5,
        ["210100"] = 7.3,
        ["210101"] = 5.5,
        ["210110"] = 5.9,
        ["210111"] = 4,
        ["210120"] = 4.1,
        ["210121"] = 2,
        ["210200"] = 5.4,
        ["210201"] = 4.3,
        ["210210"] = 4.5,
        ["210211"] = 2.2,
        ["210220"] = 2,
        ["210221"] = 1.1,
        ["211000"] = 7.5,
        ["211001"] = 5.5,
        ["211010"] = 5.8,
        ["211011"] = 4.5,
        ["211020"] = 4,
        ["211021"] = 2.1,
        ["211100"] = 6.1,
        ["211101"] = 5.1,
        ["211110"] = 4.8,
        ["211111"] = 1.8,
        ["211120"] = 2,
        ["211121"] = 0.9,
        ["211200"] = 4.6,
        ["211201"] = 1.8,
        ["211210"] = 1.7,
        ["211211"] = 0.7,
        ["211220"] = 0.8,
        ["211221"] = 0.2,
        ["212001"] = 5.3,
        ["212011"] = 2.4,
        ["212021"] = 1.4,
        ["212101"] = 2.4,
        ["212111"] = 1.2,
        ["212121"] = 0.5,
        ["212201"] = 1,
        ["212211"] = 0.3,
        ["212221"] = 0.1,
    };

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
