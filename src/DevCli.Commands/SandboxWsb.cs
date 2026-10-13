using System.Text;

namespace DevCli.Commands;

/// <summary>
/// Windows Sandbox (.wsb) generation for `sandbox capture`. Pure string
/// building (unit-tested); the host validates mapped host dirs exist (the
/// container refuses to start otherwise) before launching.
/// vGPU defaults OFF: paravirtualized GPU is faster but, when wedged, kills
/// boots pre-logon with zero diagnostics; WARP renders identical pixels.
/// Pass vgpu: true to opt back in.
/// </summary>
public static class SandboxWsb
{
    public sealed record Mapping(string HostDir, string GuestDir, bool ReadOnly);

    public static string Build(
        int memoryMb,
        bool networking,
        IReadOnlyList<Mapping> maps,
        string logonCommand,
        bool vgpu = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Configuration>");
        sb.AppendLine(vgpu ? "<VGpu>Enable</VGpu>" : "<VGpu>Disable</VGpu>");
        if (memoryMb > 0)
        {
            sb.AppendLine($"<MemoryInMB>{memoryMb}</MemoryInMB>");
        }

        sb.AppendLine(networking ? "<Networking>Default</Networking>" : "<Networking>Disable</Networking>");
        if (maps.Count > 0)
        {
            sb.AppendLine("<MappedFolders>");
            foreach (Mapping m in maps)
            {
                sb.AppendLine("<MappedFolder>");
                sb.AppendLine($"<HostFolder>{Escape(m.HostDir)}</HostFolder>");
                sb.AppendLine($"<SandboxFolder>{Escape(m.GuestDir)}</SandboxFolder>");
                sb.AppendLine(m.ReadOnly ? "<ReadOnly>true</ReadOnly>" : "<ReadOnly>false</ReadOnly>");
                sb.AppendLine("</MappedFolder>");
            }

            sb.AppendLine("</MappedFolders>");
        }

        if (!string.IsNullOrWhiteSpace(logonCommand))
        {
            sb.AppendLine($"<LogonCommand><Command>{Escape(logonCommand.Trim())}</Command></LogonCommand>");
        }

        sb.AppendLine("</Configuration>");
        return sb.ToString();
    }

    internal static string Escape(string s) =>
        (s ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
