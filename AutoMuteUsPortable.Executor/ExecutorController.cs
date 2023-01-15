using System.Diagnostics;
using System.IO.Compression;
using System.Management;
using System.Reactive.Subjects;
using AutoMuteUsPortable.PocketBaseClient;
using AutoMuteUsPortable.Shared.Controller.Executor;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationBaseNS;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationNS;
using AutoMuteUsPortable.Shared.Entity.ProgressInfo;
using AutoMuteUsPortable.Shared.Utility;
using AutoMuteUsPortable.Shared.Utility.Dotnet.ZipFileProgressExtensionsNS;
using FluentValidation;

namespace AutoMuteUsPortable.Executor;

public class ExecutorController : ExecutorControllerBase
{
    private readonly PocketBaseClientApplication _pocketBaseClientApplication = new();

    public ExecutorController(object executorConfiguration) : base(executorConfiguration)
    {
        #region Check variables

        var binaryDirectory = Utils.PropertyByName<string>(executorConfiguration, "binaryDirectory");
        if (binaryDirectory == null)
            throw new InvalidDataException("binaryDirectory cannot be null");

        var binaryVersion = Utils.PropertyByName<string>(executorConfiguration, "binaryVersion");
        if (binaryVersion == null)
            throw new InvalidDataException("binaryVersion cannot be null");

        var version = Utils.PropertyByName<string>(executorConfiguration, "version");
        if (version == null) throw new InvalidDataException("version cannot be null");

        ExecutorType? type = Utils.PropertyByName<ExecutorType>(executorConfiguration, "type");
        if (type == null) throw new InvalidDataException("type cannot be null");

        var environmentVariables =
            Utils.PropertyByName<Dictionary<string, string>>(executorConfiguration, "environmentVariables");
        if (environmentVariables == null) throw new InvalidDataException("environmentVariables cannot be null");

        #endregion

        #region Create ExecutorConfiguration and validate

        ExecutorConfiguration tmp = new()
        {
            version = version,
            type = (ExecutorType)type,
            binaryVersion = binaryVersion,
            binaryDirectory = binaryDirectory,
            environmentVariables = environmentVariables
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(tmp);

        ExecutorConfiguration = tmp;

        #endregion
    }

    public ExecutorController(object computedSimpleSettings,
        object executorConfigurationBase) : base(computedSimpleSettings, executorConfigurationBase)
    {
        #region Check variables

        var binaryDirectory = Utils.PropertyByName<string>(executorConfigurationBase, "binaryDirectory");
        if (binaryDirectory == null)
            throw new InvalidDataException("binaryDirectory cannot be null");

        var binaryVersion = Utils.PropertyByName<string>(executorConfigurationBase, "binaryVersion");
        if (binaryVersion == null)
            throw new InvalidDataException("binaryVersion cannot be null");

        var version = Utils.PropertyByName<string>(executorConfigurationBase, "version");
        if (version == null) throw new InvalidDataException("version cannot be null");

        ExecutorType? type = Utils.PropertyByName<ExecutorType>(executorConfigurationBase, "type");
        if (type == null) throw new InvalidDataException("type cannot be null");

        if (Utils.PropertyInfoByName(computedSimpleSettings, "port") == null)
            throw new InvalidDataException("port is not found in computedSimpleSettings");
        var port = Utils.PropertyByName<object>(computedSimpleSettings, "port");
        int? redisPort = Utils.PropertyByName<int>(port!, "redis");
        if (redisPort == null) throw new InvalidDataException("redisPort cannot be null");

        #endregion

        #region Create ExecutorConfiguration and validate

        ExecutorConfiguration executorConfiguration = new()
        {
            version = version,
            type = (ExecutorType)type,
            binaryVersion = binaryVersion,
            binaryDirectory = binaryDirectory,
            environmentVariables = new Dictionary<string, string>
            {
                { "REDIS_PORT", redisPort.ToString() ?? "" }
            }
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(executorConfiguration);

        ExecutorConfiguration = executorConfiguration;

        #endregion
    }

    public override async Task Run(ISubject<ProgressInfo>? progress = null)
    {
        if (IsRunning) return;

        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type.ToString()} {ExecutorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (redis.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");

        #endregion

        // TODO: Too slow. Need to be optimized or find a better way to do this
        // #region Check file integrity
        //
        // progress?.OnNext(new ProgressInfo
        // {
        //     name = $"Checking file integrity of {_executorConfiguration.type.ToString()}"
        // });
        // using (var client = new HttpClient())
        // {
        //     var hashesTxt = await client.GetStringAsync(redis.Hashes);
        //     var hashes = Utils.ParseHashesTxt(hashesTxt);
        //     var invalidFiles = Utils.CompareHashes(_executorConfiguration.binaryDirectory, hashes);
        //
        //     if (0 < invalidFiles.Count)
        //     {
        //         if (string.IsNullOrEmpty(redis.DownloadUrl))
        //             throw new InvalidDataException("DownloadUrl cannot be null or empty");
        //
        //         var binaryPath = Path.Combine(_executorConfiguration.binaryDirectory,
        //             Path.GetFileName(redis.DownloadUrl));
        //
        //         var downloadProgress = new Progress<double>();
        //         downloadProgress.ProgressChanged += (_, value) =>
        //         {
        //             progress?.OnNext(new ProgressInfo
        //             {
        //                 name = $"Downloading {_executorConfiguration.type.ToString()} {redis.Version}",
        //                 progress = value / 2.0
        //             });
        //         };
        //         await Download(redis.DownloadUrl, binaryPath, downloadProgress);
        //
        //         var extractProgress = new Progress<double>();
        //         extractProgress.ProgressChanged += (_, value) =>
        //         {
        //             progress?.OnNext(new ProgressInfo
        //             {
        //                 name = $"Extracting {Path.GetFileName(redis.DownloadUrl)}",
        //                 progress = 0.5 + value / 2.0
        //             });
        //         };
        //         await ExtractZip(binaryPath, extractProgress);
        //     }
        // }
        //
        // #endregion

        #region Search for currently running process and kill it

        var fileName = Path.Combine(ExecutorConfiguration.binaryDirectory, "redis-server.exe");

        progress?.OnNext(new ProgressInfo
        {
            name = $"Checking currently running {ExecutorConfiguration.type.ToString()}"
        });
        var wmiQueryString =
            $"SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = '{fileName.Replace(@"\", @"\\")}'";
        using (var searcher = new ManagementObjectSearcher(wmiQueryString))
        using (var results = searcher.Get())
        {
            foreach (var result in results)
                try
                {
                    var processId = (uint)result["ProcessId"];
                    var process = Process.GetProcessById((int)processId);

                    process.Kill();
                    await process.WaitForExitAsync();
                }
                catch
                {
                }
        }

        #endregion

        #region Generate config

        var redisConfPath = Path.GetTempFileName();
        var redisConf = @"";

        await File.WriteAllTextAsync(redisConfPath, redisConf);

        #endregion

        #region Start server

        var startProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"\"{redisConfPath.Replace(@"\", @"\\")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ExecutorConfiguration.binaryDirectory
            }
        };

        progress?.OnNext(new ProgressInfo
        {
            name =
                $"Starting {ExecutorConfiguration.type.ToString()} at port {ExecutorConfiguration.environmentVariables["REDIS_PORT"]}"
        });
        IsRunning = true;
        startProcess.Start();
        progress?.OnCompleted();

        #endregion
    }

    public override Task Stop(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return Task.CompletedTask;

        #region Stop server in redis manner

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(ExecutorConfiguration.binaryDirectory, "redis-cli.exe"),
                Arguments = $"-p {ExecutorConfiguration.environmentVariables["REDIS_PORT"]} shutdown",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = ExecutorConfiguration.binaryDirectory
            }
        };

        progress?.OnNext(new ProgressInfo
        {
            name = $"Stopping {ExecutorConfiguration.type.ToString()}"
        });
        process.Start();
        process.WaitForExit();
        IsRunning = false;
        OnStop();
        progress?.OnCompleted();
        return Task.CompletedTask;

        #endregion
    }

    public override async Task Restart(ISubject<ProgressInfo>? progress = null)
    {
        #region Stop server

        var stopProgress = new Subject<ProgressInfo>();
        stopProgress.Subscribe(x => progress?.OnNext(new ProgressInfo
        {
            name = x.name,
            progress = x.progress / 2.0
        }));
        await Stop();
        stopProgress.Dispose();

        #endregion

        #region Start server

        var runProgress = new Subject<ProgressInfo>();
        runProgress.Subscribe(x => progress?.OnNext(new ProgressInfo
        {
            name = x.name,
            progress = 0.5 + x.progress / 2.0
        }));
        await Run();
        runProgress.Dispose();
        progress?.OnCompleted();

        #endregion
    }

    public override async Task Install(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null)
    {
        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type.ToString()} {ExecutorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (redis.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");
        if (string.IsNullOrEmpty(redis.DownloadUrl))
            throw new InvalidDataException("DownloadUrl cannot be null or empty");

        #endregion

        #region Download

        if (!Directory.Exists(ExecutorConfiguration.binaryDirectory))
            Directory.CreateDirectory(ExecutorConfiguration.binaryDirectory);

        var binaryPath = Path.Combine(ExecutorConfiguration.binaryDirectory,
            Path.GetFileName(redis.DownloadUrl));

        var downloadProgress = new Progress<double>();
        downloadProgress.ProgressChanged += (_, value) =>
        {
            progress?.OnNext(new ProgressInfo
            {
                name = $"Downloading {ExecutorConfiguration.type.ToString()} {redis.Version}",
                progress = value / 2.0
            });
        };
        await Download(redis.DownloadUrl, binaryPath, downloadProgress);

        #endregion

        #region Extract

        var extractProgress = new Progress<double>();
        extractProgress.ProgressChanged += (_, value) =>
        {
            progress?.OnNext(new ProgressInfo
            {
                name = $"Extracting {Path.GetFileName(redis.DownloadUrl)}",
                progress = 0.5 + value / 2.0
            });
        };
        await ExtractZip(binaryPath, extractProgress);

        progress?.OnCompleted();

        #endregion
    }

    public override Task Update(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, object oldExecutorConfiguration,
        ISubject<ProgressInfo>? progress = null)
    {
        progress?.OnCompleted();
        return Task.CompletedTask;
    }

    private Task ExtractZip(string path, IProgress<double>? progress = null)
    {
        using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(Path.GetDirectoryName(path)!, true, progress);
        }

        return Task.CompletedTask;
    }

    private async Task Download(string url, string path, IProgress<double>? progress = null)
    {
        using (var client = new HttpClient())
        using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await client.DownloadDataAsync(url, fileStream, progress);
        }
    }
}