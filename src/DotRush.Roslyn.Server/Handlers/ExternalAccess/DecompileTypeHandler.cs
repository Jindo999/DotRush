using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DotRush.Protocol;
using DotRush.Protocol.Handlers;
using DotRush.Roslyn.Server.Services;

namespace DotRush.Roslyn.Server.Handlers.ExternalAccess;

/// <summary>
/// Decompiles a type by its full metadata name and opens it as a temporary document.
/// Used by external extensions (e.g. Avalonia tooling) to show navigable metadata-as-source views.
/// </summary>
public class DecompileTypeHandler : IHandler {
    private readonly NavigationService navigationService;

    public DecompileTypeHandler(NavigationService navigationService) {
        this.navigationService = navigationService;
    }

    protected async Task<DecompileTypeResult?> Handle(DecompileTypeParams request, CancellationToken token) {
        if (string.IsNullOrEmpty(request.TypeFullName))
            return null;

        // The workspace may still be settling right after load; the caller is
        // expected to retry once when the result is null.
        var solution = navigationService.OriginalSolution;
        if (solution == null)
            return null;

        foreach (var project in solution.Projects) {
            try {
                var compilation = await project.GetCompilationAsync(token);
                var symbol = compilation?.GetTypeByMetadataName(request.TypeFullName);
                if (symbol == null)
                    continue;

                var span = await navigationService.EmitDecompiledFileAsync(symbol, project, token);
                if (span == null)
                    continue;

                var line = span.Value.Span.Start.Line;
                var character = span.Value.Span.Start.Character;
                if (!string.IsNullOrEmpty(request.MemberName)) {
                    var memberPosition = FindMemberPosition(span.Value.Path, request.MemberName);
                    if (memberPosition.HasValue) {
                        line = memberPosition.Value.Line;
                        character = memberPosition.Value.Character;
                    }
                }

                return new DecompileTypeResult {
                    FilePath = span.Value.Path,
                    Line = line,
                    Character = character
                };
            } catch (Exception) {
                // Try the next project.
            }
        }

        return null;
    }

    private static (int Line, int Character)? FindMemberPosition(string filePath, string memberName) {
        try {
            var wordPattern = new Regex($@"\b{Regex.Escape(memberName)}\b");
            var declarationPattern = new Regex(
                @"^\s*(?:(?:public|private|protected|internal|static|sealed|override|virtual|abstract|new|readonly|partial|async|unsafe|extern|required|volatile)\s+)+"
                + @"[^;{}()]*?\b" + Regex.Escape(memberName) + @"\b\s*(?:\{|=>|;|\(|=|$)",
                RegexOptions.Compiled);

            var lines = File.ReadAllLines(filePath);
            var fallback = -1;
            for (var i = 0; i < lines.Length; i++) {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("using ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("namespace ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[", StringComparison.Ordinal))
                    continue;

                var declarationMatch = declarationPattern.Match(lines[i]);
                if (declarationMatch.Success)
                    return (i, declarationMatch.Index + declarationMatch.Length - 1);

                if (fallback < 0) {
                    var wordMatch = wordPattern.Match(lines[i]);
                    if (wordMatch.Success)
                        fallback = i;
                }
            }

            if (fallback >= 0)
                return (fallback, 0);
        } catch (Exception) {
            // Ignore unreadable files.
        }

        return null;
    }

    public void RegisterHandler(LanguageServer server) {
        server.AddRequestHandler<DecompileTypeParams, DecompileTypeResult?>("dotrush/decompileType", Handle);
    }
}

public class DecompileTypeParams {
    [JsonPropertyName("typeFullName")]
    public string? TypeFullName { get; set; }

    [JsonPropertyName("memberName")]
    public string? MemberName { get; set; }
}

public class DecompileTypeResult {
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }
}
