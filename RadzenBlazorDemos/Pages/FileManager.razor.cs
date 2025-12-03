using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RadzenBlazorDemos.Pages
{
    public partial class FileManager : ComponentBase
    {
        // Example: 100 MB Limit for Zipping operations
        const long MaxZipSizeBytes = 100 * 1024 * 1024;

        [Parameter] public string RootPath { get; set; } = @"C:\Sites";

        bool IsLoading = false;

        IEnumerable<string> entries;
        IEnumerable<object> checkedValues;

        // Computed property for the Status Bar text
        string SelectionSummary { get; set; } = "0 items selected";

        IEnumerable<object> CheckedValues
        {
            get => checkedValues;
            set
            {
                checkedValues = value;
                UpdateSelectionSummary();
            }
        }

        // Recalculate size when selection changes
        void UpdateSelectionSummary()
        {
            // Get the "Master" items (Roots) so we don't double count size
            var uniqueRoots = GetUniqueRootPaths(CheckedValues);

            if (!uniqueRoots.Any())
            {
                SelectionSummary = "0 items selected";
            }
            else
            {
                long totalBytes = 0;
                int totalFiles = 0;
                int totalFolders = 0;

                foreach (var path in uniqueRoots)
                {
                    // Calculate Size
                    totalBytes += FileManagerService.GetPathSize(path);

                    // Calculate Counts accurately
                    if (Directory.Exists(path))
                    {
                        totalFolders++;
                        // If it's a folder, we count the files INSIDE it for the UI
                        totalFiles += CountFilesRecursive(path);
                    }
                    else
                    {
                        totalFiles++;
                    }
                }

                var sizeStr = FileManagerService.FormatSize(totalBytes);

                // Display: "5 files, 1 folder (10.5 MB)"
                // This fixes the confusion when a folder is auto-selected
                var folderText = totalFolders > 0 ? $", {totalFolders} folder{(totalFolders > 1 ? "s" : "")}" : "";
                SelectionSummary = $"{totalFiles} file{(totalFiles != 1 ? "s" : "")}{folderText} selected ({sizeStr})";
            }
            StateHasChanged();
        }

        // Helper to count files for the UI stats
        int CountFilesRecursive(string path)
        {
            try
            {
                // Simple recursive count
                return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Length;
            }
            catch
            {
                return 0;
            }
        }

        protected override void OnInitialized()
        {
            if (Directory.Exists(RootPath)) entries = FileManagerService.GetEntries(RootPath);

            ZipService.OnJobUpdated += HandleJobUpdate;
        }

        void LoadFiles(TreeExpandEventArgs args)
        {
            var directory = args.Value as string;
            try
            {
                args.Children.Data = FileManagerService.GetEntries(directory);
                args.Children.Text = GetTextForNode;
                args.Children.HasChildren = (path) => Directory.Exists((string)path);
                args.Children.Template = FileOrFolderTemplate;
            }
            catch (UnauthorizedAccessException) { args.Children.Data = new List<string>(); }
        }

        string GetTextForNode(object data) => Path.GetFileName((string)data);

        public RenderFragment<RadzenTreeItem> FileOrFolderTemplate => item => builder =>
        {
            string path = item.Value as string;
            bool isDirectory = Directory.Exists(path);

            // Main Container
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "style", "display: inline-flex; align-items: center; width: 100%; cursor: pointer;"); // Added cursor pointer
                                                                                                                          // This calls the new method you exposed in your PR
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async () =>
            {
                await item.ToggleSelection();
            }));

            // We usually want to stop propagation so clicking the text doesn't 
            // trigger other tree events unnecessarily, though this is optional depending on your needs.
            builder.AddEventStopPropagationAttribute(3, "onclick", true);

            // RIGHT CLICK (Context Menu)
            builder.AddAttribute(3, "oncontextmenu", EventCallback.Factory.Create<MouseEventArgs>(this,
                (args) => OnShowContextMenu(args, path, isDirectory)));
            builder.AddEventPreventDefaultAttribute(4, "oncontextmenu", true);

            builder.AddEventStopPropagationAttribute(5, "oncontextmenu", true);

            // Icon
            builder.OpenComponent<RadzenIcon>(6);
            builder.AddAttribute(5, nameof(RadzenIcon.Icon), GetIconForFile(path)); 
            builder.AddAttribute(6, nameof(RadzenIcon.Style), $"margin-right: 5px; color: {GetIconColor(path)}"); 
            builder.CloseComponent();

            // Text
            builder.AddContent(9, item.Text);
            builder.CloseElement();
        };

        // --- MENU LOGIC ---

        async Task OnMenuItemClick(MenuItemEventArgs args)
        {
            int value = int.Parse(args.Value.ToString());

            if (value == 1) await DownloadSelected();
            else if (value == 2) ShowProperties(); // Show Props for Checkbox selection
            else if (value == 3)
            {
                entries = FileManagerService.GetEntries(RootPath);
                CheckedValues = null;
            }
        }
        string GetIconForFile(string path)
        {
            if (Directory.Exists(path)) return "folder";

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "picture_as_pdf",
                ".jpg" or ".jpeg" or ".png" or ".gif" => "image",
                ".mp4" or ".mov" or ".avi" => "movie",
                ".mp3" or ".wav" => "audiotrack",
                ".zip" or ".rar" or ".7z" => "folder_zip",
                ".xls" or ".xlsx" or ".csv" => "table_view",
                ".doc" or ".docx" => "description",
                ".txt" or ".log" => "article",
                _ => "insert_drive_file" // Default
            };
        }

        string GetIconColor(string path)
        {
            if (Directory.Exists(path)) return "#ffca28"; // Classic Folder Yellow

            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".pdf" => "#d32f2f", // Red
                ".xls" or ".xlsx" or ".csv" => "#2e7d32", // Green
                ".doc" or ".docx" => "#1976d2", // Blue
                ".zip" or ".rar" => "#f57c00", // Orange
                ".jpg" or ".png" => "#7b1fa2", // Purple
                _ => "inherit"
            };
        }

        void OnShowContextMenu(MouseEventArgs args, string path, bool isDirectory)
        {
            ContextMenuService.Open(args,
                new List<ContextMenuItem>
                {
                new ContextMenuItem { Text = "Download", Value = "DL", Icon = "get_app" },
                new ContextMenuItem { Text = "Properties", Value = "PROP", Icon = "info" },
                },
                (e) => ContextMenuCallback(e, path)
            );
        }

        // --- PROPERTIES LOGIC ---

        // 1. Show Properties for the CHECKBOX selection
        void ShowProperties()
        {
            if (checkedValues == null || !checkedValues.Any()) return;

            var paths = checkedValues.Cast<string>().ToList();

            if (paths.Count == 1) ShowPropertiesForSinglePath(paths.First());
            else
            {
                // Summary for multiple files
                long totalSize = paths.Sum(p => FileManagerService.GetPathSize(p));
                ShowPropertiesDialog("Multiple Selection",
                    $"Selected: {paths.Count} items",
                    "Various locations",
                    FileManagerService.FormatSize(totalSize),
                    "-", "-"
                );
            }
        }

        // 2. Show Properties for a SINGLE item (Right click or single select)
        void ShowPropertiesForSinglePath(string path)
        {
            var isDir = Directory.Exists(path);
            var info = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new System.IO.FileInfo(path);

            long size = FileManagerService.GetPathSize(path);

            ShowPropertiesDialog(
                isDir ? "Folder Properties" : "File Properties",
                info.Name,
                Path.GetDirectoryName(path),
                FileManagerService.FormatSize(size),
                info.CreationTime.ToString("g"),
                info.LastWriteTime.ToString("g")
            );
        }

        async Task DownloadSelected()
        {
            if (CheckedValues == null || !CheckedValues.Any()) return;

            // Pass the selected list to the smart engine
            await ProcessSmartDownload(CheckedValues.Cast<string>().ToList());
        }

        // Update signature to accept optional name and delete flag
        async Task DownloadSingleFile(string path, string downloadName = null, bool deleteAfter = false)
        {
            // Pass these new parameters to the service
            var token = FileManagerService.GenerateToken(path, downloadName, deleteAfter);

            var url = $"/api/download/{token}";
            await JS.InvokeVoidAsync("Radzen.triggerFileDownload", url);
        }

        // Fixed StartBackgroundZip using NotificationService
        void StartBackgroundZip(List<string> paths)
        {
            var id = ZipService.QueueJob(paths, RootPath);
            var job = ZipService.GetJob(id);
            activeJobs.Add(job);

            // FIX: Use NotificationService, not DialogService
            NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Info,
                Summary = "Zipping Started",
                Detail = "Check the task panel for progress.",
                Duration = 3000
            });
        }

        // Fixed DownloadFinishedJob passing the specific parameters
        async Task DownloadFinishedJob(ZipJob job)
        {
            // We pass the temp path, but we want the user to see "MyArchive.zip", 
            // and we want to delete the temp file after they get it.
            await DownloadSingleFile(job.TempFilePath, job.OutputFileName, deleteAfter: true);

            activeJobs.Remove(job);
        }

        async Task DownloadAsZip(List<string> paths)
        {
            // 1. Generate a token that holds the LIST of files
            var token = FileManagerService.GenerateZipToken(paths, RootPath);

            // 2. Trigger download. The server will stream the zip creation directly to the response.
            var url = $"/api/download/{token}";
            await JS.InvokeVoidAsync("Radzen.triggerFileDownload", url);
        }

        // Update Context Menu callback to use the new method
        async void ContextMenuCallback(MenuItemEventArgs args, string path)
        {
            ContextMenuService.Close();

            if (args.Value.ToString() == "DL")
            {
                // Wrap the single path in a list and send to Smart Process.
                // This ensures right-clicking a 5GB folder triggers the Background check!
                await ProcessSmartDownload(new List<string> { path });
            }
            else if (args.Value.ToString() == "PROP")
            {
                ShowPropertiesForSinglePath(path);
            }
        }

        private List<string> GetUniqueRootPaths(IEnumerable<object> rawSelection)
        {
            if (rawSelection == null || !rawSelection.Any()) return new List<string>();

            // 1. Cast to string and remove exact duplicates (just in case)
            var paths = rawSelection.Cast<string>().Distinct().ToList();

            // 2. Sort by length (Shortest paths are likely the parents)
            // Example: "C:\Data" comes before "C:\Data\File.txt"
            paths.Sort((a, b) => a.Length.CompareTo(b.Length));

            var uniqueRoots = new List<string>();

            foreach (var path in paths)
            {
                // 3. Check if this path is a child of any path we have already added
                // We check if it StartsWith a root AND ensures there is a separator 
                // (to prevent C:\Folder2 matching C:\Folder)
                bool isChild = uniqueRoots.Any(root =>
                    path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(root, StringComparison.OrdinalIgnoreCase));

                if (!isChild)
                {
                    uniqueRoots.Add(path);
                }
            }

            return uniqueRoots;
        }

        #region Large Zip Logic
        List<ZipJob> activeJobs = new();

        public void Dispose()
        {
            // Clean up subscription to prevent memory leaks
            ZipService.OnJobUpdated -= HandleJobUpdate;
        }

        void HandleJobUpdate(Guid jobId)
        {
            // Since this event comes from a background thread, we must InvokeAsync
            InvokeAsync(() =>
            {
                // Refresh our local view of jobs
                StateHasChanged();
            });
        }
        void CancelBackgroundJob(ZipJob job)
        {
            // Notify the service
            ZipService.CancelJob(job.Id);

            // Optimistically update UI so it feels instant
            job.Status = ZipJobStatus.Cancelled;
        }

        void RemoveJobFromList(ZipJob job)
        {
            activeJobs.Remove(job);
        }

        #endregion
        async Task ProcessSmartDownload(List<string> paths)
        {
            // 1. Separate Files and Directories
            var selectedFiles = paths.Where(File.Exists).ToList();
            var selectedDirs = paths.Where(Directory.Exists).ToList();

            // CASE A: Single File (Direct Download) - No Spinner needed
            if (selectedFiles.Count == 1 && selectedDirs.Count == 0)
            {
                await DownloadSingleFile(selectedFiles[0]);
                return;
            }

            // CASE B: Zip Logic
            try
            {
                IsLoading = true;
                StateHasChanged(); // Force UI to show spinner
                await Task.Yield();

                var distinctRoots = GetUniqueRootPaths(paths);

                // 2. Calculate Size (Run in background)
                long totalSize = await Task.Run(() =>
                {
                    long size = 0;
                    foreach (var p in distinctRoots)
                    {
                        size += FileManagerService.GetPathSize(p);
                    }
                    return size;
                });

                // 3. Check Limit
                if(totalSize > MaxZipSizeBytes)
                {
                    IsLoading = false;
                    StateHasChanged();

                    var readableSize = FileManagerService.FormatSize(totalSize);
                    var readableLimit = FileManagerService.FormatSize(MaxZipSizeBytes);

                    // CLEANER: Open the specific component and pass parameters via Dictionary
                    var result = await DialogService.OpenAsync<ConfirmLargeDownloadDialog>("Large Download Detected",
                        new Dictionary<string, object>()
                        {
                { "ReadableSize", readableSize },
                { "ReadableLimit", readableLimit }
                        },
                        new DialogOptions() { Width = "400px" }); // Optional: Set a nice width

                    if (result == true)
                    {
                        StartBackgroundZip(distinctRoots);
                    }

                    return;
                }

                // 4. Immediate Zip
                await DownloadAsZip(distinctRoots);
            }
            catch (Exception ex)
            {
                // Optional: Notify user if something actually crashed
                NotificationService.Notify(NotificationSeverity.Error, "Download Failed", ex.Message);
            }
            finally
            {
                // 5. GUARANTEE SPINNER REMOVAL
                IsLoading = false;
                StateHasChanged(); // Force UI to hide spinner
            }
        }
    }
}

