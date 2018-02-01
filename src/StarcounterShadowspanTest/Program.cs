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
                Db.Transact(helper.EnsureInstance);

                // Print the tota
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

            public int Count { get; private set; }

            public bool FirstRun { get; private set; }

            public DirectoryInfo DataDirectory => new DirectoryInfo(m_dataDirectory.Value);

            public void Dispose()
            {
                Write($"Data: {Instance}, Count: {Count}");
                m_writer.Dispose();
                File.WriteAllText(m_logPath + ".done", Count.ToString());
            }

            /// <summary>
            /// Reads or creates an instance
            /// </summary>
            public void EnsureInstance()
            {
                using (var sqlE = Db.SQL<Data>("SELECT d FROM StarcounterShadowspanTest.Data d ORDER BY d.Created").GetEnumerator())
                {
                    int count = 0;

                    while (sqlE.MoveNext())
                    {
                        if (count == 0)
                        {
                            Instance = sqlE.Current;
                        }
                        count++;
                    }
                    if (Instance == null)
                    {
                        FirstRun = true;
                        Write("[FirstRun] Creating data");
                        Instance = new Data();
                        count++;
                    }
                    else
                    {
                        Write($"[NotFirstRun] Read first data: {Instance}");
                        var newInstance = new Data();
                        Write($"[NotFirstRun] Created instance #{++count}: {newInstance}");
                    }
                    Count = count;
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