using System;
using Starcounter;
using System.Linq;
using System.IO;

namespace StarcounterShadowspanTest
{
    class Program
    {
        static void Main()
        {
            using (var helper = new Helper())
            {
                helper.ShowHash(Application.Current);
                Db.Transact(helper.EnsureInstance);
            }
        }

        class Helper : IDisposable
        {
            private readonly string m_logPath;
            private readonly StreamWriter m_writer;

            public Helper()
            {
                var assDI = new FileInfo(new Uri(GetType().Assembly.Location).LocalPath).Directory;
                m_logPath = Path.Combine(assDI.FullName, "StarcounterShadowspanTest.log");
                var logFI = new FileInfo(m_logPath);
                m_writer = new StreamWriter(logFI.Open(FileMode.Create, FileAccess.Write, FileShare.Read));
                Console.WriteLine(m_logPath);
            }

            public Data Instance { get; private set; }

            public void Dispose()
            {
                m_writer.Dispose();
                File.WriteAllText(m_logPath + ".done", DateTime.Now.ToString());
            }

            /// <summary>
            /// Displays the SHA256 of the log files, this should be possible since starcounter doesn't lock the files for reading.
            /// </summary>
            /// <param name="current"></param>
            public void ShowHash(Application app)
            {
                try
                {
                    // Get the config for the current database
                    int port;

                    if (!int.TryParse(Environment.GetEnvironmentVariable("StarcounterServerPersonalPort"), out port))
                    {
                        port = 8181;
                    }
                    var db = Self.GET<Starcounter.Server.Rest.Representations.JSON.Database>($"http://localhost:{8181}/api/databases/{app.Name}/config");
                    var di = new DirectoryInfo(db.Configuration.DataDirectory);

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
                    Write($"Read data: {Instance.GetObjectID()}, {Instance.Created}");
                }


            }

            private void Write(string v)
            {
                Console.WriteLine("Creating data");
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