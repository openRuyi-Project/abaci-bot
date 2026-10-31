using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace abaci_bot.Services;

/// <summary>
/// Deep module implementing SPDX license expression validation with offline embedded dictionary,
/// legacy RPM alias normalization, and optional remote refresh from spdx.org.
/// </summary>
public class SpdxLicenseService : ISpdxLicenseService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpdxComplianceOptions _options;
    private readonly ILogger<SpdxLicenseService>? _logger;

    private readonly ConcurrentDictionary<string, string> _canonicalLicenses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _exceptions = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> LegacyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "GPLv2", "GPL-2.0-only" },
        { "GPLv2+", "GPL-2.0-or-later" },
        { "GPLv3", "GPL-3.0-only" },
        { "GPLv3+", "GPL-3.0-or-later" },
        { "LGPLv2", "LGPL-2.0-only" },
        { "LGPLv2+", "LGPL-2.0-or-later" },
        { "LGPLv2.1", "LGPL-2.1-only" },
        { "LGPLv2.1+", "LGPL-2.1-or-later" },
        { "LGPLv3", "LGPL-3.0-only" },
        { "LGPLv3+", "LGPL-3.0-or-later" },
        { "AGPLv3", "AGPL-3.0-only" },
        { "AGPLv3+", "AGPL-3.0-or-later" },
        { "GPL-1.0", "GPL-1.0-only" },
        { "GPL-2.0", "GPL-2.0-only" },
        { "GPL-3.0", "GPL-3.0-only" },
        { "LGPL-2.0", "LGPL-2.0-only" },
        { "LGPL-2.1", "LGPL-2.1-only" },
        { "LGPL-3.0", "LGPL-3.0-only" },
        { "AGPL-3.0", "AGPL-3.0-only" },
        { "ASL 2.0", "Apache-2.0" },
        { "Apache 2.0", "Apache-2.0" },
        { "Apache-2", "Apache-2.0" },
        { "MPLv1.1", "MPL-1.1" },
        { "MPLv2.0", "MPL-2.0" },
        { "CDDL", "CDDL-1.0" },
        { "EPL", "EPL-1.0" }
    };

    private static readonly string[] EmbeddedStandardLicenses =
    [
        "0BSD", "AFL-1.1", "AFL-1.2", "AFL-2.0", "AFL-2.1", "AFL-3.0", "AGPL-1.0-only", "AGPL-1.0-or-later",
        "AGPL-3.0-only", "AGPL-3.0-or-later", "Apache-1.0", "Apache-1.1", "Apache-2.0", "APSL-1.0", "APSL-2.0",
        "Artistic-1.0", "Artistic-2.0", "BSD-1-Clause", "BSD-2-Clause", "BSD-3-Clause", "BSD-3-Clause-Clear",
        "BSD-4-Clause", "BSL-1.0", "CC0-1.0", "CC-BY-1.0", "CC-BY-2.0", "CC-BY-2.5", "CC-BY-3.0", "CC-BY-4.0",
        "CC-BY-NC-4.0", "CC-BY-NC-ND-4.0", "CC-BY-NC-SA-4.0", "CC-BY-SA-1.0", "CC-BY-SA-2.0", "CC-BY-SA-3.0",
        "CC-BY-SA-4.0", "CDDL-1.0", "CDDL-1.1", "CECILL-1.0", "CECILL-2.0", "CECILL-2.1", "CECILL-B", "CECILL-C",
        "CPL-1.0", "curl", "ECL-1.0", "ECL-2.0", "EFL-1.0", "EFL-2.0", "EPL-1.0", "EPL-2.0", "EUPL-1.0", "EUPL-1.1",
        "EUPL-1.2", "FTL", "GFDL-1.1-only", "GFDL-1.1-or-later", "GFDL-1.2-only", "GFDL-1.2-or-later",
        "GFDL-1.3-only", "GFDL-1.3-or-later", "GPL-1.0-only", "GPL-1.0-or-later", "GPL-2.0-only", "GPL-2.0-or-later",
        "GPL-3.0-only", "GPL-3.0-or-later", "HPND", "IJG", "IPL-1.0", "ISC", "JSON", "LGPL-2.0-only",
        "LGPL-2.0-or-later", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later", "Libpng",
        "LIL-1.0", "Linux-OpenIB", "MIT", "MIT-0", "MPL-1.0", "MPL-1.1", "MPL-2.0", "MS-PL", "MS-RL", "MulanPSL-1.0",
        "MulanPSL-2.0", "NCSA", "ODbL-1.0", "OFL-1.1", "OpenSSL", "OSL-1.0", "OSL-2.0", "OSL-3.0", "PHP-3.0",
        "PHP-3.01", "PostgreSQL", "Python-2.0", "Qhull", "Ruby", "Sendmail", "SGI-B-2.0", "Sleepycat", "UPL-1.0",
        "Unlicense", "Vim", "W3C", "WTFPL", "X11", "Xnet", "Zlib", "ZPL-2.0", "ZPL-2.1"
    ];

    private static readonly string[] EmbeddedExceptions =
    [
        "389-exception", "Asterisk-exception", "Asterisk-linking-protocols-exception", "Autoconf-exception-2.0",
        "Autoconf-exception-3.0", "Autoconf-exception-generic", "Autoconf-exception-generic-3.0", "Autoconf-exception-macro",
        "Bison-exception-1.24", "Bison-exception-2.2", "Bootloader-exception", "CGAL-linking-exception",
        "Classpath-exception-2.0", "CLISP-exception-2.0", "cryptsetup-OpenSSL-exception", "DigiRule-FOSS-exception",
        "eCos-exception-2.0", "erlang-otp-linking-exception", "Fawkes-Runtime-exception", "FLTK-exception",
        "fmt-exception", "Font-exception-2.0", "freertos-exception-2.0", "GCC-exception-2.0",
        "GCC-exception-2.0-note", "GCC-exception-3.1", "Gmsh-exception", "GNAT-exception",
        "GNOME-examples-exception", "GNU-compiler-exception", "gnu-javamail-exception", "GPL-3.0-389-ds-base-exception",
        "GPL-3.0-interface-exception", "GPL-3.0-linking-exception", "GPL-3.0-linking-source-exception", "GPL-CC-1.0",
        "GStreamer-exception-2005", "GStreamer-exception-2008", "harbour-exception", "i2p-gpl-java-exception",
        "Independent-modules-exception", "KiCad-libraries-exception", "LGPL-3.0-linking-exception", "libpri-OpenH323-exception",
        "Libtool-exception", "Linux-syscall-note", "LLGPL", "LLVM-exception",
        "LZMA-exception", "mif-exception", "mxml-exception", "Nokia-Qt-exception-1.1",
        "OCaml-LGPL-linking-exception", "OCCT-exception-1.0", "OpenJDK-assembly-exception-1.0", "openvpn-openssl-exception",
        "PCRE2-exception", "PS-or-PDF-font-exception-20170817", "QPL-1.0-INRIA-2004-exception", "Qt-GPL-exception-1.0",
        "Qt-LGPL-exception-1.1", "Qwt-exception-1.0", "romic-exception", "RRDtool-FLOSS-exception-2.0",
        "SANE-exception", "SHL-2.0", "SHL-2.1", "stunnel-exception",
        "SWI-exception", "Swift-exception", "Texinfo-exception", "u-boot-exception-2.0",
        "UBDL-exception", "Universal-FOSS-exception-1.0", "vsftpd-openssl-exception", "WxWindows-exception-3.1",
        "x11vnc-openssl-exception"
    ];

    public SpdxLicenseService(
        IHttpClientFactory httpClientFactory,
        IOptions<SpdxComplianceOptions> options,
        ILogger<SpdxLicenseService>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        LoadEmbeddedLicenses();
        LoadEmbeddedExceptions();

        if (_canonicalLicenses.IsEmpty)
        {
            foreach (var id in EmbeddedStandardLicenses)
            {
                _canonicalLicenses[id] = id;
            }
        }

        if (_exceptions.IsEmpty)
        {
            foreach (var ex in EmbeddedExceptions)
            {
                _exceptions[ex] = ex;
            }
        }
    }

    public bool IsValidSpdxIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var trimmed = identifier.Trim();

        // Check exact canonical license
        if (_canonicalLicenses.ContainsKey(trimmed))
            return true;

        // Check if trailing '+' (e.g. Apache-2.0+ or GPL-2.0+)
        if (trimmed.EndsWith('+'))
        {
            var baseId = trimmed[..^1];
            if (_canonicalLicenses.ContainsKey(baseId) || (_options.AllowLegacyAliases && LegacyAliases.ContainsKey(baseId)))
                return true;
        }

        // Check legacy aliases if permitted
        if (_options.AllowLegacyAliases && LegacyAliases.ContainsKey(trimmed))
            return true;

        return false;
    }

    public bool IsValidSpdxException(string exceptionId)
    {
        if (string.IsNullOrWhiteSpace(exceptionId))
            return false;

        return _exceptions.ContainsKey(exceptionId.Trim());
    }

    public SpdxValidationResult ValidateExpression(string? licenseExpression)
    {
        if (string.IsNullOrWhiteSpace(licenseExpression))
        {
            return new SpdxValidationResult(
                IsValid: false,
                InvalidIdentifiers: Array.Empty<string>(),
                ErrorMessage: "License field is missing or empty.");
        }

        var normalized = licenseExpression;
        if (_options.AllowLegacyAliases)
        {
            normalized = normalized
                .Replace("ASL 2.0", "Apache-2.0", StringComparison.OrdinalIgnoreCase)
                .Replace("Apache 2.0", "Apache-2.0", StringComparison.OrdinalIgnoreCase);
        }

        var tokens = Tokenize(normalized);
        if (tokens.Count == 0)
        {
            return new SpdxValidationResult(
                IsValid: false,
                InvalidIdentifiers: Array.Empty<string>(),
                ErrorMessage: "No valid license tokens found.");
        }

        var parser = new ExpressionParser(tokens, this);
        return parser.Parse();
    }

    public async Task<bool> RefreshRemoteMetadataAsync(CancellationToken cancellationToken = default)
    {
        var refreshedAny = false;

        if (!string.IsNullOrWhiteSpace(_options.UpdateRemoteUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("SpdxLicenseClient");
                var response = await client.GetAsync(_options.UpdateRemoteUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    if (doc.RootElement.TryGetProperty("licenses", out var licensesArray) &&
                        licensesArray.ValueKind == JsonValueKind.Array)
                    {
                        var fetchedLicenses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var element in licensesArray.EnumerateArray())
                        {
                            if (element.TryGetProperty("licenseId", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    fetchedLicenses.Add(id);
                                }
                            }
                        }

                        if (fetchedLicenses.Count > 0)
                        {
                            var addedCount = fetchedLicenses.Count(id => !_canonicalLicenses.ContainsKey(id));
                            var removedCount = _canonicalLicenses.Keys.Count(id => !fetchedLicenses.Contains(id));

                            _canonicalLicenses.Clear();
                            foreach (var id in fetchedLicenses)
                            {
                                _canonicalLicenses[id] = id;
                            }

                            _logger?.LogInformation("Successfully reconciled SPDX licenses: {Total} total (+{Added}/-{Removed}).",
                                _canonicalLicenses.Count, addedCount, removedCount);
                            refreshedAny = true;
                        }
                        else
                        {
                            _logger?.LogWarning("Remote SPDX license update returned 0 valid entries; retaining current dictionary.");
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning("Remote SPDX license update returned status code {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to refresh remote SPDX licenses. Retaining current dictionary.");
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.UpdateExceptionsRemoteUrl))
        {
            try
            {
                var client = _httpClientFactory.CreateClient("SpdxLicenseClient");
                var response = await client.GetAsync(_options.UpdateExceptionsRemoteUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    if (doc.RootElement.TryGetProperty("exceptions", out var exceptionsArray) &&
                        exceptionsArray.ValueKind == JsonValueKind.Array)
                    {
                        var fetchedExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var element in exceptionsArray.EnumerateArray())
                        {
                            if (element.TryGetProperty("licenseExceptionId", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    fetchedExceptions.Add(id);
                                }
                            }
                        }

                        if (fetchedExceptions.Count > 0)
                        {
                            var addedCount = fetchedExceptions.Count(id => !_exceptions.ContainsKey(id));
                            var removedCount = _exceptions.Keys.Count(id => !fetchedExceptions.Contains(id));

                            _exceptions.Clear();
                            foreach (var id in fetchedExceptions)
                            {
                                _exceptions[id] = id;
                            }

                            _logger?.LogInformation("Successfully reconciled SPDX exceptions: {Total} total (+{Added}/-{Removed}).",
                                _exceptions.Count, addedCount, removedCount);
                            refreshedAny = true;
                        }
                        else
                        {
                            _logger?.LogWarning("Remote SPDX exception update returned 0 valid entries; retaining current dictionary.");
                        }
                    }
                }
                else
                {
                    _logger?.LogWarning("Remote SPDX exception update returned status code {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to refresh remote SPDX exceptions. Retaining current dictionary.");
            }
        }

        return refreshedAny;
    }

    private static List<Token> Tokenize(string expression)
    {
        var tokens = new List<Token>();
        var i = 0;
        var len = expression.Length;

        while (i < len)
        {
            var ch = expression[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '(')
            {
                tokens.Add(new Token(TokenType.OpenParen, "("));
                i++;
                continue;
            }

            if (ch == ')')
            {
                tokens.Add(new Token(TokenType.CloseParen, ")"));
                i++;
                continue;
            }

            // Word token: identifier or keyword
            var start = i;
            while (i < len && !char.IsWhiteSpace(expression[i]) && expression[i] != '(' && expression[i] != ')')
            {
                i++;
            }

            var word = expression[start..i];
            if (string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase))
                tokens.Add(new Token(TokenType.And, "AND"));
            else if (string.Equals(word, "OR", StringComparison.OrdinalIgnoreCase))
                tokens.Add(new Token(TokenType.Or, "OR"));
            else if (string.Equals(word, "WITH", StringComparison.OrdinalIgnoreCase))
                tokens.Add(new Token(TokenType.With, "WITH"));
            else
                tokens.Add(new Token(TokenType.Identifier, word));
        }

        return tokens;
    }

    private enum TokenType
    {
        Identifier,
        And,
        Or,
        With,
        OpenParen,
        CloseParen
    }

    private record Token(TokenType Type, string Value);

    private class ExpressionParser
    {
        private readonly List<Token> _tokens;
        private readonly SpdxLicenseService _service;
        private readonly List<string> _invalidIdentifiers = new();
        private int _index;

        public ExpressionParser(List<Token> tokens, SpdxLicenseService service)
        {
            _tokens = tokens;
            _service = service;
        }

        public SpdxValidationResult Parse()
        {
            try
            {
                var sb = new StringBuilder();
                ParseOr(sb);

                if (_index < _tokens.Count)
                {
                    return new SpdxValidationResult(
                        IsValid: false,
                        InvalidIdentifiers: _invalidIdentifiers,
                        ErrorMessage: $"Unexpected trailing token '{_tokens[_index].Value}'.");
                }

                var isValid = _invalidIdentifiers.Count == 0;
                return new SpdxValidationResult(
                    IsValid: isValid,
                    InvalidIdentifiers: _invalidIdentifiers,
                    NormalizedExpression: sb.ToString(),
                    ErrorMessage: isValid ? null : $"Unknown or non-standard license identifiers: {string.Join(", ", _invalidIdentifiers)}");
            }
            catch (Exception ex)
            {
                return new SpdxValidationResult(
                    IsValid: false,
                    InvalidIdentifiers: _invalidIdentifiers,
                    ErrorMessage: ex.Message);
            }
        }

        private void ParseOr(StringBuilder sb)
        {
            ParseAnd(sb);
            while (Match(TokenType.Or))
            {
                sb.Append(" OR ");
                ParseAnd(sb);
            }
        }

        private void ParseAnd(StringBuilder sb)
        {
            ParseWith(sb);
            while (Match(TokenType.And))
            {
                sb.Append(" AND ");
                ParseWith(sb);
            }
        }

        private void ParseWith(StringBuilder sb)
        {
            ParsePrimary(sb);
            if (Match(TokenType.With))
            {
                sb.Append(" WITH ");
                if (_index >= _tokens.Count || _tokens[_index].Type != TokenType.Identifier)
                {
                    throw new InvalidOperationException("Expected exception identifier after 'WITH'.");
                }

                var exToken = _tokens[_index++];
                if (!_service.IsValidSpdxException(exToken.Value))
                {
                    _invalidIdentifiers.Add(exToken.Value);
                }
                sb.Append(exToken.Value);
            }
        }

        private void ParsePrimary(StringBuilder sb)
        {
            if (Match(TokenType.OpenParen))
            {
                sb.Append('(');
                ParseOr(sb);
                if (!Match(TokenType.CloseParen))
                {
                    throw new InvalidOperationException("Missing closing parenthesis ')'.");
                }
                sb.Append(')');
            }
            else if (_index < _tokens.Count && _tokens[_index].Type == TokenType.Identifier)
            {
                var idToken = _tokens[_index++];
                if (!_service.IsValidSpdxIdentifier(idToken.Value))
                {
                    _invalidIdentifiers.Add(idToken.Value);
                }
                sb.Append(idToken.Value);
            }
            else
            {
                var tokenStr = _index < _tokens.Count ? _tokens[_index].Value : "EOF";
                throw new InvalidOperationException($"Expected license identifier or '(' but encountered '{tokenStr}'.");
            }
        }

        private bool Match(TokenType type)
        {
            if (_index < _tokens.Count && _tokens[_index].Type == type)
            {
                _index++;
                return true;
            }
            return false;
        }
    }

    private void LoadEmbeddedLicenses()
    {
        try
        {
            var asm = typeof(SpdxLicenseService).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("spdx-licenses.json", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("licenses", out var licensesArray) &&
                        licensesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in licensesArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("licenseId", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    _canonicalLicenses[id] = id;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load embedded spdx-licenses.json resource.");
        }
    }

    private void LoadEmbeddedExceptions()
    {
        try
        {
            var asm = typeof(SpdxLicenseService).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("spdx-exceptions.json", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var doc = JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("exceptions", out var exceptionsArray) &&
                        exceptionsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in exceptionsArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("licenseExceptionId", out var idProp))
                            {
                                var id = idProp.GetString();
                                if (!string.IsNullOrWhiteSpace(id))
                                {
                                    _exceptions[id] = id;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load embedded spdx-exceptions.json resource.");
        }
    }
}
