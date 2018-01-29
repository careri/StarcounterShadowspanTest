using System;
using Starcounter;
using System.Linq;
using System.IO;
using Starcounter.Internal;

namespace StarcounterShadowspanTest
{
    class Program
    {
        static void Main()
        {
            using (var helper = new Helper())
            {
                helper.ShowHash();
                Db.Transact(helper.EnsureInstance);
                helper.ShowHash();
            }
        }

        class Helper : IDisposable
        {
            private readonly string m_logPath;
            private readonly StreamWriter m_writer;
            private readonly Lazy<string> m_dataDirectory;

            public Helper()
            {
                App = Application.Current;
                m_dataDirectory = new Lazy<string>(InitDataDirectory);
                var workDI = new DirectoryInfo(App.WorkingDirectory);
                m_logPath = Path.Combine(workDI.FullName, "StarcounterShadowspanTest.log");
                var logFI = new FileInfo(m_logPath);
                m_writer = new StreamWriter(logFI.Open(FileMode.Create, FileAccess.Write, FileShare.Read));
                m_writer.AutoFlush = true;
                Console.WriteLine($"App logfile: {m_logPath}");
            }

            private string InitDataDirectory()
            {
                // Get the config for the current database
                int port;

                if (!int.TryParse(Environment.GetEnvironmentVariable("StarcounterServerPersonalPort"), out port))
                {
                    port = 8181;
                }
                var resp = Http.GET($"http://localhost:{port}/api/databases/{StarcounterEnvironment.DatabaseNameLower}/config");
                var db = new Starcounter.Server.Rest.Representations.JSON.Database();
                db.PopulateFromJson(resp.Body);
                return db.Configuration.DataDirectory;
            }

            public Application App { get; }

            public Data Instance { get; private set; }

            public DirectoryInfo DataDirectory => new DirectoryInfo(m_dataDirectory.Value);

            public void Dispose()
            {
                Write($"Data: {Instance}");
                m_writer.Dispose();
                File.WriteAllText(m_logPath + ".done", DateTime.Now.ToString());
            }

            /// <summary>
            /// Displays the SHA256 of the log files, this should be possible since starcounter doesn't lock the files for reading.
            /// </summary>
            /// <param name="current"></param>
            public void ShowHash()
            {
                try
                {
                    var di = DataDirectory;

                    using (var sha256 = System.Security.Cryptography.SHA256.Create())
                    {
                        foreach (var logFI in di.GetFiles("*.*log"))
                        {
                            try
                            {
                                using (var stream = logFI.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    var hash = sha256.ComputeHash(stream);
                                    Write($"[{logFI.FullName}] {Convert.ToBase64String(hash)}");
                                }
                            }
                            catch (Exception fiEX)
                            {
                                Write($"Failed to compute file hash: {logFI.FullName}");
                                Write(fiEX.ToString());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Write($"Failed to compute hash");
                    Write(ex.ToString());
                }
            }

            /// <summary>
            /// Reads or creates an instance
            /// </summary>
            public void EnsureInstance()
            {
                Instance = Db.SQL<Data>("SELECT d FROM StarcounterShadowspanTest.Data d").FirstOrDefault();

                if (Instance == null)
                {
                    Write("Creating data");
                    Instance = new Data();
                }
                else
                {
                    Write($"Read data: {Instance}");
                }


            }

            private void Write(string v)
            {
                Console.WriteLine(v);
                m_writer.WriteLine(v);
            }
        }
    }

    [Database]
    public class Data
    {
        public Data()
        {
            Created = DateTime.Now;
        }

        public DateTime Created { get; }

        public override string ToString()
        {
            return $"[{this.GetObjectID()}] Created: {Created}";
        }
    }
}