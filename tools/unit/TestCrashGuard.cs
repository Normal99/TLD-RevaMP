// Compiled into every unit test (Directory.Build.props): a failing test prints its error and exits with code 1
// instead of crashing, so Windows shows no "application error" dialog. Process-local; changes no system setting.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

static class TestCrashGuard
{
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);

    [ModuleInitializer]
    internal static void Install()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002 | 0x8000);   // no critical-error, GP-fault or open-file boxes
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine("Unhandled: " + e.ExceptionObject);
            Console.Error.Flush(); Console.Out.Flush();
            Environment.Exit(1);
        };
    }
}
