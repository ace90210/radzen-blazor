using Radzen.Blazor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace RadzenBlazorDemos.Services
{
    using System.Collections.Concurrent;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using System.Threading.Channels;

    public class ZipBackgroundService : BackgroundService
    {
        // Store jobs in memory so UI can query them
        private readonly ConcurrentDictionary<Guid, ZipJob> _jobs = new();

        // A queue to process zips one by one (or concurrent, depending on config)
        private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

        // Event to notify UI (Blazor Server specific)
        public event Action<Guid> OnJobUpdated;

        public Guid QueueJob(List<string> paths, string rootPath)
        {
            var job = new ZipJob
            {
                SourcePaths = paths,
                RootPath = rootPath,
                OutputFileName = $"Archive_{DateTime.Now:HHmm}.zip"
            };

            _jobs[job.Id] = job;
            _queue.Writer.TryWrite(job.Id); // Add to queue
            return job.Id;
        }

        public ZipJob GetJob(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (!_jobs.TryGetValue(jobId, out var job)) continue;

                // Check if cancelled before we even start
                if (job.Cts.IsCancellationRequested)
                {
                    UpdateStatus(job, ZipJobStatus.Cancelled, 0);
                    continue;
                }

                try
                {
                    UpdateStatus(job, ZipJobStatus.Processing, 0);
                    job.TempFilePath = Path.GetTempFileName();

                    long totalBytes = CalculateTotalSizeBytes(job.SourcePaths);
                    long bytesProcessed = 0;
                    int lastReportedPercent = 0;

                    using (var fileStream = new FileStream(job.TempFilePath, FileMode.Create))
                    using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
                    {
                        foreach (var sourcePath in job.SourcePaths)
                        {
                            // 2. PASS THE TOKEN (job.Cts.Token)
                            await AddToZipRecursiveBytesAsync(archive, sourcePath, job.RootPath, totalBytes, job.Cts.Token,
                                (bytesRead) =>
                                {
                                    bytesProcessed += bytesRead;
                                    int percent = (int)((double)bytesProcessed / totalBytes * 100);
                                    if (percent > lastReportedPercent)
                                    {
                                        lastReportedPercent = percent;
                                        UpdateStatus(job, ZipJobStatus.Processing, percent);
                                    }
                                });
                        }
                    }

                    UpdateStatus(job, ZipJobStatus.Completed, 100);
                }
                catch (OperationCanceledException)
                {
                    // 3. HANDLE CANCELLATION GRACEFULLY
                    UpdateStatus(job, ZipJobStatus.Cancelled, 0);

                    // Cleanup partial file
                    try { if (File.Exists(job.TempFilePath)) File.Delete(job.TempFilePath); } catch { }
                }
                catch (Exception ex)
                {
                    job.ErrorMessage = ex.Message;
                    UpdateStatus(job, ZipJobStatus.Failed, 0);
                }
                finally
                {
                    // Dispose the CTS to free resources
                    job.Cts.Dispose();
                }
            }
        }

        // -------------------------------------------------------------
        // NEW HELPER METHODS
        // -------------------------------------------------------------

        private long CalculateTotalSizeBytes(List<string> paths)
        {
            long size = 0;
            foreach (var path in paths)
            {
                if (File.Exists(path)) size += new FileInfo(path).Length;
                else if (Directory.Exists(path))
                {
                    // Recursive size calculation
                    var di = new DirectoryInfo(path);
                    try
                    {
                        size += di.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    }
                    catch { /* Ignore permission errors */ }
                }
            }
            // Prevent Division by Zero if empty folders selected
            return size == 0 ? 1 : size;
        }

        // Updated Helper: Accepts CancellationToken
        private async Task AddToZipRecursiveBytesAsync(ZipArchive archive, string path, string root, long totalBytes, CancellationToken token, Action<long> onBytesRead)
        {
            // Check for cancel at directory level
            token.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var entryName = Path.GetRelativePath(root, path);
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

                using var entryStream = entry.Open();
                using var sourceStream = File.OpenRead(path);

                byte[] buffer = new byte[81920];
                int bytesRead;

                // 4. CHECK TOKEN DURING READ/WRITE
                // This is what makes it stop in the middle of a 6GB file!
                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await entryStream.WriteAsync(buffer, 0, bytesRead, token);
                    onBytesRead(bytesRead);
                }
            }
            else if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                foreach (var file in di.GetFiles())
                {
                    await AddToZipRecursiveBytesAsync(archive, file.FullName, root, totalBytes, token, onBytesRead);
                }
                foreach (var dir in di.GetDirectories())
                {
                    await AddToZipRecursiveBytesAsync(archive, dir.FullName, root, totalBytes, token, onBytesRead);
                }
            }
        }

        private void UpdateStatus(ZipJob job, ZipJobStatus status, int percent)
        {
            job.Status = status;
            job.ProgressPercent = percent;
            OnJobUpdated?.Invoke(job.Id); // Notify UI
        }


        public void CancelJob(Guid id)
        {
            if (_jobs.TryGetValue(id, out var job))
            {
                // This sends a signal to the running loop to stop immediately
                if (!job.Cts.IsCancellationRequested)
                {
                    job.Cts.Cancel();
                }
            }
        }
    }
}
