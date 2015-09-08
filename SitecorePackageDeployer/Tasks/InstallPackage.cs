using Sitecore.Configuration;
using Sitecore.Diagnostics;
using Sitecore.IO;
using Sitecore.Update;
using Sitecore.Update.Installer;
using Sitecore.Update.Installer.Exceptions;
using Sitecore.Update.Installer.Installer.Utils;
using Sitecore.Update.Installer.Utils;
using Sitecore.Update.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using log4net.spi;
using Hhogdev.SitecorePackageDeployer.Logging;
using log4net.Repository.Hierarchy;
using System.Threading;
using Sitecore;
using Sitecore.Update.Metadata;
using Hhogdev.SitecorePackageDeployer.Metadata;
using System.Xml.Serialization;
using Sitecore.Web;
using System.Net;

namespace Hhogdev.SitecorePackageDeployer.Tasks
{
    public class InstallPackage
    {
        internal const string STARTUP_POST_STEP_PACKAGE_FILENAME = "StartPostStepPackage.xml";

        //Points at the folder where new packages will be stored
        string _packageSource;
        //Url to make a request to for restarting the web server
        string _restartUrl;
        //Determines if the config files should be updated
         bool _updateConfigurationFiles;

        //Indicates that a package is being installed
        static bool _installingPackage;

        public static bool ShutdownDetected { get; set; }

        public InstallPackage()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _packageSource = GetPackageSource();
            _restartUrl = Settings.GetSetting("SitecorePackageDeployer.RestartUrl");
            _updateConfigurationFiles = Settings.GetBoolSetting("SitecorePackageDeployer.UpdateConfigurationFiles",
                false);
        }

        internal static string GetPackageSource()
        {
            string packageSource = Settings.GetSetting("SitecorePackageDeployer.PackageSource");

            if (!Directory.Exists(packageSource))
            {
                Directory.CreateDirectory(packageSource);
            }

            return packageSource;
        }

        public void Run()
        {
            InstallPackages();
        }

        /// <summary>
        /// Installs the packages found in the package source folder.
        /// </summary>
        private void InstallPackages()
        {
            //Check to see if there is a post-step pending, and skip package install if there is
            if (File.Exists(Path.Combine(_packageSource, STARTUP_POST_STEP_PACKAGE_FILENAME)))
            {
                Log.Info("Install packages skipped because there is a post step pending", this);

                return;
            }

            InstallLogger installLogger = new InstallLogger(new RootLogger(Level.ALL));

            //Return if another installation is happening
            if (_installingPackage)
            {
                Log.Info("Install packages skipped because another package is being installed.", this);

                return;
            }

            try
            {
                //Block further package installs
                _installingPackage = true;

                //Find pending packages. This loop may not complete if there were binary/config changes
                foreach (string updatePackageFilename in Directory.GetFiles(_packageSource, "*.update", SearchOption.TopDirectoryOnly))
                {
                    if (ShutdownDetected)
                    {
                        Log.Info("Install packages aborting dur to shutdown", this);

                        break;
                    }

                    //Prevent shutdown
                    using (new ShutdownGuard())
                    {
                        PackageInstallationInfo installationInfo = new PackageInstallationInfo
                        {
                            Action = UpgradeAction.Upgrade,
                            Mode = InstallMode.Install,
                            Path = updatePackageFilename
                        };

                        string installationHistoryRoot = null;
                        List<ContingencyEntry> logMessages = new List<ContingencyEntry>();

                        try
                        {
                            //Run the installer
                            logMessages = UpdateHelper.Install(installationInfo, installLogger, out installationHistoryRoot);

                            if (_updateConfigurationFiles)
                            {
                                FindAndUpdateChangedConfigs(Path.GetFileNameWithoutExtension(updatePackageFilename));
                            }

                            //Sleep for 4 seconds to see if there was a file change that could cause a problem
                            Thread.Sleep(4000);

                            //Abort if Sitecore is shutting down. The install post steps will have to be completed later
                            if (ShutdownDetected)
                            {
                                RunPostStepsAtStartup(updatePackageFilename, installationHistoryRoot);

                                RestartSitecoreServer();

                                break;
                            }
                            else
                            {
                                ExecutePostSteps(new PostStepDetails
                                {
                                    HistoryPath = installationHistoryRoot,
                                    PostStepPackageFilename = updatePackageFilename
                                });
                            }
                        }
                        catch (PostStepInstallerException ex)
                        {
                            logMessages = ex.Entries;
                            installationHistoryRoot = ex.HistoryPath;

                            throw ex;
                        }
                        finally
                        {
                            //Write logs
                            installLogger.WriteMessages(Path.Combine(installationHistoryRoot, "Install.log"));

                            SaveInstallationMessages(installationHistoryRoot, logMessages);
                        }
                    }
                }
            }
            finally
            {
                _installingPackage = false;
            }
        }

        /// <summary>
        /// Restarts the web server by making a request to the webroot. The request has a short timeout and is expected to fail. 
        /// The purpose is to initiate a request to IIS to restart the AppPool. The Url is determined by the Sitecore API or an optional config parameter.
        /// </summary>
        private void RestartSitecoreServer()
        {
            string url = _restartUrl;

            if (string.IsNullOrEmpty(_restartUrl))
            {
                url = WebUtil.GetServerUrl();
            }

            Log.Info("Sitecore Package Deployer is attempting to restart Sitecore at the url: " + url, this);

            WebRequest req = WebRequest.Create(url);
            req.Timeout = 100;

            try
            {
                using (WebResponse resp = req.GetResponse())
                {

                }
            }
            catch (Exception)
            { }
        }

        /// <summary>
        /// Creates a file that causes post install steps to be executed at startup
        /// </summary>
        /// <param name="updatePackageFilename"></param>
        /// <param name="historyPath"></param>
        private void RunPostStepsAtStartup(string updatePackageFilename, string historyPath)
        {
            string startupPostStepPackageFile = Path.Combine(_packageSource, STARTUP_POST_STEP_PACKAGE_FILENAME);

            //remove post step flag file if it exists
            if (File.Exists(startupPostStepPackageFile))
            {
                File.Delete(startupPostStepPackageFile);
            }

            PostStepDetails details = new PostStepDetails
            {
                HistoryPath = historyPath,
                PostStepPackageFilename = updatePackageFilename,
            };

            XmlSerializer serializer = new XmlSerializer(typeof(PostStepDetails));
            using (TextWriter writer = new StreamWriter(startupPostStepPackageFile))
            {
                serializer.Serialize(writer, details);
            }
        }

        /// <summary>
        /// Executes the post install steps
        /// </summary>
        /// <param name="postStepDetails"></param>
        internal static void ExecutePostSteps(PostStepDetails postStepDetails)
        {
            InstallLogger installLogger = new InstallLogger(new RootLogger(Level.ALL));

            try
            {
                //Load the metadata from the update package
                MetadataView metedateView = UpdateHelper.LoadMetadata(postStepDetails.PostStepPackageFilename);
                List<ContingencyEntry> logMessages = new List<ContingencyEntry>();

                //Execute the post install steps
                DiffInstaller diffInstaller = new DiffInstaller(UpgradeAction.Upgrade);
                diffInstaller.ExecutePostInstallationInstructions(postStepDetails.PostStepPackageFilename, postStepDetails.HistoryPath, InstallMode.Update, metedateView, installLogger, ref logMessages);

                //Move the update package into the history folder
                File.Move(postStepDetails.PostStepPackageFilename, Path.Combine(postStepDetails.HistoryPath, Path.GetFileName(postStepDetails.PostStepPackageFilename)));
            }
            catch (Exception ex)
            {
                Log.Fatal("Post step execution failed", ex, "InstallPackage");
            }
            finally
            {
                //Write logs
                installLogger.WriteMessages(Path.Combine(postStepDetails.HistoryPath, "Install.log"));
            }
        }

        /// <summary>
        /// Locates changed configs installed by the package installer
        /// </summary>
        private void FindAndUpdateChangedConfigs(string installPackageName)
        {
            string appConfigFolder = MainUtil.MapPath("/App_Config");

            foreach (string newConfigFile in Directory.GetFiles(appConfigFolder, "*.config." + installPackageName, SearchOption.AllDirectories))
            {
                string oldConfigFile = Path.Combine(Path.GetDirectoryName(newConfigFile), Path.GetFileNameWithoutExtension(newConfigFile));
                string backupConfigFile = newConfigFile + string.Format(".backup{0:yyyyMMddhhmmss}", DateTime.Now);

                //Backup the existing config file
                if (File.Exists(oldConfigFile))
                {
                    if (File.Exists(backupConfigFile))
                    {
                        File.Delete(backupConfigFile);
                    }

                    File.Move(oldConfigFile, backupConfigFile);
                }

                //Move the new file into place
                File.Copy(newConfigFile, oldConfigFile);

                //Someone didn't cleanup their handle to the new config file. This waits for it to be cleaned up
                GC.WaitForPendingFinalizers();

                try
                {
                    File.Delete(newConfigFile);
                }
                catch (Exception)
                {
                    Log.Warn(string.Format("Can't delete file {0}", newConfigFile), this);
                }
            }
        }

        /// <summary>
        /// Writes the log messages to the message log
        /// </summary>
        /// <param name="installationHistoryRoot"></param>
        /// <param name="logMessages"></param>
        private void SaveInstallationMessages(string installationHistoryRoot, List<ContingencyEntry> logMessages)
        {
            if (string.IsNullOrEmpty(installationHistoryRoot))
            {
                installationHistoryRoot = FileUtil.MakePath(FileUtils.InstallationHistoryRoot, "Upgrade_FAILURE_" + DateTime.Now.ToString("yyyyMMddTHHmmss") + DateTime.Now.Millisecond);
            }

            string messagesFile = Path.Combine(installationHistoryRoot, "messages.xml");
            FileUtil.EnsureFolder(messagesFile);

            using (FileStream fileStream = File.Create(messagesFile))
            {
                XmlEntrySerializer xmlEntrySerializer = new XmlEntrySerializer();
                xmlEntrySerializer.Serialize(logMessages, fileStream);
            }
        }
    }
}
