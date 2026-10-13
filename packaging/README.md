# deco packaging (v0.1.2)

## Install on this PC (recommended)

```powershell
packaging\install.ps1          # publish single-file + PATH + completions (idempotent)
packaging\install.ps1 -Uninstall
```

Installs `%LOCALAPPDATA%\Programs\deco\deco.exe` (single file, ~12 MB,
framework-dependent — needs the .NET 10 runtime, checked at install),
adds it to the **user** PATH (no elevation), and wires
`deco completions` into `$PROFILE` (marker block, never duplicated).
Restart terminals afterwards, then `deco --help` works everywhere.

Three more install paths, cheapest first. All produce the same `deco` on PATH.

## 1. dotnet tool (local nupkg today, nuget.org later)

```powershell
dotnet pack src/DevCli/DevCli.csproj -c Release
dotnet tool install --global --add-source ./src/DevCli/bin/Release DevEcosystem.Deco
deco --version   # 0.1.0
```

To publish: `dotnet nuget push *.nupkg --source https://api.nuget.org/v3/index.json`.
(`PackAsTool` + `ToolCommandName=deco` are already set in `DevCli.csproj`.)

## 2. Winget (portable zip)

```powershell
dotnet publish src/DevCli/DevCli.csproj -c Release -r win-x64 `
  -p:PublishSingleFile=true -p:SelfContained=true `
  -o out/deco-0.1.0-win-x64
Compress-Archive out/deco-0.1.0-win-x64/deco.exe out/deco-0.1.0-win-x64.zip
# upload the zip to the GitHub Release tagged v0.1.0, then fill
# InstallerUrl/InstallerSha256 in packaging/winget/*.installer.yaml
winget validate packaging/winget
```

Manifests live in `packaging/winget/` (min OS 10.0.17763 = Windows 10 1809,
matching the plan's floor). PR the folder to `microsoft/winget-pkgs`.

## 3. Repo checkout (dev loop)

```powershell
dotnet build DevCli.slnx -c Release
# alias or shim to src/DevCli/bin/Release/net10.0/deco.exe
```

## Shell completions

```powershell
deco completions --shell powershell >> $PROFILE   # or --shell bash|zsh
```
