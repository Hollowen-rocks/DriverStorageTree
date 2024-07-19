using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

class Program
{
    static void Main()
    {
#if WINDOWS
        if (!IsAdministrator())
        {
            WriteError("This application requires administrative privileges to run.");
            Console.WriteLine("Please run the application as an administrator.");
            return;
        }
#endif

        DisplayAvailableDrives();

        string driveLetter = GetDriveLetterFromUser();
        string drivePath = $"{driveLetter}:\\";

        if (!ValidateDrive(drivePath))
        {
            WriteError("Invalid drive or drive not ready.");
            return;
        }

        bool includeSystemAndHidden = GetIncludeSystemAndHiddenOption();
        int topCount = GetTopCountFromUser();

        var folderSizes = new ConcurrentDictionary<string, FolderInfo>();
        var folderCount = new AtomicInteger();
        var totalFolders = Directory.GetDirectories(drivePath, "*", SearchOption.AllDirectories).Length;

        WriteInfo("Scanning drive. This might take a while...");
        Task.Run(() => ScanDirectory(drivePath, folderSizes, includeSystemAndHidden, folderCount, totalFolders)).Wait();

        var topFolders = folderSizes.OrderByDescending(pair => pair.Value.TotalSize).Take(topCount);

        DisplayTopFolders(topFolders);

        if (GetExportOptionFromUser())
        {
            ExportToCsv(topFolders, "FolderSizes.csv");
            WriteSuccess("Results exported to FolderSizes.csv");
        }
    }

#if WINDOWS
    static bool IsAdministrator()
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent();
            var currentPrincipal = new WindowsPrincipal(currentUser);
            return currentPrincipal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    static bool HasAccess(string path)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(path);
            var security = directoryInfo.GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(NTAccount));

            var currentUser = WindowsIdentity.GetCurrent();
            var currentPrincipal = new WindowsPrincipal(currentUser);

            foreach (FileSystemAccessRule rule in rules)
            {
                if (currentUser.User?.Value == rule.IdentityReference.Value ||
                    currentPrincipal.IsInRole(rule.IdentityReference.Value))
                {
                    if ((rule.FileSystemRights & FileSystemRights.Read) != 0)
                    {
                        if (rule.AccessControlType == AccessControlType.Deny)
                        {
                            return false;
                        }
                        else if (rule.AccessControlType == AccessControlType.Allow)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
#endif

    static void DisplayAvailableDrives()
    {
        WriteInfo("Available Drives:");
        foreach (var drive in DriveInfo.GetDrives())
        {
            WriteInfo($"{drive.Name} - {drive.DriveType}");
        }
    }

    static string GetDriveLetterFromUser()
    {
        WritePrompt("Enter the drive letter you want to check (e.g., C): ");
        return Console.ReadLine() ?? string.Empty;
    }

    static bool ValidateDrive(string drivePath)
    {
        var driveInfo = new DriveInfo(drivePath);
        return driveInfo.IsReady;
    }

    static bool GetIncludeSystemAndHiddenOption()
    {
        WritePrompt("Include system and hidden files/folders? (yes/no): ");
        return string.Equals(Console.ReadLine(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    static int GetTopCountFromUser()
    {
        WritePrompt("Enter the number of top folders to display: ");
        if (!int.TryParse(Console.ReadLine(), out int topCount) || topCount <= 0)
        {
            topCount = 100; // Default to top 100
        }
        return topCount;
    }

    static bool GetExportOptionFromUser()
    {
        WritePrompt("Do you want to export the results to a CSV file? (yes/no): ");
        return string.Equals(Console.ReadLine(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    static void ScanDirectory(string directory, ConcurrentDictionary<string, FolderInfo> folderSizes, bool includeSystemAndHidden, AtomicInteger folderCount, int totalFolders)
    {
        try
        {
#if WINDOWS
            if (IsWindows() && !HasAccess(directory))
            {
                LogError($"Access denied to directory {directory}");
                return;
            }
#endif

            var folderInfo = new FolderInfo();

            foreach (var file in Directory.GetFiles(directory))
            {
                FileInfo fileInfo = new(file);
                if (includeSystemAndHidden || (!fileInfo.Attributes.HasFlag(FileAttributes.Hidden) && !fileInfo.Attributes.HasFlag(FileAttributes.System)))
                {
                    folderInfo.TotalSize += fileInfo.Length;
                    folderInfo.FileTypes[fileInfo.Extension] = folderInfo.FileTypes.GetValueOrDefault(fileInfo.Extension, 0) + 1;
                }
            }

            Parallel.ForEach(Directory.GetDirectories(directory), subDirectory =>
            {
                DirectoryInfo dirInfo = new(subDirectory);
                if (includeSystemAndHidden || (!dirInfo.Attributes.HasFlag(FileAttributes.Hidden) && !dirInfo.Attributes.HasFlag(FileAttributes.System)))
                {
                    ScanDirectory(subDirectory, folderSizes, includeSystemAndHidden, folderCount, totalFolders);
                }
            });

            folderSizes[directory] = folderInfo;
            folderCount.Increment();

            DisplayProgress(folderCount.Value, totalFolders);

            if (folderCount.Value % 100 == 0)
            {
                LogProgress($"{folderCount.Value} folders scanned...");
            }
        }
        catch (UnauthorizedAccessException)
        {
            LogError($"Access denied to directory {directory}");
        }
        catch (PathTooLongException)
        {
            LogError($"Path too long: {directory}");
        }
        catch (Exception ex)
        {
            LogError($"Error scanning directory {directory}: {ex.Message}");
        }
    }

    static void DisplayProgress(int current, int total)
    {
        double progress = (double)current / total;
        int progressBarWidth = 50;
        int progressWidth = (int)(progress * progressBarWidth);

        Console.Write("\r[");
        Console.Write(new string('#', progressWidth));
        Console.Write(new string('-', progressBarWidth - progressWidth));
        Console.Write($"] {progress:P1}");
    }

    static void DisplayTopFolders(IEnumerable<KeyValuePair<string, FolderInfo>> topFolders)
    {
        WriteInfo("Top folders by size:");
        foreach (var folder in topFolders)
        {
            WriteInfo($"{folder.Key} - {folder.Value.TotalSize / (1024.0 * 1024.0 * 1024.0):F2} GB");
            WriteInfo("File types breakdown:");
            foreach (var fileType in folder.Value.FileTypes)
            {
                WriteInfo($"  {fileType.Key}: {fileType.Value} files");
            }
        }
    }

    static void LogError(string message)
    {
        File.AppendAllText("ErrorLog.txt", $"{DateTime.Now}: {message}{Environment.NewLine}");
        WriteError(message);
    }

    static void LogProgress(string message)
    {
        File.AppendAllText("ProgressLog.txt", $"{DateTime.Now}: {message}{Environment.NewLine}");
        WriteInfo(message);
    }

    static void ExportToCsv(IEnumerable<KeyValuePair<string, FolderInfo>> topFolders, string fileName)
    {
        using var writer = new StreamWriter(fileName);
        writer.WriteLine("Folder,Size (GB),File Types Breakdown");
        foreach (var folder in topFolders)
        {
            var fileTypes = string.Join(" | ", folder.Value.FileTypes.Select(ft => $"{ft.Key}: {ft.Value}"));
            writer.WriteLine($"{folder.Key},{folder.Value.TotalSize / (1024.0 * 1024.0 * 1024.0):F2},{fileTypes}");
        }
    }

    static void WritePrompt(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(message);
        Console.ResetColor();
    }

    static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}

class FolderInfo
{
    public long TotalSize { get; set; }
    public Dictionary<string, int> FileTypes { get; set; } = new();
}

class AtomicInteger
{
    private int _value;

    public int Value => Interlocked.CompareExchange(ref _value, 0, 0);

    public void Increment() => Interlocked.Increment(ref _value);
}
