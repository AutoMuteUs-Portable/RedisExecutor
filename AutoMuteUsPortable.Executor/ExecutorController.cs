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
    public new static Dictionary<string, Parameter> InstallParameters = new();
    public new static Dictionary<string, Parameter> UpdateParameters = new();
    private readonly ExecutorConfiguration _executorConfiguration;
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

        _executorConfiguration = tmp;

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

        _executorConfiguration = executorConfiguration;

        #endregion
    }

    public override async Task Run(ISubject<ProgressInfo>? progress = null)
    {
        if (IsRunning) return;

        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == _executorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (redis.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");

        #endregion

        #region Check file integrity

        progress?.OnNext(new ProgressInfo
        {
            name = $"Checking file integrity of {_executorConfiguration.type.ToString()}"
        });
        using (var client = new HttpClient())
        {
            var hashesTxt = await client.GetStringAsync(redis.Hashes);
            var hashes = Utils.ParseHashesTxt(hashesTxt);
            var invalidFiles = Utils.CompareHashes(_executorConfiguration.binaryDirectory, hashes);

            if (0 < invalidFiles.Count)
            {
                if (string.IsNullOrEmpty(redis.DownloadUrl))
                    throw new InvalidDataException("DownloadUrl cannot be null or empty");

                var binaryPath = Path.Combine(_executorConfiguration.binaryDirectory,
                    Path.GetFileName(redis.DownloadUrl));

                var downloadProgress = new Progress<double>();
                downloadProgress.ProgressChanged += (_, value) =>
                {
                    progress?.OnNext(new ProgressInfo
                    {
                        name = $"Downloading {_executorConfiguration.type.ToString()} {redis.Version}",
                        progress = value / 2.0
                    });
                };
                await Download(redis.DownloadUrl, binaryPath, downloadProgress);

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
            }
        }

        #endregion

        #region Search for currently running process and kill it

        var fileName = Path.Combine(_executorConfiguration.binaryDirectory, "redis-server.exe");

        progress?.OnNext(new ProgressInfo
        {
            name = $"Checking currently running {_executorConfiguration.type.ToString()}"
        });
        var wmiQueryString =
            $"SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = '{fileName.Replace(@"\", @"\\")}'";
        using (var searcher = new ManagementObjectSearcher(wmiQueryString))
        using (var results = searcher.Get())
        {
            foreach (var result in results)
            {
                var processId = (uint)result["ProcessId"];
                var process = Process.GetProcessById((int)processId);

                process.Kill();
                process.WaitForExit();
            }
        }

        #endregion

        #region Generate config

        var redisConfPath = Path.GetTempFileName();
        var redisConf = "bind 127.0.0.1" + "\r\n" +
                        "protected-mode yes" + "\r\n" +
                        $"port {_executorConfiguration.environmentVariables["REDIS_PORT"]}" + "\r\n" +
                        "tcp-backlog 511" + "\r\n" +
                        "timeout 0" + "\r\n" +
                        "tcp-keepalive 300" + "\r\n" +
                        "loglevel notice" + "\r\n" +
                        "logfile \"\"" + "\r\n" +
                        "databases 16" + "\r\n" +
                        "always-show-logo yes" + "\r\n" +
                        "save 900 1" + "\r\n" +
                        "save 300 10" + "\r\n" +
                        "save 60 10000" + "\r\n" +
                        "stop-writes-on-bgsave-error yes" + "\r\n" +
                        "rdbcompression yes" + "\r\n" +
                        "rdbchecksum yes" + "\r\n" +
                        "dbfilename dump.rdb" + "\r\n" +
                        "dir ./" + "\r\n" +
                        "replica-serve-stale-data yes" + "\r\n" +
                        "replica-read-only yes" + "\r\n" +
                        "repl-diskless-sync no" + "\r\n" +
                        "repl-diskless-sync-delay 5" + "\r\n" +
                        "repl-disable-tcp-nodelay no" + "\r\n" +
                        "replica-priority 100" + "\r\n" +
                        "lazyfree-lazy-eviction no" + "\r\n" +
                        "lazyfree-lazy-expire no" + "\r\n" +
                        "lazyfree-lazy-server-del no" + "\r\n" +
                        "replica-lazy-flush no" + "\r\n" +
                        "appendonly no" + "\r\n" +
                        "appendfilename \"appendonly.aof\"" + "\r\n" +
                        "appendfsync everysec" + "\r\n" +
                        "no-appendfsync-on-rewrite no" + "\r\n" +
                        "auto-aof-rewrite-percentage 100" + "\r\n" +
                        "auto-aof-rewrite-min-size 64mb" + "\r\n" +
                        "aof-load-truncated yes" + "\r\n" +
                        "aof-use-rdb-preamble yes" + "\r\n" +
                        "lua-time-limit 5000" + "\r\n" +
                        "slowlog-log-slower-than 10000" + "\r\n" +
                        "slowlog-max-len 128" + "\r\n" +
                        "latency-monitor-threshold 0" + "\r\n" +
                        "notify-keyspace-events \"\"" + "\r\n" +
                        "hash-max-ziplist-entries 512" + "\r\n" +
                        "hash-max-ziplist-value 64" + "\r\n" +
                        "list-max-ziplist-size -2" + "\r\n" +
                        "list-compress-depth 0" + "\r\n" +
                        "set-max-intset-entries 512" + "\r\n" +
                        "zset-max-ziplist-entries 128" + "\r\n" +
                        "zset-max-ziplist-value 64" + "\r\n" +
                        "hll-sparse-max-bytes 3000" + "\r\n" +
                        "stream-node-max-bytes 4096" + "\r\n" +
                        "stream-node-max-entries 100" + "\r\n" +
                        "activerehashing yes" + "\r\n" +
                        "client-output-buffer-limit normal 0 0 0" + "\r\n" +
                        "client-output-buffer-limit replica 256mb 64mb 60" + "\r\n" +
                        "client-output-buffer-limit pubsub 32mb 8mb 60" + "\r\n" +
                        "hz 10" + "\r\n" +
                        "dynamic-hz yes" + "\r\n" +
                        "aof-rewrite-incremental-fsync yes" + "\r\n" +
                        "rdb-save-incremental-fsync yes" + "\r\n";

        File.WriteAllText(redisConfPath, redisConf);

        #endregion

        #region Start server

        var startProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"\"{redisConfPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _executorConfiguration.binaryDirectory
            }
        };

        progress?.OnNext(new ProgressInfo
        {
            name =
                $"Starting {_executorConfiguration.type.ToString()} at port {_executorConfiguration.environmentVariables["REDIS_PORT"]}"
        });
        IsRunning = true;
        startProcess.Start();
        await startProcess.WaitForExitAsync();
        progress?.OnCompleted();

        #endregion
    }

    public override Task Stop(ISubject<ProgressInfo>? progress = null)
    {
        if (IsRunning) return Task.CompletedTask;

        #region Stop server in redis manner

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(_executorConfiguration.binaryDirectory, "redis-cli.exe"),
                Arguments = $"-p {_executorConfiguration.environmentVariables["REDIS_PORT"]} shutdown",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _executorConfiguration.binaryDirectory
            }
        };

        progress?.OnNext(new ProgressInfo
        {
            name = $"Stopping {_executorConfiguration.type.ToString()}"
        });
        process.Start();
        process.WaitForExit();
        IsRunning = false;
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

    public override async Task Install(Dictionary<string, string> parameters, ISubject<ProgressInfo>? progress = null)
    {
        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == _executorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not found in the database");
        // TODO: This doesn't work due to a bug of PocketBaseClient-csharp
        // if (redis.CompatibleExecutors.All(x => x.Version != _executorConfiguration.version))
        //     throw new InvalidDataException(
        //         $"{_executorConfiguration.type.ToString()} {_executorConfiguration.binaryVersion} is not compatible with Executor {_executorConfiguration.version}");
        if (string.IsNullOrEmpty(redis.DownloadUrl))
            throw new InvalidDataException("DownloadUrl cannot be null or empty");

        #endregion

        #region Download

        if (!Directory.Exists(_executorConfiguration.binaryDirectory))
            Directory.CreateDirectory(_executorConfiguration.binaryDirectory);

        var binaryPath = Path.Combine(_executorConfiguration.binaryDirectory,
            Path.GetFileName(redis.DownloadUrl));

        var downloadProgress = new Progress<double>();
        downloadProgress.ProgressChanged += (_, value) =>
        {
            progress?.OnNext(new ProgressInfo
            {
                name = $"Downloading {_executorConfiguration.type.ToString()} {redis.Version}",
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

    public override Task Update(Dictionary<string, string> parameters, ISubject<ProgressInfo>? progress = null)
    {
        progress?.OnCompleted();
        return Task.CompletedTask;
    }

    public override async Task InstallBySimpleSettings(object simpleSettings, object executorConfigurationBase,
        ISubject<ProgressInfo>? progress = null)
    {
        await Install(new Dictionary<string, string>(), progress);
    }

    public override async Task UpdateBySimpleSettings(object simpleSettings, object executorConfigurationBase,
        ISubject<ProgressInfo>? progress = null)
    {
        await Update(new Dictionary<string, string>(), progress);
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