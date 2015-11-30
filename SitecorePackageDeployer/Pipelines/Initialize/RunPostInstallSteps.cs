using Hhogdev.SitecorePackageDeployer.Logging;
using Hhogdev.SitecorePackageDeployer.Metadata;
using Hhogdev.SitecorePackageDeployer.Tasks;
using log4net.Repository.Hierarchy;
using log4net.spi;
using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.Pipelines;
using Sitecore.SecurityModel;
using System;
using System.IO;
using System.Xml.Serialization;

namespace Hhogdev.SitecorePackageDeployer.Pipelines.Initialize
{
    public class RunPostInstallSteps
    {
        string _packageSource;

        public RunPostInstallSteps()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _packageSource = InstallPackage.GetPackageSource();
        }

        public void Process(PipelineArgs args)
        {
            try
            {
                RunPostInitializeStepsIfNeeded();
            }
            catch(Exception ex)
            {
                Log.Error("Failed to complete post initialize steps", ex, this);
            }
        }

        private void RunPostInitializeStepsIfNeeded()
        {
            string startupPostStepPackageFile = Path.Combine(_packageSource, InstallPackage.STARTUP_POST_STEP_PACKAGE_FILENAME);

            //remove post step flag file if it exists
            if (File.Exists(startupPostStepPackageFile))
            {
                try
                {
                    using (new SecurityDisabler())
                    {
                        //Load the post step details
                        XmlSerializer serializer = new XmlSerializer(typeof(PostStepDetails));
                        using (TextReader writer = new StreamReader(startupPostStepPackageFile))
                        {
                            PostStepDetails details = serializer.Deserialize(writer) as PostStepDetails;

                            if (details != null)
                            {
                                InstallLogger installLogger = new InstallLogger(new RootLogger(Level.ALL));

                                try
                                {
                                    InstallPackage.ExecutePostSteps(installLogger, details);
                                }
                                finally
                                {
                                    installLogger.WriteMessages(Path.Combine(details.HistoryPath, "Install.log"));
                                }
                            }
                        }
                    }
                }
                finally
                {
                    //cleanup the post step
                    File.Delete(startupPostStepPackageFile);
                }
            }
        }
    }
}
