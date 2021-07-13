﻿namespace Kudu.Services.Performance
{
    public class LogFile
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string RelativePath { get; set; }
        public long Size { get; set; }
        public string ProcessName { get; set; }
        public int ProcessId { get; set; }
    }
}