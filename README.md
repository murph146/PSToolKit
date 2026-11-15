# PSToolKit

A Windows Forms front end for everyday admin PowerShell commands: system info, services and processes, network tools, and Active Directory user/group management.

## Requirements

- Windows 10/11 or Windows Server 2016+
- .NET 8 SDK to build (`winget install Microsoft.DotNet.SDK.8`) — or Visual Studio 2022 with the ".NET desktop development" workload
- Windows PowerShell 5.1 (built into Windows) — the app always runs commands in it
- **Active Directory commands:** RSAT AD module
  - Windows 10/11: `Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0`
  - Server: `Install-WindowsFeature RSAT-AD-PowerShell`
- **Remote computers:** WinRM enabled on the target for CIM / `Invoke-Command` commands (`Enable-PSRemoting` on the target, or via GPO)

## Build and run

```powershell
cd PSToolKit
dotnet run                         # build + launch
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish
```

`publish\PSToolKit.exe` plus `publish\commands.json` is everything you need to copy to other admins' machines (they need the .NET 8 Desktop Runtime; use `--self-contained true` to avoid that).

To open in Visual Studio, open `PSToolKit.sln`. The UI is built in code (`MainForm.cs`), so there's no designer surface.

## Using it

- Pick a command on the left (Ctrl+F to search), fill in the parameters, press **Run** / F5 / Enter. Esc or **Stop** kills a running command.
- Commands marked ⚠ change something and ask for confirmation first.
- Values carry over between commands — type a computer name or username once and it's pre-filled for the next command that uses the same parameter.
- **Show script** displays the exact PowerShell that will run, so you can audit it or paste it into a console.
- Output is live; errors are red. Copy or save it with the buttons.
- The title bar shows "(Administrator)" when elevated. For AD changes with a separate admin account, Shift+right-click the .exe → **Run as different user**.

## Adding your own commands

All commands live in `commands.json` next to the .exe. Edit it (File → Edit commands.json…) and press Ctrl+R to reload — no recompile.

```json
{
  "category": "Services & Processes",
  "name": "Restart print spooler",
  "description": "Restarts the Spooler service.",
  "confirm": true,
  "requires": "Administrator rights",
  "parameters": [
    { "name": "ComputerName", "label": "Computer", "hint": "Leave blank for this PC" }
  ],
  "script": [
    "Get-Service Spooler @Target | Restart-Service -Force",
    "Get-Service Spooler @Target"
  ]
}
```

**Parameters** become PowerShell variables (`"name": "User"` → `$User`). Fields:

| Field | Meaning |
|---|---|
| `name` | Variable name (letters, digits, `_`) |
| `label` | Text shown in the form (defaults to `name`) |
| `kind` | `Text` (default), `Number`, `Choice`, `Switch` (checkbox → `$true`/`$false`) |
| `options` | List of values for `Choice` |
| `default` | Initial value (`"true"` for a ticked Switch) |
| `required` | Block Run until filled in |
| `hint` | Placeholder / tooltip text |

**ComputerName is special:** any command with a `ComputerName` parameter also gets `$Target` (a splat that adds `-ComputerName` only when a remote machine was entered) and `$IsRemote`. Use `Get-CimInstance ... @Target` for cmdlets that take `-ComputerName`, or `if ($IsRemote) { Invoke-Command ... }` for ones that don't.

**Safety:** values are inserted as single-quoted PowerShell literals, so text typed into a field can't inject code. Inside your own script, prefer passing variables to cmdlet parameters rather than building filter strings from them (see "Find user", which escapes input for its LDAP filter). When passing a value to an external program (e.g. `quser`, `ping.exe`), validate it first — Windows PowerShell 5.1 doesn't escape embedded double quotes in native-command arguments.

## Security notes

- **`commands.json` is code.** Every script in it runs with `-ExecutionPolicy Bypass` as whoever launches the app — often an elevated or domain-admin account. Anyone who can edit `commands.json` (or drop a DLL next to the .exe) can run code as that admin.
- **Install only to a folder standard users can't write to**, such as `C:\Program Files\PSToolKit` or a file share where users have read-only access. Don't run it from Downloads, the desktop, or a share that users can write to.
- **Review changes to `commands.json`** before deploying them, the same way you'd review a script.
- **Distribute builds you made from source**, preferably Authenticode-signed. Don't run prebuilt binaries whose origin you can't verify.

## Included commands

| Category | Commands |
|---|---|
| System Info | System summary, Disk space, Installed software, Installed updates, Recent errors in event log, Logged-on users, Group Policy Update ⚠ |
| Services & Processes | List services, Service details, Start/stop/restart service ⚠, Top processes, Stop process ⚠ |
| Network | Ping, Test port, DNS lookup, Traceroute, IP configuration, Network connections, Flush DNS cache ⚠ |
| Active Directory | Find user, User details, Locked-out users, Unlock account ⚠, Enable/disable account ⚠, User's groups, Group members, Add user to group ⚠, Remove user from group ⚠, Copy groups from another user ⚠, Inactive accounts |

## Project layout

```
PSToolKit.csproj
Program.cs                    entry point
MainForm.cs                   all UI
CommandDefinition.cs          commands.json model + loader/validation
PowerShellRunner.cs           runs scripts in powershell.exe, streams output, Stop support
commands.json                 the command catalog
```

Each run is a separate `powershell.exe -NoProfile` process, so a hung command never freezes the window and runs don't share state.
