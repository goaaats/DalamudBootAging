using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Swan.Logging;

namespace DalamudBootAging
{
    class Program
    {
        public enum Result
        {
            Success,
            StartFailed,
            InjectorFailed,
            Timeout,
        }

        private static void KillGames()
        {
            foreach (var process in Process.GetProcessesByName("ffxiv_dx11"))
            {
                process.Kill();
            }
            
            foreach (var process in Process.GetProcessesByName("Dalamud.Injector"))
            {
                process.Kill();
            }
        }

        private static void SendBadWindowMessages()
        {
            foreach (var process in Process.GetProcessesByName("ffxiv_dx11"))
            {
                var handles = process.GetWindowHandles();
                foreach (var handle in handles)
                {
                    ProcessExtensions.Win32.PostMessage(handle, 0x219, new IntPtr(7), IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dalamudBinaryDir">Path to Dalamud binaries</param>
        /// <param name="gameExecutable">Path to the game executable</param>
        /// <param name="runs">Number of times to start the game</param>
        static void Main(DirectoryInfo dalamudBinaryDir, FileInfo gameExecutable, int runs = 1000)
        {
            var apiModule = new ApiController();
            
            using var server = CreateWebServer(apiModule);
            server.RunAsync();

            var rng = new Random();
            var results = new List<Result>();
            
            var signal = new ManualResetEvent(false);
            
            apiModule.IndicatedSuccess += () =>
            {
                signal.Set();
            };
            
            for (var i = 0; i < runs; i++)
            {
                try
                {
                    signal.Reset();
                    
                    var psi = new ProcessStartInfo(Path.Combine(dalamudBinaryDir.FullName, "Dalamud.Injector.exe"))
                    {
                        WorkingDirectory = dalamudBinaryDir.FullName,
                        Arguments = $"launch -g \"{gameExecutable.FullName}\" -- DEV.TestSID=0 region={rng.Next(0, 3)} language={rng.Next(0, 3)} DEV.MaxEntitledExpansionID={rng.Next(0, 5)}",
                    };
                
                    var process = Process.Start(psi);
                    var started = DateTime.Now;
                    
                    if (process == null)
                    {
                        $"[{i}/{runs}] Injector process was null".Error();
                        results.Add(Result.InjectorFailed);
                        continue;
                    }
                    
                    while (true)
                    {
                        if (process.HasExited && process.ExitCode != 0)
                        {
                            $"[{i}/{runs}] Injector had non-zero exit code".Error();
                            results.Add(Result.InjectorFailed);
                            continue;
                        }
                        
                        if (signal.WaitOne(TimeSpan.FromMilliseconds(100)))
                        {
                            results.Add(Result.Success);
                            break;
                        }
                        
                        if (DateTime.Now - started > TimeSpan.FromSeconds(60))
                        {
                            $"[{i}/{runs}] Timeout exceeded".Error();
                            results.Add(Result.Timeout);
                            break;
                        }
                        
                        SendBadWindowMessages();
                    }
                }
                catch (Exception e)
                {
                    $"[{i}/{runs}] Failed to start injector\n{e}".Error();
                    results.Add(Result.StartFailed);
                }
                
                KillGames();
                Thread.Sleep(1000);
            }
        }

        private static WebServer CreateWebServer(ApiController module)
        {
            var server = new WebServer(o => o
                .WithUrlPrefix("http://localhost:1415")
                .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/aging", o => o.WithController(() => module));
            
            server.StateChanged += (sender, args) => $"WebServer New State - {args.NewState}".Info();

            return server;
        }
    }

    class ApiController : WebApiController
    {
        public event Action IndicatedSuccess;

        [Route(HttpVerbs.Post, "/success")]
        public void Success()
        {
            IndicatedSuccess?.Invoke();
        }
    }
}