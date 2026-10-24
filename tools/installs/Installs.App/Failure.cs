using System;
using System.IO;
using System.Linq;
using TLDInstalls;

namespace TLDInstalls.App;

/// <summary>A failed job's text for the log, and its full details in a file next to the cache.</summary>
static class Failure
{
    public static string Describe(Exception e)
    {
        var inner = e is AggregateException a && a.InnerExceptions.Count == 1 ? a.InnerExceptions[0] : e;
        var message = inner.Message.Trim();
        var where = (inner.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at "));
        return (message.Length > 0 && inner is UserError ? message : $"{inner.GetType().Name}: {(message.Length > 0 ? message : "(no message)")}")
               + (inner is UserError || where == null ? "" : $" ({where})");
    }

    public static string Record(string title, Exception e)
    {
        var file = Path.Combine(Env.CacheDir, "copies-errors.log");
        try
        {
            Directory.CreateDirectory(Env.CacheDir);
            File.AppendAllText(file, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {title}\n{e}\n\n");
        }
        catch (IOException) { return "not written: " + file; }
        catch (UnauthorizedAccessException) { return "not written: " + file; }
        return file;
    }
}
