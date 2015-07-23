namespace SQLBackupUtility
{
    using System;
    using System.Collections.Generic;
    using System.Configuration;
    using System.Data;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using Microsoft.SqlServer.Management.Common;
    using Microsoft.SqlServer.Management.Smo;

    class Program
    {

        private static Server _server;
        private static string _logFile;
        private static readonly string TimeStamp = DateTime.Now.ToString(ConfigurationManager.AppSettings.Get("TimeStampFormat"), CultureInfo.InvariantCulture);
        static void Main(string[] args)
        {
            Dictionary<string, string> ftpFiles = new Dictionary<string, string>();
            _logFile = ConfigurationManager.AppSettings.Get("BackUpDirectory") + "\\Log_" + TimeStamp + ".txt";
            try
            {
                using (var sqlConnection = new SqlConnection(ConfigurationManager.ConnectionStrings["sqlConnection"].ConnectionString))
                {
                    //  ServerConnection serverConnection = new ServerConnection(ConfigurationManager.AppSettings.Get("ServerConnection"));
                    var serverConnection = new ServerConnection(sqlConnection);
                    serverConnection.StatementTimeout = 0; //no timeout
                    string[] excludeDbs = ConfigurationManager.AppSettings.Get("ExcludeDbs").Split(',');
                    string[] incDbs = ConfigurationManager.AppSettings.Get("IncrementalBackups").Split(',');
                    string[] defrags = ConfigurationManager.AppSettings.Get("Defrags").Split(',');

                    var filePath = ConfigurationManager.AppSettings.Get("BackUpDirectory");
                    var fullBackupDay = (DayOfWeek)Enum.ToObject(typeof(DayOfWeek),
                        Convert.ToInt16(ConfigurationManager.AppSettings.Get("FullBackupDay")));
                    DirectoryInfo directory = new DirectoryInfo(filePath);
                    _server = new Server(serverConnection);
                    char[] split = { ',' };
                    string[] includeFtpDbs = ConfigurationManager.AppSettings.Get("IncludeFtpDbs").Split(split, StringSplitOptions.RemoveEmptyEntries);
                    foreach (Database db in _server.Databases)
                    {
                        string zipFileName = string.Empty;
                        bool backupDb = excludeDbs.All(dbname => dbname != db.Name);//test for excluded dbs
                        bool isFull = true;
                        if (DateTime.Now.DayOfWeek.CompareTo(fullBackupDay) != 0)
                            if (incDbs.Any(dbname => dbname == db.Name))
                            {
                                isFull = false;
                            }
                        bool isFtp = false;
                        if (Convert.ToBoolean(ConfigurationManager.AppSettings.Get("SentToFtp")))
                        {
                            if (includeFtpDbs.Length == 0)//send all
                                isFtp = true;
                            else
                            {
                                if (includeFtpDbs.Any(dbname => dbname == db.Name))
                                {
                                    isFtp = true;
                                }
                            }
                        }
                        Boolean doDefrag = defrags.Any(dbname => dbname == db.Name);

                        if (db.ID >= 5 && backupDb)//if less than 5 then system db so do not backup
                        // if (db.ID == 5)
                        {
                            WriteLog("********************************************************************************");
                            WriteLog(db.Name.ToUpper() + "                                                            ");
                            WriteLog("********************************************************************************");
                            try
                            {
                                var toDirectory = new StringBuilder();
                                toDirectory.Append(filePath);
                                toDirectory.Append("\\");
                                toDirectory.Append(db.Name);
                                if (isFull)//do fullbackup
                                {
                                    toDirectory.Append("\\");
                                    toDirectory.Append("Full");
                                }

                                if (!Directory.Exists(toDirectory.ToString()))
                                    Directory.CreateDirectory(toDirectory.ToString());

                                var fileName = new StringBuilder(toDirectory.ToString());
                                fileName.Append("\\");
                                fileName.Append(db.Name);
                                fileName.Append("_");
                                fileName.Append(TimeStamp);
                                fileName.Append(".bak");

                                if (doDefrag)
                                {
                                    try
                                    {
                                        var cmd = new SqlCommand(string.Format("USE {0} ALTER INDEX PK_tblFile ON tblFile REBUILD ALTER FULLTEXT CATALOG DocumentCatalog REORGANIZE", db.Name), sqlConnection);
                                        cmd.CommandTimeout = 0;//no timeout
                                        cmd.CommandType = CommandType.Text;
                                        cmd.ExecuteNonQuery();
                                        WriteLog(fileName + " Index Defrag Succeeded ");
                                    }
                                    catch (Exception ex)
                                    {
                                        WriteLog(ex.Message + " Index Defrag Failed ");
                                    }
                                }

                                //backup db
                                var backup = new Backup();

                                // backup.CompressionOption = BackupCompressionOptions.On; dosnt work with sqlExpress
                                backup.Action = BackupActionType.Database;

                                if (isFull)//do fullbackup
                                    backup.Incremental = false;
                                else
                                    backup.Incremental = true;
                                backup.Database = db.Name;

                                BackupDeviceItem backupDeviceItem = new BackupDeviceItem(fileName.ToString(), DeviceType.File);
                                backup.Devices.Add(backupDeviceItem);
                                backup.SqlBackup(_server);
                                WriteLog(fileName + " Backup Succeeded ");

                                //zip file
                                zipFile(fileName.ToString(), out zipFileName);
                                WriteLog(fileName + " Zip Succeeded ");

                                //Remove .bak once zipped and any old files if config DeletePreviousFiles set to true
                                RemoveFiles(toDirectory, fileName, zipFileName, isFull || Array.Find(incDbs, o => o == db.Name) == null);

                            }
                            catch (Exception ex)
                            {
                                var err = new StringBuilder(ex.Message + " Backup Failed ");
                                var innerEx = ex.InnerException;
                                while (innerEx != null)
                                {
                                    err.Append(DateTime.Now.ToShortDateString() + ",\r\n");
                                    err.Append(innerEx.Source + ",\r\n");
                                    err.Append(innerEx.Message + ",\r\n");

                                    // Remember innermost for StackTrace
                                    ex = innerEx;

                                    // Find next
                                    innerEx = innerEx.InnerException;
                                }
                                err.Append("\n" + ex.StackTrace + "\n\n");
                                WriteLog(err.ToString());
                            }

                            if (isFtp && zipFileName != string.Empty)
                            {
                                FtpUpload(zipFileName);
                            }
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                using (var sw = new StreamWriter(_logFile, true))
                {
                    sw.WriteLine(ex.Message + DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture));
                }
            }
        }

        private static void RemoveFiles(StringBuilder toDirectory, StringBuilder fileName, string zipFileName, bool removeAll)
        {
            if (File.Exists(fileName.ToString()) && zipFileName != string.Empty)
            {
                try
                {
                    File.Delete(fileName.ToString());
                    WriteLog(fileName + " Remove Bak File Succeeded ");
                }
                catch (System.IO.IOException e)
                {
                    WriteLog(e.Message + " Remove Bak File Failed ");
                }
            }
            else if (zipFileName == string.Empty)
                zipFileName = fileName.ToString();

            //remove the previous zips if they exist
            if (Convert.ToBoolean(ConfigurationManager.AppSettings.Get("DeletePreviousFiles")))
            {
                if (removeAll)//only remove old zips if it is a fullbackup day
                {
                    toDirectory = toDirectory.Replace("\\Full", "");
                    foreach (FileInfo fileInfo in new DirectoryInfo(toDirectory.ToString()).GetFiles("*.*", SearchOption.AllDirectories))
                    {
                        if (fileInfo.Name.CompareTo(Path.GetFileName(zipFileName)) == -1)
                        {
                            fileInfo.Delete();
                            WriteLog(fileInfo.Name + " Removed ");
                        }
                    }
                }
            }
        }

        private static void zipFile(string fileName, out string zipFileName)
        {
            #region 7zip live code
            zipFileName = fileName.Replace(".bak", ".7z");
            var f = new FileInfo(fileName);


            var sb = new StringBuilder();
            sb.Append(string.Format(" a -t7z -mx9 -p{0} ", ConfigurationManager.AppSettings["ZipPassword"]));
            if (f.Length > 3221225472) //3GB
            {
                zipFileName = string.Empty;
                return;
            }
            else if (f.Length > 524228000) //500MB
                sb.Replace("-mx9", "-mx5");
            sb.Append(zipFileName);
            sb.Append(" ");
            sb.Append(fileName);

            //strSB = " a -t7z -mx9 C:\\SQLBackup\\Brighton\\test.7z C:\\SQLBackup\\Brighton\\Brighton_2010-04-19_1559.bak";

            ProcessStartInfo psiOpt = new ProcessStartInfo(@"C:\Program Files\7-Zip\7z.exe", sb.ToString());
            psiOpt.WindowStyle = ProcessWindowStyle.Normal;
            psiOpt.RedirectStandardOutput = true;
            psiOpt.UseShellExecute = false;
            psiOpt.CreateNoWindow = true;
            // Create the actual process object
            Process procCommand = Process.Start(psiOpt);
            // Receives the output of the Command Prompt
            StreamReader srIncoming = procCommand.StandardOutput;
            // Show the result
            WriteLog(srIncoming.ReadToEnd() + " 7-zip ");
            // Close the process
            procCommand.WaitForExit();

            #endregion

            #region Previous live code
            /*            zipFileName = fileName.Replace(".bak", ".zip");
            ZipOutputStream zipOut = new ZipOutputStream(File.Create(zipFileName));
            FileInfo fi = new FileInfo(fileName);
            ZipEntry entry = new ZipEntry(fi.Name);
            FileStream sReader = File.OpenRead(fileName);
            //byte[] buff = new byte[Convert.ToInt32(sReader.Length)];
            //sReader.Read(buff, 0, (int)sReader.Length);
            entry.DateTime = fi.LastWriteTime;
            entry.Size = sReader.Length;
            zipOut.PutNextEntry(entry);
            // Create a buffer for reading the files
            byte[] buf = new byte[32768];
            // Transfer bytes from the file to the ZIP file
            int len;
            while ((len = sReader.Read(buf, 0, buf.Length)) > 0)
            {
                zipOut.Write(buf, 0, len);
            }
            sReader.Close();
            //zipOut.Write(buff, 0, buff.Length);
            zipOut.CloseEntry();
            zipOut.Finish();
            zipOut.Close(); */

            #endregion

            #region Alternative Read
            //creates the directory structure inside zip
            ////create our zip file
            //ZipFile z = ZipFile.Create(zipFileName);
            ////initialize the file so that it can accept updates
            //z.BeginUpdate();

            ////add the file to the zip file
            //z.Add(fileName);

            ////commit the update once we are done
            //z.CommitUpdate();
            ////close the file
            //z.Close();
            #endregion
        }

        private static void FtpUpload(string filename)
        {
            FileInfo fileInfo = new FileInfo(filename);
            string uri = ConfigurationManager.AppSettings.Get("FtpServer") + fileInfo.Name;
            FtpWebRequest reqFTP;

            string ftpUserID = ConfigurationManager.AppSettings.Get("FtpUser");
            string ftpPassword = ConfigurationManager.AppSettings.Get("FtpPassword");
            if (ftpUserID != "" && ftpPassword != "")
            {
                // Create FtpWebRequest object from the Uri provided
                reqFTP = (FtpWebRequest)FtpWebRequest.Create(new Uri(uri));

                // Provide the WebPermission Credintials
                reqFTP.Credentials = new NetworkCredential(ftpUserID, ftpPassword);

                // By default KeepAlive is true, where the control connection is not closed
                // after a command is executed.
                reqFTP.KeepAlive = false;

                // Specify the command to be executed.
                reqFTP.Method = WebRequestMethods.Ftp.UploadFile;

                // Specify the data transfer type.
                reqFTP.UseBinary = true;

                // Notify the server about the size of the uploaded file
                reqFTP.ContentLength = fileInfo.Length;

                // The buffer size is set to 2kb
                int buffLength = 2048;
                byte[] buff = new byte[buffLength];
                int contentLen;

                // Opens a file stream (System.IO.FileStream) to read the file to be uploaded
                using (var fs = fileInfo.OpenRead())
                {
                    try
                    {
                        // Stream to which the file to be upload is written
                        using (var strm = reqFTP.GetRequestStream())
                        {

                            // Read from the file stream 2kb at a time
                            contentLen = fs.Read(buff, 0, buffLength);

                            // Till Stream content ends
                            while (contentLen != 0)
                            {
                                // Write Content from the file stream to the FTP Upload Stream
                                strm.Write(buff, 0, contentLen);
                                contentLen = fs.Read(buff, 0, buffLength);
                            }
                        }

                        WriteLog(filename + " Ftp Complete ");
                    }
                    catch (Exception ex)
                    {
                        WriteLog(ex.Message + " " + filename + " Ftp Failed ");
                    }
                }
            }
            else
            {
                WriteLog("No ftp credentials " + filename + " Ftp Failed ");
            }
        }

        private static void WriteLog(string msg)
        {
            try
            {
                Console.WriteLine(msg);

                if (Convert.ToBoolean(ConfigurationManager.AppSettings.Get("WriteToLog")))
                {
                    using (var sw = new StreamWriter(_logFile, true))
                    {
                        sw.WriteLine(msg + DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.Message);
            }

        }
    }

}


