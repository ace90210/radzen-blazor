using System;
using System.Collections.Generic;
using System.IO;

namespace Radzen.Blazor
{
    using System.Collections.Concurrent;
    using System.IO.Compression;
    using System.Linq;
    using System.Threading.Tasks;

    public class FileManagerService
    {
        // UPDATED TUPLE: Added 'DownloadName' and 'DeleteAfter'
        private static readonly ConcurrentDictionary<string, (string Path, bool IsZip, IEnumerable<string> SourcePaths, string DownloadName, bool DeleteAfter)> _tokens = new();

        public IEnumerable<string> GetEntries(string path)
        {
            try
            {
                var all = Directory.EnumerateFileSystemEntries(path).ToList();

                // Sort: Directories first, then Files. Both alphabetical.
                return all.OrderByDescending(x => Directory.Exists(x)) // true (folder) comes before false (file)
                          .ThenBy(x => x);
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        // Creates a ZIP file in memory containing the selected files/folders
        public byte[] CreateZipFromPaths(IEnumerable<string> paths)
        {
            using var memoryStream = new MemoryStream();

            // leaveOpen: true is required to reset position before returning
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                var addedPaths = new HashSet<string>();

                foreach (var path in paths)
                {
                    if (File.Exists(path))
                    {
                        AddFileToZip(archive, path, Path.GetDirectoryName(path), addedPaths);
                    }
                    else if (Directory.Exists(path))
                    {
                        // For a selected folder, we want it to appear as a folder inside the zip
                        // So we treat the parent directory as the "root" for the relative path
                        var parentDir = Directory.GetParent(path)?.FullName ?? path;
                        AddDirectoryToZip(archive, path, parentDir, addedPaths);
                    }
                }
            }

            memoryStream.Position = 0;
            return memoryStream.ToArray();
        }

        public long GetPathSize(string path)
        {
            if (File.Exists(path))
            {
                return new System.IO.FileInfo(path).Length;
            }
            else if (Directory.Exists(path))
            {
                // Calculate folder size recursively
                long size = 0;
                var di = new DirectoryInfo(path);

                try
                {
                    size += di.GetFiles().Sum(f => f.Length);
                    size += di.GetDirectories().Sum(d => GetPathSize(d.FullName));
                }
                catch { /* Ignore permission errors */ }

                return size;
            }
            return 0;
        }

        public string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        // Updated GenerateToken to support renaming and cleanup
        public string GenerateToken(string path, string downloadName = null, bool deleteAfter = false)
        {
            var token = Guid.NewGuid().ToString();
            // If downloadName is null, the API will calculate it from the path later
            _tokens[token] = (path, false, null, downloadName, deleteAfter);
            return token;
        }

        public string GenerateZipToken(IEnumerable<string> paths, string rootPath)
        {
            var token = Guid.NewGuid().ToString();
            // Zips are generated on the fly, so they don't need cleanup (DeleteAfter = false)
            // We set a default name like "Download.zip" here, or handle it in Program.cs
            _tokens[token] = (rootPath, true, paths, "Download.zip", false);
            return token;
        }

        public bool TryGetToken(string token, out (string Path, bool IsZip, IEnumerable<string> SourcePaths, string DownloadName, bool DeleteAfter) entry)
        {
            return _tokens.TryRemove(token, out entry);
        }

        private void AddDirectoryToZip(ZipArchive archive, string dirPath, string rootDir, HashSet<string> addedPaths)
        {
            var di = new DirectoryInfo(dirPath);

            // Add Files
            foreach (var file in di.GetFiles())
            {
                AddFileToZip(archive, file.FullName, rootDir, addedPaths);
            }

            // Add Subdirectories recursively
            foreach (var subDir in di.GetDirectories())
            {
                AddDirectoryToZip(archive, subDir.FullName, rootDir, addedPaths);
            }
        }

        private void AddFileToZip(ZipArchive archive, string filePath, string rootDir, HashSet<string> addedPaths)
        {
            if (addedPaths.Contains(filePath)) return;

            // Calculate relative path so the zip structure mirrors the folder structure
            var relativePath = Path.GetRelativePath(rootDir, filePath);

            archive.CreateEntryFromFile(filePath, relativePath);
            addedPaths.Add(filePath);
        }// ---------------------------------------------------------
         // NEW: Streaming Methods for the API Endpoint
         // ---------------------------------------------------------

        public async Task DownloadZipToStreamAsync(IEnumerable<string> paths, Stream outputStream)
        {
            // 'leaveOpen: true' is important so we don't accidentally close the HTTP response stream
            // when the ZipArchive is disposed. The ZipArchive disposal triggers the writing 
            // of the Zip footer, which is required for a valid zip file.
            using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
            var addedPaths = new HashSet<string>();

            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    await AddFileToZipAsync(archive, path, Path.GetDirectoryName(path), addedPaths);
                }
                else if (Directory.Exists(path))
                {
                    var parentDir = Directory.GetParent(path)?.FullName ?? path;
                    await AddDirectoryToZipAsync(archive, path, parentDir, addedPaths);
                }
            }
        }

        private async Task AddDirectoryToZipAsync(ZipArchive archive, string dirPath, string rootDir, HashSet<string> addedPaths)
        {
            var di = new DirectoryInfo(dirPath);

            // Recursively add sub-directories
            foreach (var subDir in di.GetDirectories())
            {
                await AddDirectoryToZipAsync(archive, subDir.FullName, rootDir, addedPaths);
            }

            // Add files in this directory
            foreach (var file in di.GetFiles())
            {
                await AddFileToZipAsync(archive, file.FullName, rootDir, addedPaths);
            }
        }

        private async Task AddFileToZipAsync(ZipArchive archive, string filePath, string rootDir, HashSet<string> addedPaths)
        {
            if (addedPaths.Contains(filePath)) return;

            // Calculate relative path to maintain folder structure inside the zip
            var relativePath = Path.GetRelativePath(rootDir, filePath);

            // Create the entry in the zip
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

            // Stream the file data directly into the zip entry
            // This uses CopyToAsync to ensure we don't block the server threads
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(filePath);
            await fileStream.CopyToAsync(entryStream);

            addedPaths.Add(filePath);
        }
    }
}
