using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PSToolKit.Models;

namespace PSToolKit.Services;

/// <summary>
/// Runs a script in a separate Windows PowerShell 5.1 process and streams its output back.
/// A separate process keeps the UI responsive, lets Stop kill a hung command, and gives
/// every run a clean session (no state leaks between commands).
/// </summary>
public sealed class PowerShellRunner
{
    /// <summary>Raised for each output line. Raised on a background thread.</summary>
    public event Action<string, bool>? OutputReceived; // (line, isError)

    private static readonly string PowerShellExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    // PowerShell treats these curly quotes as single quotes too, so all of them must be doubled.
    private static readonly Regex SingleQuoteChars = new("['‘’‚‛]", RegexOptions.Compiled);

    /// <summary>Wraps a value as a PowerShell single-quoted literal. Nothing inside is expanded or executed.</summary>
    public static string Quote(string value) => "'" + SingleQuoteChars.Replace(value, "$0$0") + "'";

    /// <summary>
    /// The script the user would recognize: parameter variables followed by the command's script.
    /// This is what "Show script" displays.
    /// </summary>
    public static string BuildUserScript(CommandDefinition command, IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder();
        foreach (var p in command.Parameters)
        {
            values.TryGetValue(p.Name, out var raw);
            raw ??= "";
            string literal = p.Kind switch
            {
                ParameterKind.Switch => raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? "$true" : "$false",
                ParameterKind.Number => string.IsNullOrWhiteSpace(raw) ? "$null" : "[int]" + Quote(raw.Trim()),
                _ => Quote(raw.Trim())
            };
            sb.Append('$').Append(p.Name).Append(" = ").AppendLine(literal);
        }

        // $Target splats -ComputerName only when a remote machine was given,
        // so local queries don't need WinRM/DCOM to be configured.
        if (command.Parameters.Any(p => p.Name.Equals("ComputerName", StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine("$Target = @{}");
            sb.AppendLine("$IsRemote = $ComputerName -and ($ComputerName -notin @('localhost', '.', '127.0.0.1', $env:COMPUTERNAME))");
            sb.AppendLine("if ($IsRemote) { $Target['ComputerName'] = $ComputerName }");
        }

        sb.AppendLine();
        sb.AppendLine(command.ScriptText);
        return sb.ToString();
    }

    /// <summary>Adds the plumbing that routes errors/warnings and formats output for a text window.</summary>
    private static string WrapForExecution(string userScript)
    {
        // Indent nothing; the user script runs inside a script block so 'return' etc. behave normally.
        return
$@"$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try {{ [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 }} catch {{ }}
$script:__hadErrors = $false
try {{
    & {{
{userScript}
    }} *>&1 | ForEach-Object {{
        if ($_ -is [System.Management.Automation.ErrorRecord]) {{
            $script:__hadErrors = $true
            [Console]::Error.WriteLine($_.ToString())
        }}
        elseif ($_ -is [System.Management.Automation.WarningRecord]) {{ 'WARNING: ' + $_.Message }}
        elseif ($_ -is [System.Management.Automation.VerboseRecord]) {{ 'VERBOSE: ' + $_.Message }}
        elseif ($_ -is [System.Management.Automation.DebugRecord])   {{ 'DEBUG: ' + $_.Message }}
        elseif ($_ -is [System.Management.Automation.InformationRecord]) {{ [string]$_.MessageData }}
        else {{ $_ }}
    }} | Out-String -Stream -Width 250
}}
catch {{
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}}
if ($script:__hadErrors) {{ exit 1 }} else {{ exit 0 }}
";
    }

    /// <summary>Runs the script and returns the process exit code (0 = success, 1 = errors, -1 = stopped).</summary>
    public async Task<int> RunAsync(string userScript, CancellationToken cancellationToken)
    {
        var fullScript = WrapForExecution(userScript);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(fullScript));

        var psi = new ProcessStartInfo
        {
            FileName = File.Exists(PowerShellExe) ? PowerShellExe : "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data, false); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputReceived?.Invoke(e.Data, true); };

        process.Start();
        process.StandardInput.Close(); // nothing to send; prevents prompts from hanging the run
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit(); // flush remaining async output events
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return -1;
        }
    }
}
