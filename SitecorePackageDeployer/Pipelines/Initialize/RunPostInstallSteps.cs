using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using Hhogdev.SitecorePackageDeployer.Logging;
using Hhogdev.SitecorePackageDeployer.Metadata;
using Hhogdev.SitecorePackageDeployer.Tasks;
using log4net.Repository.Hierarchy;
using log4net.spi;
using Sitecore.Diagnostics;
using Sitecore.Pipelines;
using Sitecore.SecurityModel;

namespace Hhogdev.SitecorePackageDeployer.Pipelines.Initialize
{
    public class RunPostInstallSteps
    {
        private string _packageSource;
        private string _restartFile;

        public RunPostInstallSteps()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _packageSource = InstallPackage.GetPackageSource();
            _restartFile = InstallPackage.GetRestartFile();
        }

        public void Process(PipelineArgs args)
        {
            Log.Info(
                "Sitecore package deployer starting. Version: " + FileVersionInfo
                    .GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion, this);

            //Check to see if we need to force the start of an install since the state is incorrect
            if (File.Exists(_restartFile))
            {
                File.Delete(_restartFile);
                InstallPackage.ResetInstallState();
            }

            //Check to see if we can run post steps
            var state = InstallPackage.GetInstallerState();
            if (state == InstallPackage.InstallerState.InstallingPackage ||
                state == InstallPackage.InstallerState.InstallingPostSteps)
            {
                Log.Warn(string.Format("Can't run post steps. Package installer state is {0}", state), this);

                return;
            }

            try
            {
                RunPostInitializeStepsIfNeeded();

                InstallAdditionalPackages();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to complete post initialize steps", ex, this);
            }
        }

        private void InstallAdditionalPackages()
        {
            ThreadPool.QueueUserWorkItem(ctx =>
            {
                var installer = new InstallPackage();
                installer.Run();
            });
        }

        private void RunPostInitializeStepsIfNeeded()
        {
            var startupPostStepPackageFile =
                Path.Combine(_packageSource, InstallPackage.STARTUP_POST_STEP_PACKAGE_FILENAME);

            //remove post step flag file if it exists
            if (File.Exists(startupPostStepPackageFile))
            {
                try
                {
                    using (new SecurityDisabler())
                    {
                        //Load the post step details
                        var serializer = new XmlSerializer(typeof(PostStepDetails));
                        using (TextReader writer = new StreamReader(startupPostStepPackageFile))
                        {
                            var details = serializer.Deserialize(writer) as PostStepDetails;

                            if (details != null)
                            {
                                var installLogger = new InstallLogger(new RootLogger(Level.ALL));

                                try
                                {
                                    InstallPackage.ExecutePostSteps(installLogger, details, true);

                                    InstallPackage.NotifiyPackageComplete(InstallPackage.SUCCESS, details);
                                }
                                catch (Exception ex)
                                {
                                    Log.Fatal("An error occured when running post steps", ex, this);

                                    InstallPackage.NotifiyPackageComplete(InstallPackage.FAIL, details);
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