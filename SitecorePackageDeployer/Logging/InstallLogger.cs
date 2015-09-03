using log4net;
using log4net.spi;
using Sitecore.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hhogdev.SitecorePackageDeployer.Logging
{
    /// <summary>
    /// Implements ILog to capture log messages during install
    /// </summary>
    internal class InstallLogger : ILog
    {
        ILogger _logger;
        List<string> _messages = new List<string>();

        public InstallLogger(ILogger logger)
        {
            _logger = logger;
        }

        #region ILog implementation
        public bool IsDebugEnabled
        {
            get
            {
                return true;
            }
        }

        public bool IsErrorEnabled
        {
            get
            {
                return true;
            }
        }

        public bool IsFatalEnabled
        {
            get
            {
                return true;
            }
        }

        public bool IsInfoEnabled
        {
            get
            {
                return true;
            }
        }

        public bool IsWarnEnabled
        {
            get
            {
                return true;
            }
        }

        public ILogger Logger
        {
            get
            {
                return _logger;
            }
        }

        public void Debug(object message)
        {
            Log.Debug(message.ToString());

            WriteMessage(message, null);
        }

        public void Debug(object message, Exception ex)
        {
            Log.Debug(message.ToString());

            WriteMessage(message, ex);
        }

        public void Error(object message)
        {
            Log.Error(message.ToString(), this);

            WriteMessage(message, null);
        }

        public void Error(object message, Exception ex)
        {
            Log.Error(message.ToString(), ex, this);

            WriteMessage(message, ex);
        }

        public void Fatal(object message)
        {
            Log.Fatal(message.ToString(), this);

            WriteMessage(message, null);
        }

        public void Fatal(object message, Exception ex)
        {
            Log.Fatal(message.ToString(), ex, this);

            WriteMessage(message, ex);
        }

        public void Info(object message)
        {
            Log.Info(message.ToString(), this);

            WriteMessage(message, null);
        }

        public void Info(object message, Exception ex)
        {
            Log.Info(message.ToString(), this);

            WriteMessage(message, ex);
        }

        public void Warn(object message)
        {
            Log.Warn(message.ToString(), this);

            WriteMessage(message, null);
        }

        public void Warn(object message, Exception ex)
        {
            Log.Warn(message.ToString(), ex, this);

            WriteMessage(message, ex);
        }
        #endregion

        /// <summary>
        /// Adds a message to the list of messages strings
        /// </summary>
        /// <param name="message"></param>
        /// <param name="ex"></param>
        /// <param name="dEBUG"></param>
        private void WriteMessage(object message, Exception ex)
        {
            StringBuilder messageBuilder = new StringBuilder(message.ToString());

            if (ex!=null)
            {
                messageBuilder.Append("\n\t");
                messageBuilder.Append(ex.ToString());
            }

            _messages.Add(messageBuilder.ToString());
        }

        /// <summary>
        /// Writes messages to a log file
        /// </summary>
        /// <param name="logFile"></param>
        public void WriteMessages(string logFile)
        {
            File.AppendAllLines(logFile, _messages);
        }
    }
}
