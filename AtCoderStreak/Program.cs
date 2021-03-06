﻿using AtCoderStreak.Model;
using AtCoderStreak.Service;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace AtCoderStreak
{
    public class Program : ConsoleAppBase
    {
        static string AppDir { get; } = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
        static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {

                    services.AddHttpClient("allowRedirect")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new HttpClientHandler()
                        {
                            AllowAutoRedirect = true,
                            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                        };
                    });
                    services.AddHttpClient("disallowRedirect")
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        return new HttpClientHandler()
                        {
                            AllowAutoRedirect = false,
                            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                        };
                    });
                    services.AddSingleton<IDataService>(
                        new DataService(Path.Combine(AppDir, "data.sqlite")));
                    services.AddSingleton<IStreakService, StreakService>();
                })
                .ConfigureLogging(logging =>
                {
#if DEBUG
                    logging.SetMinimumLevel(LogLevel.Debug);
#else
                    logging.SetMinimumLevel(LogLevel.Information).ReplaceToSimpleConsole();
#endif
                })
                .RunConsoleAppFrameworkAsync<Program>(args);
        }


        private IDataService DataService { get; }
        private IStreakService StreakService { get; }
        public Program(
            IDataService dataService,
            IStreakService streakService
            )
        {
            this.DataService = dataService;
            this.StreakService = streakService;
        }



        private string? LoadCookie(string? argCookie)
        {
            if (!string.IsNullOrEmpty(argCookie))
            {
                if (File.Exists(argCookie))
                    argCookie = File.ReadAllText(argCookie);

                if (!argCookie.Contains("%00"))
                    argCookie = HttpUtility.UrlEncode(argCookie);
                return argCookie;
            }
            else
            {
                return DataService.GetSession();
            }
        }


        [Command("login", "save atcoder cookie")]
        public async Task<int> Login(
            [Option("u", "username")] string? user = null)
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                Console.Write("input username: ");
                if (string.IsNullOrWhiteSpace(user = Console.ReadLine()))
                {
                    Context.Logger.LogError("Error: name is empty");
                    return 99;
                }
            }

            Console.Write("input password: ");
            var password = ConsoleUtil.ReadPassword();
            if (string.IsNullOrWhiteSpace(password))
            {
                Context.Logger.LogError("Error: password is empty");
                return 99;
            }

            return await LoginInternal(user, password);
        }

        internal async Task<int> LoginInternal(string user, string password)
        {
            var cookie = await StreakService.LoginAsync(user, password, Context.CancellationToken);
            if (cookie == null)
            {
                Context.Logger.LogError("Error: login failed");
                return 1;
            }
            DataService.SaveSession(cookie);
            Context.Logger.LogInformation("login success");
            return 0;
        }

        [Command("add", "add source code")]
        public int Add(
            [Option("u", "target task url")] string url,
            [Option("l", "language ID")] string lang,
            [Option("f", "source file path")] string file,
            [Option("p", "priority")] int priority = 0
            )
        {
            if (!File.Exists(file))
            {
                Context.Logger.LogError("Error:file not found");
                return 1;
            }
            foreach (var s in DataService.GetSourcesByUrl(url))
            {
                Context.Logger.LogInformation("[Warning]exist: {0}", s.ToString());
            }
            DataService.SaveSource(url, lang, priority, File.ReadAllBytes(file));
            Context.Logger.LogInformation($"finish: {url}, {file}, lang:{lang}, priority:{priority}");
            return 0;
        }

        [Command("restore", "restore source code")]
        public int Restore(
            [Option("f", "source file path")] string file,
            [Option(0, "source id")] int id = -1,
            [Option("u", "target task url")] string? url = null)
        {
            SavedSource? source;
            try
            {
                source = RestoreInternal(id, url);
            }
            catch (ArgumentException e)
            {
                Context.Logger.LogError(e.Message);
                return 128;
            }
            if (source != null)
            {
                Context.Logger.LogInformation("restore: {0}", source.ToString());
                File.WriteAllText(file, source.SourceCode, new UTF8Encoding(true));
                return 0;
            }
            else
            {
                Context.Logger.LogError($"Error: not found source");
                return 1;
            }
        }
        internal SavedSource? RestoreInternal(int id = -1, string? url = null)
        {
            if (string.IsNullOrWhiteSpace(url) == (id < 0))
                throw new ArgumentException($"Error: must use either {nameof(url)} or {nameof(id)}");

            if (!string.IsNullOrWhiteSpace(url))
                return DataService.GetSourcesByUrl(url).FirstOrDefault();
            else if (id >= 0)
                return DataService.GetSourceById(id);

            throw new InvalidOperationException("never");
        }

        [Command("latest", "get latest submit")]
        public async Task<int> Latest(
            [Option("c", "cookie header string or textfile")] string? cookie = null)
        {
            cookie = LoadCookie(cookie);
            if (cookie == null)
            {
                Context.Logger.LogError("Error: no session");
                return 255;
            }

            if (await LatestInternal(cookie) is { } max)
            {
                Context.Logger.LogInformation(max.ToString());
                return 0;
            }
            else
            {
                Context.Logger.LogError("Error: no AC submit");
                return 1;
            }
        }
        internal async Task<ProblemsSubmission?> LatestInternal(string cookie)
        {
            var submits = await StreakService.GetACSubmissionsAsync(cookie, Context.CancellationToken);
            return submits.Latest();
        }

        [Command("submitfile", "submit source from file")]
        public async Task<int> SubmitFile(
            [Option("f", "source file path")] string file,
            [Option("u", "target task url")] string url,
            [Option("l", "language ID")] string lang,
            [Option("c", "cookie header string or textfile")] string? cookie = null)
        {
            if (!File.Exists(file))
            {
                Context.Logger.LogError("Error: file not found");
                return 1;
            }
            return await SubmitFileInternal(File.ReadAllText(file), url, lang, cookie);
        }

        internal async Task<int> SubmitFileInternal(string sourceCode, string url, string lang, string? cookie = null)
        {
            cookie = LoadCookie(cookie);
            if (cookie == null)
            {
                Context.Logger.LogError("Error: no session");
                return 255;
            }
            try
            {
                var source = new SavedSource(0, url, lang, sourceCode, 0);
                var submitRes = await StreakService.SubmitSource(source, cookie, false, Context.CancellationToken);
                return 0;
            }
            catch (HttpRequestException e)
            {
                Context.Logger.LogError(e, "Error: submit error");
                return 2;
            }
        }

        [Command("submit", "submit source")]
        public async Task<int> Submit(
            [Option("o", "db order")] SourceOrder order = SourceOrder.None,
            [Option("f", "submit force")] bool force = false,
            [Option("p", "parallel count. if 0, streak mode")] int paralell = 0,
            [Option("c", "cookie header string or textfile")] string? cookie = null)
        {
            cookie = LoadCookie(cookie);
            if (cookie == null)
            {
                Context.Logger.LogError("Error: no session");
                return 255;
            }

            if (paralell > 0)
                return await SubmitParallel(order, paralell, cookie);

            try
            {
                if (await SubmitInternal(order, force, cookie) is { } latest)
                {
                    Context.Logger.LogInformation(latest.ToString());
                    return 0;
                }
                else
                {
                    Context.Logger.LogError("Error: not found new source");
                    return 1;
                }
            }
            catch (HttpRequestException e)
            {
                Context.Logger.LogError(e, "Error: submit error");
                return 2;
            }
        }

        internal async Task<int> SubmitParallel(SourceOrder order, int paralell, string cookie)
        {
            var res = await SubmitInternalParallel(order, cookie, paralell);
            foreach (var (source, submitSuccess) in res)
            {
                if (submitSuccess)
                {
                    Context.Logger.LogInformation("Submit: {0}", source.TaskUrl);
                }
                else
                {
                    Context.Logger.LogError("Failed to submit: {0}", source.TaskUrl);
                }
            }
            return 0;
        }


        internal static bool IsToday(DateTime dateTime)
            => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), TimeSpan.FromHours(9)).Date >= DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(9)).Date;

        internal async Task<(string contest, string problem, DateTime time)?>
            SubmitInternal(SourceOrder order, bool force, string cookie)
        {
            ProblemsSubmission[] submits = Array.Empty<ProblemsSubmission>();
            if (!force)
            {
                submits = await StreakService.GetACSubmissionsAsync(cookie, Context.CancellationToken);
                var latest = submits.Latest();
                if (latest != null && IsToday(latest.DateTime))
                    return (latest.ContestId!, latest.ProblemId!, latest.DateTime);
            }

            var accepted = new HashSet<(string contest, string problem)>(submits.Select(s => (s.ContestId!, s.ProblemId!)));
            (string contest, string problem, DateTime time)? submitRes = null;
            var usedIds = new List<int>();
            foreach (var source in DataService.GetSources(order))
            {

                if (source.CanParse())
                {
                    var (contest, problem, _) = source.SubmitInfo();
                    if (!accepted.Contains((contest, problem)))
                        submitRes = await StreakService.SubmitSource(source, cookie, true, Context.CancellationToken);
                }
                usedIds.Add(source.Id);
                if (submitRes.HasValue && submitRes.Value.time >= DateTime.SpecifyKind(DateTime.UtcNow.AddHours(9).Date, DateTimeKind.Unspecified))
                    break;
            }

            DataService.DeleteSources(usedIds);
            return submitRes;
        }

        internal async Task<(SavedSource source, bool submitSuccess)[]>
            SubmitInternalParallel(SourceOrder order, string cookie, int paralellNum)
        {
            ProblemsSubmission[] submits = Array.Empty<ProblemsSubmission>();
            var tasks = new List<(SavedSource source, Task<(string contest, string problem, DateTime time)?> submitTask)>();
            foreach (var source in DataService.GetSources(order))
            {
                if (source.CanParse())
                {
                    if (--paralellNum < 0)
                        break;
                    tasks.Add((source, StreakService.SubmitSource(source, cookie, false, Context.CancellationToken)));
                }
            }
            await Task.WhenAll(tasks.Select(t => t.submitTask));

            var usedIds = new List<int>();
            var res = new List<(SavedSource source, bool submitSuccess)>();
            foreach (var (source, task) in tasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    usedIds.Add(source.Id);
                    res.Add((source, true));
                }
                else
                {
                    res.Add((source, false));
                }
            }

            DataService.DeleteSources(usedIds);
            return res.ToArray();
        }

    }
}
