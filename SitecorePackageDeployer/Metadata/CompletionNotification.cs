using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
