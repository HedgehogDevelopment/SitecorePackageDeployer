using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hhogdev.SitecorePackageDeployer.Metadata
{
    [Serializable]
    public class PostStepDetails
    {
        public string PostStepPackageFilename { get; set; }
        public string HistoryPath { get; set; }
    }
}
