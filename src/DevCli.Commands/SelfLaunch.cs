using System.Reflection;

namespace DevCli.Commands;

/// <summary>
/// How to re-invoke ourselves (child stages, scheduled tasks). Handles all
/// launch modes without Assembly.Location (empty in single-file apps):
/// published/apphost exe (use directly), `dotnet deco.dll` (re-invoke
/// through the dotnet host + entry-assembly path). Pure and tested.
/// </summary>
public static class SelfLaunch
{
    public static (string FileName, List<string> PreArgs) LaunchPrefix()
    {
        string self = Environment.ProcessPath ?? string.Empty;
        if (self.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return ("dotnet", [self]);
        }

        if (Path.GetFileName(self).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? entry = Assembly.GetEntryAssembly()?.GetName().Name;
            string dll = string.IsNullOrWhiteSpace(entry)
                ? string.Empty
                : Path.Combine(AppContext.BaseDirectory, entry + ".dll");
            if (!string.IsNullOrWhiteSpace(dll) && File.Exists(dll))
            {
                return (self, [dll]);
            }
        }

        return (self, []);
    }
}
