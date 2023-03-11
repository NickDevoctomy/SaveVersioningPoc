using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SaveVersioningPoc;
using SaveVersioningPoc.Model;
using System.Diagnostics;

var configJson = await File.ReadAllTextAsync("Config/config.json");
var config = JObject.Parse(configJson);

if(config == null)
{
    Console.WriteLine("Error reading configuration");
    Console.ReadKey();
    return;
}

var versionFoldersArray = config?["VersionFolders"]?.Value<JArray>();
if(versionFoldersArray == null || versionFoldersArray.Count == 0)
{
    Console.WriteLine("No version folders configured.");
    Console.ReadKey();
    return;
}

var watchers = new Dictionary<FileSystemWatcher, VersionFolder>();
var pendingBackups = new List<BackupOperation>();
var versionFolders = JsonConvert.DeserializeObject<List<VersionFolder>>(versionFoldersArray.ToString());
foreach(var curFolder in versionFolders)
{
    var backupRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var backupRootDI = new DirectoryInfo(backupRoot);
    var appDirDI = backupRootDI.CreateSubdirectory("SaveVersioningPoc");
    var versionFolderDir = appDirDI.CreateSubdirectory(curFolder.Name);

    var fileSystemWatcher = new FileSystemWatcher(
        curFolder.Path,
        curFolder.Filter)
    {
        IncludeSubdirectories = curFolder.IncludeSubdirectories,
        NotifyFilter = NotifyFilters.Attributes |
        NotifyFilters.CreationTime |
        NotifyFilters.FileName |
        NotifyFilters.LastAccess |
        NotifyFilters.LastWrite |
        NotifyFilters.Size |
        NotifyFilters.Security
    };
    fileSystemWatcher.Created += FileSystemWatcher_Created;
    fileSystemWatcher.Changed += FileSystemWatcher_Changed;
    watchers.Add(fileSystemWatcher, curFolder);
}

watchers.Keys.ToList().ForEach(fsw =>
{
    var versionFolder = watchers[fsw];
    Console.WriteLine($"Starting file system watcher for version folder '{versionFolder.Name}'");
    fsw.EnableRaisingEvents = true;
});

Console.WriteLine("Running, press any key to quit.");
Console.ReadKey();

void FileSystemWatcher_Created(
    object sender,
    FileSystemEventArgs e)
{
    var versionFolder = watchers[(FileSystemWatcher)sender];
    var relativePath = e.FullPath.Replace(versionFolder.Path, string.Empty);
    Console.WriteLine($"File '{relativePath}' created in version folder '{versionFolder.Name}'");
    QueueBackup(versionFolder, e.FullPath);
}

// This can fire multiple times, for seemingly single events, and is a known 'feature'
void FileSystemWatcher_Changed(
    object sender,
    FileSystemEventArgs e)
{
    var versionFolder = watchers[(FileSystemWatcher)sender];
    var relativePath = e.FullPath.Replace(versionFolder.Path, string.Empty);
    Console.WriteLine($"File '{relativePath}' changed in version folder '{versionFolder.Name}'");
    QueueBackup(versionFolder, e.FullPath);
}

void QueueBackup(
    VersionFolder versionFolder,
    string path)
{
    lock(pendingBackups)
    {
        var existing = pendingBackups.SingleOrDefault(x =>
        x.VersionFolder == versionFolder &&
        x.Path == path);
        if(existing != null)
        {
            existing.Changed();
            return;
        }

        var backupOperation = new BackupOperation(
            versionFolder,
            path);
        pendingBackups.Add(backupOperation);
        backupOperation.Ready += BackupOperation_Ready;
        backupOperation.Changed();
    }
}

void BackupOperation_Ready(object? sender, EventArgs e)
{
    lock (pendingBackups)
    {
        var backupOperation = sender as BackupOperation;
        var backedUp = PerformBackup(
            backupOperation.VersionFolder,
            backupOperation.Path);
        if(!backedUp)
        {
            backupOperation.Changed();
            return;
        }
        
        pendingBackups.Remove(backupOperation);
    }
}

bool PerformBackup(
    VersionFolder versionFolder,
    string path)
{
    var block = versionFolder.BackupBlockingProcesses.Any(x => Process.GetProcessesByName(x).Length > 0);
    if(block)
    {
        return false;
    }

    var backupPath = GetVersionFolderBackupPath(versionFolder);
    var relativePath = path.Replace(versionFolder.Path, string.Empty);
    var cleanPath = relativePath.Replace('\\', '/').TrimStart('/');
    var fileName = cleanPath[(cleanPath.LastIndexOf('/') + 1)..];
    var pathNoFile = cleanPath[..cleanPath.LastIndexOf('/')];
    var backupFullPath = $"{backupPath}/{cleanPath}_{DateTime.Now.ToString("ddMMyyyy-HHmmss")}.bak";

    backupPath.CreateSubdirectory(pathNoFile);
    File.Copy(
        path,
        backupFullPath);

    Console.WriteLine($"Creating versioned copy of file '{relativePath}' for version folder '{versionFolder.Name}'");
    return true;
}

DirectoryInfo GetVersionFolderBackupPath(VersionFolder versionFolder)
{
    var backupRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var backupRootDI = new DirectoryInfo(backupRoot);
    var appDirDI = backupRootDI.CreateSubdirectory("SaveVersioningPoc");
    var versionFolderDir = appDirDI.CreateSubdirectory(versionFolder.Name);
    return versionFolderDir;
}
