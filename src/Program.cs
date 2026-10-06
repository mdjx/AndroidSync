using MediaDevices;
using System.Text.Json;
using Spectre.Console;

#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
#endif

#if NET5_0_OR_GREATER
[SupportedOSPlatform("windows10.0.18362")]
#endif
class Program
{
    static void Main()
    {

        AnsiConsole.Write(
            new Panel("[bold underline white]AndroidSync[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.White))
                .Padding(10, 1)
        );

        var devices = MediaDevice.GetDevices()
            .Where(d => d.DeviceId.Contains("android", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No Android devices found. Please connect a phone and try again.[/]");
            AnsiConsole.MarkupLine("[yellow]Press Enter to exit[/]");
            Console.ReadLine();
            return;
        }

        var deviceChoices = devices.Select((dev, i) => $"{i + 1}. [bold]{dev.FriendlyName}[/] - {dev.Description} ([blue]{dev.Manufacturer.Trim().ToUpperInvariant()}[/])").ToArray();
        var selectedString = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select a device to sync:[/]")
                .PageSize(10)
                .AddChoices(deviceChoices)
        );
        int selectedIndex = int.Parse(selectedString.Split('.')[0]);
        var selectedDevice = devices[selectedIndex - 1];
        AnsiConsole.MarkupLine($"\n[bold green]Selected device:[/] {selectedDevice.FriendlyName}\n");

        // Load or create cache
        string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string cacheFile = Path.Combine(documentsPath, "AndroidSyncConfig.json");
        Dictionary<string, string> devicePaths = new();
        if (File.Exists(cacheFile))
        {
            try
            {
                devicePaths = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(cacheFile)) ?? new();
            }
            catch { devicePaths = new(); }
        }

        string deviceKey = selectedDevice.DeviceId;
        string cachedPath = devicePaths.ContainsKey(deviceKey) ? devicePaths[deviceKey] : string.Empty;
        string basePath = string.Empty;
        if (!string.IsNullOrEmpty(cachedPath))
        {
            AnsiConsole.MarkupLine($"[yellow]Cached sync path for this device:[/] [bold]{cachedPath}[/]");
            basePath = AnsiConsole.Prompt(
                new TextPrompt<string>("Press Enter to use this path, or type a new path:")
                    .DefaultValue(cachedPath)
                    .AllowEmpty()
            ).Trim();
            if (string.IsNullOrWhiteSpace(basePath)) basePath = cachedPath;
        }
        else
        {
            basePath = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter the destination path to sync to:")
                    .Validate(path => !string.IsNullOrWhiteSpace(path) ? ValidationResult.Success() : ValidationResult.Error("Path cannot be empty."))
            ).Trim();
        }
        // Save path to cache
        devicePaths[deviceKey] = basePath;
        File.WriteAllText(cacheFile, JsonSerializer.Serialize(devicePaths));

        selectedDevice.Connect();

        string[] syncRoots = { "DCIM", "Pictures", "Documents", "Download", "Movies", "Recordings" };
        var TopFolderDirectories = new List<(MediaDirectoryInfo Dir, string RelativePath)>();
        foreach (string root in syncRoots)
        {
            string rootPath = $@"\Internal storage\{root}";
            if (!selectedDevice.DirectoryExists(rootPath))
            {
                AnsiConsole.MarkupLine($"[grey]Not found on device, skipping:[/] {root}");
                continue;
            }
            CollectFolders(selectedDevice.GetDirectoryInfo(rootPath), root, TopFolderDirectories);
        }

        // AnsiConsole.MarkupLine($"[grey]Syncing folders:[/] [bold]{string.Join(", ", TopFolderDirectories)}[/]");

        int failedCount = 0;
        AnsiConsole.Progress()
            .Start(ctx =>
            {
                var folderTasks = new Dictionary<string, ProgressTask>();
                foreach (var (dir, relativePath) in TopFolderDirectories)
                {
                    string destFolder = Path.Combine(basePath, relativePath);
                    Directory.CreateDirectory(destFolder);
                    string taskName = Markup.Escape($@"Syncing Internal storage\{relativePath}");
                    // Use MediaFileInfo rather than path strings: the path-based MediaDevices APIs reject
                    // any phone path containing '|' or control characters, which Android allows in filenames
                    MediaFileInfo[] files = dir.EnumerateFiles().ToArray();
                    if (files.Length == 0)
                    {
                        var t = ctx.AddTask(taskName, maxValue: 1);
                        t.Increment(1);
                        continue;
                    }
                    var task = ctx.AddTask(taskName, maxValue: files.Length);
                    folderTasks[relativePath] = task;
                    foreach (var file in files)
                    {
                        string newPath = Path.Combine(destFolder, ToWindowsName(file.Name));
                        if (!File.Exists(newPath))
                        {
                            try
                            {
                                using var source = file.OpenRead();
                                using var destination = File.Create(newPath);
                                source.CopyTo(destination);
                            }
                            catch (Exception ex)
                            {
                                failedCount++;
                                AnsiConsole.MarkupLine($"[red]Failed to sync: {Markup.Escape($@"Internal storage\{relativePath}\{file.Name}")}[/]");
                                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.ToString())}[/]");
                                // Remove any partially written file so it's retried on the next sync rather than skipped
                                try { File.Delete(newPath); } catch { }
                            }
                        }
                        task.Increment(1);
                    }
                }
            });

        selectedDevice.Disconnect();
        if (failedCount > 0)
            AnsiConsole.MarkupLine($"[bold yellow]Sync complete, but {failedCount} file(s) failed - see errors above.[/]");
        else
            AnsiConsole.MarkupLine("[bold green]Sync complete![/]");
        AnsiConsole.MarkupLine("[yellow]Press Enter to exit[/]");
        Console.ReadLine();
    }

    // Adds dir and all of its non-hidden subfolders (recursively) to result, with their paths relative to Internal storage
    static void CollectFolders(MediaDirectoryInfo dir, string relativePath, List<(MediaDirectoryInfo, string)> result)
    {
        result.Add((dir, relativePath));
        foreach (var subDir in dir.EnumerateDirectories())
        {
            if (subDir.Name.StartsWith('.')) { AnsiConsole.MarkupLine($"[yellow]Skipping hidden folder:[/] {Markup.Escape(subDir.Name)}"); continue; }
            CollectFolders(subDir, Path.Combine(relativePath, ToWindowsName(subDir.Name)), result);
        }
    }

    // Android allows characters in names that Windows doesn't (e.g. : ? * | "), so replace them
    static string ToWindowsName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
