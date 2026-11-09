using System.Security;
using System.Security.Principal;

namespace TaskManager.Engine;

/// <summary>
/// "Start with Windows" as a Task Scheduler logon task with highest privileges, so TaskMaster starts with admin
/// rights at sign-in without a UAC prompt every time. Creating or removing the task needs admin once (the
/// elevated --set-startup helper, see Shortcuts.SetStartWithWindows). Uses the Task Scheduler COM API, no schtasks.
/// </summary>
public static class StartupTask
{
    private const string Name = "TaskMaster";
    private const int CreateOrUpdate = 6, InteractiveToken = 3;

    public static bool Exists()
    {
        try { _ = Root().GetTask(Name); return true; }
        catch { return false; }
    }

    public static void Set(bool on, string exe)
    {
        var folder = Root();
        if (!on)
        {
            try { folder.DeleteTask(Name, 0); }
            catch (System.IO.FileNotFoundException) { /* not there */ }
            return;
        }
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts TaskMaster in the notification area when you sign in, with administrator rights.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        folder.RegisterTask(Name, xml, CreateOrUpdate, null, null, InteractiveToken, null);
    }

    private static dynamic Root()
    {
        dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        svc.Connect();
        return svc.GetFolder("\\");
    }
}
