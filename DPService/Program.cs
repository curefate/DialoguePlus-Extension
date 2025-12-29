using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DialoguePlus.Compilation;

class Program
{
    private static readonly CacheContentProvider _cache = new();
    private static readonly Compiler compiler = new(new ContentResolver().Register(_cache).Register(new FileContentProvider()));

    static void Main(string[] args)
    {
        Console.Error.WriteLine("[DS C#] C# process started");
        string input;
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
                            if (filePath != null) _cache.AddOrUpdate(new Uri(filePath), content ?? string.Empty);
                            // Console.Error.WriteLine($"[DS C#] Opened file: {filePath}");
                            break;
                        }
                    case "update":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            var changes = root.GetProperty("changes");
                            if (filePath != null)
                            {
                                if (!_cache.TryGetValue(new Uri(filePath), out var text))
                                    text = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;

                                // TODO range patch
                                text = changes.GetString() ?? text;
                                _cache.AddOrUpdate(new Uri(filePath), text);
                            }
                            // Console.Error.WriteLine($"[DS C#] Updated file: {filePath}");
                            break;
                        }
                    case "closeFile":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            if (filePath != null) _cache.Remove(new Uri(filePath));
                            // Console.Error.WriteLine($"[DS C#] Closed file: {filePath}");
                            break;
                        }
                    case "analyze":
                        {
                            var id = root.GetProperty("id").GetString();
                            var filePath = root.GetProperty("filePath").GetString();

                            if (filePath == null) break;

                            Console.Error.WriteLine($"[DS C#] Analyzing file: {filePath}.");

                            var result = 
                            Console.WriteLine(JsonSerializer.Serialize(result));
                            break;
                        }
                    case "definition":
                        {
                            var filePath = root.GetProperty("filePath").GetString();
                            var pos = root.GetProperty("position");
                            var line = pos.GetProperty("line").GetInt32();
                            var col = pos.GetProperty("character").GetInt32();

                            if (filePath == null) break;
                            //
                            Console.Error.WriteLine($"[DS C#] Finding definition: ln{line} col{col}");
                            if (def.HasValue)
                            {
                                var position = new Position()
                                {
                                    FilePath = def.Value.file,
                                    StartLine = def.Value.line,
                                    StartColumn = def.Value.col,
                                    EndLine = def.Value.line,
                                    EndColumn = def.Value.col + def.Value.length,
                                };
                                Console.WriteLine(JsonSerializer.Serialize(new Result
                                {
                                    Type = "DefinitionResult",
                                    Positions = [position]
                                }));
                            }
                            else
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new Result
                                {
                                    Type = "DefinitionResult",
                                }));
                            }
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
}

public class Position
{
    public string FilePath { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}