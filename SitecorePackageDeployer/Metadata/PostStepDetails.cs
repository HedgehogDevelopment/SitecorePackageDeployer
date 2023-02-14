using System;

namespace Hhogdev.SitecorePackageDeployer.Metadata
{
    [Serializable]
    public class PostStepDetails
    {
        public string PostStepPackageFilename { get; set; }
        public string HistoryPath { get; set; }
        public string ResultFileName { get; set; }
    }
}