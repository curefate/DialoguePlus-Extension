using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DialoguePlus.Compilation;
using DialoguePlus.Diagnostics;

class Program
{
    private static readonly CacheContentProvider _cache = new();
    private static readonly Compiler _compiler = new(new ContentResolver().Register(_cache).Register(new FileContentProvider()));

    private static Uri PathToUri(string filePath)
    {
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
            return uri;
        return new Uri(Path.GetFullPath(filePath));
    }

    static void Main(string[] args)
    {
        Console.Error.WriteLine("[DS C#] C# process started");
        string? input;
        while (!string.IsNullOrEmpty(input = Console.ReadLine()))
        {
            try
            {
                using var doc = JsonDocument.Parse(input);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                switch (type)
                {
                    case "openFile":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            var content = root.GetProperty("content").GetString();
                            if (filePath != null) _cache.AddOrUpdate(PathToUri(filePath), content ?? string.Empty);
                            // Console.Error.WriteLine($"[DS C#] Opened file: {filePath}");
                            break;
                        }
                    case "update":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            var changes = root.GetProperty("changes");
                            if (filePath != null)
                            {
                                var uri = PathToUri(filePath);
                                if (!_cache.TryGetValue(uri, out var text))
                                    text = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;

                                // TODO range patch
                                text = changes.GetString() ?? text;
                                _cache.AddOrUpdate(uri, text);
                            }
                            // Console.Error.WriteLine($"[DS C#] Updated file: {filePath}");
                            break;
                        }
                    case "closeFile":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            if (filePath != null) _cache.Remove(PathToUri(filePath));
                            // Console.Error.WriteLine($"[DS C#] Closed file: {filePath}");
                            break;
                        }
                    case "analyze":
                        {
                            var id = root.GetProperty("id").GetString();
                            var filePath = root.GetProperty("filePath").GetString();

                            if (filePath == null) break;

                            Console.Error.WriteLine($"[DS C#] Analyzing file: {filePath}.");
                            Console.Error.WriteLine($"[DS C#] Request ID: {id}");

                            try
                            {
                                Console.Error.WriteLine($"[DS C#] Starting compilation...");
                                var result = _compiler.Compile(filePath);
                                Console.Error.WriteLine($"[DS C#] Compilation finished successfully.");

                                // Map diagnostics to ensure proper JSON format
                                // Compiler uses 1-based line numbers, LSP uses 0-based, so subtract 1
                                Console.Error.WriteLine($"[DS C#] Mapping {result.Diagnostics.Count} diagnostics...");
                                var diagnostics = result.Diagnostics.Select(d => new DiagnosticResponse
                                {
                                    Message = d.Message,
                                    Line = Math.Max(0, d.Line - 1),  // Convert 1-based to 0-based
                                    Column = Math.Max(0, d.Column - 1),  // Convert 1-based to 0-based
                                    Span = d.Span.HasValue ? new TextSpanResponse
                                    {
                                        StartLine = Math.Max(0, d.Span.Value.StartLine - 1),  // Convert 1-based to 0-based
                                        StartColumn = Math.Max(0, d.Span.Value.StartColumn - 1),  // Convert 1-based to 0-based
                                        EndLine = Math.Max(0, d.Span.Value.EndLine - 1),  // Convert 1-based to 0-based
                                        EndColumn = Math.Max(0, d.Span.Value.EndColumn - 1)  // Convert 1-based to 0-based
                                    } : null,
                                    Severity = (int)d.Severity
                                }).ToArray();

                                Console.Error.WriteLine($"[DS C#] Analysis complete. Found {diagnostics.Length} diagnostics.");

                                var response = JsonSerializer.Serialize(new ReturnResult
                                {
                                    Type = "AnalyzeResult",
                                    Id = id,  // Include the request ID in the response
                                    Diagnostics = diagnostics
                                });
                                
                                Console.WriteLine(response);
                                Console.Out.Flush();  // Force flush to ensure output is sent immediately
                            }
                            catch (Exception compileEx)
                            {
                                Console.Error.WriteLine($"[DS C#] Compilation error: {compileEx.Message}");
                                Console.Error.WriteLine($"[DS C#] Stack trace: {compileEx.StackTrace}");
                                
                                // Send back an error response so the request doesn't timeout
                                var errorResponse = JsonSerializer.Serialize(new ReturnResult
                                {
                                    Type = "AnalyzeResult",
                                    Id = id,
                                    Diagnostics = [new DiagnosticResponse
                                    {
                                        Message = $"Compilation failed: {compileEx.Message}",
                                        Line = 0,
                                        Column = 0,
                                        Severity = 1  // Error
                                    }]
                                });
                                
                                Console.WriteLine(errorResponse);
                                Console.Out.Flush();  // Force flush
                            }
                            break;
                        }
                    case "definition":
                        {
                            var id = root.GetProperty("id").GetString();
                            var filePath = root.GetProperty("filePath").GetString();
                            var pos = root.GetProperty("position");
                            var line = pos.GetProperty("line").GetInt32();  // LSP sends 0-based
                            var col = pos.GetProperty("character").GetInt32();

                            if (filePath == null) break;

                            Console.Error.WriteLine($"[DS C#] Finding definition: ln{line} col{col}");

                            // Convert LSP 0-based line to compiler's 1-based for GetWord
                            if (GetWord(filePath, line + 1, col + 1, out var word))
                            {
                                var fileUri = PathToUri(filePath).AbsoluteUri;
                                List<SymbolPosition> defs;
                                if (word.StartsWith("$"))
                                {
                                    defs = _compiler.SymbolTables.FindVariableDefinition(fileUri, word[1..]);
                                }
                                else
                                {
                                    defs = _compiler.SymbolTables.FindLabelDefinition(fileUri, word);
                                }
                                if (defs.Count > 0)
                                {
                                    // Convert compiler's 1-based line numbers back to LSP's 0-based
                                    Console.WriteLine(JsonSerializer.Serialize(new ReturnResult
                                    {
                                        Type = "DefinitionResult",
                                        Id = id,  // Include the request ID in the response
                                        Positions = [.. defs.ConvertAll(def => new ReturnPosition
                                            {
                                                FilePath = def.SourceID,
                                                StartLine = Math.Max(0, def.Line - 1),  // Convert 1-based to 0-based
                                                StartColumn = Math.Max(0, def.Column - 1),  // Convert 1-based to 0-based
                                                EndLine = Math.Max(0, def.Line - 1),  // Convert 1-based to 0-based
                                                EndColumn = Math.Max(0, def.Column - 1),  // Convert 1-based to 0-based
                                            })]
                                    }));
                                    break;
                                }
                            }
                            Console.WriteLine(JsonSerializer.Serialize(new ReturnResult
                            {
                                Type = "DefinitionResult",
                                Id = id,  // Include the request ID even when no definition found
                            }));
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Error = $"[DS C#] Unexpected error: {ex.Message}"
                }));
            }
        }
    }

    static bool GetWord(string filepath, int line, int col, out string word)
    {
        word = string.Empty;
        var uri = PathToUri(filepath);
        if (!_cache.TryGetValue(uri, out var text))
        {
            return false;
        }

        var lines = text.Split('\n');

        // line and col parameters are 1-based (from compiler), convert to 0-based array indices
        var lineIndex = line - 1;
        var colIndex = col - 1;

        if (lineIndex < 0 || lineIndex >= lines.Length)
        {
            return false;
        }

        var lineText = lines[lineIndex].TrimEnd('\r');  // Handle CRLF
        if (colIndex < 0 || colIndex >= lineText.Length)
        {
            return false;
        }

        // Find word boundaries by scanning left and right from cursor position
        bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        if (!IsWordChar(lineText[colIndex]))
        {
            return false;  // Not on a word character
        }

        int start = colIndex;
        int end = colIndex;

        // Scan left to find word start
        while (start > 0 && IsWordChar(lineText[start - 1]))
            start--;

        // Scan right to find word end
        while (end < lineText.Length && IsWordChar(lineText[end]))
            end++;

        word = lineText.Substring(start, end - start);
        return true;
    }
}

public class ReturnResult
{
    public required string Type { get; set; }
    public string? Id { get; set; }  // Add Id field to match requests with responses
    public DiagnosticResponse[]? Diagnostics { get; set; }
    public ReturnPosition[]? Positions { get; set; }
}

public class DiagnosticResponse
{
    public required string Message { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public TextSpanResponse? Span { get; set; }
    public int Severity { get; set; }
}

public class TextSpanResponse
{
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}

public class ReturnPosition
{
    public required string FilePath { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}