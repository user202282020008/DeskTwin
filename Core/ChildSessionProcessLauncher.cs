// Task Scheduler launch approach derived from BetterGI, modified for DeskTwin on 2026-11-03.
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DeskTwin.Core;

internal static class ChildSessionProcessLauncher
{
    private const int TaskActionExecute = 0;
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskRunUseSessionId = 0x4;

    public static Task LaunchAsync(uint childSessionId, string executablePath)
    {
        var fullPath = ValidateExecutablePath(executablePath);
        return Task.Run(() => LaunchWithTemporaryTask(
            childSessionId,
            fullPath,
            Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory));
    }

    private static void LaunchWithTemporaryTask(
        uint childSessionId,
        string executablePath,
        string workingDirectory)
    {
        var actualSessionId = ChildSessionNative.GetSessionId();
        if (actualSessionId != childSessionId)
        {
            throw new InvalidOperationException("目标分身会话已变化，请刷新状态后重试。");
        }

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前 Windows 未提供任务计划程序。");
        var taskName = $"DeskTwin-Launch-{Guid.NewGuid():N}";
        string accountName;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            accountName = identity.Name;
        }

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        object? runningTaskObject = null;
        var taskRegistered = false;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType);
            dynamic scheduler = schedulerObject!;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject!;
            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject!;
            taskDefinition.RegistrationInfo.Author = "DeskTwin";
            taskDefinition.RegistrationInfo.Description =
                $"在 Child Session {childSessionId} 中启动 {Path.GetFileName(executablePath)}";
            taskDefinition.Settings.Enabled = true;
            taskDefinition.Settings.Hidden = true;
            taskDefinition.Settings.AllowDemandStart = true;
            taskDefinition.Settings.DisallowStartIfOnBatteries = false;
            taskDefinition.Settings.StopIfGoingOnBatteries = false;
            taskDefinition.Settings.ExecutionTimeLimit = "PT0S";
            taskDefinition.Principal.UserId = accountName;
            taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
            taskDefinition.Principal.RunLevel = TaskRunLevelHighest;

            actionObject = taskDefinition.Actions.Create(TaskActionExecute);
            dynamic action = actionObject!;
            action.Path = executablePath;
            action.WorkingDirectory = workingDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                taskName,
                taskDefinition,
                TaskCreate,
                accountName,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;

            dynamic registeredTask = registeredTaskObject!;
            runningTaskObject = registeredTask.RunEx(
                null,
                TaskRunUseSessionId,
                checked((int)childSessionId),
                null);
            if (runningTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回程序运行实例。");
            }
        }
        finally
        {
            if (taskRegistered && rootFolderObject is not null)
            {
                try
                {
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                catch (COMException)
                {
                    // 启动成功后，临时任务清理失败不影响目标程序。
                }
            }

            Release(runningTaskObject);
            Release(registeredTaskObject);
            Release(actionObject);
            Release(taskDefinitionObject);
            Release(rootFolderObject);
            Release(schedulerObject);
        }
    }

    private static string ValidateExecutablePath(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只允许选择 .exe 程序。", nameof(executablePath));
        }

        return fullPath;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
