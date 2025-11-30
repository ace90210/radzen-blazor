using System;
using System.Collections.Generic;
using System.Threading;

namespace Radzen.Blazor
{
    public class ZipJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public List<string> SourcePaths { get; set; }
        public string RootPath { get; set; }

        // State
        public ZipJobStatus Status { get; set; } = ZipJobStatus.Queued;
        public int ProgressPercent { get; set; }
        public string TempFilePath { get; set; } // Where the finished zip sits on disk
        public string OutputFileName { get; set; } // "MyBackup.zip"
        public string ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();
    }

    public enum ZipJobStatus
    {
        Queued,
        Processing,
        Completed,
        Failed,
        Cancelled
    }
}
