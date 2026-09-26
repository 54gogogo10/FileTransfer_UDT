using System.IO;

namespace TrFileTransfer
{
    /// <summary>One queued send task (captured from the client panel).</summary>
    public class QueuedTask
    {
        public string FilePath;
        public bool IsFolder;
        public string ServerIp;
        public int Port;
        /// <summary>UDT transport (historic name — the queue predates the UDP option).</summary>
        public bool IsUdp;
        /// <summary>One-way raw UDP transport (no ACK channel).</summary>
        public bool IsRawUdp;
        public int SrcPort;
        public int Concurrency;
        public bool VerifyHash;
        public int SpeedLimit;

        public string DisplayName
        {
            get
            {
                string name = IsFolder ? Path.GetFileName(FilePath.TrimEnd('\\', '/')) : Path.GetFileName(FilePath);
                if (string.IsNullOrEmpty(name)) name = FilePath;
                return name;
            }
        }
    }
}
