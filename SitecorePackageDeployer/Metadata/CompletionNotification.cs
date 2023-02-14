using System;

namespace Hhogdev.SitecorePackageDeployer.Metadata
{
    [Serializable]
    public class CompletionNotification
    {
        public string Status { get; set; }
        public string ServerName { get; set; }
        public string DeployHistoryPath { get; set; }
    }
}