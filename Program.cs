using MediaDevices;
using System.Runtime.Versioning;
using System.Text.Json;
using Spectre.Console;

[SupportedOSPlatform("windows10.0.18362")]
class Program
{
    static void Main()
    {
        var devices = MediaDevice.GetDevices()
            .Where(d => d.DeviceId.Contains("android", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No Android devices found. Please connect a phone and try again.[/]");
            return;
        }

        AnsiConsole.Write(
            new Panel("[bold underline white]AndroidSync[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.White))
                .Padding(10, 1)
        );

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

        string[] Dcim = selectedDevice.GetDirectories(@"\Internal storage\DCIM");
        string[] Pictures = selectedDevice.GetDirectories(@"\Internal storage\Pictures");
        string[] Documents = selectedDevice.GetDirectories(@"\Internal storage\Documents");
        string[] Download = selectedDevice.GetDirectories(@"\Internal storage\Download");
        string[] Movies = selectedDevice.GetDirectories(@"\Internal storage\Movies");
        string[] Recordings = selectedDevice.GetDirectories(@"\Internal storage\Recordings");

        string[][] arrays = { Dcim, Pictures, Documents, Download, Movies, Recordings };
        string[] TopFolderDirectories = arrays.SelectMany(a => a).ToArray();

        // AnsiConsole.MarkupLine($"[grey]Syncing folders:[/] [bold]{string.Join(", ", TopFolderDirectories)}[/]");

        AnsiConsole.Progress()
            .Start(ctx =>
            {
                var folderTasks = new Dictionary<string, ProgressTask>();
                foreach (string dir in TopFolderDirectories)
                {
                    string folderName = dir.Split(@"\")[^1];
                    if (folderName.StartsWith('.')) { AnsiConsole.MarkupLine($"[yellow]Skipping hidden folder:[/] {folderName}"); continue; }
                    var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    int idx = Array.IndexOf(parts, "Internal storage");
                    string relativePath = Path.Combine(parts[(idx + 1)..]);
                    string destFolder = Path.Combine(basePath, relativePath);
                    Directory.CreateDirectory(destFolder);
                    string[] files = selectedDevice.GetFiles(dir);
                    if (files.Length == 0)
                    {
                        var t = ctx.AddTask($"Syncing {dir.Trim().TrimStart('\\')}", maxValue: 1);
                        t.Increment(1);
                        continue;
                    }
                    Directory.CreateDirectory(destFolder);
                    var task = ctx.AddTask($"Syncing {dir.Trim().TrimStart('\\')}", maxValue: files.Length);
                    folderTasks[dir] = task;
                    for (int i = 0; i < files.Length; i++)
                    {
                        var file = files[i];
                        var fileName = file.Split(@"\")[^1];
                        string newPath = Path.Combine(destFolder, fileName);
                        if (!File.Exists(newPath))
                        {
                            selectedDevice.DownloadFile(file, newPath);
                        }
                        task.Increment(1);
                    }
                }
            });

        selectedDevice.Disconnect();
        AnsiConsole.MarkupLine("[bold green]Sync complete![/]");
        AnsiConsole.MarkupLine("[yellow]Press Enter to exit[/]");
        Console.ReadLine();
    }
}
