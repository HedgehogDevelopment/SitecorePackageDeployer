using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;
using Hhogdev.SitecorePackageDeployer.Logging;
using Hhogdev.SitecorePackageDeployer.Metadata;
using log4net.Repository.Hierarchy;
using log4net.spi;
using Newtonsoft.Json;
using Sitecore;
using Sitecore.Configuration;
using Sitecore.Data;
using Sitecore.Diagnostics;
using Sitecore.IO;
using Sitecore.SecurityModel;
using Sitecore.Update;
using Sitecore.Update.Installer;
using Sitecore.Update.Installer.Exceptions;
using Sitecore.Update.Installer.Installer.Utils;
using Sitecore.Update.Installer.Utils;
using Sitecore.Update.Utils;
using Sitecore.Web;

namespace Hhogdev.SitecorePackageDeployer.Tasks
{
    public class InstallPackage
    {
        internal const string STARTUP_POST_STEP_PACKAGE_FILENAME = "StartPostStepPackage.xml";
        internal const string SUCCESS = "Success";
        internal const string FAIL = "Fail";
        internal const string INSTALLER_STATE_PROPERTY = "SPD_InstallerState_";

        private static readonly string sitecoreUpdatePath =
            Assembly.GetAssembly(typeof(PackageInstallationInfo)).Location;

        private static readonly Assembly sitecoreUpdateAssembly = Assembly.LoadFile(sitecoreUpdatePath);
        private static readonly Type updateHelperType = sitecoreUpdateAssembly.GetType("Sitecore.Update.UpdateHelper");

        private readonly FileVersionInfo sitecoreUpdateVersionInfo =
            FileVersionInfo.GetVersionInfo(sitecoreUpdateAssembly.Location);

        //Points at the folder where new packages will be stored
        private string _packageSource;

        //File for restarting the web server
        private string _restartFile;

        //Url to make a request to for restarting the web server
        private string _restartUrl;

        //Determines if the config files should be updated
        private bool _updateConfigurationFiles;

        public InstallPackage()
        {
            LoadSettings();
        }

        public static bool ShutdownDetected { get; set; }

        private void LoadSettings()
        {
            _packageSource = GetPackageSource();
            _restartUrl = Settings.GetSetting("SitecorePackageDeployer.RestartUrl");
            _restartFile = GetRestartFile();
            _updateConfigurationFiles =
                Settings.GetBoolSetting("SitecorePackageDeployer.UpdateConfigurationFiles", true);
        }

        internal static string GetPackageSource()
        {
            var packageSource = Settings.GetSetting("SitecorePackageDeployer.PackageSource");

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

        internal static string GetRestartFile()
        {
            var restartFile = Settings.GetSetting("SitecorePackageDeployer.RestartFile");

            if (restartFile.StartsWith("/"))
            {
                restartFile = MainUtil.MapPath(restartFile);
            }

            return restartFile;
        }

        public void Run()
        {
            InstallPackages();
        }

        /// <summary>
        ///     Installs the packages found in the package source folder.
        /// </summary>
        private void InstallPackages()
        {
            //Check to see if there is a post-step pending, and skip package install if there is
            if (File.Exists(Path.Combine(_packageSource, STARTUP_POST_STEP_PACKAGE_FILENAME)))
            {
                Log.Info("Install packages skipped because there is a post step pending", this);

                return;
            }

            var installLogger = new InstallLogger(new RootLogger(Level.ALL));

            //Check to see if we need to force the start of an install since the state is incorrect
            Log.Info(string.Format("Checking for a restart file at {0}", _restartFile), this);
            if (File.Exists(_restartFile))
            {
                Log.Info("Found a restart file", this);
                File.Delete(_restartFile);
                ResetInstallState();
            }

            //Return if another installation is happening
            if (GetInstallerState() != InstallerState.Ready)
            {
                Log.Info(string.Format("Install packages skipped because install state is {0}. ", GetInstallerState()),
                    this);

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
                    foreach (var updatePackageFilename in Directory
                                 .GetFiles(_packageSource, "*.update", SearchOption.TopDirectoryOnly).OrderBy(f => f))
                    {
                        var updatePackageFilenameStripped = updatePackageFilename.Split('\\').Last();
                        if (ShutdownDetected)
                        {
                            Log.Info("Install packages aborting due to shutdown", this);

                            if (GetInstallerState() != InstallerState.WaitingForPostSteps)
                            {
                                SetInstallerState(InstallerState.Ready);
                            }

                            break;
                        }

                        Log.Info(string.Format("Begin Installation: {0}", updatePackageFilenameStripped), this);

                        string installationHistoryRoot = null;
                        var logMessages = new List<ContingencyEntry>();

                        var postStepDetails = new PostStepDetails
                        {
                            PostStepPackageFilename = updatePackageFilename,
                            ResultFileName = Path.Combine(Path.GetDirectoryName(updatePackageFilename),
                                Path.GetFileNameWithoutExtension(updatePackageFilename) + ".json")
                        };

                        string installStatus = null;

                        try
                        {
                            //Run the installer
                            if (sitecoreUpdateVersionInfo.ProductMajorPart == 1)
                            {
                                logMessages = UpdateHelper.Install(BuildPackageInfo(updatePackageFilename),
                                    installLogger, out installationHistoryRoot);
                            }
                            else
                            {
                                object[] installationParamaters =
                                    { BuildReflectedPackageInfo(updatePackageFilename), installLogger, null };
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

                                Log.Info(string.Format("Installation Complete: {0}", updatePackageFilenameStripped),
                                    this);
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
                            Log.Error(string.Format("Installation Failed: {0}", updatePackageFilenameStripped), ex,
                                this);
                            installLogger.Fatal("Package install failed", ex);

                            ThreadPool.QueueUserWorkItem(ctx =>
                            {
                                try
                                {
                                    //The update package may be locked because the file object hasn't been disposed. Wait for it.
                                    Thread.Sleep(100);

                                    //I really hate this, but I couldn't find another reliable way to ensure the locked file is closed before I move it.
                                    GC.Collect(2);
                                    GC.WaitForPendingFinalizers();

                                    File.Move(updatePackageFilename,
                                        updatePackageFilename + ".error_" + DateTime.Now.ToString("yyyyMMdd.hhmmss"));
                                }
                                catch (Exception ex1)
                                {
                                    Log.Error("Error moving broken package", ex1, this);
                                }
                            });

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

            var coreDb = Database.GetDatabase("core");
            // Use reflection to get the int value because the properties base class changed in 9.3
            if (coreDb.GetType().Assembly.GetName().Version.Major >= 14)
            {
                //coreDb.PropertyStore.SetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)installState);

                var propertyStorePropertyInfo = coreDb.GetType().GetProperty("PropertyStore");
                var propertyStoreGetMethod = propertyStorePropertyInfo.GetGetMethod();
                var propertyStore = propertyStoreGetMethod.Invoke(coreDb, null);

                var setIntValueMethodInfo = propertyStore.GetType().GetMethod("SetIntValue");
                setIntValueMethodInfo.Invoke(propertyStore,
                    new object[] { INSTALLER_STATE_PROPERTY + Environment.MachineName, installState });
            }
            else
            {
                //coreDb.Properties.SetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)installState);

                var propertiesPropertyInfo = coreDb.GetType().GetProperty("Properties");
                var propertiesGetMethod = propertiesPropertyInfo.GetGetMethod();
                var properties = propertiesGetMethod.Invoke(coreDb, null);

                var setIntValueMethodInfo = properties.GetType().GetMethod("SetIntValue");
                setIntValueMethodInfo.Invoke(properties,
                    new object[] { INSTALLER_STATE_PROPERTY + Environment.MachineName, installState });
            }
        }

        /// <summary>
        ///     Gets the current install state
        /// </summary>
        /// <returns></returns>
        internal static InstallerState GetInstallerState()
        {
            var coreDb = Database.GetDatabase("core");

            // Use reflection to get the int value because the properties base class changed in 9.3
            if (coreDb.GetType().Assembly.GetName().Version.Major >= 14)
            {
                //return (InstallerState)coreDb.PropertyStore.GetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)InstallerState.Ready);

                var propertyStorePropertyInfo = coreDb.GetType().GetProperty("PropertyStore");
                var propertyStoreGetMethod = propertyStorePropertyInfo.GetGetMethod();
                var propertyStore = propertyStoreGetMethod.Invoke(coreDb, null);

                var getIntValueMethodInfo = propertyStore.GetType().GetMethod("GetIntValue");
                return (InstallerState)getIntValueMethodInfo.Invoke(propertyStore,
                    new object[] { INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)InstallerState.Ready });
            }
            else
            {
                //return (InstallerState)coreDb.Properties.GetIntValue(INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)InstallerState.Ready);

                var propertiesPropertyInfo = coreDb.GetType().GetProperty("Properties");
                var propertiesGetMethod = propertiesPropertyInfo.GetGetMethod();
                var properties = propertiesGetMethod.Invoke(coreDb, null);

                var getIntValueMethodInfo = properties.GetType().GetMethod("GetIntValue");
                return (InstallerState)getIntValueMethodInfo.Invoke(properties,
                    new object[] { INSTALLER_STATE_PROPERTY + Environment.MachineName, (int)InstallerState.Ready });
            }
        }

        /// <summary>
        ///     Restarts the web server by making a request to the webroot. The request has a short timeout and is expected to
        ///     fail.
        ///     The purpose is to initiate a request to IIS to restart the AppPool. The Url is determined by the Sitecore API or an
        ///     optional config parameter.
        /// </summary>
        private void RestartSitecoreServer()
        {
            try
            {
                //Force restart of all apppools pointing the website. This can happen during long installs
                Log.Info("Forcing restart", this);

                var configFile = MainUtil.MapPath("/App_Config/Include/SPD_Restart.config");

                using (var fs = File.Open(configFile, FileMode.Create))
                {
                    using (var sw = new StreamWriter(fs))
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

            var url = _restartUrl;

            if (string.IsNullOrEmpty(_restartUrl))
            {
                url = WebUtil.GetServerUrl();
            }

            Log.Info("Sitecore Package Deployer is attempting to restart Sitecore at the url: " + url, this);

            var req = WebRequest.Create(url);
            req.Timeout = 100;

            try
            {
                using (var resp = req.GetResponse())
                {
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        ///     Creates a file that causes post install steps to be executed at startup
        /// </summary>
        /// <param name="updatePackageFilename"></param>
        /// <param name="historyPath"></param>
        private void RunPostStepsAtStartup(string updatePackageFilename, string historyPath, PostStepDetails details)
        {
            var startupPostStepPackageFile = Path.Combine(_packageSource, STARTUP_POST_STEP_PACKAGE_FILENAME);

            //remove post step flag file if it exists
            if (File.Exists(startupPostStepPackageFile))
            {
                File.Delete(startupPostStepPackageFile);
            }

            var serializer = new XmlSerializer(typeof(PostStepDetails));
            using (TextWriter writer = new StreamWriter(startupPostStepPackageFile))
            {
                serializer.Serialize(writer, details);
            }
        }

        /// <summary>
        ///     Executes the post install steps
        /// </summary>
        /// <param name="postStepDetails"></param>
        internal static void ExecutePostSteps(InstallLogger installLogger, PostStepDetails postStepDetails,
            bool setReadyStateWhenDone)
        {
            try
            {
                SetInstallerState(InstallerState.InstallingPostSteps);

                //Load the metadata from the update package
                var metedateView = UpdateHelper.LoadMetadata(postStepDetails.PostStepPackageFilename);
                var logMessages = new List<ContingencyEntry>();

                try
                {
                    //Execute the post install steps
                    var diffInstaller = new DiffInstaller(UpgradeAction.Upgrade);
                    diffInstaller.ExecutePostInstallationInstructions(postStepDetails.PostStepPackageFilename,
                        postStepDetails.HistoryPath, InstallMode.Update, metedateView, installLogger, ref logMessages);
                }
                finally
                {
                    //Move the update package into the history folder
                    File.Move(postStepDetails.PostStepPackageFilename,
                        Path.Combine(postStepDetails.HistoryPath,
                            Path.GetFileName(postStepDetails.PostStepPackageFilename)));
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
        ///     Locates changed configs installed by the package installer
        /// </summary>
        private void FindAndUpdateChangedConfigs(string installPackageName)
        {
            var appConfigFolder = MainUtil.MapPath("/");

            foreach (var newConfigFile in Directory.GetFiles(appConfigFolder, "*.config." + installPackageName,
                         SearchOption.AllDirectories))
            {
                Log.Info(string.Format("Found changed config {0}", newConfigFile), this);

                var configExtensionPos = newConfigFile.LastIndexOf(".config") + 7;
                var oldConfigFile = Path.Combine(Path.GetDirectoryName(newConfigFile),
                    newConfigFile.Substring(0, configExtensionPos));
                var backupConfigFile = newConfigFile + string.Format(".backup{0:yyyyMMddhhmmss}", DateTime.Now);

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
        ///     Writes the log messages to the message log
        /// </summary>
        /// <param name="installationHistoryRoot"></param>
        /// <param name="logMessages"></param>
        private void SaveInstallationMessages(string installationHistoryRoot, List<ContingencyEntry> logMessages)
        {
            try
            {
                if (string.IsNullOrEmpty(installationHistoryRoot))
                {
                    installationHistoryRoot = FileUtil.MakePath(FileUtils.InstallationHistoryRoot,
                        "Upgrade_FAILURE_" + DateTime.Now.ToString("yyyyMMddTHHmmss") + DateTime.Now.Millisecond);
                }

                var messagesFile = Path.Combine(installationHistoryRoot, "messages.xml");
                FileUtil.EnsureFolder(messagesFile);

                using (var fileStream = File.Create(messagesFile))
                {
                    var xmlEntrySerializer = new XmlEntrySerializer();

                    //For some reason, this has changed in Sitecore 9. We are using reflection to get it because the fileStream parameter can be a FileStream or Stream
                    //xmlEntrySerializer.Serialize(logMessages, fileStream);
                    var serializerType = xmlEntrySerializer.GetType();

                    var serializeMethod = serializerType.GetMethod("Serialize",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { logMessages.GetType(), typeof(FileStream) },
                        null);

                    if (serializeMethod == null)
                    {
                        serializeMethod = serializerType.GetMethod("Serialize",
                            BindingFlags.Public | BindingFlags.Instance,
                            null,
                            new[] { logMessages.GetType(), typeof(Stream) },
                            null);
                    }

                    if (serializeMethod != null)
                    {
                        serializeMethod.Invoke(xmlEntrySerializer, new object[] { logMessages, fileStream });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Fatal("Error saving installation messages", ex, typeof(InstallPackage));
            }
        }

        /// <summary>
        ///     Writes the notification .json to the install folder
        /// </summary>
        /// <param name="status"></param>
        /// <param name="postStepDetails"></param>
        public static void NotifiyPackageComplete(string status, PostStepDetails postStepDetails)
        {
            try
            {
                using (var sw = File.CreateText(postStepDetails.ResultFileName))
                {
                    var completionNotification = new CompletionNotification
                    {
                        Status = status,
                        ServerName = Environment.MachineName,
                        DeployHistoryPath = postStepDetails.HistoryPath
                    };

                    sw.WriteLine(JsonConvert.SerializeObject(completionNotification));
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
            var installationInfo = new PackageInstallationInfo
            {
                Action = UpgradeAction.Upgrade,
                Mode = InstallMode.Install,
                Path = updatePackageFilename
            };
            return installationInfo;
        }

        private object BuildReflectedPackageInfo(string updatePackageFilename)
        {
            //Get PackageInstallationInfo type and create instance for reflection
            var packageInstallationInfoType = sitecoreUpdateAssembly.GetType("Sitecore.Update.PackageInstallationInfo");
            var packageInstallationInfoInstance = Activator.CreateInstance(packageInstallationInfoType);
            //Get and set properties
            var actionPropertyInfo = packageInstallationInfoType.GetProperty("Action");
            var modePropertyInfo = packageInstallationInfoType.GetProperty("Mode");
            var pathPropertyInfo = packageInstallationInfoType.GetProperty("Path");
            var processingModePropertyInfo = packageInstallationInfoType.GetProperty("ProcessingMode");
            actionPropertyInfo.SetValue(packageInstallationInfoInstance, GetUpgradeActionValue("Upgrade"), null);
            modePropertyInfo.SetValue(packageInstallationInfoInstance, GetInstallModeValue("Install"), null);
            pathPropertyInfo.SetValue(packageInstallationInfoInstance, updatePackageFilename, null);
            processingModePropertyInfo.SetValue(packageInstallationInfoInstance, GetProcessingModeValue("All"), null);
            return packageInstallationInfoInstance;
        }

        /// <summary>
        ///     Gets upgrade action value.
        /// </summary>
        /// <remarks>
        ///     Created  on 12/27/2016.
        /// </remarks>
        /// <param name="upgradeAction">
        ///     The upgrade action accepting any of the following values
        ///     {
        ///     "Preview",
        ///     "Upgrade"
        ///     }
        ///     from Sitecore.Update v2 dll's Sitecore.Update.Installer.Utils.UpgradeAction enum.
        /// </param>
        /// <returns>
        ///     The upgrade action value.
        /// </returns>
        private object GetUpgradeActionValue(string upgradeAction)
        {
            var upgradeActionType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Installer.Utils.UpgradeAction");
            return Enum.Parse(upgradeActionType, upgradeAction);
        }

        /// <summary>
        ///     Gets install mode value.
        /// </summary>
        /// <remarks>
        ///     Created  on 12/27/2016.
        /// </remarks>
        /// <param name="installMode">
        ///     The install mode accepting any of the following values
        ///     {
        ///     "Install",
        ///     "Update"
        ///     }
        ///     from Sitecore.Update v2 dll's Sitecore.Update.Utils.InstallMode enum
        /// </param>
        /// <returns>
        ///     The install mode value.
        /// </returns>
        private object GetInstallModeValue(string installMode)
        {
            var installModeType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Utils.InstallMode");
            return Enum.Parse(installModeType, installMode);
        }

        /// <summary>
        ///     Gets processing mode value.
        /// </summary>
        /// <remarks>
        ///     Created  on 12/27/2016.
        /// </remarks>
        /// <param name="processingMode">
        ///     The processing mode accepting any of the following values
        ///     {
        ///     "None",
        ///     "Files",
        ///     "Items",
        ///     "PostStep",
        ///     "All"
        ///     }
        ///     from Sitecore.Update v2 dll's Sitecore.Update.Utils.ProcessingMode enum.
        /// </param>
        /// <returns>
        ///     The processing mode value.
        /// </returns>
        private object GetProcessingModeValue(string processingMode)
        {
            var processingModeType = sitecoreUpdateAssembly.GetType("Sitecore.Update.Utils.ProcessingMode");
            return Enum.Parse(processingModeType, processingMode);
        }

        internal enum InstallerState
        {
            Ready = 0,
            InstallingPackage = 1,
            WaitingForPostSteps = 2,
            InstallingPostSteps = 3
        }
    }
}