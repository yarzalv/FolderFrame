using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

public class RegistryHelper
{
    public string regKeyName;
    public RegistryHelper(string regkeyname)
    {
        this.regKeyName = regkeyname;
    }
    public void WriteToRegistryRoot(string keyName, object value)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey($"SOFTWARE\\{regKeyName}"))
            {
                key.SetValue(keyName, value);
            }
        }
        catch { }
    }
    public bool KeyExistsRoot(string keyName)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey($"SOFTWARE\\{regKeyName}")!)
            {
                if (key != null)
                {
                    if (key.GetValue(keyName) != null)
                    {
                        Debug.WriteLine($"exists: {keyName},{key.GetValue(keyName)}");

                        return true;
                    }
                }
            }
            Debug.WriteLine($"doesnt exist: {keyName}");
            return false;
        }
        catch
        {
            Debug.WriteLine($"error opening HKCU\\SOFTWARE\\{regKeyName}, {keyName}");
            return false;
        }
    }
    public object ReadKeyValueRoot(string keyName)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey($"SOFTWARE\\{regKeyName}")!)
            {
                if (key != null)
                {
                    object value = key.GetValue(keyName)!;
                    if (value != null)
                    {
                        if (value is bool)
                        {
                            Debug.WriteLine($"returned bool for {keyName}");
                            return (bool)value;
                        }
                        else if (bool.TryParse(value.ToString(), out bool boolValue))
                        {
                            Debug.WriteLine($"returned Parsed bool for {keyName}");
                            return boolValue;
                        }
                        else
                        {
                            Debug.WriteLine($"returned string for {keyName}");
                            return (string)value;
                        }

                    }
                }
            }
            Debug.WriteLine($"couldn't return for {keyName}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error ReadKeyValueRoot: " + ex.Message);
            return null;
        }
    }
    public object ReadKeyValueRootInt(string keyName)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey($"SOFTWARE\\{regKeyName}")!)
            {
                if (key != null)
                {
                    return Int32.Parse(key.GetValue(keyName).ToString());
                }
            }

            Debug.WriteLine($"couldn't return value for {keyName}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error ReadKeyValueRootInt: " + ex.Message);
            return null;
        }
    }

    private const string RunKeyPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";

    // Autostart strategy: a Task Scheduler task with a logon trigger. It starts as soon as the user
    // session begins, in parallel with Explorer, while HKCU\...\Run entries are only launched by
    // Explorer after the desktop loads and Windows' startup delay. The Run key is kept as a fallback
    // when the task cannot be created.
    public void AddToAutoRun(string appName, string appPath)
    {
        appPath = appPath.Trim('\"');
        if (CreateLogonTask(appName, appPath))
        {
            // The task replaces the Run entry; having both would just start a second instance
            RemoveRunKeyValue(appName);
            WriteToRegistryRoot("AutoRunPath", appPath);
            return;
        }

        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)!)
            {
                key?.SetValue(appName, $"\"{appPath}\"");
            }
            WriteToRegistryRoot("AutoRunPath", appPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error adding to autorun: " + ex.Message);
        }
    }

    public bool IsInAutoRun(string appName)
    {
        if (LogonTaskExists(appName)) return true;
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false)!)
            {
                return key?.GetValue(appName) != null;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Registers autostart if missing, or re-registers it when the executable was moved.
    /// </summary>
    public void EnsureAutoRun(string appName, string appPath)
    {
        appPath = appPath.Trim('\"');
        string? registeredPath = KeyExistsRoot("AutoRunPath") ? ReadKeyValueRoot("AutoRunPath")?.ToString() : null;
        bool pathChanged = !string.Equals(registeredPath, appPath, StringComparison.OrdinalIgnoreCase);
        if (pathChanged || !IsInAutoRun(appName))
        {
            AddToAutoRun(appName, appPath);
        }
    }

    public void RemoveFromAutoRun(string appName)
    {
        RunSchtasks($"/Delete /TN \"{appName}\" /F");
        RemoveRunKeyValue(appName);
    }

    private static void RemoveRunKeyValue(string appName)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)!)
            {
                key?.DeleteValue(appName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error removing from autorun: " + ex.Message);
        }
    }

    private static bool LogonTaskExists(string appName)
    {
        return RunSchtasks($"/Query /TN \"{appName}\"") == 0;
    }

    private static bool CreateLogonTask(string appName, string appPath)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"{appName}_autostart.xml");
        try
        {
            string user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
            string command = System.Security.SecurityElement.Escape(appPath);
            string workDir = System.Security.SecurityElement.Escape(Path.GetDirectoryName(appPath) ?? "");

            // Priority 4 = normal process priority (the Task Scheduler default, 7, runs below normal).
            // No delay, no battery restrictions, no time limit.
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Starts {appName} when you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{command}</Command><WorkingDirectory>{workDir}</WorkingDirectory></Exec></Actions>
</Task>";
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{appName}\" /XML \"{xmlPath}\" /F") == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error creating logon task: " + ex.Message);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            // Full path: a bare "schtasks.exe" is searched in the app's own folder first, so a
            // planted copy next to FolderFrame.exe would run instead of Windows' one.
            string schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            using var process = Process.Start(new ProcessStartInfo(schtasks, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000)) return -1;
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }



}

