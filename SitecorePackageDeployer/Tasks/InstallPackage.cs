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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using System.Reflection;
using Sitecore.SecurityModel;
using Sitecore.Data;
using Sitecore.Syndication;

namespace Hhogdev.SitecorePackageDeployer.Tasks
{
    public class InstallPackage
    {
        internal enum InstallerState
        {
            Ready = 0,
            InstallingPackage = 1,
            WaitingForPostSteps = 2,
            InstallingPostSteps = 3
        }

        internal const string STARTUP_POST_STEP_PACKAGE_FILENAME = "StartPostStepPackage.xml";
        internal const string SUCCESS = "Success";
        internal const string FAIL = "Fail";
        internal const string INSTALLER_STATE_PROPERTY = "SPD_InstallerState_";

        static string sitecoreUpdatePath = Assembly.GetAssembly(typeof(PackageInstallationInfo)).Location;
        static Assembly sitecoreUpdateAssembly = Assembly.LoadFile(sitecoreUpdatePath);
        FileVersionInfo sitecoreUpdateVersionInfo = FileVersionInfo.GetVersionInfo(sitecoreUpdateAssembly.Location);
        static Type updateHelperType = sitecoreUpdateAssembly.GetType("Sitecore.Update.UpdateHelper");

        //Points at the folder where new packages will be stored
        string _packageSource;
        //Url to make a request to for restarting the web server
        string _restartUrl;
        //Determines if the config files should be updated
        bool _updateConfigurationFiles;

        public static bool ShutdownDetected { get; set; }

        public InstallPackage()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            _packageSource = GetPackageSource();
            _restartUrl = Settings.GetSetting("SitecorePackageDeployer.RestartUrl");
            _updateConfigurationFiles = Settings.GetBoolSetting("SitecorePackageDeployer.UpdateConfigurationFiles", true);
        }

        internal static string GetPackageSource()
        {
            string packageSource = Settings.GetSetting("SitecorePackageDeployer.PackageSource");

            //See if the package source is a web path instead of a file system path.
            if (packageSource.StartsWith("/"))
            {
                packageSource = MainUtil.MapPath(packageSource);
            }

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
            if (GetInstallerState() != InstallerState.Ready)
            {
                Log.Info(string.Format("Install packages skipped because install state is {0}. ", GetInstallerState()), this);

                return;
            }

            //Prevent shutdown
            using (new ShutdownGuard())
            {
                //If Sitecore is shutting down, don't start the installer
                if (ShutdownDetected)
                {
                    Log.Info("Skipping Install because shutdown is pending", this);

                    return;
                }

                //Block further package installs
                SetInstallerState(InstallerState.InstallingPackage);

                using (new SecurityDisabler())
                {
                    //Find pending packages. This loop may not complete if there were binary/config changes
                    foreach (string updatePackageFilename in Directory.GetFiles(_packageSource, "*.update", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                    {
                        string updatePackageFilenameStripped = updatePackageFilename.Split('\\').Last();
                        if (ShutdownDetected)
                        {
                            Log.Info("Install packages aborting due to shutdown", this);

                            if (GetInstallerState() != InstallerState.WaitingForPostSteps)
                            {
                                SetInstallerState(InstallerState.Ready);
                            }

                            break;
                        }

                        Log.Info(String.Format("Begin Installation: {0}", updatePackageFilenameStripped), this);

                        string installationHistoryRoot = null;
                        List<ContingencyEntry> logMessages = new List<ContingencyEntry>();

                        PostStepDetails postStepDetails = new PostStepDetails
                        {
                            PostStepPackageFilename = updatePackageFilename,
                            ResultFileName = Path.Combine(Path.GetDirectoryName(updatePackageFilename), Path.GetFileNameWithoutExtension(updatePackageFilename) + ".json")
                        };

                        string installStatus = null;

                        try
                        {
                            //Run the installer
                            if (sitecoreUpdateVersionInfo.ProductMajorPart == 1)
                            {
                                logMessages = UpdateHelper.Install(BuildPackageInfo(updatePackageFilename), installLogger, out installationHistoryRoot);
                            }
                            else
                            {
                                object[] installationParamaters = new object[] { BuildReflectedPackageInfo(updatePackageFilename), installLogger, null };
                                logMessages = (List<ContingencyEntry>)updateHelperType.InvokeMember("Install",
                                BindingFlags.InvokeMethod | BindingFlags.Static | BindingFlags.Public,
                                null, null, installationParamaters, null);
                                installationHistoryRoot = installationParamaters[2].ToString();
                            }

                            postStepDetails.HistoryPath = installationHistoryRoot;

                            if (_updateConfigurationFiles)
                            {
                                FindAndUpdateChangedConfigs(Path.GetFileNameWithoutExtension(updatePackageFilename));
                            }

                            //Sleep for 4 seconds to see if there was a file change that could cause a problem
                            Thread.Sleep(4000);

                            //Abort if Sitecore is shutting down. The install post steps will have to be completed later
                            if (ShutdownDetected)
                            {
                                SetInstallerState(InstallerState.WaitingForPostSteps);

                                RunPostStepsAtStartup(updatePackageFilename, installationHistoryRoot, postStepDetails);

                                RestartSitecoreServer();

                                break;
                            }
                            else
                            {
                                ExecutePostSteps(installLogger, postStepDetails, false);
                                installStatus = SUCCESS;

                                Log.Info(String.Format("Installation Complete: {0}", updatePackageFilenameStripped), this);
                                SetInstallerState(InstallerState.InstallingPackage);
                            }
                        }
                        catch (PostStepInstallerException ex)
                        {
                            installStatus = FAIL;

                            logMessages = ex.Entries;
                            installationHistoryRoot = ex.HistoryPath;
                            installLogger.Fatal("Package install failed", ex);

                            throw ex;
                        }
                        catch (Exception ex)
                        {
                            installStatus = FAIL;
                            Log.Error(String.Format("Installation Failed: {0}", updatePackageFilenameStripped), ex, this);
                            installLogger.Fatal("Package install failed", ex);

                            ThreadPool.QueueUserWorkItem(new WaitCallback((ctx) =>
                            {
                                try
                                {
                                    //The update package may be locked because the file object hasn't been disposed. Wait for it.
                                    Thread.Sleep(100);

                                    //I really hate this, but I couldn't find another reliable way to ensure the locked file is closed before I move it.
                                    GC.Collect(2);
                                    GC.WaitForPendingFinalizers();

                                    File.Move(updatePackageFilename, updatePackageFilename + ".error_" + DateTime.Now.ToString("yyyyMMdd.hhmmss"));
                                }
                                catch (Exception ex1)
                                {
                                    Log.Error("Error moving broken package", ex1, this);
                                }
                            }));

                            break;
                        }
                        finally
                        {
                            if (installationHistoryRoot != null)
                            {
                                //Write logs
                                installLogger.WriteMessages(Path.Combine(installationHistoryRoot, "Install.log"));

                                SaveInstallationMessages(installationHistoryRoot, logMessages);
                            }

                            //Send the status if there is one
                            if (installStatus != null)
                            {
                                NotifiyPackageComplete(installStatus, postStepDetails);
                            }
                        }
                    }

                    if (!ShutdownDetected)
                    {
                        //Allow additional installs
                        SetInstallerState(InstallerState.Ready);
                    }
                }
            }
        }

        internal static void SetInstallerState(InstallerState installState)
        {
            Log.Info(string.Format("Setting installer state to {0}", installState), typeof(InstallPackage));

            Database coreDb = Database.GetDatabase("core");
            coreDb.Properties.SetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)installState);
        }

        /// <summary>
        /// Gets the current install state
        /// </summary>
        /// <returns></returns>
        internal static InstallerState GetInstallerState()
        {
            Database coreDb = Database.GetDatabase("core");
            return (InstallerState)coreDb.Properties.GetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)InstallerState.Ready);
        }

        /// <summary>
        /// Restarts the web server by making a request to the webroot. The request has a short timeout and is expected to fail. 
        /// The purpose is to initiate a request to IIS to restart the AppPool. The Url is determined by the Sitecore API or an optional config parameter.
        /// </summary>
        private void RestartSitecoreServer()
        {

            try
            {
                //Force restart of all apppools pointing the website. This can happen during long installs
                Log.Info("Forcing restart", this);

                string configFile = MainUtil.MapPath("/App_Config/Include/SPD_Restart.config");

                using (FileStream fs = File.Open(configFile, FileMode.Create))
                {
                    using (StreamWriter sw = new StreamWriter(fs))
                    {
                        sw.WriteLine("<!-- Restart -->");
                    }
                }

                Thread.Sleep(250);
                File.Delete(configFile);
            }
            catch (Exception ex)
            {
                Log.Error("Exception when forcing restart", ex, this);
            }

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
        private void RunPostStepsAtStartup(string updatePackageFilename, string historyPath, PostStepDetails details)
        {
            string startupPostStepPackageFile = Path.Combine(_packageSource, STARTUP_POST_STEP_PACKAGE_FILENAME);

            //remove post step flag file if it exists
            if (File.Exists(startupPostStepPackageFile))
            {
                File.Delete(startupPostStepPackageFile);
            }

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
        internal static void ExecutePostSteps(InstallLogger installLogger, PostStepDetails postStepDetails, bool setReadyStateWhenDone)
        {
            try
            {
                SetInstallerState(InstallerState.InstallingPostSteps);

                //Load the metadata from the update package
                MetadataView metedateView = UpdateHelper.LoadMetadata(postStepDetails.PostStepPackageFilename);
                List<ContingencyEntry> logMessages = new List<ContingencyEntry>();

                try
                {
                    //Execute the post install steps
                    DiffInstaller diffInstaller = new DiffInstaller(UpgradeAction.Upgrade);
                    diffInstaller.ExecutePostInstallationInstructions(postStepDetails.PostStepPackageFilename, postStepDetails.HistoryPath, InstallMode.Update, metedateView, installLogger, ref logMessages);
                }
                finally
                {
                    //Move the update package into the history folder
                    File.Move(postStepDetails.PostStepPackageFilename, Path.Combine(postStepDetails.HistoryPath, Path.GetFileName(postStepDetails.PostStepPackageFilename)));
                }
            }
            catch (Exception ex)
            {
                Log.Fatal("Post step execution failed", ex, "InstallPackage");

                installLogger.Fatal("Post step execution failed", ex);

                //If the post step fails, we need to make the installer ready for the next package so it doesn't get stuck waiting for this one to finish.
                SetInstallerState(InstallerState.Ready);

                throw;
            }
            finally
            {
                if (setReadyStateWhenDone)
                {
                    SetInstallerState(InstallerState.Ready);
                }
            }
        }

        /// <summary>
        /// Locates changed configs installed by the package installer
        /// </summary>
        private void FindAndUpdateChangedConfigs(string installPackageName)
        {
            string appConfigFolder = MainUtil.MapPath("/");

            foreach (string newConfigFile in Directory.GetFiles(appConfigFolder, "*.config." + installPackageName, SearchOption.AllDirectories))
            {
                Log.Info(string.Format("Found changed config {0}", newConfigFile), this);

                int configExtensionPos = newConfigFile.LastIndexOf(".config") + 7;
                string oldConfigFile = Path.Combine(Path.GetDirectoryName(newConfigFile), newConfigFile.Substring(0, configExtensionPos));
                string backupConfigFile = newConfigFile + string.Format(".backup{0:yyyyMMddhhmmss}", DateTime.Now);

                //Backup the existing config file
                if (File.Exists(oldConfigFile))
                {
                    if (File.Exists(backupConfigFile))
                    {
                        File.Delete(backupConfigFile);
                    }

                    Log.Info(string.Format("Backing up config file {0} as {1}", oldConfigFile, backupConfigFile), this);

                    File.Move(oldConfigFile, backupConfigFile);
                }

                Log.Info(string.Format("Copying new config file from {0} to {1}", newConfigFile, oldConfigFile), this);

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
            try
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
            catch (Exception ex)
            {
                Log.Fatal("Error saving installation messages", ex, typeof(InstallPackage));
            }
        }

        /// <summary>
        /// Writes the notification .json to the install folder
        /// </summary>
        /// <param name="status"></param>
        /// <param name="postStepDetails"></param>
        public static void NotifiyPackageComplete(string status, PostStepDetails postStepDetails)
        {
            try
            {
                using (StreamWriter sw = File.CreateText(postStepDetails.ResultFileName))
                {
                    CompletionNotification completionNotification = new CompletionNotification
                    {
                        Status = status,
                        ServerName = Environment.MachineName,
                        DeployHistoryPath = postStepDetails.HistoryPath
                    };

                    sw.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(completionNotification));
                }
            }
            catch (Exception ex)
            {
                Log.Fatal("Error posting to notification url", ex, typeof(InstallPackage));
            }
        }

        public static void ResetInstallState()
        {
            SetInstallerState(InstallerState.Ready);
        }

        private PackageInstallationInfo BuildPackageInfo(string updatePackageFilename)
        {
            PackageInstallationInfo installationInfo = new PackageInstallationInfo
            {
                Action = UpgradeAction.Upgrade,
                Mode = InstallMode.Install,
                Path = updatePackageFilename,
            };
            return installationInfo;
        }

        private object BuildReflectedPackageInfo(string updatePackageFilename)
        {
            //Get PackageInstallationInfo type and create instance for reflection
            Type packageInstallationInfoType = sitecoreUpdateAssembly.GetType("Sitecore.Update.PackageInstallationInfo");
            object packageInstallationInfoInstance = Activator.CreateInstance(packageInstallationInfoType);
            //Get and set properties
            PropertyInfo actionPropertyInfo = packageInstallationInfoType.GetProperty("Action");
            PropertyInfo modePropertyInfo = packageInstallationInfoType.GetProperty("Mode");
            PropertyInfo pathPropertyInfo = packageInstallationInfoType.GetProperty("Path");
            PropertyInfo processingModePropertyInfo = packageInstallationInfoType.GetProperty("ProcessingMode");
            actionPropertyInfo.SetValue(packageInstallationInfoInstance, GetUpgradeActionValue("Upgrade"), null);
            modePropertyInfo.SetValue(packageInstallationInfoInstance, GetInstallModeValue("Install"), null);
            pathPropertyInfo.SetValue(packageInstallationInfoInstance, updatePackageFilename, null);
            processingModePropertyInfo.SetValue(packageInstallationInfoInstance, GetProcessingModeValue("All"), null);
            return packageInstallationInfoInstance;
        }

        /// <summary>
        ///   Gets upgrade action value.
        /// </summary>
        ///
        /// <remarks>
        ///   Created  on 12/27/2016.
        /// </remarks>
        ///
        /// <param name="upgradeAction"> The upgrade action accepting any of the following values 
        ///  {
        ///   "Preview",
        ///   "Upgrade"
        ///  }
        /// from Sitecore.Update v2 dll's Sitecore.Update.Installer.Utils.UpgradeAction enum.
        /// </param>
        /// 
        ///
        /// <returns>
        ///   The upgrade action value.
        /// </returns>

        private object GetUpgradeActionValue(string upgradeAction)
        {
            Type upgradeActionType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Installer.Utils.UpgradeAction");
            return Enum.Parse(upgradeActionType, upgradeAction);
        }

        /// <summary>
        ///   Gets install mode value.
        /// </summary>
        ///
        /// <remarks>
        ///   Created  on 12/27/2016.
        /// </remarks>
        ///
        /// <param name="installMode">  The install mode accepting any of the following values
        ///   {
        ///   "Install",
        ///		"Update"
        ///   }
        /// from Sitecore.Update v2 dll's Sitecore.Update.Utils.InstallMode enum
        /// </param>
        ///
        /// <returns>
        ///   The install mode value.
        /// </returns>

        private object GetInstallModeValue(string installMode)
        {
            Type installModeType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Utils.InstallMode");
            return Enum.Parse(installModeType, installMode);
        }

        /// <summary>
        ///   Gets processing mode value.
        /// </summary>
        ///
        /// <remarks>
        ///   Created  on 12/27/2016.
        /// </remarks>
        ///
        /// <param name="processingMode"> The processing mode accepting any of the following values 
        ///  {
        ///  "None",
        ///  "Files",
        ///  "Items",
        ///  "PostStep",
        ///  "All"
        /// }
        /// from Sitecore.Update v2 dll's Sitecore.Update.Utils.ProcessingMode enum.
        /// </param>
        /// 
        ///
        /// <returns>
        ///   The processing mode value.
        /// </returns>

        private object GetProcessingModeValue(string processingMode)
        {
            Type processingModeType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Utils.ProcessingMode");
            return Enum.Parse(processingModeType, processingMode);
        }
    }
}
