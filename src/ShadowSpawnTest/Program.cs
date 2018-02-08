using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowSpawnTest
{
    class Program
    {
        private static readonly string s_workingDir;
        private static readonly int s_verbosity = 2;
        private static readonly bool s_isTrace;
        private static readonly CancellationTokenSource s_cancelSrc = new CancellationTokenSource();
        private static int s_indent;
        private static bool s_hasConsole;
        private static bool s_redirectStdErr;

        static Program()
        {
            s_workingDir = System.IO.Directory.GetCurrentDirectory();
            s_isTrace = Debugger.IsAttached;
            try
            {
                Console.TreatControlCAsInput = true;
                s_hasConsole = true;
            }
            catch
            {
            }
        }

        public class Megabyte
        {
            public const int One = 1024 * 1024;

            public static int ToBytes(int mb) => One * mb;
        }

        static void Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                s_redirectStdErr = "redirect".Equals(args[0]);
            }

            if (s_hasConsole)
            {
                Console.WriteLine("Hit CTRL+C to quit"); 
            }
            var t = Task.Factory.StartNew(Test.RunTest, TaskCreationOptions.LongRunning);

            if (s_hasConsole)
            {
                do
                {
                    var key = Console.ReadKey();

                    if (key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control)
                    {
                        Warn("!! Stopping after current loop !!");
                        s_cancelSrc.Cancel();
                        break;
                    }
                } while (true); 
            }


            t.Wait();
        }


        private class Test : IDisposable
        {
            public static void RunTest()
            {
                using (var t = new Test(256, 2))
                {
                    t.Run();
                }
            }

            private readonly Dictionary<int, FileStream> m_streams =
                new Dictionary<int, FileStream>();

            private readonly BlockHelper m_helper;
            
            private readonly byte[] m_buffer = new byte[Megabyte.One];
            private readonly Random rnd = new Random();            

            /// <summary>
            /// 
            /// </summary>
            /// <param name="sizeMB">The file size in megabytes</param>
            /// <param name="count">The number of files</param>
            public Test(int sizeMB, int count)
            {
                SizeMB = sizeMB;
                SizeBytes = Megabyte.ToBytes(sizeMB);
                Count = count;
                m_helper = new BlockHelper(this);
            }
            public int SizeMB { get; }
            public int SizeBytes { get; }
            public int Count { get; }

            private void Run()
            {
                // Create x files of y mb  files and write to their streams
                var assFI = new FileInfo(new Uri(typeof(Program).Assembly.Location).LocalPath);
                var shadowspawn = new FileInfo(Path.Combine(assFI.DirectoryName, "shadowspawn.exe"));

                if (!shadowspawn.Exists)
                {
                    Error($"[Shadowspawn] {shadowspawn.FullName} doesn't exists", -1);
                }


                Section("Clean up");
                RecreateDirs(ShadowBackupDI, DirectBackupDI, DataDI);

                // Setup the files
                Section("Creating files");

                for (int i = 0; i < Count; i++)
                {
                    var fi = GetFile(DataDI, SizeMB, i);
                    fi.Directory.Create();


                    // Using the file flag no buffering and write through seems to fool shadowspawn
                    FileOptions FileFlagNoBuffering = (FileOptions)0x20000000;
                    var stream = new FileStream(fi.FullName, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.WriteThrough | FileFlagNoBuffering);
                    fi.Refresh();
                    Info($"[{i}] Creating: {fi.FullName}");
                    Trace($"[{i}] LastWrite: {fi.LastWriteTime}");
                    stream.SetLength(SizeBytes);
                    m_streams[i] = stream;
                }

                var blockCount = SizeMB * Count;

                Section($"Writing blocks 0 -> {blockCount}");

                using (var md5 = MD5Cng.Create())
                {
                    for (int i = 0; i < blockCount; i++)
                    {
                        if (s_cancelSrc.IsCancellationRequested)
                        {
                            break;
                        }
                        s_indent = 0;
                        Section($"Loop#{i}");
                        s_indent++;

                        rnd.NextBytes(m_buffer);
                        var dataHash = Convert.ToBase64String(md5.ComputeHash(m_buffer));
                        Trace($"[Memory] Hash: {dataHash}");

                        // Update the block information
                        m_helper.Update(i);
                        WriteStream();

                        // Read the hash to make sure properly written
                        ReadFile(DataDI);
                        var writtenHash = Convert.ToBase64String(md5.ComputeHash(m_buffer));
                        Trace($"[DataDI] Hash: {dataHash}");

                        if (!string.Equals(dataHash, writtenHash))
                        {
                            Error($"[Write], hash mismatch: {dataHash} != {writtenHash}", -11);
                        }

                        //// Plain copy
                        //DirectCopy();

                        //ReadFile(DirectBackupDI);
                        //var directHash = Convert.ToBase64String(md5.ComputeHash(m_buffer));
                        //Trace($"[DirectCopy] Hash: {directHash}");

                        //if (!string.Equals(dataHash, directHash))
                        //{
                        //    Error($"[DirectCopy], hash mismatch: {dataHash} != {directHash}", -12);
                        //}

                        // Shadow Copy
                        MakeShadowCopy(shadowspawn);

                        // Now read the block from backup
                        ReadFile(ShadowBackupDI);
                        var shadowHash = Convert.ToBase64String(md5.ComputeHash(m_buffer));
                        Trace($"[ShadowCopy] Hash: {shadowHash}");

                        if (!string.Equals(dataHash, shadowHash))
                        {
                            Error($"[ShadowCopy], hash mismatch: {dataHash} != {shadowHash}", -13);
                        }
                    }
                }
            }

            private FileStream WriteStream()
            {
                Section($"[Write] Block: {m_helper.BlockIndex}");
                FileStream stream;

                if (!m_streams.TryGetValue(m_helper.StreamIndex, out stream))
                {
                    Error($"[Write] No file with index: {m_helper.StreamIndex}", -666);
                }

                Info($"[Write] [{stream.Name}] StreamBlock: {m_helper.StreamIndex}.{m_helper.StreamBlockIndex} @ {m_helper.StreamOffset}");
                stream.Seek(m_helper.StreamOffset, SeekOrigin.Begin);
                stream.Write(m_buffer, 0, m_buffer.Length);
                return stream;
            }

            private void ReadFile(DirectoryInfo di)
            {
                Section($"[Read] Block: {m_helper.BlockIndex}");
                var fi = GetFile(di, SizeMB, m_helper.StreamIndex);

                using (var stream = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ReadStream(stream);
                }
            }

            private void ReadStream(FileStream stream)
            { 
                stream.Seek(m_helper.StreamOffset, SeekOrigin.Begin);
                Info($"[Read] [{stream.Name}] StreamBlock: {m_helper.StreamIndex}.{m_helper.StreamBlockIndex} @ {m_helper.StreamOffset}");
                stream.Read(m_buffer, 0, m_buffer.Length);
            }

            public void Dispose()
            {
                foreach (var s in m_streams.Values)
                {
                    try
                    {
                        s.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private class BlockHelper
        {
            private readonly Test m_test;

            public BlockHelper(Test test)
            {
                m_test = test;
            }

            public int BlockIndex { get; private set; }

            /// <summary>
            /// The index of the stream
            /// </summary>
            public int StreamIndex { get; private set; }

            /// <summary>
            /// The block in the stream
            /// </summary>
            public int StreamBlockIndex { get; private set; }

            /// <summary>
            /// The offset in bytes into the stream
            /// </summary>
            public int StreamOffset { get; private set; }

            /// <summary>
            /// Calculates the offset based on the given block index
            /// </summary>
            /// <param name="blockIndex"></param>
            internal void Update(int blockIndex)
            {
                BlockIndex = blockIndex;

                // Calculate the stream
                StreamIndex = (blockIndex / m_test.SizeMB);

                // Calculate the block index in the stream 
                StreamBlockIndex = blockIndex - (StreamIndex * m_test.SizeMB);

                // Convert the block to bytes
                StreamOffset = Megabyte.ToBytes(StreamBlockIndex);
            }
        }

        private static void RecreateDirs(params DirectoryInfo[] dis)
        {
            if (dis != null)
            {
                foreach (var di in dis)
                {
                    try
                    {
                        if (di != null)
                        {
                            di.Refresh();

                            if (di.Exists)
                            {
                                di.Delete(true);
                            }                            
                        }
                    }
                    catch
                    {
                    }
                    di.Create();
                }
            }
        }

        private static void Section(string v)
        {
            Info($"{Environment.NewLine}{"*".PadRight(s_indent + 1, '*')} {v}{Environment.NewLine}");
        }

        private static DirectoryInfo DataDI => new DirectoryInfo(Path.Combine(s_workingDir, "Data"));

        private static DirectoryInfo ShadowBackupDI => new DirectoryInfo(Path.Combine(s_workingDir, "ShadowBackup"));
        private static DirectoryInfo DirectBackupDI => new DirectoryInfo(Path.Combine(s_workingDir, "Backup"));

        private static void MakeShadowCopy(FileInfo exe)
        {
            Section($"ShadowCopy");
            var drive = GetFreeDrive();
            var di = ShadowBackupDI;            
            var rcArgs = GetRobocopyCommand(drive + ":\\", di.FullName);
            var args = $"/verbosity={s_verbosity} \"{DataDI.FullName}\" {drive}: {rcArgs}";
            var info = "Shadowspawn";
            var exitCode = Run(info, exe.FullName, args);

            if (exitCode > ExitCodes.OK)
            {
                // Removed the shadowcopy offset to get the real exit code
                exitCode = exitCode - ExitCodes.OK;

                if (exitCode >= 7)
                {
                    Error($"{info} robocopy failed with exit code: {exitCode}", exitCode);
                }
            }
            else
            {
                if (exitCode == -1073741515)
                {
                    // Missing VC++
                    Error($"{info} Missing VC++ Redistribule, shadowspawn doesn't work", exitCode);
                }
                else if (exitCode != 0)
                {
                    Error($"{info} Failed with exit coode {exitCode}", exitCode);
                }
            }



        }

        private static void DirectCopy()
        {
            Section($"DirectCopy");
            Robocopy(DataDI, DirectBackupDI);
        }

        private static void RestoreBackup()
        {
            Section($"RestoreBackup");
            Robocopy(ShadowBackupDI, DataDI);
        }

        private static void Robocopy(DirectoryInfo from, DirectoryInfo to)
        { 
            var rcArgs = GetRobocopyArgs(from.FullName, to.FullName);
            var info = "Robocopy";
            var exitCode = Run(info, info, rcArgs);

            if (exitCode >= 7)
            {
                Error($"{info} failed with exit code: {exitCode}", exitCode);
            }

        }

        private static int Run(string info, string exe, string args)
        {
            var watch = Stopwatch.StartNew();

            using (var p = new Process())
            {
                var psi = p.StartInfo;

                psi.FileName = exe;
                psi.UseShellExecute = false;
                psi.Arguments = args;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;
                Info($"{info} {psi.Arguments}");
                p.OutputDataReceived += OnProcessOut;
                p.ErrorDataReceived += OnProcessError;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                p.WaitForExit();
                p.OutputDataReceived -= OnProcessOut;
                p.ErrorDataReceived -= OnProcessError;

                Info($"{info} Took {watch.Elapsed}");
                return p.ExitCode;
            }
        }

        private static string GetRobocopyCommand(string from, string to)
        {
            return $"robocopy {GetRobocopyArgs(from, to)}";
        }

        private static string GetRobocopyArgs(string from, string to)
        {
            return $"{from} {to} /MIR /NP /ndl /njh /IS /r:1 /w:1";
        }

        private static void OnProcessError(object sender, DataReceivedEventArgs e)
        {
            Warn(e.Data);
        }

        private static void OnProcessOut(object sender, DataReceivedEventArgs e)
        {
            Write(e.Data, ConsoleColor.Cyan, ConsoleColor.Black);
        }

        internal static char GetFreeDrive()
        {
            var drives = Directory.GetLogicalDrives().ToDictionary(d => d.ToLower()[0]);

            for (char i = 'a'; i < 'z'; i++)
            {
                if (!drives.ContainsKey(i))
                {
                    return i;
                }
            }
            throw new ApplicationException("No free drive letter, cancelling mapping of drive");
        }




        private static FileInfo GetFile(DirectoryInfo di, int sizeMB, int i)
        {
            return new FileInfo(Path.Combine(di.FullName, $"data.{sizeMB}.{i}.dat"));
        }

        private static void Info(string v)
        {
            Write(v, ConsoleColor.Green, ConsoleColor.Black);
        }

        private static void Trace(string v)
        {
            if (s_isTrace)
            {
                Write(" " + v, ConsoleColor.Black, ConsoleColor.White); 
            }
        }

        private static void Warn(string v)
        {
            Write(v, ConsoleColor.Yellow, ConsoleColor.Black);
        }

        private static void Write(string v, ConsoleColor fg, ConsoleColor bg)
        {
            var ofg = Console.ForegroundColor;
            var obg = Console.BackgroundColor;

            try
            {
                Console.ForegroundColor = fg;
                Console.BackgroundColor = bg;
                Console.WriteLine(v);
            }
            finally
            {
                Console.ForegroundColor = ofg;
                Console.BackgroundColor = obg;
            }
        }

        private static void Error(string msg, int exitcode)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            var stdErr = Console.Error;

            if (s_redirectStdErr)
            {
                stdErr = Console.Out;
            }
            stdErr.WriteLine(msg);

            if (Debugger.IsAttached)
            {
                
                stdErr.WriteLine("Waiting for debugger");
                Debugger.Break();
            }

            Environment.Exit(exitcode);
        }

        private abstract class ExitCodes
        {
            public const int ProcessingError = 1;
            public const int UserError = 2;
            public const int OK = 0x8000;

            public const string Help =
@"Exit Status:

If there is an error while processing (e.g. ShadowSpawn fails to
create the shadow copy), ShadowSpawn exits with status 1.

If there is an error in usage (i.e. the user specifies an unknown
option), ShadowSpawn exits with status 2.

If everything else executes as expected and <command> exits with
status zero, ShadowSpawn also exits with status 0.

If everything else executes as expected and <command> exits with a
nonzero status code n, ShadowSpawn exits with status n logically OR'ed
with 32768 (0x8000). For example, robocopy exits with status 1 when
one or more files are Scopied. So, when executing

  shadowspawn C:\foo X: robocopy X:\ C:\path\to\backup /mir

the exit code of ShadowSpawn would be 32769 (0x8000 | 0x1).";
        }
    }
}
