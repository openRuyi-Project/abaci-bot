using System.Reflection;
using System.Text.Json;
using abaci_bot.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace abaci_bot.Tests;

public class DemoConfigurationOptionsTests
{
    private static readonly string DemoConfigPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.demo.jsonc"));

    [Fact]
    public void AppSettingsDemoJsonc_Exists()
    {
        Assert.True(File.Exists(DemoConfigPath), $"appsettings.demo.jsonc must exist at {DemoConfigPath}");
    }

    [Fact]
    public void AllOptionsProperties_ArePresentInDemoConfig_AndMatchDefaultValues()
    {
        Assert.True(File.Exists(DemoConfigPath), $"appsettings.demo.jsonc must exist at {DemoConfigPath}");

        var json = File.ReadAllText(DemoConfigPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = doc.RootElement;

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(DemoConfigPath, optional: false, reloadOnChange: false)
            .Build();

        var optionsAssembly = typeof(TargetClassificationOptions).Assembly;
        var optionsTypes = optionsAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "abaci_bot.Options" && t.Name.EndsWith("Options"))
            .OrderBy(t => t.Name)
            .ToList();

        Assert.NotEmpty(optionsTypes);

        // Nested option types (for example PackageUpdate:Lookout) are properties of
        // another options type. They do not own a root-level section, so only the
        // types that are not referenced by another options type are checked here.
        var nestedOptionTypes = optionsTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.PropertyType))
            .ToHashSet();

        var rootOptionTypes = optionsTypes
            .Where(t => !nestedOptionTypes.Contains(t))
            .ToList();

        foreach (var type in rootOptionTypes)
        {
            var sectionName = type.Name[..^"Options".Length];
            Assert.True(
                root.TryGetProperty(sectionName, out var sectionElement),
                $"Section '{sectionName}' for options type '{type.Name}' is missing from appsettings.demo.jsonc");

            var defaultInstance = Activator.CreateInstance(type)!;
            var boundInstance = Activator.CreateInstance(type)!;
            configuration.GetSection(sectionName).Bind(boundInstance);

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .ToList();

            foreach (var prop in properties)
            {
                var fullPath = $"{sectionName}:{prop.Name}";

                // 1. Assert property is explicitly present in appsettings.demo.jsonc
                Assert.True(
                    sectionElement.TryGetProperty(prop.Name, out var propElement),
                    $"Property '{fullPath}' is declared in '{type.Name}' but missing from appsettings.demo.jsonc");

                // 2. Assert property has default value in appsettings.demo.jsonc
                var defaultValue = prop.GetValue(defaultInstance);

                if (defaultValue == null)
                {
                    Assert.True(
                        propElement.ValueKind == JsonValueKind.Null,
                        $"Property '{fullPath}' has null default value, but demo config specifies '{propElement}'");
                }
                else if (defaultValue is IEnumerable<string> expectedList)
                {
                    Assert.True(
                        propElement.ValueKind == JsonValueKind.Array,
                        $"Property '{fullPath}' is a collection, but demo config is {propElement.ValueKind}");

                    var actualList = propElement.EnumerateArray()
                        .Select(e => e.GetString())
                        .ToList();

                    Assert.Equal(expectedList, actualList);
                }
                else if (IsComplexType(prop.PropertyType))
                {
                    // Nested options objects have reference equality only; compare
                    // their serialized shape instead of the instances themselves.
                    var expectedJson = JsonSerializer.Serialize(defaultValue, prop.PropertyType);
                    var actualJson = JsonSerializer.Serialize(prop.GetValue(boundInstance), prop.PropertyType);
                    Assert.Equal(expectedJson, actualJson);
                }
                else
                {
                    var actualBoundValue = prop.GetValue(boundInstance);
                    Assert.Equal(defaultValue, actualBoundValue);
                }
            }
        }
    }

    private static bool IsComplexType(Type type)
    {
        return type != typeof(string)
               && !type.IsPrimitive
               && !type.IsEnum
               && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }
}
