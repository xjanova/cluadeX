using System.Diagnostics;
using System.IO;
using CluadeX.Models;

namespace CluadeX.Services;

public class CodeExecutionService
{
    private readonly SettingsService _settingsService;

    public CodeExecutionService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task<CodeExecutionResult> ExecuteAsync(string code, string language, CancellationToken ct = default)
    {
        return language.ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cs" => await ExecuteCSharpAsync(code, ct),
            "python" or "py" => await ExecuteScriptAsync("python", code, ".py", ct),
            "javascript" or "js" => await ExecuteScriptAsync("node", code, ".js", ct),
            "typescript" or "ts" => await ExecuteScriptAsync("npx tsx", code, ".ts", ct),
            "powershell" or "ps1" => await ExecuteScriptAsync("powershell -ExecutionPolicy Bypass -File", code, ".ps1", ct),
            "bash" or "sh" => await ExecuteScriptAsync("bash", code, ".sh", ct),
            "batch" or "bat" or "cmd" => await ExecuteScriptAsync("cmd /c", code, ".bat", ct),
            _ => new CodeExecutionResult { Success = false, Error = $"Unsupported language: {language}" }
        };
    }

    private async Task<CodeExecutionResult> ExecuteCSharpAsync(string code, CancellationToken ct)
    {
        string tempDir = Path.Combine(_settingsService.Settings.TempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Check if it's a full program or just a script snippet
            bool isFullProgram = code.Contains("static void Main") || code.Contains("static async Task Main")
                || code.Contains("namespace ") || code.Contains("class Program");

            string programCode;
            if (isFullProgram)
            {
                programCode = code;
            }
            else
            {
                // Wrap in a minimal program
                programCode = $$"""
                    using System;
                    using System.Collections.Generic;
                    using System.Linq;
                    using System.IO;
                    using System.Text;
                    using System.Threading.Tasks;
                    using System.Net.Http;
                    using System.Text.Json;

                    class Program
                    {
                        static async Task Main(string[] args)
                        {
                            {{code}}
                        }
                    }
                    """;
            }

            // Create project
            string csproj = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """;

            await File.WriteAllTextAsync(Path.Combine(tempDir, "TempProject.csproj"), csproj, ct);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Program.cs"), programCode, ct);

            // Build and run
            var result = await RunProcessAsync("dotnet", $"run --project \"{tempDir}\"", tempDir, ct, 30000);
            return result;
        }
        finally
        {
            // Cleanup
            _ = Task.Run(() =>
            {
                try { Directory.Delete(tempDir, true); } catch { }
            });
        }
    }

    private async Task<CodeExecutionResult> ExecuteScriptAsync(string interpreter, string code, string extension, CancellationToken ct)
    {
        string tempFile = Path.Combine(_settingsService.Settings.TempDirectory, $"temp_{Guid.NewGuid():N}{extension}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            await File.WriteAllTextAsync(tempFile, code, ct);

            string args;
            string exe;
            if (interpreter.Contains(' '))
            {
                var parts = interpreter.Split(' ', 2);
                exe = parts[0];
                args = $"{parts[1]} \"{tempFile}\"";
            }
            else
            {
                exe = interpreter;
                args = $"\"{tempFile}\"";
            }

            return await RunProcessAsync(exe, args, Path.GetDirectoryName(tempFile)!, ct, 30000);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static async Task<CodeExecutionResult> RunProcessAsync(
        string fileName, string arguments, string workDir, CancellationToken ct, int timeoutMs = 30000)
    {
        var result = new CodeExecutionResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Error = $"Failed to start process: {fileName}";
                return result;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                result.Error = "Execution timed out (30s limit).";
                result.TimedOut = true;
                return result;
            }

            result.Output = await outputTask;
            result.Error = await errorTask;
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            result.Error = $"Execution failed: {ex.Message}";
        }

        return result;
    }

    public List<CodeBlock> ExtractCodeBlocks(string text)
    {
        var blocks = new List<CodeBlock>();
        int searchStart = 0;

        while (true)
        {
            int start = text.IndexOf("```", searchStart);
            if (start == -1) break;

            int langEnd = text.IndexOf('\n', start + 3);
            if (langEnd == -1) break;

            string lang = text.Substring(start + 3, langEnd - start - 3).Trim().ToLowerInvariant();

            int end = text.IndexOf("```", langEnd + 1);
            if (end == -1) break;

            string code = text.Substring(langEnd + 1, end - langEnd - 1).Trim();

            if (!string.IsNullOrWhiteSpace(code))
            {
                blocks.Add(new CodeBlock
                {
                    Language = string.IsNullOrEmpty(lang) ? "text" : lang,
                    Code = code,
                    StartIndex = start,
                    EndIndex = end + 3,
                });
            }

            searchStart = end + 3;
        }

        return blocks;
    }
}

public class CodeExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; } = -1;
    public bool TimedOut { get; set; }

    public string FullOutput => string.IsNullOrEmpty(Error)
        ? Output
        : string.IsNullOrEmpty(Output)
            ? $"ERROR:\n{Error}"
            : $"{Output}\n\nERROR:\n{Error}";
}
