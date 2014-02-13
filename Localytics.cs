using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Windows.Security.Cryptography;
using MonoMac.Foundation;

namespace Localytics
{
    public class LocalyticsSession
    {
        #region library constants
        private const int maxStoredSessions = 10;
        private const int maxNameLength = 100;
        private const string libraryVersion = "xam_mac_1.0";
        private const string directoryName = "localytics";
        private const string sessionFilePrefix = "s_";
        private const string uploadFilePrefix = "u_";
        private const string metaFileName = "m_meta";

        private const string serviceURLBase = "http://analytics.localytics.com/api/v2/applications/";
        #endregion

        #region private members
        private string sessionUUID;
        private string sessionFilename;
        private bool isSessionOpen = false;
        private bool isSessionClosed = false;
        private double sessionStartTime = 0;
        #endregion

        #region static members
        private static bool isUploading = false;
        private static DirectoryInfo localStorage = null;
        private string appKey;
        #endregion

        #region private methods

        #region Storage
        /// <summary>
        /// Caches the reference to the app's isolated storage
        /// </summary>
        private static DirectoryInfo GetStore()
        {
            if (localStorage == null)
            {
                var appSupportDirectory = NSSearchPath.GetDirectories (NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User, true) [0];
                var folderName = Path.Combine (appSupportDirectory, directoryName);
                if(!Directory.Exists(folderName))
                    Directory.CreateDirectory (folderName);
                localStorage = new DirectoryInfo (folderName);
            }

            return localStorage;
        }

        /// <summary>
        /// Tallies up the number of files whose name starts w/ sessionFilePrefix in the localytics dir
        /// </summary>
        private static int GetNumberOfStoredSessions()
        {
            var store = GetStore();
            try
            {
                var files = store.EnumerateFiles();
                return files.Count(x => x.Name.StartsWith(sessionFilePrefix));
            }
            catch (FileNotFoundException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Gets a stream pointing to the requested file.  If the file does not exist it is created.
        /// </summary>
        /// <param name="filename">Name of the file (w/o directory) to create</param>
        private static Stream GetStreamForFile(string filename)
        {
            var file = File.Open(Path.Combine (GetStore().FullName, filename), FileMode.OpenOrCreate);
            return file;
        }

        /// <summary>
        /// Appends a string to the end of a text file.
        /// </summary>
        /// <param name="text">Text to append</param>
        /// <param name="filename">Name of file to append to</param>
        private static void AppendTextToFile(string text, string filename)
        {
            using (var file = GetStreamForFile(filename))
            {
                file.Seek(0, SeekOrigin.End);
                using (var writer = new StreamWriter(file))
                {
                    writer.Write(text);
                    writer.Flush();
                }
            }
        }

        /// <summary>
        /// Reads a file and returns its contents as a string
        /// </summary>
        /// <param name="filename">file to read (w/o directory prefix)</param>
        /// <returns>the contents of the file</returns>
        private static string GetFileContents(string filename)
        {
            using (var file = File.OpenRead(Path.Combine(GetStore().FullName, filename)))
            {
                using (var reader = new StreamReader(file))
                {
                    return reader.ReadToEnd();
                }
            }
        }
        #endregion

        #region Upload

        /// <summary>
        /// Goes through all the upload files and collects their contents for upload
        /// </summary>
        /// <returns>A string containing the concatenated </returns>
        private static string GetUploadContents()
        {
            StringBuilder contents = new StringBuilder();
            var files = GetStore().EnumerateFiles();
            foreach (var file in files.Where(x => x.Name.StartsWith(uploadFilePrefix)))
            {
                contents.Append(GetFileContents(file.Name));
            }

            return contents.ToString();
        }

        /// <summary>
        /// loops through all the files in the directory deleting the upload files
        /// </summary>
        private static void DeleteUploadFiles()
        {
            var files = GetStore().EnumerateFiles();
            foreach (var file in files.Where(x => x.Name.StartsWith(uploadFilePrefix)))
            {
                file.Delete();
            }
        }

        /// <summary>
        /// Rename any open session files. This way events recorded during uploaded get written safely to disk
        /// and threading difficulties are missed.
        /// </summary>
        private void RenameOrAppendSessionFiles()
        {
            var files = GetStore().EnumerateFiles();
            bool addedHeader = false;

            string destinationFileName = uploadFilePrefix + Guid.NewGuid().ToString();
            foreach (var file in files.Where(x => x.Name.StartsWith(sessionFilePrefix)))
            {
                // Any time sessions are appended, an upload header should be added. But only one is needed regardless of number of files added
                if (!addedHeader)
                {
                    AppendTextToFile(GetBlobHeader(), destinationFileName);
                    addedHeader = true;
                }
                AppendTextToFile(GetFileContents(file.Name), destinationFileName);
                file.Delete();
            }
        }

        /// <summary>
        /// Runs on a seperate thread and is responsible for renaming and uploading files as appropriate
        /// </summary>
        private void BeginUpload()
        {
            SafeExecute(() =>
            {
                LogMessage("Beginning upload");
                RenameOrAppendSessionFiles();

                // begin the upload
                string url = serviceURLBase + this.appKey + "/uploads";
                LogMessage("Uploading to: " + url);

                HttpWebRequest myRequest = (HttpWebRequest)WebRequest.Create(url);
                myRequest.Method = "POST";
                myRequest.ContentType = "application/x-gzip";
                myRequest.BeginGetRequestStream(HttpRequestCallback, myRequest);
            });
        }

        private static void HttpRequestCallback(IAsyncResult asynchronousResult)
        {
            SafeExecute(() =>
            {
                HttpWebRequest request = (HttpWebRequest)asynchronousResult.AsyncState;
                using (Stream postStream = request.EndGetRequestStream(asynchronousResult))
                {
                    String contents = GetUploadContents();
                    byte[] byteArray = Encoding.UTF8.GetBytes(contents);
                    using (var zips = new GZipStream(postStream, CompressionMode.Compress))
                    {
                        zips.Write(byteArray, 0, byteArray.Length);
                    }
                }

                request.BeginGetResponse(GetResponseCallback, request);
            });
        }

        private static void GetResponseCallback(IAsyncResult asynchronousResult)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)asynchronousResult.AsyncState;
                using (HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(asynchronousResult))
                {
                    using (Stream streamResponse = response.GetResponseStream())
                    {
                        using (StreamReader streamRead = new StreamReader(streamResponse))
                        {
                            string responseString = streamRead.ReadToEnd();

                            LogMessage("Upload complete. Response: " + responseString);
                            DeleteUploadFiles();
                        }
                    }
                }
            }
            catch (WebException e)
            {
                Debug.WriteLine("WebException raised.");
                Debug.WriteLine("\n{0}", e.Message);
                Debug.WriteLine("\n{0}", e.Status);
            }
            catch (Exception e)
            {
                Debug.WriteLine("Exception raised!");
                Debug.WriteLine("Message : " + e.Message);
            }
            finally
            {
                isUploading = false;
            }
        }

        #endregion

        #region Data Lookups

        /// <summary>
        /// Gets the sequence number for the next upload blob. 
        /// </summary>
        /// <returns>Sequence number as a string</returns>
        private static string GetSequenceNumber()
        {
            // open the meta file and read the next sequence number.
            FileInfo file = FileExists(GetStore(), metaFileName);
            if (file == null || IsFileEmpty(file))
            {
                SetNextSequenceNumber("1");
                return "1";
            }
            string sequenceNumber;

            using (var stream = file.OpenRead())
            {
                using (TextReader reader = new StreamReader(stream))
                {
                    string installID = reader.ReadLine();
                    sequenceNumber = reader.ReadLine();
                }
            }

            return sequenceNumber;
        }

        /// <summary>
        /// Sets the next sequence number in the metadata file. Creates the file if its not already there
        /// </summary>
        /// <param name="number">Next sequence number</param>
        private static void SetNextSequenceNumber(string number)
        {
            FileInfo file = FileExists(GetStore(), metaFileName);
            if (file == null || IsFileEmpty(file))
            {
                // Create a new metadata file consisting of a unique installation ID and a sequence number
                AppendTextToFile(Guid.NewGuid().ToString() + Environment.NewLine + number, metaFileName);
            }
            else
            {
                string installId;
                using (var filestream = file.OpenRead())
                {
                    using (TextReader reader = new StreamReader(filestream))
                    {
                        installId = reader.ReadLine();
                    }
                }

                // overwite the file w/ the old install ID and the new sequence number
                using (var fileOut = file.Open(FileMode.Create, FileAccess.Write))
                {
                    using (TextWriter writer = new StreamWriter(fileOut))
                    {
                        writer.WriteLine(installId);
                        writer.Write(number);
                        writer.Flush();
                    }
                }
            }
        }

        private static FileInfo FileExists(DirectoryInfo folder, string filename)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(folder.FullName, filename));
                if(fi.Exists)
                    return fi;
                else
                    throw new FileNotFoundException();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static bool IsFileEmpty(FileInfo file)
        {
            using (var stream = file.OpenRead())
            {
                return stream.Length == 0;
            }
        }

        /// <summary>
        /// Gets the timestamp of the storage file containing the sequence numbers. This allows processing to
        /// ignore duplicates or identify missing uploads
        /// </summary>
        /// <returns>A string containing a Unixtime</returns>
        private static string GetPersistStoreCreateTime()
        {
            FileInfo file = FileExists(GetStore(), metaFileName);
            DateTimeOffset dto;
            if (file == null || IsFileEmpty(file))
            {
                SetNextSequenceNumber("1");
                dto = DateTimeOffset.MinValue;
            }
            else
            {
                dto = file.CreationTime;
            }
            int secondsSinceUnixEpoch = (int)Math.Round((dto.DateTime - new DateTime(1970, 1, 1).ToLocalTime()).TotalSeconds);
            return secondsSinceUnixEpoch.ToString();
        }

        /// <summary>
        /// Gets the Installation ID out of the metadata file
        /// </summary>
        private static string GetInstallId()
        {
            using (var file = File.OpenRead(Path.Combine(GetStore().FullName, metaFileName)))
            {
                using (TextReader reader = new StreamReader(file))
                {
                    return reader.ReadLine();
                }
            }
        }

        private static string _version;
        /// <summary>
        /// Retreives the Application Version from the metadata
        /// </summary>
        /// <returns>User generated app version</returns>
        public static string GetAppVersion()
        {
            if (string.IsNullOrEmpty(_version))
                _version = (NSString)NSBundle.MainBundle.InfoDictionary ["CFBundleShortVersionString"];
            return _version;
        }

        /// <summary>
        /// Gets the current date/time as a string which can be inserted in the DB
        /// </summary>
        /// <returns>A formatted string with date, time, and timezone information</returns>
        private static string GetDatestring()
        {
            return DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'");
        }

        /// <summary>
        /// Gets the current time in unixtime
        /// </summary>
        /// <returns>The current time in unixtime</returns>
        private static double GetTimeInUnixTime()
        {
            return Math.Round(((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds), 0);
        }
        #endregion

        /// <summary>
        /// Constructs a blob header for uploading
        /// </summary>
        /// <returns>A string containing a blob header</returns>
        private string GetBlobHeader()
        {
            StringBuilder blobString = new StringBuilder();

            //{ "dt":"h",  // data type, h for header
            //  "pa": int, // persistent store created at
            //  "seq": int,  // blob sequence number, incremented on each new blob, 
            //               // remembered in the persistent store
            //  "u": string, // A unique ID for the blob. Must be the same if the blob is re-uploaded!
            //  "attrs": {
            //    "dt": "a" // data type, a for attributes
            //    "au":string // Localytics Application Id
            //    "du":string // Device UUID
            //    "s":boolean // Whether the app has been stolen (optional)
            //    "j":boolean // Whether the device has been jailbroken (optional)
            //    "lv":string // Library version
            //    "av":string // Application version
            //    "dp":string // Device Platform
            //    "dll":string // Locale Language (optional)
            //    "dlc":string // Locale Country (optional)
            //    "nc":string // Network Country (iso code) (optional)
            //    "dc":string // Device Country (iso code) (optional)
            //    "dma":string // Device Manufacturer (optional)
            //    "dmo":string // Device Model
            //    "dov":string // Device OS Version
            //    "nca":string // Network Carrier (optional)
            //    "dac":string // Data Connection Type (optional)
            //    "mnc":int // mobile network code (optional)
            //    "mcc":int // mobile country code (optional)
            //    "tdid":string // Telephony Device Id (meid or imei) (optional)
            //    "wmac":string // hashed wifi mac address (optional)
            //    "emac":string // hashed ethernet mac address (optional)
            //    "bmac":string // hashed bluetooth mac address (optional)
            //    "iu":string // install id
            //    "udid":string } } // client side hashed version of the udid

            blobString.Append("{\"dt\":\"h\",");
            blobString.Append("\"pa\":" + GetPersistStoreCreateTime() + ",");

            string sequenceNumber = GetSequenceNumber();
            blobString.Append("\"seq\":" + sequenceNumber + ",");
            SetNextSequenceNumber((int.Parse(sequenceNumber) + 1).ToString());

            blobString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
            blobString.Append("\"attrs\":");
            blobString.Append("{\"dt\":\"a\",");
            blobString.Append("\"au\":\"" + this.appKey + "\",");
            blobString.Append("\"du\":\"" + GetDeviceInfo() + "\",");
            blobString.Append("\"lv\":\"" + libraryVersion + "\",");
            blobString.Append("\"av\":\"" + GetAppVersion() + "\",");
            blobString.Append("\"dp\":\"Mac OS X\",");
            blobString.Append("\"dll\":\"" + CultureInfo.CurrentCulture.TwoLetterISOLanguageName + "\",");
            blobString.Append("\"dmo\":\"" + "Mac Device" + "\",");
            blobString.Append("\"dov\":\"" + NSProcessInfo.ProcessInfo.OperatingSystemVersionString + "\",");
            blobString.Append("\"iu\":\"" + GetInstallId() + "\"");
            blobString.Append("}}");
            blobString.Append(Environment.NewLine);

            return blobString.ToString();
        }

        private static string GetDeviceInfo()
        {
            var userGuid = NSUserDefaults.StandardUserDefaults.StringForKey ("UserGUID");
            if (userGuid == null || string.IsNullOrEmpty (userGuid))
            {
                userGuid = Guid.NewGuid ().ToString ();
                NSUserDefaults.StandardUserDefaults.SetString (userGuid, "UserGUID");
            }

            return userGuid;
        }


        /// <summary>
        /// Formats an input string for YAML
        /// </summary>       
        /// <returns>string sorrounded in quotes, with dangerous characters escaped</returns>
        private static string EscapeString(string input)
        {
            string escapedSlahes = input.Replace("\\", "\\\\");
            return "\"" + escapedSlahes.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Outputs a message to the debug console
        /// </summary>
        private static void LogMessage(string msg)
        {
            Debug.WriteLine("(localytics) " + msg);
        }

        private static void SafeExecute(Action action)
        {
            SafeExecuteInternal(() => { action(); return 0; });
        }

        private static T SafeExecuteInternal<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception e)
            {
                LogMessage("Swallowing exception: " + e.Message);
            }

            return default(T);
        }

        #endregion

        #region public methods
        /// <summary>
        /// Creates a Localytics Session object
        /// </summary>
        /// <param name="appKey"> The key unique for each application generated at www.localytics.com</param>
        public LocalyticsSession(string appKey)
        {
            this.appKey = appKey;

            // Store the time and sequence number 
        }

        /// <summary>
        /// Opens or resumes the Localytics session.
        /// </summary>
        public void Open()
        {
            SafeExecute(() =>
            {
                if (this.isSessionOpen)
                {
                    LogMessage("Session is already opened");
                    return;
                }

                if (appKey == null)
                {
                    LogMessage("Invalid Localytics App Key specified. Session not opened");
                    return;
                }

                if (this.isSessionClosed)
                {
                    LogMessage("Previous session has been closed. Trying to opening a new session");
                }

                if (GetNumberOfStoredSessions() > maxStoredSessions)
                {
                    LogMessage("Local stored session count exceeded");
                    return;
                }

                this.sessionUUID = Guid.NewGuid().ToString();
                this.sessionFilename = sessionFilePrefix + this.sessionUUID; this.sessionStartTime = GetTimeInUnixTime();
                this.isSessionClosed = false;

                // Format of an open session:
                //{ "dt":"s",       // This is a session blob
                //  "ct": long,     // seconds since Unix epoch
                //  "u": string     // A unique ID attached to this session 
                //  "nth": int,     // This is the nth session on the device. (not required)
                //  "new": boolean, // New vs returning (not required)
                //  "sl": long,     // seconds since last session (not required)
                //  "lat": double,  // latitude (not required)
                //  "lng": double,  // longitude (not required)
                //  "c0" : string,  // custom dimensions (not required)
                //  "c1" : string,
                //  "c2" : string,
                //  "c3" : string }

                StringBuilder openString = new StringBuilder();
                openString.Append("{\"dt\":\"s\",");
                openString.Append("\"ct\":" + GetTimeInUnixTime().ToString() + ",");
                openString.Append("\"u\":\"" + this.sessionUUID + "\"");
                openString.Append("}");
                openString.Append(Environment.NewLine);

                AppendTextToFile(openString.ToString(), this.sessionFilename);

                LogMessage("Session opened");
                this.isSessionOpen = true;
            });
        }

        /// <summary>
        /// Closes the Localytics session.
        /// </summary>
        public void Close()
        {
            SafeExecute(() =>
                    {
                        if (this.isSessionOpen == false || this.isSessionClosed == true)
                        {
                            LogMessage("Session not closed because it is either not open or already closed");
                            return;
                        }


                        //{ "dt":"c", // close data type
                        //  "u":"abec86047d-ae51", // unique id for the close
                        //  "ss": session_start_time, // time the session was started
                        //  "su":"696c44ebf6f",   // session uuid
                        //  "ct":1302559195,  // client time
                        //  "ctl":114,  // session length (optional)
                        //  "cta":60, // active time length (optional)
                        //  "fl":["1","2","3","4","5","6","7","8","9"], // Flows (optional)
                        //  "lat": double,  // lat (optional)
                        //  "lng": double,  // lng (optional)
                        //  "c0" : string,  // custom dimensions (otpinal)
                        //  "c1" : string,
                        //  "c2" : string,
                        //  "c3" : string }

                        StringBuilder closeString = new StringBuilder();
                        closeString.Append("{\"dt\":\"c\",");
                        closeString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
                        closeString.Append("\"ss\":" + this.sessionStartTime.ToString() + ",");
                        closeString.Append("\"su\":\"" + this.sessionUUID + "\",");
                        closeString.Append("\"ct\":" + GetTimeInUnixTime().ToString());
                        closeString.Append("}");
                        closeString.Append(Environment.NewLine);
                        AppendTextToFile(closeString.ToString(), this.sessionFilename); // the close blob

                        this.isSessionOpen = false;
                        this.isSessionClosed = true;
                        LogMessage("Session closed");
                    });
        }

        /// <summary>
        /// Creates a new thread which collects any files and uploads them. Returns immediately if an upload
        /// is already happenning.
        /// </summary>
        public void Upload()
        {
            SafeExecute(() =>
            {
                if (isUploading)
                {
                    return;
                }

                isUploading = true;

                // Do all the upload work on a seperate thread.
                Task.Run((Action)BeginUpload);
                LogMessage("Done uploading!!!");
            });
        }

        /// <summary>
        /// Records a specific event as having occured and optionally records some attributes associated with this event.
        /// This should not be called inside a loop. It should not be used to record personally identifiable information
        /// and it is best to define all your event names rather than generate them programatically.
        /// </summary>
        /// <param name="eventName">The name of the event which occured. E.G. 'button pressed'</param>
        /// <param name="attributes">Key value pairs that record data relevant to the event.</param>
        public void TagEvent(string eventName, Dictionary<string, string> attributes = null)
        {
            SafeExecute(() =>
            {
                if (this.isSessionOpen == false)
                {
                    LogMessage("Event not tagged because session is not open.");
                    return;
                }

                //{ "dt":"e",  // event data time
                //  "ct":1302559181,   // client time
                //  "u":"48afd8beebd3",   // unique id
                //  "su":"696c44ebf6f",   // session id
                //  "n":"Button Clicked",  // event name
                //  "lat": double,   // lat (optional)
                //  "lng": double,   // lng (optional)
                //  "attrs":   // event attributes (optional)
                //  {
                //      "Button Type":"Round"
                //  },
                //  "c0" : string, // custom dimensions (optional)
                //  "c1" : string,
                //  "c2" : string,
                //  "c3" : string }


                StringBuilder eventString = new StringBuilder();
                eventString.Append("{\"dt\":\"e\",");
                eventString.Append("\"ct\":" + GetTimeInUnixTime().ToString() + ",");
                eventString.Append("\"u\":\"" + Guid.NewGuid().ToString() + "\",");
                eventString.Append("\"su\":\"" + this.sessionUUID + "\",");
                eventString.Append("\"n\":" + EscapeString(eventName));

                if (attributes != null)
                {
                    eventString.Append(",\"attrs\": {");
                    bool first = true;
                    foreach (string key in attributes.Keys)
                    {
                        if (!first) { eventString.Append(","); }
                        eventString.Append(EscapeString(key ?? string.Empty) + ":" + EscapeString(attributes[key] ?? string.Empty));
                        first = false;
                    }
                    eventString.Append("}");
                }
                eventString.Append("}");
                eventString.Append(Environment.NewLine);

                AppendTextToFile(eventString.ToString(), this.sessionFilename); // the close blob
                LogMessage("Tagged event: " + EscapeString(eventName));
            });
        }
        #endregion
    }
}






