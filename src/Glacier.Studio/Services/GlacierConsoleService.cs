using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Showcase.Services.GlacierStudio;

public class GlacierConsoleService
{
    private readonly List<ConsoleLogEntry> _logs = new();
    private readonly object _lock = new();
    private readonly string _workspaceRoot;
    private readonly string _shellExecutable;

    public string CurrentWorkingDirectory { get; private set; }

    public IReadOnlyList<ConsoleLogEntry> Logs
    {
        get
        {
            lock (_lock)
            {
                return _logs.ToArray();
            }
        }
    }

    public event Action? OnLogAdded;

    public GlacierConsoleService(string? workspaceRoot = null)
    {
        _workspaceRoot = !string.IsNullOrEmpty(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : GlacierWorkspaceResolver.ResolveWorkspaceRoot();

        CurrentWorkingDirectory = _workspaceRoot;
        _shellExecutable = ResolveShellExecutable();

        AddLog("SYSTEM", "Glacier Antigravity 2 Studio Console Initialized. Ready for User & Agent commands.");
        AddLog("SYSTEM", $"Workspace Root: {_workspaceRoot}");
        AddLog("SYSTEM", $"Active Shell: {_shellExecutable} (CWD: {CurrentWorkingDirectory})");
    }

    public static string ResolveShellExecutable()
    {
        try
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(p.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return "pwsh.exe";
                }
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pwshStandard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(pwshStandard))
            {
                return pwshStandard;
            }
        }
        catch { }

        return "powershell.exe";
    }

    public void AddLog(string source, string message, bool isError = false, string? command = null)
    {
        lock (_lock)
        {
            if (_logs.Count > 1000)
            {
                _logs.RemoveRange(0, 100);
            }

            _logs.Add(new ConsoleLogEntry
            {
                Source = source,
                Message = message,
                IsError = isError,
                Command = command
            });
        }

        OnLogAdded?.Invoke();
    }

    public void ClearLogs()
    {
        lock (_lock)
        {
            _logs.Clear();
        }
        AddLog("SYSTEM", "Console cleared.");
    }

    public async Task<(int ExitCode, string Output)> ExecuteCommandAsync(
        string command, 
        string source = "USER", 
        CancellationToken cancellationToken = default)
    {
        AddLog(source, $"$ {command}", false, command);

        string trimmed = command.Trim();

        // 1. Interactive working directory tracking across 'cd' / 'Set-Location'
        bool isCd = trimmed.StartsWith("cd", StringComparison.OrdinalIgnoreCase);
        bool isSetLocation = trimmed.StartsWith("Set-Location", StringComparison.OrdinalIgnoreCase);

        if ((isCd && (trimmed.Length == 2 || char.IsWhiteSpace(trimmed[2]) || trimmed[2] == '.' || trimmed[2] == '\\' || trimmed[2] == '/')) ||
            (isSetLocation && (trimmed.Length == 12 || char.IsWhiteSpace(trimmed[12]))))
        {
            string arg = isCd ? trimmed[2..].Trim() : trimmed[12..].Trim();
            string targetDir;

            if (string.IsNullOrWhiteSpace(arg) || arg == "~")
            {
                targetDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                arg = arg.Trim('"', '\'');
                targetDir = Path.IsPathRooted(arg)
                    ? Path.GetFullPath(arg)
                    : Path.GetFullPath(Path.Combine(CurrentWorkingDirectory, arg));
            }

            if (Directory.Exists(targetDir))
            {
                CurrentWorkingDirectory = targetDir;
                string msg = $"Directory changed to: {CurrentWorkingDirectory}";
                AddLog("SYSTEM", msg);
                return (0, msg + Environment.NewLine);
            }
            else
            {
                string err = $"Cannot find path '{targetDir}' because it does not exist.";
                AddLog("SYSTEM", err, isError: true);
                return (1, err + Environment.NewLine);
            }
        }

        // 2. Child process execution
        var outputBuilder = new StringBuilder();
        var outputLock = new object();
        int exitCode = -1;

        try
        {
            string workingDir = Directory.Exists(CurrentWorkingDirectory) ? CurrentWorkingDirectory : _workspaceRoot;
            string scriptWithUtf8 = $"$ProgressPreference = 'SilentlyContinue'; {command}";
            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(scriptWithUtf8));

            var psi = new ProcessStartInfo
            {
                FileName = _shellExecutable,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    lock (outputLock)
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                    AddLog(source, e.Data, isError: false);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    lock (outputLock)
                    {
                        outputBuilder.AppendLine(e.Data);
                    }
                    AddLog(source, e.Data, isError: true);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        AddLog("SYSTEM", $"Command cancelled by user: {command}", isError: true);
                    }
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);
            exitCode = process.ExitCode;

            AddLog("SYSTEM", $"[Process exited with code {exitCode}]", isError: exitCode != 0);
        }
        catch (OperationCanceledException)
        {
            AddLog("SYSTEM", $"Command '{command}' execution aborted.", isError: true);
            return (-1, "Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            string err = $"Failed to execute '{command}': {ex.Message}";
            outputBuilder.AppendLine(err);
            AddLog("SYSTEM", err, isError: true);
            return (-1, err);
        }

        return (exitCode, outputBuilder.ToString());
    }
}

