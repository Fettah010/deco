namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/msstore-configure.ps1 credential resolution + arg
/// building. Secrets are never logged: callers echo lengths only.
/// Env precedence per value: flag → DEVCLOUD_* (deco canonical) →
/// MSSTORE_* → PARTNER_CENTER_* → DEVTEM_STORE_*.
/// </summary>
public static class MsstoreConfig
{
    public sealed record Credentials(
        string TenantId,
        string SellerId,
        string ClientId,
        string ClientSecret,
        string CertThumbprint,
        string CertFilePath,
        string CertPassword,
        string TenantSource,
        string SellerSource,
        string ClientSource);

    /// <summary>
    /// Finds the real Developer CLI on PATH and rejects impostors (e.g. the
    /// inbox MSStore.exe app): the candidate must answer --version with exit 0.
    /// </summary>
    public static string? FindCli(Infrastructure.IProcessRunner runner, string[] pathDirs)
    {
        foreach (string dir in pathDirs)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim(), "msstore.exe");
            }
            catch
            {
                continue;
            }

            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                Infrastructure.ProcessResult r = runner.RunAsync(
                    candidate, ["--version"], timeout: TimeSpan.FromSeconds(30))
                    .GetAwaiter().GetResult();
                if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut + r.StdErr))
                {
                    return candidate;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    /// <summary>
    /// FindCli that throws instead of returning null. The message matches the
    /// historical call-site text byte-for-byte in plain mode. Tested.
    /// </summary>
    public static string RequireCli(
        Infrastructure.IProcessRunner runner,
        string? purpose = null)
    {
        string? cli = FindCli(runner,
            (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
        if (cli is null)
        {
            throw new Core.DecoException(
                string.IsNullOrWhiteSpace(purpose)
                    ? "msstore (Microsoft Store Developer CLI v0.4.3+) not found on PATH. Install: winget install \"Microsoft Store Developer CLI\"."
                    : $"msstore CLI not found ({purpose}). Install: winget install \"Microsoft Store Developer CLI\".",
                "msstore-missing",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        return cli;
    }

    public static string ResolveValue(string flag, Func<string, string?>? lookup = null, params string[] envNames)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag;
        }

        foreach (string n in envNames)
        {
            string? v = lookup(n);
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return string.Empty;
    }

    public static string ResolveSource(string flag, string resolved, params string[] envNames)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return "flag";
        }

        foreach (string n in envNames)
        {
            if (resolved == Environment.GetEnvironmentVariable(n))
            {
                return "env " + n;
            }
        }

        return "unresolved";
    }

    public static Credentials Resolve(
        string tenantId, string sellerId, string clientId, string clientSecret,
        string certThumbprint, string certFilePath, string certPassword,
        Func<string, string?>? envLookup = null)
    {
        string[] tenantEnvs = ["DEVCLOUD_STORE_TENANT_ID", "DECO_STORE_TENANT_ID", "MSSTORE_TENANT_ID", "PARTNER_CENTER_TENANT_ID", "DEVTEM_STORE_TENANT_ID"];
        string[] sellerEnvs = ["DEVCLOUD_STORE_SELLER_ID", "DECO_STORE_SELLER_ID", "MSSTORE_SELLER_ID", "PARTNER_CENTER_SELLER_ID", "DEVTEM_STORE_SELLER_ID"];
        string[] clientEnvs = ["DEVCLOUD_STORE_CLIENT_ID", "DECO_STORE_CLIENT_ID", "MSSTORE_CLIENT_ID", "PARTNER_CENTER_CLIENT_ID", "DEVTEM_STORE_CLIENT_ID"];
        string[] secretEnvs = ["DEVCLOUD_STORE_CLIENT_SECRET", "DECO_STORE_CLIENT_SECRET", "MSSTORE_CLIENT_SECRET", "PARTNER_CENTER_CLIENT_SECRET", "DEVTEM_STORE_CLIENT_SECRET"];

        string tenant = ResolveValue(tenantId, envLookup, tenantEnvs);
        string seller = ResolveValue(sellerId, envLookup, sellerEnvs);
        string client = ResolveValue(clientId, envLookup, clientEnvs);
        string secret = ResolveValue(clientSecret, envLookup, secretEnvs);
        string thumb = ResolveValue(certThumbprint, envLookup, ["DEVCLOUD_STORE_CERT_THUMBPRINT", "MSSTORE_CERT_THUMBPRINT"]);
        string certFile = ResolveValue(certFilePath, envLookup, ["DEVCLOUD_STORE_CERT_FILE", "MSSTORE_CERT_FILE"]);
        string certPass = ResolveValue(certPassword, envLookup, ["DEVCLOUD_STORE_CERT_PASSWORD", "MSSTORE_CERT_PASSWORD"]);

        return new Credentials(
            tenant, seller, client, secret, thumb, certFile, certPass,
            ResolveSource(tenantId, tenant, tenantEnvs),
            ResolveSource(sellerId, seller, sellerEnvs),
            ResolveSource(clientId, client, clientEnvs));
    }

    public static bool IsEmpty(Credentials c) =>
        string.IsNullOrWhiteSpace(c.TenantId) && string.IsNullOrWhiteSpace(c.SellerId)
        && string.IsNullOrWhiteSpace(c.ClientId) && string.IsNullOrWhiteSpace(c.ClientSecret)
        && string.IsNullOrWhiteSpace(c.CertThumbprint) && string.IsNullOrWhiteSpace(c.CertFilePath);

    public static List<string> MissingIdentity(Credentials c)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(c.TenantId))
        {
            missing.Add("TenantId (MSSTORE_TENANT_ID)");
        }

        if (string.IsNullOrWhiteSpace(c.SellerId))
        {
            missing.Add("SellerId (MSSTORE_SELLER_ID)");
        }

        if (string.IsNullOrWhiteSpace(c.ClientId))
        {
            missing.Add("ClientId (MSSTORE_CLIENT_ID)");
        }

        return missing;
    }

    /// <summary>Dry-run display: secret/identity values become &lt;redacted&gt;. Pure and tested.</summary>
    public static List<string> RedactForDisplay(List<string> args, Credentials c)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal)
        {
            c.TenantId, c.SellerId, c.ClientId, c.ClientSecret, c.CertPassword,
        };
        secrets.RemoveWhere(string.IsNullOrWhiteSpace);
        return args.Select(a => secrets.Contains(a) ? "<redacted>" : a).ToList();
    }
    public static List<string> ReconfigureArgs(Credentials c)
    {
        bool hasSecret = !string.IsNullOrWhiteSpace(c.ClientSecret);
        bool hasThumb = !string.IsNullOrWhiteSpace(c.CertThumbprint);
        bool hasCertFile = !string.IsNullOrWhiteSpace(c.CertFilePath);
        if (!hasSecret && !hasThumb && !hasCertFile)
        {
            throw new Core.DecoException(
                "Provide either --client-secret or a certificate (--cert-thumbprint / --cert-file).",
                "msstore-no-auth",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        var args = new List<string>
            { "reconfigure", "--tenantId", c.TenantId, "--sellerId", c.SellerId, "--clientId", c.ClientId };
        if (hasSecret)
        {
            args.AddRange(["--clientSecret", c.ClientSecret]);
        }
        else if (hasThumb)
        {
            args.AddRange(["--certificateThumbprint", c.CertThumbprint]);
        }
        else
        {
            args.AddRange(["--certificateFilePath", c.CertFilePath]);
            if (!string.IsNullOrWhiteSpace(c.CertPassword))
            {
                args.AddRange(["--certificatePassword", c.CertPassword]);
            }
        }

        return args;
    }
}
