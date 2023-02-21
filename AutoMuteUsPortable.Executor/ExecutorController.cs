using System.Diagnostics;
using System.Management;
using System.Reactive.Subjects;
using AutoMuteUsPortable.PocketBaseClient;
using AutoMuteUsPortable.Shared.Controller.Executor;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationBaseNS;
using AutoMuteUsPortable.Shared.Entity.ExecutorConfigurationNS;
using AutoMuteUsPortable.Shared.Entity.ProgressInfo;
using AutoMuteUsPortable.Shared.Utility;
using CliWrap;
using CliWrap.EventStream;
using FluentValidation;

namespace AutoMuteUsPortable.Executor;

public class ExecutorController : ExecutorControllerBase
{
    private readonly PocketBaseClientApplication _pocketBaseClientApplication = new();
    private CancellationTokenSource _forcefulCTS = new();
    private CancellationTokenSource _gracefulCTS = new();

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
                ["REDIS_PORT"] = redisPort.ToString() ?? ""
            }
        };

        var validator = new ExecutorConfigurationValidator();
        validator.ValidateAndThrow(executorConfiguration);

        ExecutorConfiguration = executorConfiguration;

        #endregion
    }

    public override async Task Run(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new Dictionary<string, object?>
            {
                ["File integrity check"] = new List<string>
                {
                    "Checking file integrity",
                    "Downloading",
                    "Extracting"
                },
                ["Killing currently running server"] = null,
                ["Starting server"] = null
            })
            : null;

        #endregion

        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not found in the database");
        if (redis.CompatibleExecutors.All(x => x.Version != ExecutorConfiguration.version))
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not compatible with Executor {ExecutorConfiguration.version}");

        #endregion

        #region Check file integrity

        var checksumUrl = Utils.GetChecksum(redis.Checksum);

        if (string.IsNullOrEmpty(checksumUrl))
        {
#if DEBUG
            // Continue without checksum file
            // TODO: log out as DEBUG Level
            taskProgress?.NextTask(3);
#else
                throw new InvalidDataException("Checksum cannot be null or empty");
#endif
        }
        else
        {
            using (var client = new HttpClient())
            {
                var res = await client.GetStringAsync(checksumUrl, cancellationToken);
                var checksum = Utils.ParseChecksumText(res);
                var checksumProgress = taskProgress?.GetSubjectProgress();
                checksumProgress?.OnNext(new ProgressInfo
                {
                    name = string.Format("{0}のファイルの整合性を確認しています", ExecutorConfiguration.type),
                    IsIndeterminate = true
                });
                var invalidFiles = Utils.CompareChecksum(ExecutorConfiguration.binaryDirectory, checksum, cancellationToken);
                taskProgress?.NextTask();

                if (0 < invalidFiles.Count)
                {
                    var downloadUrl = Utils.GetDownloadUrl(redis.DownloadUrl);
                    if (string.IsNullOrEmpty(downloadUrl))
                        throw new InvalidDataException("DownloadUrl cannot be null or empty");

                    var binaryPath = Path.Combine(ExecutorConfiguration.binaryDirectory,
                        Path.GetFileName(downloadUrl));

                    var downloadProgress = taskProgress?.GetProgress();
                    if (taskProgress?.ActiveLeafTask != null)
                        taskProgress.ActiveLeafTask.Name =
                            string.Format("{0}の実行に必要なファイルをダウンロードしています", ExecutorConfiguration.type);
                    await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress, cancellationToken);
                    taskProgress?.NextTask();

                    var extractProgress = taskProgress?.GetProgress();
                    if (taskProgress?.ActiveLeafTask != null)
                        taskProgress.ActiveLeafTask.Name =
                            string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
                    Utils.ExtractZip(binaryPath, extractProgress, cancellationToken);
                    taskProgress?.NextTask();
                }
                else
                {
                    taskProgress?.NextTask(2);
                }
            }
        }

        #endregion

        #region Search for currently running process and kill it

        var fileName = Path.Combine(ExecutorConfiguration.binaryDirectory, "redis-server.exe");

        var killingProgress = taskProgress?.GetSubjectProgress();
        killingProgress?.OnNext(new ProgressInfo
        {
            name = string.Format("既に起動している{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        var wmiQueryString =
            $"SELECT ProcessId FROM Win32_Process WHERE ExecutablePath = '{fileName.Replace(@"\", @"\\")}'";
        using (var searcher = new ManagementObjectSearcher(wmiQueryString))
        using (var results = searcher.Get())
        {
            foreach (var result in results)
            {
                try
                {
                    var processId = (uint)result["ProcessId"];
                    var process = Process.GetProcessById((int)processId);

                    process.Kill();
                }
                catch
                {
                    // ignored
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        taskProgress?.NextTask();

        #endregion

        #region Generate config

        var redisConfPath = Path.GetTempFileName();
        var redisConf = @"";

        await File.WriteAllTextAsync(redisConfPath, redisConf, cancellationToken);

        #endregion

        #region Start server

        var startProgress = taskProgress?.GetSubjectProgress();
        startProgress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を起動しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        var cmd = Cli.Wrap(fileName)
            .WithArguments($"\"{redisConfPath.Replace(@"\", @"\\")}\"")
            .WithWorkingDirectory(ExecutorConfiguration.binaryDirectory)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(ProcessStandardOutput))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(ProcessStandardError));

        _forcefulCTS = new CancellationTokenSource();
        var linkedForcefulCTS = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _forcefulCTS.Token);
        _gracefulCTS = new CancellationTokenSource();
        try
        {
            cmd.Observe(Console.OutputEncoding, Console.OutputEncoding, linkedForcefulCTS.Token, _gracefulCTS.Token)
                .Subscribe(
                    e =>
                    {
                        switch (e)
                        {
                            case StartedCommandEvent started:
                                OnStart();
                                break;
                            case ExitedCommandEvent exited:
                                OnStop();
                                break;
                        }
                    }, _ => OnStop(), OnStop);
        }
        catch (OperationCanceledException ex)
        {
            // ignored
        }
        catch
        {
            // ignored
            // TODO: handle exception more elegantly
        }

        taskProgress?.NextTask();

        #endregion
    }

    public override async Task GracefullyStop(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;

        #region Stop server in redis manner

        progress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        await Cli.Wrap(Path.Combine(ExecutorConfiguration.binaryDirectory, "redis-cli.exe"))
            .WithArguments($"-p {ExecutorConfiguration.environmentVariables["REDIS_PORT"]} shutdown")
            .WithWorkingDirectory(ExecutorConfiguration.binaryDirectory)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(ProcessStandardOutput))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(ProcessStandardError))
            .ExecuteAsync(cancellationToken);

        #endregion
    }

    public override Task ForciblyStop(ISubject<ProgressInfo>? progress = null)
    {
        if (!IsRunning) return Task.CompletedTask;

        #region Stop server

        var ewh = new AutoResetEvent(false);
        Stopped += (sender, args) => ewh.Set();

        progress?.OnNext(new ProgressInfo
        {
            name = string.Format("{0}を終了しています", ExecutorConfiguration.type),
            IsIndeterminate = true
        });
        _forcefulCTS.Cancel();
        ewh.WaitOne();

        return Task.CompletedTask;

        #endregion
    }

    public override async Task Restart(ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning) return;

        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new List<string>
            {
                "Stopping",
                "Starting"
            })
            : null;

        #endregion

        #region Stop server

        var stopProgress = taskProgress?.GetSubjectProgress();
        await GracefullyStop(stopProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion

        #region Start server

        var runProgress = taskProgress?.GetSubjectProgress();
        await Run(runProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion
    }

    public override async Task Install(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, ISubject<ProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        #region Setup progress

        var taskProgress = progress != null
            ? new TaskProgress(progress, new List<string>
            {
                "Downloading",
                "Extracting"
            })
            : null;

        #endregion

        #region Retrieve data from PocketBase

        var redis =
            _pocketBaseClientApplication.Data.RedisCollection.FirstOrDefault(x =>
                x.Version == ExecutorConfiguration.binaryVersion);
        if (redis == null)
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not found in the database");
        if (redis.CompatibleExecutors.All(x => x.Version != ExecutorConfiguration.version))
            throw new InvalidDataException(
                $"{ExecutorConfiguration.type} {ExecutorConfiguration.binaryVersion} is not compatible with Executor {ExecutorConfiguration.version}");
        var downloadUrl = Utils.GetDownloadUrl(redis.DownloadUrl);
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidDataException("DownloadUrl cannot be null or empty");

        #endregion

        #region Download

        if (!Directory.Exists(ExecutorConfiguration.binaryDirectory))
            Directory.CreateDirectory(ExecutorConfiguration.binaryDirectory);

        var binaryPath = Path.Combine(ExecutorConfiguration.binaryDirectory,
            Path.GetFileName(downloadUrl));

        var downloadProgress = taskProgress?.GetProgress();
        if (taskProgress?.ActiveLeafTask != null)
            taskProgress.ActiveLeafTask.Name = string.Format("{0}の実行に必要なファイルをダウンロードしています", ExecutorConfiguration.type);
        await Utils.DownloadAsync(downloadUrl, binaryPath, downloadProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion

        #region Extract

        var extractProgress = taskProgress?.GetProgress();
        if (taskProgress?.ActiveLeafTask != null)
            taskProgress.ActiveLeafTask.Name = string.Format("{0}の実行に必要なファイルを解凍しています", ExecutorConfiguration.type);
        Utils.ExtractZip(binaryPath, extractProgress, cancellationToken);
        taskProgress?.NextTask();

        #endregion
    }

    public override Task Update(
        Dictionary<ExecutorType, ExecutorControllerBase> executors, object oldExecutorConfiguration,
        ISubject<ProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private void ProcessStandardOutput(string text)
    {
        StandardOutput.OnNext(text);
    }

    private void ProcessStandardError(string text)
    {
        StandardError.OnNext(text);
    }
}