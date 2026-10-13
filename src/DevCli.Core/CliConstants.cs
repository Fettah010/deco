namespace DevCli.Core;

/// <summary>Single source of truth for tool identity, versions and contracts (v0.2.0).</summary>
public static class CliConstants
{
    public const string ToolName = "deco";
    public const string ToolTitle = "DevEcosystem Commander";
    public const string Version = "0.2.0";
    public const string ManifestFileName = ".devcli.json";

    public static readonly string[] ValidVerbosity = ["quiet", "minimal", "normal", "detailed"];
    public static readonly string[] ValidFormats = ["table", "json"];

    /// <summary>Legacy locale default (pre-manifest repos). Manifest `locales` overrides this.</summary>
    public static readonly string[] DefaultLocales = ["en-US", "es-ES", "fr-FR"];
}
