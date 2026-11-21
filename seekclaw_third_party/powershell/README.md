# PowerShell 7 for Windows x64

SeekClaw ships the extracted Microsoft PowerShell 7.6.10 Windows x64 release in `win-x64/`. The Windows x64 CLI publish includes these files under `powershell/win-x64/`. Desktop ships the same CLI publish output in its `resources/runtime/` directory. The directory is tracked using ordinary Git.

- Upstream asset [PowerShell-7.6.10-win-x64.zip](https://github.com/PowerShell/PowerShell/releases/download/v7.6.10/PowerShell-7.6.10-win-x64.zip)
- Archive SHA-256 `7d871531806eb1646264e4bbabd9f0c30c23355591bc756bb9909448c29ae166`
- Upstream redistribution notices [`LICENSE.txt`](win-x64/LICENSE.txt) and [`ThirdPartyNotices.txt`](win-x64/ThirdPartyNotices.txt)

To update, verify the official archive hash, replace `win-x64/` with the complete extracted distribution, and update this record and the pinned file hashes in `seekclaw_cli/seekclaw_cli.csproj`.
